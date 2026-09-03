using System;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AudioDispatcher.Audio;

/// <summary>
/// 单个目标设备的完整渲染流:环形缓冲 + (重采样) + 位深/声道转换 + WasapiOut 事件驱动渲染。
/// 写端唯一:捕获线程 WriteSamples(经 _sync 锁,测试音写入也走同一锁);
/// 读端唯一:WasapiOut 事件回调线程(RingReadProvider.Read,含漂移补偿)。
/// </summary>
public sealed class TargetOutput : IDisposable
{
    private readonly MMDevice _device;
    private readonly string _deviceId;
    private readonly string _deviceName;
    private readonly int _sourceRate;
    private readonly WaveFormat _mixFormat;
    private readonly object _sync = new();
    private SampleRingBuffer _ring = null!;
    private WasapiOut? _out;
    private NAudio.CoreAudioApi.AudioEndpointVolume? _endpointVolume;

    // 音量/静音由设备端点主音量控制(应用滑块绑定系统音量),渲染端不再做增益。
    // MasterGain 例外:系统主音量(CABLE Input 端点音量,驱动直通无效)由分发器
    // 以软件增益实现,作用于全部目标(任务栏音量条/静音键由此真正生效)。
    internal volatile float MasterGain = 1f;
    // 以下字段由 UI 线程写、渲染线程读(volatile),或经 Interlocked 累计。
    internal volatile bool SilentMode;
    internal volatile bool CapturePaused;
    internal long OverrunFrames;
    internal long UnderrunFrames;
    internal float LevelRms;

    public TargetOutput(MMDevice device, int sourceRate)
    {
        _device = device;
        // 设备标识构造时缓存:熄屏/断连后 MMDevice COM 引用会失效(RCW 断链),
        // 运行期任何 COM 调用都会抛 E_NOINTERFACE,导致巡检/恢复逻辑炸掉。
        _deviceId = device.ID;
        _deviceName = device.FriendlyName;
        _sourceRate = sourceRate;
        _mixFormat = device.AudioClient.MixFormat;
    }

    public MMDevice Device => _device;
    public string DeviceId => _deviceId;
    public string Name => _deviceName;
    public WaveFormat OutputFormat => _mixFormat;
    public bool IsRunning => _out != null;

    /// <summary>渲染流非主动停止(设备会话中断/故障)时触发,由引擎决定重启。</summary>
    public event Action<Exception?>? PlaybackStopped;

    /// <summary>懒创建并缓存端点音量控制(避免每 500ms Activate);失败时自愈:下次访问重建。</summary>
    public bool TryGetEndpointVolume(out NAudio.CoreAudioApi.AudioEndpointVolume volume)
    {
        try
        {
            _endpointVolume ??= _device.AudioEndpointVolume;
            volume = _endpointVolume;
            return true;
        }
        catch (Exception)
        {
            _endpointVolume = null;
            volume = null!;
            return false;
        }
    }

