using System;
using System.Threading;

namespace AudioDispatcher.Audio;

/// <summary>
/// 单写单读的 float32 交织采样环形缓冲(帧粒度,2 的幂容量)。
/// 写端:捕获线程(或测试音线程,经调用方锁保证唯一);读端:设备渲染线程。
/// 帧位置用单调长整型计数,索引取模容量,读写整帧操作,无需互斥。
/// </summary>
public sealed class SampleRingBuffer
{
    private readonly float[] _data;
    private readonly int _channels;
    private readonly int _capacityFrames;
    private readonly int _mask;
    private long _readFrame;
    private long _writeFrame;

    public SampleRingBuffer(int capacityFrames, int channels)
    {
        _channels = channels;
        _capacityFrames = NextPowerOfTwo(Math.Max(16, capacityFrames));
        _mask = _capacityFrames - 1;
        _data = new float[_capacityFrames * channels];
    }

    public int Channels => _channels;
    public int CapacityFrames => _capacityFrames;

    public int AvailableFrames =>
        (int)(Volatile.Read(ref _writeFrame) - Volatile.Read(ref _readFrame));

    public int FreeFrames => _capacityFrames - AvailableFrames;

    /// <summary>写入最多 frames 帧,空间不足时丢弃写不下的部分并返回实际写入数。</summary>
    public int Write(float[] src, int srcOffsetFrames, int frames)
    {
        if (frames <= 0)
        {
            return 0;
        }
        var wf = Volatile.Read(ref _writeFrame);
        var rf = Volatile.Read(ref _readFrame);
        var space = _capacityFrames - (int)(wf - rf);
        if (space <= 0)
        {
            return 0;
        }
        var n = Math.Min(frames, space);
        var srcOffset = srcOffsetFrames * _channels;
        var first = (int)(wf & _mask) * _channels;
        var firstLen = Math.Min(n * _channels, _data.Length - first);
        Array.Copy(src, srcOffset, _data, first, firstLen);
        if (firstLen < n * _channels)
        {
            Array.Copy(src, srcOffset + firstLen, _data, 0, n * _channels - firstLen);
        }
        Volatile.Write(ref _writeFrame, wf + n);
        return n;
    }

    /// <summary>读取最多 frames 帧,不足时只读可读部分。</summary>
    public int Read(float[] dst, int dstOffsetFrames, int frames)
    {
        if (frames <= 0)
        {
            return 0;
        }
        var rf = Volatile.Read(ref _readFrame);
        var avail = (int)(Volatile.Read(ref _writeFrame) - rf);
        if (avail <= 0)
        {
            return 0;
        }
        var n = Math.Min(frames, avail);
        var dstOffset = dstOffsetFrames * _channels;
        var first = (int)(rf & _mask) * _channels;
        var firstLen = Math.Min(n * _channels, _data.Length - first);
        Array.Copy(_data, first, dst, dstOffset, firstLen);
        if (firstLen < n * _channels)
        {
            Array.Copy(_data, 0, dst, dstOffset + firstLen, n * _channels - firstLen);
        }
        Volatile.Write(ref _readFrame, rf + n);
        return n;
    }

    /// <summary>读端丢弃最旧 frames 帧(漂移补偿溢出处理)。</summary>
    public void SkipOldest(int frames)
    {
        if (frames <= 0)
        {
            return;
        }
        var rf = Volatile.Read(ref _readFrame);
        var avail = (int)(Volatile.Read(ref _writeFrame) - rf);
        var n = Math.Min(frames, Math.Max(0, avail));
        Volatile.Write(ref _readFrame, rf + n);
    }

    public void Clear()
    {
        Volatile.Write(ref _readFrame, 0);
        Volatile.Write(ref _writeFrame, 0);
        Array.Clear(_data);
    }

    private static int NextPowerOfTwo(int v)
    {
        var p = 1;
        while (p < v)
        {
            p <<= 1;
        }
        return p;
    }
}
