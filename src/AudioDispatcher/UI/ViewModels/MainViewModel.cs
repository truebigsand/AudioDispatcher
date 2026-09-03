using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using AudioDispatcher.Audio;
using AudioDispatcher.Settings;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace AudioDispatcher.UI.ViewModels;

/// <summary>
/// 主窗口状态。引擎事件到达时由主窗口 marshal 到 UI 线程调用本类方法;
/// 实时电平/统计由主窗口 DispatcherTimer 轮询 RefreshRealtime()。
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly DispatcherEngine _engine;
    private readonly AppSettings _settings;
    private readonly System.Collections.Generic.Dictionary<string, DeviceRowViewModel> _rows = new();

    private string _sourceLine = "正在检测 VB-Audio Cable…";
    private Brush _sourceBrush = Brushes.Gray;
    private string _runButtonText = "开始分发";
    private string _statusLine = "就绪";
    private bool _running;
    private bool _hasSource;
    private bool _sourceSilent;
    private SourceInfo? _selectedSource;

    public MainViewModel(DispatcherEngine engine, AppSettings settings)
    {
        _engine = engine;
        _settings = settings;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<DeviceRowViewModel> Devices { get; } = new();
    public ObservableCollection<SourceInfo> Sources { get; } = new();

    /// <summary>源下拉当前选中(双向绑定)。</summary>
    public SourceInfo? SelectedSource
    {
        get => _selectedSource;
        set
        {
            _selectedSource = value;
            On(nameof(SelectedSource));
        }
    }

    /// <summary>RefreshDevices 重建源列表期间置位,抑制 SelectionChanged 副作用。</summary>
    public bool SuppressSourceSelection { get; set; }

    public string SourceLine { get => _sourceLine; private set { _sourceLine = value; On(nameof(SourceLine)); } }
    public Brush SourceBrush { get => _sourceBrush; private set { _sourceBrush = value; On(nameof(SourceBrush)); } }
    public string RunButtonText { get => _runButtonText; private set { _runButtonText = value; On(nameof(RunButtonText)); } }
    public string StatusLine { get => _statusLine; private set { _statusLine = value; On(nameof(StatusLine)); } }
    public bool Running { get => _running; private set { _running = value; On(nameof(Running)); } }
    public bool HasSource { get => _hasSource; private set { _hasSource = value; On(nameof(HasSource)); On(nameof(ShowGuide)); } }
    public bool ShowGuide => !HasSource;
    public bool SourceSilent { get => _sourceSilent; private set { _sourceSilent = value; On(nameof(SourceSilent)); } }

    /// <summary>源候选与设备清单刷新(引擎 EndpointsChanged / 启动时)。UI 线程调用。</summary>
    public void RefreshDevices()
    {
        // 源下拉(保留当前选择);重建期间抑制 SelectionChanged 副作用,
        // 否则 ComboBox 绑定推送会触发主窗口"切换源"逻辑(运行中全停/重启风暴)。
        var selectedId = SelectedSource?.Id ?? _settings.SourceDeviceId;
        SuppressSourceSelection = true;
        try
        {
            Sources.Clear();
            foreach (var s in _engine.SourceCandidates())
            {
                Sources.Add(s);
                if (s.Id == selectedId)
                {
                    SelectedSource = s;
                }
            }
            if (SelectedSource == null && Sources.Count > 0)
            {
                SelectedSource = Sources[0];
            }
        }
        finally
        {
            SuppressSourceSelection = false;
        }

        // 设备行:可用(在线)设备排在前面,组内保持枚举顺序(OrderBy 稳定)
        var ordered = _engine.Candidates
            .OrderByDescending(c => c.Present)
            .ToList();
        var ids = ordered.Select(c => c.Id).ToHashSet();
        foreach (var stale in _rows.Keys.Where(id => !ids.Contains(id)).ToArray())
        {
            _rows.Remove(stale);
        }
        Devices.Clear();
        foreach (var c in ordered)
        {
            if (!_rows.TryGetValue(c.Id, out var row))
            {
                row = new DeviceRowViewModel
                {
                    Id = c.Id,
                    Name = c.Name,
                    IsChecked = _settings.Targets.FirstOrDefault(t => t.DeviceId == c.Id)?.Enabled ?? false,
                };
                if (c.Present)
                {
                    // 行音量/静音 = 设备系统音量(仅新行读取一次;之后靠 500ms 轮询同步)
                    var (vol, muted) = _engine.GetVolumeState(c.Id);
                    row.Volume = vol * 100;
                    row.IsMuted = muted;
                }
                _rows[c.Id] = row;
            }
            row.Format = c.Format;
            row.IsPresent = c.Present;
            row.Error = c.Present ? (_engine.GetError(c.Id) ?? "") : "设备已断开(插回后自动恢复)";
            row.Stats = "";
            row.Level = 0;
            Devices.Add(row);
        }
        RefreshSourceState();
    }

    /// <summary>周期同步设备系统音量到行 UI(用户在系统侧调节音量时滑块跟随)。</summary>
    public void RefreshDeviceVolumes()
    {
        foreach (var row in Devices)
        {
            if (!row.IsPresent)
            {
                continue;
            }
            var (vol, muted) = _engine.GetVolumeState(row.Id);
            var newVol = vol * 100;
            if (Math.Abs(newVol - row.Volume) > 0.5)
            {
                row.Volume = newVol;
            }
            if (muted != row.IsMuted)
            {
                row.IsMuted = muted;
            }
        }
    }

    /// <summary>源状态(有无/采样率/静默)与引擎运行状态刷新。</summary>
    public void RefreshSourceState()
    {
        HasSource = _engine.Source != null || Sources.Count > 0;
        var src = _engine.Source;

        if (!HasSource)
        {
            SourceLine = "未检测到 VB-Audio Cable 输出端点";
            SourceBrush = Brushes.IndianRed;
            Running = false;
            RunButtonText = "开始分发";
            StatusLine = "就绪 · 等待安装虚拟声卡";
            SourceSilent = false;
            return;
        }

        SourceSilent = _engine.SourceSilent;
        if (src != null && _engine.Running)
        {
            SourceLine = $"● {src.DeviceName} · {src.SampleRate / 1000.0:0.#} kHz" +
                         (SourceSilent ? " · 等待音频数据" : "");
            SourceBrush = SourceSilent ? Brushes.DarkOrange : Brushes.ForestGreen;
            RunButtonText = "停止分发";
            StatusLine = $"分发中 · 目标 {_engine.ActiveTargets.Count} 个 · 缓冲 {_engine.BufferMs} ms";
        }
        else if (src != null)
        {
            SourceLine = $"● {src.DeviceName} · {src.SampleRate / 1000.0:0.#} kHz";
            SourceBrush = Brushes.SteelBlue;
            RunButtonText = "开始分发";
            StatusLine = "就绪";
        }
        else
        {
            var name = SelectedSource?.Name ?? "自动检测";
            SourceLine = $"○ 源: {name}";
            SourceBrush = Brushes.SteelBlue;
            RunButtonText = "开始分发";
            StatusLine = "就绪";
        }
        Running = _engine.Running;
    }

    /// <summary>电平/丢补统计实时刷新(UI 定时器调用)。</summary>
    public void RefreshRealtime()
    {
        foreach (var row in Devices)
        {
            var t = _engine.GetTarget(row.Id);
            if (t != null)
            {
                row.Level = LevelToMeter(t.LevelRms);
                var over = t.OverrunFrames;   // 64 位进程下 long 读原子
                var under = t.UnderrunFrames;
                row.Stats = (over + under) == 0 ? "" : $"丢 {over} · 补 {under}";
            }
            else
            {
                row.Level = 0;
            }
        }
    }

    /// <summary>RMS(0..1)→ 进度条值:60dB 对数刻度。</summary>
    private static double LevelToMeter(float rms)
    {
        if (rms <= 1e-5f)
        {
            return 0;
        }
        var db = 20 * Math.Log10(rms);
        return Math.Clamp((db + 60) / 60.0, 0, 1);
    }

    private void On(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