    /// <summary>启动渲染流。失败抛出(设备被占用/失效),由引擎决定处置。</summary>
    public void Start(int bufferMs)
    {
        if (_out != null)
        {
            return;
        }
        var capFrames = Math.Max(64, bufferMs * _sourceRate / 1000);
        _ring = new SampleRingBuffer(capFrames, 2);
        var ringProvider = new RingReadProvider(this, _ring);
        ISampleProvider src = ringProvider;
        if (_mixFormat.SampleRate != _sourceRate)
        {
            src = new WdlResamplingSampleProvider(ringProvider, _mixFormat.SampleRate);
        }
        var outProvider = new FloatToDeviceProvider(src, _mixFormat);
        try
        {
            var latency = Math.Clamp(bufferMs, 20, 500);
            _out = new WasapiOut(_device, AudioClientShareMode.Shared, useEventSync: true, latency);
            _out.PlaybackStopped += OnPlaybackStopped;
            _out.Init(outProvider);
            _out.Play();
        }
        catch (Exception ex)
        {
            Stop();
            throw new InvalidOperationException($"设备 {Name} 渲染流启动失败: {ex.Message}", ex);
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        // 引擎主动停止时先从自身列表移除再 Dispose,事件在此处不再需要上报;
        // 这里只对仍处于运行状态的意外停止转发。
        PlaybackStopped?.Invoke(e.Exception);
    }

    public void Stop()
    {
        var o = _out;
        _out = null;
        if (o != null)
        {
            try
            {
                o.Stop();
            }
            catch
            {
                // 设备可能已断开
            }
            o.Dispose();
        }
    }

    /// <summary>捕获线程写入样本(2ch 交织 float32)。SilentMode/CapturePaused 时丢弃。</summary>
    public void WriteSamples(float[] data, int frames)
    {
        if (SilentMode || CapturePaused)
        {
            return;
        }
        lock (_sync)
        {
            _ring.Write(data, 0, frames);
        }
    }

    /// <summary>进入静默(源无数据):清空缓冲,读端输出静音且不计数。</summary>
    public void EnterSilentMode()
    {
        lock (_sync)
        {
            SilentMode = true;
            _ring.Clear();
        }
    }

    /// <summary>源恢复:清空旧数据后退出静默。</summary>
    public void ResumeFromSilence()
    {
        lock (_sync)
        {
            if (!SilentMode)
            {
                return;
            }
            _ring.Clear();
            SilentMode = false;
        }
    }

    /// <summary>对**本设备**播放 1kHz 短哔测试音(200ms,淡入淡出)。</summary>
    public void PlayTestTone()
    {
        var rate = _sourceRate;
        var totalFrames = rate * 200 / 1000;
        var tone = new float[totalFrames * 2];
        var step = 2.0 * Math.PI * 1000.0 / rate;
        var fadeFrames = rate * 5 / 1000;
        for (var i = 0; i < totalFrames; i++)
        {
            var env = 1.0;
            if (i < fadeFrames)
            {
                env = (double)i / fadeFrames;
            }
            else if (i > totalFrames - fadeFrames)
            {
                env = (double)(totalFrames - i) / fadeFrames;
            }
            var v = (float)(0.125 * env * Math.Sin(step * i));
            tone[i * 2] = v;
            tone[i * 2 + 1] = v;
        }

        var wasSilent = SilentMode;
        if (wasSilent)
        {
            SilentMode = false; // 静默态下读端不消费,先放行让测试音能播出
        }
        lock (_sync)
        {
            CapturePaused = true;
            _ring.Clear();
            var w = 0;
            while (w < totalFrames)
            {
                var n = _ring.Write(tone, w, totalFrames - w);
                if (n == 0)
                {
                    break; // 读端消费慢于写入,丢弃尾部
                }
                w += n;
            }
            CapturePaused = false;
        }

        if (wasSilent)
        {
            // 若期间捕获没有恢复(ring 仍空),播放完测试音后回到静默,避免欠载统计暴涨。
            _ = Task.Run(async () =>
            {
                await Task.Delay(500);
                lock (_sync)
                {
                    if (!SilentMode && !CapturePaused && _ring.AvailableFrames == 0)
                    {
                        SilentMode = true;
                        _ring.Clear();
                    }
                }
            });
        }
    }

    public void Dispose()
    {
        Stop();
        _device.Dispose();
    }

    /// <summary>读端:环形缓冲 → 输出。漂移补偿 + 增益 + RMS 电平都在渲染线程这里做。</summary>
    private sealed class RingReadProvider : ISampleProvider
    {
        private readonly TargetOutput _owner;
        private readonly SampleRingBuffer _ring;

        public RingReadProvider(TargetOutput owner, SampleRingBuffer ring)
        {
            _owner = owner;
            _ring = ring;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(owner._sourceRate, channels: 2);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            if (_owner.SilentMode)
            {
                Array.Clear(buffer, offset, count);
                return count;
            }

            var frames = count / 2;
            var cap = _ring.CapacityFrames;

            // 溢出补偿:水位高于 92% 容量时丢弃最旧帧至 85% 水位
            var avail = _ring.AvailableFrames;
            if (avail > cap * 0.92f)
            {
                var drop = avail - (int)(cap * 0.85f);
                if (drop > 0)
                {
                    _ring.SkipOldest(drop);
                    Interlocked.Add(ref _owner.OverrunFrames, drop);
                    avail -= drop;
                }
            }

            var got = _ring.Read(buffer, offset / 2, frames);
            if (got < frames)
            {
                // 欠载:补静音
                Array.Clear(buffer, offset + got * 2, (frames - got) * 2);
                Interlocked.Add(ref _owner.UnderrunFrames, frames - got);
            }

            // 系统主音量软件增益 + 电平(设备端点音量在硬件层,此处仅主增益)
            var master = _owner.MasterGain;
            var sum = 0.0;
            if (master != 1f)
            {
                for (var s = offset; s < offset + frames * 2; s++)
                {
                    var v = buffer[s] * master;
                    buffer[s] = v;
                    sum += v * v;
                }
            }
            else
            {
                for (var s = offset; s < offset + frames * 2; s++)
                {
                    sum += buffer[s] * buffer[s];
                }
            }
            _owner.LevelRms = (float)Math.Sqrt(sum / Math.Max(1, frames * 2));
            return count;
        }
    }
}
