using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AudioDispatcher.Logging;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AudioDispatcher.Audio;

public sealed record SourceInfo(string Id, string Name);

public sealed record RenderInfo(string Id, string Name, string Format, bool Present);

/// <summary>
/// 音频端点枚举与热插拔通知(带 300ms 节流)。VB-Audio 虚拟线的 render 端(Input)按防环规则排除。
/// </summary>
public sealed class DeviceService : IDisposable
{
    // 注意:MMDeviceEnumerator 在创建线程(UI/STA)之外使用会不稳定(E_NOINTERFACE),
    // 因此枚举/GetDevice 一律用线程内新建的局部实例;此长生命周期实例仅供通知注册。
    private readonly MMDeviceEnumerator _notificationEnumerator = new();
    private readonly NotificationSink _sink = new();
    private readonly object _sync = new();
    // NAudio 的 MMDeviceEnumerator 包装非线程安全:并发枚举/GetDevice/Dispose 会损坏
    // RCW(症状:偶发 E_NOINTERFACE)。所有公开 COM 方法串行化。
    private readonly object _comLock = new();
    private readonly Timer _throttle;
    private bool _pending;

    public event Action? Changed;

    public DeviceService()
    {
        _sink.Changed += OnSinkChanged;
        _notificationEnumerator.RegisterEndpointNotificationCallback(_sink);
        _throttle = new Timer(_ => RaiseChanged(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>VB-Audio 类虚拟声卡的捕获(Output)端点候选,即"源"候选。</summary>
    public List<SourceInfo> SourceCandidates()
    {
        lock (_comLock)
        {
            return SourceCandidatesCore();
        }
    }

    private List<SourceInfo> SourceCandidatesCore()
    {
        var result = new List<SourceInfo>();
        using var en = new MMDeviceEnumerator();
        var col = en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
        using var e = col.GetEnumerator();
        while (true)
        {
            try
            {
                if (!e.MoveNext())
                {
                    break;
                }
            }
            catch (Exception)
            {
                break; // 枚举器级异常(设备剧变),放弃剩余
            }
            var d = e.Current;
            try
            {
                var name = d.FriendlyName;
                if (name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("CABLE", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(new SourceInfo(d.ID, name));
                }
            }
            catch (Exception)
            {
                // 设备状态变化竞态,跳过该项
            }
        }
        return result;
    }

    /// <summary>打开源捕获设备(调用方负责 Dispose)。</summary>
    public MMDevice? OpenSourceDevice(string deviceId)
    {
        lock (_comLock)
        {
            try
            {
                using var en = new MMDeviceEnumerator();
                return en.GetDevice(deviceId);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"打开源设备失败 {deviceId}: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// 渲染候选(目标设备列表)。Present=false 表示设备存在但不在 Active 状态(已拔/休眠)。
    /// 防环:排除全部 VB-Audio 虚拟线 render 端(新版驱动命名为
    /// "扬声器 (VB-Audio Virtual Cable)" / "CABLE In 16 Ch",不能按 Input 后缀匹配)
    /// + blockedNames 匹配项。
    /// </summary>
    public List<RenderInfo> RenderCandidates(IReadOnlyCollection<string> blockedNames)
    {
        lock (_comLock)
        {
            var active = ActiveRenderIdsCore();

            var result = new List<RenderInfo>();
            using var en = new MMDeviceEnumerator();
            var all = en.EnumerateAudioEndPoints(
                DataFlow.Render, DeviceState.Active | DeviceState.Unplugged | DeviceState.NotPresent);
            using var e = all.GetEnumerator();
            while (true)
            {
                MMDevice d;
                try
                {
                    if (!e.MoveNext())
                    {
                        break;
                    }
                    d = e.Current;
                }
                catch (Exception)
                {
                    break; // 枚举器级异常(设备剧变),放弃剩余
                }
                try
                {
                    var name = d.FriendlyName;
                    if (IsVbVirtualRender(name) ||
                        blockedNames.Any(b => name.Contains(b, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }
                    var present = active.Contains(d.ID);
                    result.Add(new RenderInfo(d.ID, name, DescribeFormat(d), present));
                }
                catch (Exception)
                {
                    // 设备状态变化竞态,跳过该项
                }
            }
            return result;
        }
    }

    /// <summary>当前处于 Active 状态的渲染端点 ID 集合(看门狗巡检用,轻量,不含格式描述)。</summary>
    public HashSet<string> GetActiveRenderIds()
    {
        lock (_comLock)
        {
            return ActiveRenderIdsCore();
        }
    }

    private HashSet<string> ActiveRenderIdsCore()
    {
        var active = new HashSet<string>();
        using var en = new MMDeviceEnumerator();
        var col = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        using var e = col.GetEnumerator();
        while (true)
        {
            MMDevice d;
            try
            {
                if (!e.MoveNext())
                {
                    break;
                }
                d = e.Current;
            }
            catch (Exception)
            {
                break; // 枚举器级异常,放弃剩余
            }
            try
            {
                active.Add(d.ID);
            }
            catch (Exception)
            {
                // 设备状态变化竞态,跳过该项
            }
        }
        return active;
    }

    /// <summary>打开目标渲染设备(调用方负责 Dispose)。</summary>
    public MMDevice? OpenRenderDevice(string deviceId)
    {
        lock (_comLock)
        {
            try
            {
                using var en = new MMDeviceEnumerator();
                return en.GetDevice(deviceId);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"打开目标设备失败 {deviceId}: {ex.Message}");
                return null;
            }
        }
    }

    private static bool IsVbVirtualRender(string name) =>
        name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase);

    private static string DescribeFormat(MMDevice d)
    {
        try
        {
            var f = d.AudioClient.MixFormat;
            return $"{f.SampleRate / 1000.0:0.#} kHz / {f.BitsPerSample} bit / {f.Channels} 声道";
        }
        catch
        {
            return "格式未知";
        }
    }

    private void OnSinkChanged()
    {
        lock (_sync)
        {
            if (_pending)
            {
                return;
            }
            _pending = true;
            // 800ms 合并窗口:熄屏重开瞬间端点反复 Active/Unplugged 的通知风暴只触发一次处理
            _throttle.Change(800, Timeout.Infinite);
        }
    }

    private void RaiseChanged()
    {
        lock (_sync)
        {
            _pending = false;
        }
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "设备变更通知处理失败");
        }
    }

    public void Dispose()
    {
        _throttle.Dispose();
        try
        {
            _notificationEnumerator.UnregisterEndpointNotificationCallback(_sink);
        }
        catch
        {
            // 忽略注销失败
        }
        _notificationEnumerator.Dispose();
    }

    private sealed class NotificationSink : IMMNotificationClient
    {
        public event Action? Changed;

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => Changed?.Invoke();

        public void OnDeviceAdded(string pwstrDeviceId) => Changed?.Invoke();

        public void OnDeviceRemoved(string deviceId) => Changed?.Invoke();

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => Changed?.Invoke();

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
