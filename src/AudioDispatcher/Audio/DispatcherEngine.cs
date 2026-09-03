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
    private string _candidatesSig = "";
    private string _sourceSig = "";
    // 音频线程(捕获回调/渲染)经此快照访问目标列表,避免与 UI 持锁(设备启停/枚举)互等
    private volatile TargetOutput[] _targetSnapshot = System.Array.Empty<TargetOutput>();

    private volatile bool _running;
    private volatile bool _silent;
    private int _bufferMs;
    private long _lastWatchFrames;
    private DateTime _lastDataUtc = DateTime.UtcNow;
    private DateTime _lastContentUtc = DateTime.UtcNow;
    private DateTime _lastRestartUtc = DateTime.MinValue;
    private DateTime _lastTargetScanUtc = DateTime.UtcNow;
    // 目标启动失败/中断的退避重试(熄屏恢复、设备未就绪等场景)
    private readonly Dictionary<string, DateTime> _retryAfter = new();
    private readonly Dictionary<string, int> _retryCount = new();
    private DateTime _lastVolumeWarnUtc = DateTime.MinValue;
    private DateTime _lastLockStallWarnUtc = DateTime.MinValue;
    private readonly DateTime _engineBornUtc = DateTime.UtcNow;
    // 设备列表后台刷新(锁内零 COM):通知只调度,枚举在独立线程执行,挂起可超时自愈
    private volatile bool _refreshRunning;
    private DateTime _refreshStartedUtc = DateTime.MinValue;
    private DateTime _lastListRefreshUtc = DateTime.MinValue;
    private int _refreshStuckCount;
    private DateTime _refreshPausedUntilUtc = DateTime.MinValue;

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
            if (_source != null && _source.DeviceId == deviceId)
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
        if (t != null && t.TryGetEndpointVolume(out var vol))
        {
            try
            {
                action(vol);
                return;
            }
            catch (Exception ex)
            {
                WarnVolumeThrottled($"运行目标 {deviceId} 端点音量操作失败: {ex.Message}");
                return;
            }
        }

        // 目标未在分发(或运行目标音量组件失效):瞬时打开设备执行(经 COM 串行锁)
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
            WarnVolumeThrottled($"设置设备 {deviceId} 端点音量失败: {ex.Message}");
        }
        finally
        {
            dev.Dispose();
        }
    }

    private void WarnVolumeThrottled(string message)
    {
        var now = DateTime.UtcNow;
        if (now - _lastVolumeWarnUtc < TimeSpan.FromSeconds(10))
        {
            return;
        }
        _lastVolumeWarnUtc = now;
        AppLog.Warn(message);
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
        // 用缓存候选(后台刷新填充),锁内不做 COM 枚举
        var candidates = _sourceCandidates;
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
        if (DateTime.UtcNow < _retryAfter.GetValueOrDefault(deviceId))
        {
            return; // 退避期内
        }
        var device = _devices.OpenRenderDevice(deviceId);
        if (device == null)
        {
            BackoffLocked(deviceId);
            _errors[deviceId] = "设备不存在";
            return;
        }
        try
        {
            var target = new TargetOutput(device, _source!.SampleRate);
            target.PlaybackStopped += ex => OnTargetPlaybackStopped(target, ex);
            target.Start(_bufferMs);
            _targets.Add(target);
            _targetById[deviceId] = target;
            _targetSnapshot = _targets.ToArray(); // 持锁刷新音频线程快照
            ClearBackoffLocked(deviceId);
            _errors.Remove(deviceId);
            AppLog.Info($"目标启动: {target.Name}");
        }
        catch (Exception ex)
        {
            BackoffLocked(deviceId);
            _errors[deviceId] = ex.Message;
            device.Dispose();
            AppLog.Warn($"目标启动失败(将退避重试): {deviceId}: {ex.Message}");
            TargetError?.Invoke(deviceId, ex.Message);
        }
    }

    private void BackoffLocked(string deviceId)
    {
        var n = _retryCount.GetValueOrDefault(deviceId) + 1;
        _retryCount[deviceId] = n;
        var delaySec = Math.Min(2 * n, 10); // 2s 起,指数到 10s 封顶(熄屏恢复别等太久)
        _retryAfter[deviceId] = DateTime.UtcNow.AddSeconds(delaySec);
    }

    private void ClearBackoffLocked(string deviceId)
    {
        _retryAfter.Remove(deviceId);
        _retryCount.Remove(deviceId);
    }

    /// <summary>渲染流意外中断(设备会话失效,如 HDMI 熄屏/驱动重置)→ 停掉该路并按退避自动重启。</summary>
    private void OnTargetPlaybackStopped(TargetOutput target, Exception? ex)
    {
        lock (_lock)
        {
            if (!_targetById.ContainsKey(target.DeviceId))
            {
                return; // 引擎主动停止(已移除),忽略 NAudio 停止事件
            }
            StopTargetLocked(target.DeviceId, "播放中断");
            BackoffLocked(target.DeviceId);
            var msg = ex != null ? $"播放中断: {ex.Message}" : "播放中断";
            _errors[target.DeviceId] = msg;
            AppLog.Warn($"目标播放中断,退避重试: {target.Name}: {ex?.Message}");
            TargetError?.Invoke(target.DeviceId, "设备会话中断,正在自动恢复…");
        }
    }

    /// <summary>目标周期巡检(2s,锁内纯内存,基于缓存候选):运行中目标端点离线则停;
    /// 配置启用、端点在线但未运行(启动失败/中断/熄屏恢复)的按退避重启。
    /// 不依赖设备通知事件,覆盖通知缺失或恢复时机未就绪的场景。</summary>
    private void TargetMaintenanceLocked()
    {
        if (!_running || _source == null)
        {
            return;
        }
        var now = DateTime.UtcNow;
        var present = _candidates.Where(c => c.Present).Select(c => c.Id).ToHashSet();

        // 1) 端点已不在 Active → 停(引擎显示目标会话状态与设备实际一致)
        foreach (var t in _targets.ToArray())
        {
            if (!present.Contains(t.DeviceId))
            {
                StopTargetLocked(t.DeviceId, "端点离线(巡检)");
                TargetError?.Invoke(t.DeviceId, "设备已断开,自动恢复中…");
            }
        }

        // 2) 配置启用 + 端点在线 + 未运行 + 过了退避期 → 启动
        foreach (var cfg in _settings.Targets)
        {
            if (!cfg.Enabled || _targetById.ContainsKey(cfg.DeviceId) || !present.Contains(cfg.DeviceId))
            {
                continue;
            }
            if (now < _retryAfter.GetValueOrDefault(cfg.DeviceId))
            {
                continue;
            }
            TryStartTargetLocked(cfg.DeviceId);
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

    /// <summary>设备通知(任意 COM 线程):只做轻量调度,枚举在后台线程执行,避免锁内 COM 挂起拖垮引擎。</summary>
    private void OnDevicesChanged() => ScheduleListRefresh(immediate: true);

    /// <summary>调度一次后台设备枚举(单飞;运行中挂起超时后放弃并允许下轮重试)。</summary>
    private void ScheduleListRefresh(bool immediate)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (now < _refreshPausedUntilUtc)
            {
                return; // 连续挂起后的暂停期
            }
            if (_refreshRunning)
            {
                // 已在跑:若超过 8s 视为挂起,放弃等待(卡住的线程自生自灭),下轮重新调度
                if ((now - _refreshStartedUtc).TotalSeconds > 8)
                {
                    _refreshStuckCount++;
                    AppLog.Warn($"设备列表刷新疑似挂起(第 {_refreshStuckCount} 次),放弃本轮");
                    _refreshRunning = false;
                    if (_refreshStuckCount >= 3)
                    {
                        _refreshPausedUntilUtc = now.AddSeconds(60);
                        _refreshStuckCount = 0;
                        AppLog.Warn("设备列表刷新连续挂起,暂停刷新 60s");
                    }
                }
                else
                {
                    return;
                }
            }
            if (!immediate && (now - _lastListRefreshUtc).TotalSeconds < 5)
            {
                return; // 周期刷新节流(通知触发的 immediate 不受限)
            }
            _refreshRunning = true;
            _refreshStartedUtc = DateTime.UtcNow;
            _lastListRefreshUtc = DateTime.UtcNow;
            _ = Task.Run(RefreshListsInBackground);
        }
    }

    /// <summary>后台线程:枚举设备(COM,可能慢/挂起)→ 持锁应用结果与状态机。</summary>
    private void RefreshListsInBackground()
    {
        try
        {
            var sourceCandidates = _devices.SourceCandidates();
            var renderCandidates = _devices.RenderCandidates(_settings.BlockedDeviceNames);
            lock (_lock)
            {
                _refreshRunning = false;
                _sourceCandidates = sourceCandidates;
                _candidates = renderCandidates;
                var candidatesSig = string.Join('|', _candidates.Select(c => $"{c.Id}:{c.Name}:{c.Format}:{c.Present}"));
                var sourcesSig = string.Join('|', _sourceCandidates.Select(c => $"{c.Id}:{c.Name}"));
                var changed = candidatesSig != _candidatesSig || sourcesSig != _sourceSig;
                _candidatesSig = candidatesSig;
                _sourceSig = sourcesSig;

                SourceMaintenanceLocked();
                StopOfflineTargetsLocked();

                if (changed || _refreshStuckCount > 0)
                {
                    _refreshStuckCount = 0;
                    SourceCandidatesChanged?.Invoke();
                    EndpointsChanged?.Invoke();
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"设备列表后台刷新失败(下轮重试): {ex}");
            lock (_lock)
            {
                _refreshRunning = false;
            }
        }
    }

    /// <summary>源设备消失/恢复处理(基于缓存候选,调用方持锁)。</summary>
    private void SourceMaintenanceLocked()
    {
        if (_source != null)
        {
            var stillThere = _sourceCandidates.Any(c => c.Id == _source.DeviceId);
            if (!stillThere)
            {
                var lostName = _source.DeviceName;
                StopSourceLocked("设备消失");
                SourceLost?.Invoke($"源设备 {lostName} 已断开");
            }
        }
        else if (_sourceCandidates.Count > 0 && _running)
        {
            TryEnsureSourceLocked();
        }
    }

    /// <summary>缓存候选中的目标端点已离线 → 停路(恢复由巡检负责,调用方持锁)。</summary>
    private void StopOfflineTargetsLocked()
    {
        foreach (var id in _targets.Select(t => t.DeviceId).ToArray())
        {
            var c = _candidates.FirstOrDefault(x => x.Id == id);
            if (c == null || !c.Present)
            {
                StopTargetLocked(id, "设备消失");
                TargetError?.Invoke(id, "设备已断开,自动恢复中…");
            }
        }
    }

    private void RefreshDeviceLists()
    {
        // 启动时同步枚举一次(此时设备列表稳定,无风暴);此后全部走后台刷新
        try
        {
            _sourceCandidates = _devices.SourceCandidates();
            _candidates = _devices.RenderCandidates(_settings.BlockedDeviceNames);
            _candidatesSig = string.Join('|', _candidates.Select(c => $"{c.Id}:{c.Name}:{c.Format}:{c.Present}"));
            _sourceSig = string.Join('|', _sourceCandidates.Select(c => $"{c.Id}:{c.Name}"));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"初始设备枚举失败: {ex.Message}");
        }
    }

    private void OnTick()
    {
        try
        {
            // 锁争用探测:若锁被长期持有(某调用卡在锁内)则记录并跳过本轮,不阻塞看门狗。
            // 启动 8s 内豁免(自动开始分发时锁内做设备初始化,属正常长锁)。
            var engineAge = DateTime.UtcNow - _engineBornUtc;
            bool entered;
            if (engineAge.TotalSeconds > 8)
            {
                entered = Monitor.TryEnter(_lock, TimeSpan.FromMilliseconds(800));
            }
            else
            {
                Monitor.Enter(_lock);
                entered = true;
            }
            if (!entered)
            {
                var now0 = DateTime.UtcNow;
                if (now0 - _lastLockStallWarnUtc > TimeSpan.FromSeconds(10))
                {
                    _lastLockStallWarnUtc = now0;
                    AppLog.Warn("引擎锁争用:持锁调用疑似卡住(>800ms),本轮巡检跳过");
                }
                return;
            }
            try
            {
                OnTickLocked();
            }
            finally
            {
                Monitor.Exit(_lock);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "看门狗异常");
        }
    }

    private void OnTickLocked()
    {
        if (!_running)
        {
            return;
        }
        var now = DateTime.UtcNow;
        // 周期保底:通知之外的候选刷新(熄屏恢复不依赖通知)
        ScheduleListRefresh(immediate: false);

        if (_source == null || !_source.IsRunning)
        {
            // 源失效:尝试重建(限频 10s)
            TryRestartSourceLocked();
            return;
        }

        // 0) 目标周期巡检(熄屏/断线恢复不依赖通知事件)
        if ((now - _lastTargetScanUtc).TotalSeconds >= 2)
        {
            _lastTargetScanUtc = now;
            TargetMaintenanceLocked();
        }

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
        ScheduleListRefresh(immediate: true); // 刷新候选后由 SourceMaintenance 自动恢复源
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
