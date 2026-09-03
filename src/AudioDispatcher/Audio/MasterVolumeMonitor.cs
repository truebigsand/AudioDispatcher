using System;
using NAudio.CoreAudioApi;

namespace AudioDispatcher.Audio;

/// <summary>
/// 系统主音量监视器:跟踪"默认多媒体渲染端点"(即用户任务栏音量条控制的设备,
/// 在分发架构下是 CABLE Input)的端点音量与静音,通过 Changed 事件广播。
/// VB-Cable 驱动对输入端音量直通(不衰减信号),因此分发器需要自行把
/// 主音量作为软件增益应用,任务栏音量条与静音键才能真正控制最终响度。
/// Refresh() 须在后台线程调用(COM);同设备重复调用只做一次订阅。
/// </summary>
public sealed class MasterVolumeMonitor : IDisposable
{
    private readonly object _sync = new();
    private MMDevice? _device;
    private AudioEndpointVolume? _volume;

    /// <summary>主音量(0..1)与静音状态;在 COM 回调线程触发,订阅方须快速返回。</summary>
    public event Action<float, bool>? Changed;

    /// <summary>重新解析默认多媒体端点;设备未变化时保持原订阅。由后台刷新周期调用。</summary>
    public void Refresh()
    {
        try
        {
            MMDevice dev;
            using (var en = new MMDeviceEnumerator())
            {
                dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }

            lock (_sync)
            {
                if (_device != null)
                {
                    try
                    {
                        if (_device.ID == dev.ID)
                        {
                            dev.Dispose();
                            return; // 默认端点未变
                        }
                    }
                    catch (Exception)
                    {
                        // 旧设备引用已失效,按设备变化处理
                    }
                }
                SwapLocked(dev);
            }
            RaiseCurrent();
        }
        catch (Exception)
        {
            // 无默认端点等异常:忽略,下次刷新再试
        }
    }

    /// <summary>立即读取当前主音量并广播(供启动/设备切换后初始化)。调用方持 _sync 之外任意线程。</summary>
    public void ReadCurrent()
    {
        AudioEndpointVolume? vol = null;
        lock (_sync)
        {
            vol = _volume;
        }
        if (vol != null)
        {
            try
            {
                Changed?.Invoke(vol.MasterVolumeLevelScalar, vol.Mute);
            }
            catch (Exception)
            {
                // 忽略订阅方异常
            }
        }
    }

    /// <summary>换绑默认端点(或传 null 解除绑定)。调用方须持 _sync。</summary>
    private void SwapLocked(MMDevice? newDevice)
    {
        var oldVolume = _volume;
        var oldDevice = _device;
        _volume = null;
        _device = null;
        if (oldVolume != null)
        {
            try { oldVolume.OnVolumeNotification -= OnVolumeNotification; }
            catch { }
        }
        if (oldDevice != null)
        {
            try { oldDevice.Dispose(); }
            catch { }
        }
        if (newDevice != null)
        {
            _device = newDevice;
            _volume = newDevice.AudioEndpointVolume;
            _volume.OnVolumeNotification += OnVolumeNotification;
        }
    }

    private void OnVolumeNotification(AudioVolumeNotificationData data)
    {
        try
        {
            Changed?.Invoke(data.MasterVolume, data.Muted);
        }
        catch (Exception)
        {
            // 忽略订阅方异常
        }
    }

    private void RaiseCurrent()
    {
        AudioEndpointVolume? vol = null;
        lock (_sync)
        {
            vol = _volume;
        }
        if (vol != null)
        {
            try
            {
                Changed?.Invoke(vol.MasterVolumeLevelScalar, vol.Mute);
            }
            catch (Exception)
            {
                // 忽略订阅方异常
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            SwapLocked(null);
        }
    }

}
