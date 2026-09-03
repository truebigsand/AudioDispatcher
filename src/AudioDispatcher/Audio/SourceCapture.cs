using System;
using System.Threading;
using AudioDispatcher.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioDispatcher.Audio;

/// <summary>
/// WASAPI 共享模式事件驱动捕获(NAudio WasapiCapture)。
/// DataAvailable 在捕获回调线程同步转 2ch float32 后广播(SamplesReady 共享 buffer,
/// 订阅者必须同步消费完)。错误处理依赖引擎 watchdog:数据停止超时即触发重建。
/// </summary>
public sealed class SourceCapture : IDisposable
{
    private readonly MMDevice _device;
    private readonly object _sync = new();
    private WasapiCapture? _capture;
    private CaptureToFloatConverter? _converter;
    private float[] _outBuf = new float[0];
    private long _totalFrames;
    private bool _running;

    public SourceCapture(MMDevice device)
    {
        _device = device;
        CaptureFormat = device.AudioClient.MixFormat;
    }

    public MMDevice Device => _device;
    public WaveFormat CaptureFormat { get; }
    public int SampleRate => CaptureFormat.SampleRate;
    public bool IsRunning => _running;
    public string DeviceName => _device.FriendlyName;

    /// <summary>2ch float32 交织共享缓冲(每次回调覆盖)。订阅者须同步消费。</summary>
    public event Action<float[], int>? SamplesReady;

    /// <summary>启动捕获。失败抛出,由引擎按退避策略重试。</summary>
    public void Start()
    {
        lock (_sync)
        {
            if (_running)
            {
                return;
            }
            _converter = new CaptureToFloatConverter(CaptureFormat);
            var cap = new WasapiCapture(_device, useEventSync: true, audioBufferMillisecondsLength: 20);
            cap.DataAvailable += OnDataAvailable;
            _capture = cap;
            cap.StartRecording();
            _running = true;
            AppLog.Info($"源捕获已启动: {DeviceName} ({CaptureFormat.SampleRate}Hz/{CaptureFormat.BitsPerSample}bit/{CaptureFormat.Channels}ch)");
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (!_running)
            {
                return;
            }
            _running = false;
            var cap = _capture!;
            _capture = null;
            cap.DataAvailable -= OnDataAvailable;
            try
            {
                cap.StopRecording();
            }
            catch
            {
                // 设备可能已失效
            }
            cap.Dispose();
            AppLog.Info("源捕获已停止");
        }
    }

    /// <summary>累计捕获帧数(引擎 watchdog 据此判断数据是否停止)。</summary>
    public long TotalFrames => Interlocked.Read(ref _totalFrames);

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        try
        {
            var conv = _converter!;
            var frames = e.BytesRecorded / (conv.SourceChannels * conv.Format.BitsPerSample / 8);
            if (frames <= 0)
            {
                return;
            }
            if (_outBuf.Length < frames * 2)
            {
                _outBuf = new float[frames * 2];
            }
            var written = conv.Convert(e.Buffer, 0, e.BytesRecorded, _outBuf);
            if (written > 0)
            {
                Interlocked.Add(ref _totalFrames, written / 2);
                SamplesReady?.Invoke(_outBuf, written);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "捕获数据处理失败");
        }
    }

    public void Dispose()
    {
        Stop();
        _device.Dispose();
    }
}
