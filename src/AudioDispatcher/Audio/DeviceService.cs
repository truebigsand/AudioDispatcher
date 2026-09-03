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
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly NotificationSink _sink = new();
    private readonly object _sync = new();
    private readonly Timer _throttle;
    private bool _pending;

    public event Action? Changed;

    public DeviceService()
    {
        _sink.Changed += OnSinkChanged;
        _enumerator.RegisterEndpointNotificationCallback(_sink);
        _throttle = new Timer(_ => RaiseChanged(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>VB-Audio 类虚拟声卡的捕获(Output)端点候选,即"源"候选。</summary>
    public List<SourceInfo> SourceCandidates()
    {
        var result = new List<SourceInfo>();
        var col = _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
        foreach (var d in col)
        {
            var name = d.FriendlyName;
            if (name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("CABLE", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new SourceInfo(d.ID, name));
            }
        }
        return result;
    }

    /// <summary>打开源捕获设备(调用方负责 Dispose)。</summary>
    public MMDevice? OpenSourceDevice(string deviceId)
    {
        try
        {
            return _enumerator.GetDevice(deviceId);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"打开源设备失败 {deviceId}: {ex.Message}");
            return null;
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
        var active = new HashSet<string>();
        foreach (var d in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            active.Add(d.ID);
        }

        var result = new List<RenderInfo>();
        var all = _enumerator.EnumerateAudioEndPoints(
            DataFlow.Render, DeviceState.Active | DeviceState.Unplugged | DeviceState.NotPresent);
        foreach (var d in all)
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
        return result;
    }

    /// <summary>打开目标渲染设备(调用方负责 Dispose)。</summary>
    public MMDevice? OpenRenderDevice(string deviceId)
    {
        try
        {
            return _enumerator.GetDevice(deviceId);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"打开目标设备失败 {deviceId}: {ex.Message}");
            return null;
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
            _throttle.Change(300, Timeout.Infinite);
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
            _enumerator.UnregisterEndpointNotificationCallback(_sink);
        }
        catch
        {
            // 忽略注销失败
        }
        _enumerator.Dispose();
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
