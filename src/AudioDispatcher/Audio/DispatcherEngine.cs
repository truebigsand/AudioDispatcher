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
    private HashSet<string> _lastPresentIds = new(); // 上次刷新时在线端点(检测"恢复"转换)
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
    private readonly Dictionary<string, DateTime> _stoppedAtUtc = new(); // 停路时间(振荡抑制)
    private readonly HashSet<string> _startingIds = new(); // 已排队启动(防巡检/StartAll 重复入队)
    private DateTime _lastVolumeWarnUtc = DateTime.MinValue;
    private DateTime _lastLockStallWarnUtc = DateTime.MinValue;
    private readonly DateTime _engineBornUtc = DateTime.UtcNow;
    // ---- 后台音频操作执行器:引擎锁内永不执行音频 COM ----
    // 设备唤醒/失效瞬间 WasapiOut/WasapiCapture 的 Start/Stop/Dispose 可能永久挂起,
    // 若发生在锁内会拖死整个引擎与 UI;一律排队到此串行队列执行。
    private readonly object _execLock = new();
    private Task _execTail = Task.CompletedTask;
    // 系统主音量(默认端点=CABLE Input 的端点音量,VB-Cable 驱动直通不衰减,
    // 由分发器以软件增益实现"任务栏音量真正控制最终响度")
    private readonly MasterVolumeMonitor _masterMonitor = new();
    private volatile float _masterVol = 1f;
    private volatile bool _masterMuted;

    private void EnqueueAudioOp(Action op)
    {
        lock (_execLock)
        {
            _execTail = _execTail.ContinueWith(
                _ =>
                {
                    try
                    {
                        op();
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warn($"后台音频操作异常: {ex.Message}");
                    }
                },
                TaskScheduler.Default);
        }
    }
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
        _masterMonitor.Changed += OnMasterVolumeChanged;
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

    /// <summary>确保源在跑(锁内纯检查+调度;真实 COM 启动在后台执行器完成)。</summary>
    private bool TryEnsureSourceLocked()
    {
        if (_source != null)
        {
            return true;
        }
        var id = PickSourceIdLocked();
        if (string.IsNullOrEmpty(id))
        {
            return false;
        }
        EnqueueAudioOp(() => DoEnsureSource(id));
        return true; // 异步就绪(通常数百 ms 内)
    }

    private string? PickSourceIdLocked()
    {
        // 用缓存候选(后台刷新填充),锁内不做 COM 枚举
        var candidates = _sourceCandidates;
        var id = _settings.SourceDeviceId;
        if (string.IsNullOrEmpty(id) || !candidates.Any(c => c.Id == id))
        {
            id = candidates.Count == 1 ? candidates[0].Id :
                 candidates.FirstOrDefault(c => c.Id == _settings.SourceDeviceId)?.Id ?? "";
        }
        return string.IsNullOrEmpty(id) ? null : id;
    }

    /// <summary>后台执行器:打开并启动源捕获(COM,可能挂起,只影响执行器队列)。</summary>
    private void DoEnsureSource(string deviceId)
    {
        SourceCapture? source = null;
        try
        {
            var device = _devices.OpenSourceDevice(deviceId);
            if (device == null)
            {
                AppLog.Warn("源设备打开失败(后台),将按看门狗节奏重试");
                return;
            }
            source = new SourceCapture(device);
            source.SamplesReady += OnSamplesReady;
            source.Start();
            lock (_lock)
            {
                if (_source != null)
                {
                    // 竞态:已有源就绪,释放本次创建的
                    var s = source;
                    source = null;
                    EnqueueAudioOp(() =>
                    {
                        try { s.Dispose(); }
                        catch { }
                    });
                    return;
                }
                _source = source;
                source = null;
                if (_running)
                {
                    _lastDataUtc = DateTime.UtcNow;
                    _lastContentUtc = DateTime.UtcNow;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"源捕获启动失败(后台): {ex.Message}");
            if (source != null)
            {
                try { source.Dispose(); }
                catch { }
            }
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

    /// <summary>计划启动目标(锁内纯检查+调度;真实 COM 启动在后台执行器)。</summary>
    private void TryStartTargetLocked(string deviceId)
    {
        if (_targetById.ContainsKey(deviceId) || _startingIds.Contains(deviceId))
        {
            return;
        }
        if (DateTime.UtcNow < _retryAfter.GetValueOrDefault(deviceId))
        {
            return; // 退避期内
        }
        if (_source == null)
        {
            return;
        }
        _startingIds.Add(deviceId);
        var sourceRate = _source.SampleRate;
        var bufferMs = _bufferMs;
        EnqueueAudioOp(() => DoStartTarget(deviceId, sourceRate, bufferMs));
    }

    /// <summary>后台执行器:打开并启动目标渲染流(COM,可能挂起,只影响执行器队列)。</summary>
    private void DoStartTarget(string deviceId, int sourceRate, int bufferMs)
    {
        MMDevice? device = null;
        TargetOutput? target = null;
        try
        {
            device = _devices.OpenRenderDevice(deviceId);
            if (device == null)
            {
                lock (_lock)
                {
                    BackoffLocked(deviceId);
                    _errors[deviceId] = "设备不存在";
                }
                return;
            }
            target = new TargetOutput(device, sourceRate);
            var startedId = target.DeviceId;
            Action<Exception?> handler = null!;
            handler = ex => OnTargetPlaybackStopped(startedId, ex);
            target.PlaybackStopped += handler;
            target.Start(bufferMs);
            lock (_lock)
            {
                if (!_running || _targetById.ContainsKey(deviceId))
                {
                    // 引擎已停止或该路已被其它路径启动:释放本次创建的流(退订,避免误触发中断回调)
                    var t = target;
                    target = null;
                    t.PlaybackStopped -= handler;
                    EnqueueAudioOp(() =>
                    {
                        try { t.Dispose(); }
                        catch { }
                    });
                    return;
                }
                _targets.Add(target);
                _targetById[deviceId] = target;
                _targetSnapshot = _targets.ToArray(); // 持锁刷新音频线程快照
                target.MasterGain = _masterMuted || _masterVol <= 0f ? 0f : _masterVol;
                _startingIds.Remove(deviceId);
                ClearBackoffLocked(deviceId);
                _errors.Remove(deviceId);
                AppLog.Info($"目标启动: {target.Name}");
                target = null;
                device = null;
            }
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _startingIds.Remove(deviceId);
                BackoffLocked(deviceId);
                _errors[deviceId] = ex.Message;
            }
            AppLog.Warn($"目标启动失败(将退避重试): {deviceId}: {ex.Message}");
            TargetError?.Invoke(deviceId, ex.Message);
        }
        finally
        {
            lock (_lock)
            {
                _startingIds.Remove(deviceId);
            }
            if (target != null)
            {
                try { target.Dispose(); }
                catch { }
            }
            else if (device != null)
            {
                try { device.Dispose(); }
                catch { }
            }
        }
    }

    private void BackoffLocked(string deviceId)
    {
        var n = _retryCount.GetValueOrDefault(deviceId) + 1;
        _retryCount[deviceId] = n;
        // 指数退避:2/4/8/16/32/60s 封顶。设备持续不可用(启动失败/反复中断)时
        // 把尝试频率压到每分钟一次,避免空转占用 CPU/COM
        var delaySec = n switch
        {
            1 => 2,
            2 => 4,
            3 => 8,
            4 => 16,
            5 => 32,
            _ => 60,
        };
        _retryAfter[deviceId] = DateTime.UtcNow.AddSeconds(delaySec);
    }

    private void ClearBackoffLocked(string deviceId)
    {
        _retryAfter.Remove(deviceId);
        _retryCount.Remove(deviceId);
    }

    /// <summary>渲染流意外中断(设备会话失效,如 HDMI 熄屏/驱动重置)→ 停掉该路并按退避自动重启。
    /// 由 NAudio 播放线程回调,须快速返回且不得抛异常。</summary>
    private void OnTargetPlaybackStopped(string deviceId, Exception? ex)
    {
        try
        {
            lock (_lock)
            {
                if (!_targetById.ContainsKey(deviceId))
                {
                    return; // 引擎主动停止(已移除),忽略 NAudio 停止事件
                }
                StopTargetLocked(deviceId, "播放中断");
                BackoffLocked(deviceId);
                var msg = ex != null ? $"播放中断: {ex.Message}" : "播放中断";
                _errors[deviceId] = msg;
                AppLog.Warn($"目标播放中断,退避重试: {deviceId}: {ex?.Message}");
                TargetError?.Invoke(deviceId, "设备会话中断,正在自动恢复…");
            }
        }
        catch (Exception callbackEx)
        {
            AppLog.Error(callbackEx, "播放中断回调处理异常");
        }
    }

    /// <summary>主音量变化(COM 通知线程):写 volatile 并应用到全部运行目标。</summary>
    private void OnMasterVolumeChanged(float volume, bool muted)
    {
        _masterVol = volume;
        _masterMuted = muted;
        var gain = muted || volume <= 0f ? 0f : volume;
        foreach (var t in _targetSnapshot)
        {
            t.MasterGain = gain;
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
            _stoppedAtUtc[deviceId] = DateTime.UtcNow;
            AppLog.Info($"目标停止({reason}): {deviceId}");
            // Dispose(COM)在后台执行器释放,锁内不碰音频
            EnqueueAudioOp(() =>
            {
                try { t.Dispose(); }
                catch (Exception ex) { AppLog.Warn($"目标 {deviceId} 释放异常: {ex.Message}"); }
            });
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
            // Dispose(COM)在后台执行器释放,锁内不碰音频
            EnqueueAudioOp(() =>
            {
                try { s.Dispose(); }
                catch (Exception ex) { AppLog.Warn($"源释放异常({reason}): {ex.Message}"); }
            });
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
            try
            {
                _masterMonitor.Refresh(); // 跟随默认端点变化(COM,后台线程)
            }
            catch (Exception ex)
            {
                AppLog.Warn($"主音量监视器刷新失败: {ex.Message}");
            }
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
                // 端点从离线恢复在线:若该设备近期没有反复启停(10s 冷却),清除退避并
                // 立即重试一次;振荡设备(状态秒级横跳)不触发快路径,由退避+巡检压制
                var nowPresent = _candidates.Where(c => c.Present).Select(c => c.Id).ToHashSet();
                if (_lastPresentIds.Count > 0)
                {
                    var now = DateTime.UtcNow;
                    foreach (var id in nowPresent)
                    {
                        if (_lastPresentIds.Contains(id))
                        {
                            continue;
                        }
                        var cfg = _settings.Targets.FirstOrDefault(t => t.DeviceId == id);
                        if (cfg is { Enabled: true } && !_targetById.ContainsKey(id) &&
                            now - _stoppedAtUtc.GetValueOrDefault(id) > TimeSpan.FromSeconds(10))
                        {
                            ClearBackoffLocked(id);
                            TryStartTargetLocked(id);
                        }
                    }
                }
                _lastPresentIds = nowPresent;

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
        _masterMonitor.Changed -= OnMasterVolumeChanged;
        _masterMonitor.Dispose();
        List<IDisposable> toDispose;
        lock (_lock)
        {
            toDispose = _targets.Cast<IDisposable>().ToList();
            _targets.Clear();
            _targetById.Clear();
            _targetSnapshot = System.Array.Empty<TargetOutput>();
            if (_source != null)
            {
                _source.SamplesReady -= OnSamplesReady;
                toDispose.Add(_source);
                _source = null;
            }
            _errors.Clear();
            _silent = false;
        }
        // 释放放后台执行器,不阻塞退出:设备释放(COM)可能挂起,进程退出时
        // 由操作系统回收音频会话,绝不让退出流程卡住
        if (toDispose.Count > 0)
        {
            EnqueueAudioOp(() =>
            {
                foreach (var d in toDispose)
                {
                    try { d.Dispose(); }
                    catch { }
                }
            });
        }
    }
}
