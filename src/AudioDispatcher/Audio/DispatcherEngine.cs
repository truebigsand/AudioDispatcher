using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AudioDispatcher.Logging;
using AudioDispatcher.Settings;
using NAudio.CoreAudioApi;

namespace AudioDispatcher.Audio;

/// <summary>
/// 分发编排核心。
/// - 运行状态机:SetRunning 启动/停止捕获与全部已启用目标;
/// - 数据路径:SourceCapture.SamplesReady → 各 TargetOutput.WriteSamples(捕获线程);
/// - watchdog(500ms):源数据停止检测(进入静默/重建捕获)、设备热插拔刷新、断线目标自动处置;
/// - UI 通过事件感知结构变化,通过轮询快照读实时值(电平/统计)。
/// 线程纪律:_lock 保护结构;volatile 保护状态标志;渲染/捕获线程只经各自对象锁。
/// </summary>
public sealed class DispatcherEngine : IDisposable
{
    private readonly DeviceService _devices;
    private readonly AppSettings _settings;
    private readonly object _lock = new();
    private readonly Timer _watchdog;
    private readonly Dictionary<string, string> _errors = new();

    private SourceCapture? _source;
    private readonly List<TargetOutput> _targets = new();
    private readonly Dictionary<string, TargetOutput> _targetById = new();
    private List<RenderInfo> _candidates = new();
    private List<SourceInfo> _sourceCandidates = new();
    // 音频线程(捕获回调/渲染)经此快照访问目标列表,避免与 UI 持锁(设备启停/枚举)互等
    private volatile TargetOutput[] _targetSnapshot = System.Array.Empty<TargetOutput>();

    private volatile bool _running;
    private volatile bool _silent;
    private int _bufferMs;
    private long _lastWatchFrames;
    private DateTime _lastDataUtc = DateTime.UtcNow;
    private DateTime _lastContentUtc = DateTime.UtcNow;
    private DateTime _lastRestartUtc = DateTime.MinValue;

    // ---- 事件(均在非音频线程触发,UI 自行 marshal) ----
    public event Action? EndpointsChanged;
    public event Action? SourceCandidatesChanged;
    public event Action<string, string>? TargetError;
    public event Action<string>? SourceLost;
    public event Action? RunningChanged;

    public DispatcherEngine(DeviceService devices, AppSettings settings)
    {
        _devices = devices;
        _settings = settings;
        _bufferMs = settings.BufferMs;
        _devices.Changed += OnDevicesChanged;
        _watchdog = new Timer(_ => OnTick(), null, 300, 500);
        RefreshDeviceLists();
    }

    public bool Running => _running;
    public bool SourceSilent => _silent;
    public int BufferMs => _bufferMs;

    public SourceCapture? Source => _source;
    public IReadOnlyList<RenderInfo> Candidates => _candidates;
    public IReadOnlyList<TargetOutput> ActiveTargets
    {
        get
        {
            lock (_lock)
            {
                return _targets.ToArray();
            }
        }
    }

    public string? GetError(string deviceId)
    {
        lock (_lock)
        {
            return _errors.TryGetValue(deviceId, out var e) ? e : null;
        }
    }

    public TargetOutput? GetTarget(string deviceId)
    {
        lock (_lock)
        {
            return _targetById.GetValueOrDefault(deviceId);
        }
    }

    /// <summary>源候选(供 UI 下拉)。附带当前源端点名(未启动时为 null)。</summary>
    public IReadOnlyList<SourceInfo> SourceCandidates() => _sourceCandidates;

    // ================= 运行控制(UI 线程调用) =================

    /// <summary>启动/停止分发。返回是否成功(启动时源不可用返回 false 并触发 SourceLost)。</summary>
    public bool SetRunning(bool on)
    {
        lock (_lock)
        {
            if (on == _running)
            {
                return _running || TryEnsureSourceLocked();
            }
            if (!on)
            {
                StopAllLocked("手动停止");
                SetRunningField(false);
                return true;
            }

            // 启动
            if (_source == null && !TryEnsureSourceLocked())
            {
                SetRunningField(false);
                SourceLost?.Invoke("未找到 VB-Audio Cable 捕获端点,请先安装并连接虚拟声卡");
                return false;
            }
            StartAllTargetsLocked();
            SetRunningField(true);
            _lastDataUtc = DateTime.UtcNow;
            _lastContentUtc = DateTime.UtcNow;
            _silent = false;
            AppLog.Info($"分发启动: {_targets.Count} 个目标设备, 缓冲 {_bufferMs}ms");
            return true;
        }
    }

