using System;
using NAudio.Wave;

namespace AudioDispatcher.Audio;

/// <summary>
/// 捕获字节流 → 2ch float32 交织。通道数 >2 时按全部通道平均下混;
/// 32bit 容器按 IEEE float 处理(PCM32 极罕见)。
/// </summary>
public sealed class CaptureToFloatConverter
{
    private readonly WaveFormat _format;
    private readonly int _channels;
    private readonly int _bytesPerSample;
    private readonly bool _isFloat;

    public CaptureToFloatConverter(WaveFormat format)
    {
        _format = format;
        _channels = format.Channels;
        _bytesPerSample = format.BitsPerSample / 8;
        _isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat || _bytesPerSample == 4;
    }

    public int SourceChannels => _channels;
    public WaveFormat Format => _format;

    /// <summary>把 count 字节转为 2ch float 样本写入 out2ch,返回写入的样本数(2ch 交织,帧数×2)。</summary>
    public int Convert(byte[] src, int offset, int count, float[] out2ch)
    {
        var frames = count / (_channels * _bytesPerSample);
        var need = frames * 2;
        if (out2ch.Length < need)
        {
            throw new ArgumentException("out2ch 容量不足");
        }

        var p = offset;
        var w = 0;
        for (var f = 0; f < frames; f++)
        {
            float l;
            float r;
            if (_channels == 2)
            {
                l = ReadSample(src, p, _bytesPerSample, _isFloat);
                r = ReadSample(src, p + _bytesPerSample, _bytesPerSample, _isFloat);
                p += _bytesPerSample * 2;
            }
            else
            {
                var sum = 0.0;
                for (var c = 0; c < _channels; c++)
                {
                    sum += ReadSample(src, p + c * _bytesPerSample, _bytesPerSample, _isFloat);
                }
                p += _bytesPerSample * _channels;
                var avg = (float)(sum / _channels);
                l = avg;
                r = avg;
            }
            out2ch[w++] = l;
            out2ch[w++] = r;
        }
        return w;
    }

    private static float ReadSample(byte[] src, int offset, int bytesPerSample, bool isFloat)
    {
        if (isFloat)
        {
            return BitConverter.Int32BitsToSingle(
                src[offset] | (src[offset + 1] << 8) | (src[offset + 2] << 16) | (src[offset + 3] << 24));
        }
        switch (bytesPerSample)
        {
            case 1:
                return (src[offset] - 128) / 128f;
            case 2:
                return (short)(src[offset] | (src[offset + 1] << 8)) / 32768f;
            case 3:
            {
                var v = src[offset] | (src[offset + 1] << 8) | (src[offset + 2] << 16);
                if ((v & 0x800000) != 0)
                {
                    v |= unchecked((int)0xFF000000);
                }
                return v / 8388608f;
            }
            default:
            {
                var v = src[offset] | (src[offset + 1] << 8) | (src[offset + 2] << 16) | (src[offset + 3] << 24);
                return v / 2147483648f;
            }
        }
    }
}
