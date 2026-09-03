using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AudioDispatcher.Audio;
using AudioDispatcher.Logging;
using AudioDispatcher.Settings;
using AudioDispatcher.UI.ViewModels;

namespace AudioDispatcher;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly DispatcherEngine _engine;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _realtimeTimer;
    private readonly DispatcherTimer _slowTimer;
    private readonly DispatcherTimer _saveTimer;
    private bool _suppressRowEvents;

    /// <summary>退出流程置真后,Closing 不再最小化到托盘。</summary>
    public bool AllowClose { get; set; }

    public MainWindow(DispatcherEngine engine, AppSettings settings)
    {
        InitializeComponent();
        _engine = engine;
        _settings = settings;
        _vm = new MainViewModel(engine, settings);
        DataContext = _vm;

        _engine.EndpointsChanged += () => DispatchUi(_vm.RefreshDevices);
        _engine.SourceCandidatesChanged += () => DispatchUi(_vm.RefreshSourceState);

        _realtimeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _realtimeTimer.Tick += (_, _) => _vm.RefreshRealtime();
        _realtimeTimer.Start();

        _slowTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _slowTimer.Tick += (_, _) =>
        {
            _vm.RefreshSourceState();
            _vm.RefreshDeviceVolumes();
        };
        _slowTimer.Start();

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SettingsService.Save(_settings);
        };

        Closing += OnClosing;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        BufferTextBox.Text = _settings.BufferMs.ToString();
        UpdateLatencyHint();
        _vm.RefreshDevices();
        _vm.RefreshSourceState();
    }

    private void DispatchUi(Action action)
    {
        Dispatcher.BeginInvoke(action);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!AllowClose && _settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        // 真实退出:记录窗口位置尺寸
        if (WindowState == WindowState.Normal)
        {
            _settings.WindowLeft = Left;
            _settings.WindowTop = Top;
        }
        _settings.WindowWidth = Width;
        _settings.WindowHeight = Height;
        _realtimeTimer.Stop();
        _slowTimer.Stop();
        SettingsService.Save(_settings);
    }

    /// <summary>托盘唤起。</summary>
    public void ActivateFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
        Topmost = true;
        Topmost = false;
        RefreshFromShell();
    }

    /// <summary>外部(壳层)触发全量刷新。</summary>
    public void RefreshFromShell()
    {
        _vm.RefreshDevices();
        _vm.RefreshSourceState();
    }

    // ═══════════════════ 源状态卡 ═══════════════════

    private void OnRunButtonClick(object sender, RoutedEventArgs e)
    {
        if (!_vm.Running)
        {
            var ok = _engine.SetRunning(true);
            if (ok)
            {
                Logging.AppLog.Info("用户启动分发");
            }
        }
        else
        {
            _engine.SetRunning(false);
            Logging.AppLog.Info("用户停止分发");
        }
        _vm.RefreshSourceState();
    }

    private void OnSourceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm.SuppressSourceSelection)
        {
            return; // 代码重建源列表,非用户操作
        }
        if (_vm.SelectedSource is not { } src)
        {
            return;
        }
        // 与已应用源相同(自动恢复/重复刷新)→ 不打扰运行状态
        var applied = _engine.Source?.Device.ID;
        if (src.Id == _settings.SourceDeviceId && (applied == null || applied == src.Id))
        {
            return;
        }
        _settings.SourceDeviceId = src.Id;
        ScheduleSave();
        if (_vm.Running)
        {
            // 用户切换源 = 停止后按新源重启
            _engine.SetRunning(false);
            _engine.SetRunning(true);
            _vm.RefreshSourceState();
        }
    }

    private void OnOpenDownloadLink(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://vb-audio.com/Cable/") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "打开下载链接失败");
        }
    }

    // ═══════════════════ 设备行交互 ═══════════════════

    private DeviceRowViewModel? RowOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as DeviceRowViewModel;

    private void OnRowChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressRowEvents)
        {
            return;
        }
        if (RowOf(sender) is not { } row)
        {
            return;
        }
        _engine.SetTargetEnabled(row.Id, row.IsChecked);
        row.Error = _engine.GetError(row.Id) ?? "";
        ScheduleSave();
    }

    private void OnRowVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressRowEvents)
        {
            return;
        }
        if (RowOf(sender) is not { } row)
        {
            return;
        }
        _engine.SetVolume(row.Id, row.Volume / 100.0);
        ScheduleSave();
    }

    private void OnRowMutedChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressRowEvents)
        {
            return;
        }
        if (RowOf(sender) is not { } row)
        {
            return;
        }
        _engine.SetMuted(row.Id, row.IsMuted);
        ScheduleSave();
    }

    private void OnRowTestTone(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row)
        {
            return;
        }
        _engine.PlayTestTone(row.Id);
    }

    private void OnCheckAll(object sender, RoutedEventArgs e) => SetAllChecked(true);

    private void OnUncheckAll(object sender, RoutedEventArgs e) => SetAllChecked(false);

    private void SetAllChecked(bool enabled)
    {
        _suppressRowEvents = true;
        try
        {
            foreach (var row in _vm.Devices)
            {
                if (row.IsPresent)
                {
                    row.IsChecked = enabled;
                    _engine.SetTargetEnabled(row.Id, enabled);
                    row.Error = enabled ? (_engine.GetError(row.Id) ?? "") : "";
                }
            }
        }
        finally
        {
            _suppressRowEvents = false;
        }
        ScheduleSave();
    }

    // ═══════════════════ 缓冲设置 ═══════════════════

    private void OnBufferKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyBuffer();
        }
    }

    private void OnBufferLostFocus(object sender, RoutedEventArgs e) => ApplyBuffer();

    private void ApplyBuffer()
    {
        if (int.TryParse(BufferTextBox.Text.Trim(), out var ms))
        {
            ms = Math.Clamp(ms, 10, 500);
        }
        else
        {
            ms = 50;
        }
        BufferTextBox.Text = ms.ToString();
        _engine.SetBufferMs(ms);
        _settings.BufferMs = ms;
        UpdateLatencyHint();
        ScheduleSave();
    }

    private void UpdateLatencyHint()
    {
        if (_settings.BufferMs is >= 10 and <= 500)
        {
            LatencyHintText.Text =
                $"理论延迟 ≈ 捕获 10ms + 缓冲 {_settings.BufferMs}ms + 设备周期 ≈ {_settings.BufferMs + 20}ms";
        }
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }
}