    private void SetRunningField(bool value)
    {
        if (_running != value)
        {
            _running = value;
            RunningChanged?.Invoke();
        }
    }

    public void SetSource(string deviceId)
    {
        lock (_lock)
        {
            if (_source != null && _source.Device.ID == deviceId)
            {
                return;
            }
            _settings.SourceDeviceId = deviceId;
            StopSourceLocked("切换源");
            SetRunningField(false); // 目标流先停,等待重新启动
            StopAllTargetsLocked();
            EndpointsChanged?.Invoke();
        }
    }

    public void SetTargetEnabled(string deviceId, bool enabled)
    {
        lock (_lock)
        {
            var cfg = GetOrAddConfig(deviceId);
            cfg.Enabled = enabled;
            if (!enabled)
            {
                StopTargetLocked(deviceId, "停用");
            }
            else if (_running && _targetById.ContainsKey(deviceId) == false)
            {
                TryStartTargetLocked(deviceId);
            }
            EndpointsChanged?.Invoke();
        }
    }

    /// <summary>设置目标设备的系统音量(端点主音量,0..1)。分发未启用该设备时即时打开/释放。</summary>
    public void SetVolume(string deviceId, double volume)
    {
        WithEndpointVolume(deviceId, v =>
            v.MasterVolumeLevelScalar = (float)Math.Clamp(volume, 0, 1));
    }

    public void SetMuted(string deviceId, bool muted)
    {
        WithEndpointVolume(deviceId, v => v.Mute = muted);
    }

    /// <summary>读取目标设备当前系统音量与静音状态。</summary>
    public (double Volume, bool Muted) GetVolumeState(string deviceId)
    {
        var result = (Volume: 1.0, Muted: false);
        WithEndpointVolume(deviceId, v =>
        {
            result = (v.MasterVolumeLevelScalar, v.Mute);
        });
        return result;
    }

    private void WithEndpointVolume(string deviceId, Action<NAudio.CoreAudioApi.AudioEndpointVolume> action)
    {
        TargetOutput? t = null;
        lock (_lock)
        {
            _targetById.TryGetValue(deviceId, out t);
        }
        try
        {
            if (t != null)
            {
                action(t.Device.AudioEndpointVolume);
                return;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取运行目标 {deviceId} 端点音量失败: {ex.Message}");
        }

        // 目标未在分发:瞬时打开设备执行(滑块拖动高频,但 Activate 开销 ~0.1ms 级)
        var dev = _devices.OpenRenderDevice(deviceId);
        if (dev == null)
        {
            return;
        }
        try
        {
            action(dev.AudioEndpointVolume);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"设置设备 {deviceId} 端点音量失败: {ex.Message}");
        }
        finally
        {
            dev.Dispose();
        }
    }

    public void SetBufferMs(int ms)
    {
        ms = Math.Clamp(ms, 10, 500);
        lock (_lock)
        {
            if (ms == _bufferMs)
            {
                return;
            }
            _bufferMs = ms;
            _settings.BufferMs = ms;
            if (_running)
            {
                AppLog.Info($"缓冲调整至 {ms}ms,重建目标流");
                RestartAllTargetsLocked();
            }
        }
    }

    public void PlayTestTone(string deviceId)
    {
        GetTarget(deviceId)?.PlayTestTone();
    }

    // ================= 内部:结构管理(持 _lock) =================

