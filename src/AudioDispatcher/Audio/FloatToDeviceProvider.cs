using System;
using NAudio.Wave;

namespace AudioDispatcher.Audio;

/// <summary>
/// 把 2 声道 float32 采样源转换为设备 MixFormat 字节流(位深转换 + 声道展开),
/// 作为 WasapiOut 的输入 IWaveProvider。输出 WaveFormat 与设备 MixFormat 完全一致。
/// 批量读取源后整块转换,避免逐帧调用开销。
/// </summary>
public sealed class FloatToDeviceProvider : IWaveProvider
{
    private readonly ISampleProvider _source;
    private readonly WaveFormat _outputFormat;
    private readonly int _outputChannels;
    private readonly int _bytesPerSample;
    private readonly bool _isFloat;
    private float[] _srcScratch = new float[0];

    public FloatToDeviceProvider(ISampleProvider source, WaveFormat outputFormat)
    {
        if (source.WaveFormat.SampleRate != outputFormat.SampleRate ||
            source.WaveFormat.Channels != 2)
        {
            throw new ArgumentException("源格式必须是 2 声道且采样率等于输出格式采样率");
        }
        _source = source;
        _outputFormat = outputFormat;
        _outputChannels = outputFormat.Channels;
        _bytesPerSample = outputFormat.BitsPerSample / 8;
        _isFloat = outputFormat.Encoding == WaveFormatEncoding.IeeeFloat ||
                   _bytesPerSample == 4; // 32bit 容器按 float 处理(PCM32 极罕见)
    }

    public WaveFormat WaveFormat => _outputFormat;

    public int Read(byte[] buffer, int offset, int count)
    {
        var frameBytes = _outputChannels * _bytesPerSample;
        var frames = count / frameBytes;
        if (frames <= 0)
        {
            return 0;
        }
        if (_srcScratch.Length < frames * 2)
        {
            _srcScratch = new float[frames * 2];
        }
        var got = _source.Read(_srcScratch, 0, frames * 2);
        // 源枯竭时补静音(由调用方漂移补偿兜底,此处保证字节数完整)
        for (var s = got; s < frames * 2; s++)
        {
            _srcScratch[s] = 0f;
        }

        var written = 0;
        for (var f = 0; f < frames; f++)
        {
            var l = _srcScratch[f * 2];
            var r = _srcScratch[f * 2 + 1];
            for (var c = 0; c < _outputChannels; c++)
            {
                var v = c switch
                {
                    0 => l,
                    1 => r,
                    _ => l, // 超过 2 声道的设备:其余通道复制 L(极罕见场景的兜底)
                };
                if (_isFloat)
                {
                    WriteFloat(buffer, offset + written, v);
                }
                else
                {
                    WritePcm(buffer, offset + written, v, _bytesPerSample);
                }
                written += _bytesPerSample;
            }
        }
        return written;
    }

    private static void WriteFloat(byte[] dst, int offset, float v)
    {
        var bits = BitConverter.SingleToInt32Bits(v);
        dst[offset] = (byte)bits;
        dst[offset + 1] = (byte)(bits >> 8);
        dst[offset + 2] = (byte)(bits >> 16);
        dst[offset + 3] = (byte)(bits >> 24);
    }

    private static void WritePcm(byte[] dst, int offset, float v, int bytesPerSample)
    {
        if (v > 1f)
        {
            v = 1f;
        }
        else if (v < -1f)
        {
            v = -1f;
        }
        var max = bytesPerSample switch
        {
            1 => 127,
            2 => 32767,
            3 => 8388607,
            _ => int.MaxValue,
        };
        var sample = (int)(v * max);
        switch (bytesPerSample)
        {
            case 1:
                dst[offset] = (byte)(sample + 128);
                break;
            case 2:
                dst[offset] = (byte)sample;
                dst[offset + 1] = (byte)(sample >> 8);
                break;
            case 3:
                dst[offset] = (byte)sample;
                dst[offset + 1] = (byte)(sample >> 8);
                dst[offset + 2] = (byte)(sample >> 16);
                break;
            default:
                dst[offset] = (byte)sample;
                dst[offset + 1] = (byte)(sample >> 8);
                dst[offset + 2] = (byte)(sample >> 16);
                dst[offset + 3] = (byte)(sample >> 24);
                break;
        }
    }
}
