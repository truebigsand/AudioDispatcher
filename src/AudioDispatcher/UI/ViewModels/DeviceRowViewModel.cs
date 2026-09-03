using System.ComponentModel;

namespace AudioDispatcher.UI.ViewModels;

/// <summary>
/// 目标设备行。值变化经 PropertyChanged 通知主窗口处理(调用引擎 + 持久化),
/// 保持行 VM 与引擎解耦。Level 为 0..1 对数映射(60dB 动态),由轮询刷新。
/// </summary>
public sealed class DeviceRowViewModel : INotifyPropertyChanged
{
    private bool _checked;
    private double _volume = 100;
    private bool _muted;
    private bool _present = true;
    private string _format = "";
    private string _error = "";
    private string _stats = "";
    private double _level;

    public required string Id { get; init; }
    public required string Name { get; init; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsChecked
    {
        get => _checked;
        set
        {
            if (_checked != value)
            {
                _checked = value;
                OnChanged(nameof(IsChecked));
            }
        }
    }

    /// <summary>设备系统音量百分比(0–100),双向绑定设备端点主音量。</summary>
    public double Volume
    {
        get => _volume;
        set
        {
            if (Math.Abs(_volume - value) > 0.01)
            {
                _volume = value;
                OnChanged(nameof(Volume));
            }
        }
    }

    public bool IsMuted
    {
        get => _muted;
        set
        {
            if (_muted != value)
            {
                _muted = value;
                OnChanged(nameof(IsMuted));
            }
        }
    }

    public bool IsPresent
    {
        get => _present;
        set
        {
            if (_present != value)
            {
                _present = value;
                OnChanged(nameof(IsPresent));
                OnChanged(nameof(RowEnabled));
            }
        }
    }

    public string Format
    {
        get => _format;
        set
        {
            if (_format != value)
            {
                _format = value;
                OnChanged(nameof(Format));
            }
        }
    }

    public string Error
    {
        get => _error;
        set
        {
            if (_error != value)
            {
                _error = value;
                OnChanged(nameof(Error));
            }
        }
    }

    public string Stats
    {
        get => _stats;
        set
        {
            if (_stats != value)
            {
                _stats = value;
                OnChanged(nameof(Stats));
            }
        }
    }

    public double Level
    {
        get => _level;
        set
        {
            if (Math.Abs(_level - value) > 0.001)
            {
                _level = value;
                OnChanged(nameof(Level));
            }
        }
    }

    /// <summary>勾选框与音量等交互是否可用(设备在线才可勾选启用)。</summary>
    public bool RowEnabled => IsPresent;

    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