    private bool TryEnsureSourceLocked()
    {
        if (_source != null)
        {
            return true;
        }
        var candidates = _devices.SourceCandidates();
        var id = _settings.SourceDeviceId;
        if (string.IsNullOrEmpty(id) || !candidates.Any(c => c.Id == id))
        {
            id = candidates.Count == 1 ? candidates[0].Id :
                 candidates.FirstOrDefault(c => c.Id == _settings.SourceDeviceId)?.Id ?? "";
        }
        if (string.IsNullOrEmpty(id))
        {
            return false;
        }
        var device = _devices.OpenSourceDevice(id);
        if (device == null)
        {
            return false;
        }
        try
        {
            var source = new SourceCapture(device);
            source.SamplesReady += OnSamplesReady;
            source.Start();
            _source = source;
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "源捕获启动失败");
            device.Dispose();
            _source = null;
            return false;
        }
    }

    private void StartAllTargetsLocked()
    {
        foreach (var cfg in _settings.Targets)
        {
            if (cfg.Enabled && _candidates.Any(c => c.Id == cfg.DeviceId && c.Present))
            {
                TryStartTargetLocked(cfg.DeviceId);
            }
        }
    }

    private void TryStartTargetLocked(string deviceId)
    {
        if (_targetById.ContainsKey(deviceId))
        {
            return;
        }
        var device = _devices.OpenRenderDevice(deviceId);
        if (device == null)
        {
            _errors[deviceId] = "设备不存在";
            return;
        }
        try
        {
            var target = new TargetOutput(device, _source!.SampleRate);
            target.Start(_bufferMs);
            _targets.Add(target);
            _targetById[deviceId] = target;
            _targetSnapshot = _targets.ToArray(); // 持锁刷新音频线程快照
            _errors.Remove(deviceId);
            AppLog.Info($"目标启动: {target.Name}");
        }
        catch (Exception ex)
        {
            _errors[deviceId] = ex.Message;
            device.Dispose();
            AppLog.Error(ex, $"目标启动失败: {deviceId}");
            TargetError?.Invoke(deviceId, ex.Message);
        }
    }

    private void StopTargetLocked(string deviceId, string reason)
    {
        if (_targetById.Remove(deviceId, out var t))
        {
            _targets.Remove(t);
            _targetSnapshot = _targets.ToArray(); // 持锁刷新音频线程快照
            try
            {
                t.Dispose();
            }
            catch (Exception ex)
            {
                AppLog.Warn($"目标 {deviceId} 停止异常: {ex.Message}");
            }
            AppLog.Info($"目标停止({reason}): {deviceId}");
        }
    }

    private void StopAllTargetsLocked()
    {
        foreach (var t in _targets.ToArray())
        {
            StopTargetLocked(t.DeviceId, "全停");
        }
    }

    private void RestartAllTargetsLocked()
    {
        var ids = _targets.Select(t => t.DeviceId).ToArray();
        StopAllTargetsLocked();
        foreach (var id in ids)
        {
            TryStartTargetLocked(id);
        }
    }

    private void StopSourceLocked(string reason)
    {
        if (_source != null)
        {
            _source.SamplesReady -= OnSamplesReady;
            var s = _source;
            _source = null;
            try
            {
                s.Dispose();
            }
            catch (Exception ex)
            {
                AppLog.Warn($"源停止异常({reason}): {ex.Message}");
            }
        }
    }

    private void StopAllLocked(string reason)
    {
        StopAllTargetsLocked();
        StopSourceLocked(reason);
        _silent = false;
    }

    private TargetSetting GetOrAddConfig(string deviceId)
    {
        var cfg = _settings.Targets.FirstOrDefault(t => t.DeviceId == deviceId);
        if (cfg == null)
        {
            cfg = new TargetSetting { DeviceId = deviceId };
            _settings.Targets.Add(cfg);
        }
        return cfg;
    }

    // ================= 数据与看门狗 =================

    private void OnSamplesReady(float[] samples, int count)
    {
        // 无锁路径:快照由结构变更方在持锁时刷新
        var targets = _targetSnapshot;
        if (targets.Length == 0)
        {
            return;
        }
        _lastDataUtc = DateTime.UtcNow;
        var frames = count / 2;
        foreach (var t in targets)
        {
            try
            {
                t.WriteSamples(samples, frames);
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, $"写入目标 {t.Name} 失败");
            }
        }
    }

    private void OnDevicesChanged()
    {
        lock (_lock)
        {
            var hadSource = _source != null;
            var hadCandidates = _sourceCandidates.Count > 0;
            RefreshDeviceListsLocked();

            // 源设备消失 → 停源
            if (hadSource && _source != null)
            {
                var stillThere = _sourceCandidates.Any(c => c.Id == _source.Device.ID);
                if (!stillThere)
                {
                    var lostName = _source.DeviceName;
                    StopSourceLocked("设备消失");
                    SourceLost?.Invoke($"源设备 {lostName} 已断开");
                }
            }
            if (_source == null && hadCandidates && _sourceCandidates.Count > 0)
            {
                // 源回来了:若正在运行则自动恢复
                TryEnsureSourceLocked();
            }

            // 目标消失 → 停;回来且配置启用 → 自动重启
            foreach (var id in _targets.Select(t => t.DeviceId).ToArray())
            {
                var c = _candidates.FirstOrDefault(x => x.Id == id);
                if (c == null || !c.Present)
                {
                    StopTargetLocked(id, "设备消失");
                    TargetError?.Invoke(id, "设备已断开,分发已停止(插回后自动恢复)");
                }
            }
            if (_running)
            {
                StartAllTargetsLocked();
            }

            if (hadSource != (_source != null) || hadCandidates != (_sourceCandidates.Count > 0))
            {
                SourceCandidatesChanged?.Invoke();
            }
            EndpointsChanged?.Invoke();
        }
    }

    private void RefreshDeviceLists()
    {
        lock (_lock)
        {
            RefreshDeviceListsLocked();
        }
    }

    private void RefreshDeviceListsLocked()
    {
        _sourceCandidates = _devices.SourceCandidates();
        _candidates = _devices.RenderCandidates(_settings.BlockedDeviceNames);
    }

    private void OnTick()
    {
        try
        {
            lock (_lock)
            {
                if (!_running)
                {
                    return;
                }
                if (_source == null || !_source.IsRunning)
                {
                    // 源失效:尝试重建(限频 10s)
                    TryRestartSourceLocked();
                    return;
                }

                var now = DateTime.UtcNow;

                // 1) 设备级故障:数据帧完全停止 6s → 重建捕获
                var frames = _source.TotalFrames;
                var hasFrames = frames != _lastWatchFrames;
                _lastWatchFrames = frames;
                if (!hasFrames && now - _lastDataUtc > TimeSpan.FromSeconds(6))
                {
                    AppLog.Warn("源数据帧停止超过 6s,尝试重建捕获");
                    TryRestartSourceLocked();
                    _lastDataUtc = now;
                    return;
                }
                if (hasFrames)
                {
                    _lastDataUtc = now;
                }

                // 2) 内容级静默:帧在流动但都是静音(如无应用播放)→ 目标静默
                var hasContent = _source.LastLevelRms > 0.001f; // ≈ -60 dBFS
                if (hasContent)
                {
                    _lastContentUtc = now;
                    if (_silent)
                    {
                        _silent = false;
                        foreach (var t in _targets)
                        {
                            t.ResumeFromSilence();
                        }
                        AppLog.Info("源声音内容恢复,退出静默");
                    }
                }
                else if (!_silent && now - _lastContentUtc > TimeSpan.FromSeconds(1))
                {
                    _silent = true;
                    foreach (var t in _targets)
                    {
                        t.EnterSilentMode();
                    }
                    AppLog.Warn("源无声音内容超过 1s(无应用在播放),目标进入静默");
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "看门狗异常");
        }
    }

    private void TryRestartSourceLocked()
    {
        var now = DateTime.UtcNow;
        if (now - _lastRestartUtc < TimeSpan.FromSeconds(10))
        {
            return;
        }
        _lastRestartUtc = now;
        var had = _source != null;
        StopSourceLocked("重建");
        RefreshDeviceListsLocked();
        if (TryEnsureSourceLocked())
        {
            _silent = false;
            _lastDataUtc = DateTime.UtcNow;
            if (had)
            {
                EndpointsChanged?.Invoke();
            }
        }
    }

    public void Dispose()
    {
        _watchdog.Dispose();
        _devices.Changed -= OnDevicesChanged;
        lock (_lock)
        {
            StopAllLocked("引擎销毁");
            _errors.Clear();
        }
    }
}
