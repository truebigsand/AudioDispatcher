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
    private readonly DispatcherTimer _volumeCommitTimer;
    private readonly Dictionary<string, double> _pendingVolumes = new();
    private bool _volumeSyncRunning;
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

        _engine.EndpointsChanged += QueueRefreshDevices;
        _engine.SourceCandidatesChanged += QueueRefreshSourceState;

        _realtimeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _realtimeTimer.Tick += (_, _) => _vm.RefreshRealtime();
        _realtimeTimer.Start();

        _slowTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _slowTimer.Tick += (_, _) =>
        {
            _vm.RefreshSourceState();
            SyncVolumesInBackground();
        };
        _slowTimer.Start();

        _volumeCommitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _volumeCommitTimer.Tick += (_, _) =>
        {
            _volumeCommitTimer.Stop();
            CommitPendingVolumes();
        };

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

    private bool _refreshDevicesQueued;
    private bool _refreshSourceQueued;

    /// <summary>
    /// 后台同步设备系统音量到行 UI。UI 线程不碰音频 COM(设备唤醒/断链瞬间 Activate 可能挂起,
    /// 会冻结 Dispatcher);查询放后台线程并带 4s 超时自愈。
    /// </summary>
    private void SyncVolumesInBackground()
    {
        if (_volumeSyncRunning)
        {
            return;
        }
        _volumeSyncRunning = true;
        var rows = _vm.Devices.Where(r => r.IsPresent).Select(r => (Row: r, r.Id)).ToList();
        var sync = Task.Run(() =>
        {
            var results = new List<(UI.ViewModels.DeviceRowViewModel Row, double Vol, bool Muted)>();
            foreach (var (row, id) in rows)
            {
                var (vol, muted) = _engine.GetVolumeState(id);
                results.Add((row, vol * 100, muted));
            }
            return results;
        });
#pragma warning disable CS4014 // fire-and-forget:超时由 RunVolumeSyncWaiter 内部处理
        _ = RunVolumeSyncWaiter(sync);
#pragma warning restore CS4014
    }

    private async Task RunVolumeSyncWaiter(Task<List<(UI.ViewModels.DeviceRowViewModel Row, double Vol, bool Muted)>> sync)
    {
        var done = await Task.WhenAny(sync, Task.Delay(4000));
        Dispatcher.BeginInvoke(() =>
        {
            _volumeSyncRunning = false;
            if (done == sync)
            {
                foreach (var (row, vol, muted) in sync.Result)
                {
                    if (Math.Abs(row.Volume - vol) > 0.5)
                    {
                        row.Volume = vol;
                    }
                    if (row.IsMuted != muted)
                    {
                        row.IsMuted = muted;
                    }
                }
            }
            // 超时:放弃本轮(挂起的后台线程自生自灭),下轮重试
        });
    }

    /// <summary>滑块拖动防抖:停止 150ms 后才在后台线程提交设备音量。</summary>
    private void ScheduleVolumeCommit(DeviceRowViewModel row)
    {
        _pendingVolumes[row.Id] = row.Volume;
        _volumeCommitTimer.Stop();
        _volumeCommitTimer.Start();
    }

    private void CommitPendingVolumes()
    {
        if (_pendingVolumes.Count == 0)
        {
            return;
        }
        var pending = _pendingVolumes.ToArray();
        _pendingVolumes.Clear();
        _ = Task.Run(() =>
        {
            foreach (var (id, vol) in pending)
            {
                _engine.SetVolume(id, Math.Clamp(vol, 0, 100) / 100.0);
            }
        });
    }

    /// <summary>设备列表刷新入队(合并):通知风暴下 UI 队列只保留一次重建,防雪崩卡死。</summary>
    private void QueueRefreshDevices()
    {
        if (_refreshDevicesQueued)
        {
            return;
        }
        _refreshDevicesQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _refreshDevicesQueued = false;
            _vm.RefreshDevices();
        });
    }

    private void QueueRefreshSourceState()
    {
        if (_refreshSourceQueued)
        {
            return;
        }
        _refreshSourceQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _refreshSourceQueued = false;
            _vm.RefreshSourceState();
        });
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
        var id = row.Id;
        var enabled = row.IsChecked;
        // 启停涉及音频 COM(设备 Activate),放后台线程,避免熄屏唤醒瞬间挂起 UI
        _ = Task.Run(() =>
        {
            _engine.SetTargetEnabled(id, enabled);
            Dispatcher.BeginInvoke(() =>
            {
                if (RowOf(sender) is { } fresh)
                {
                    fresh.Error = _engine.GetError(id) ?? "";
                }
            });
        });
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
        ScheduleVolumeCommit(row); // 防抖后后台提交(UI 线程不碰音频 COM)
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
        var id = row.Id;
        var muted = row.IsMuted;
        _ = Task.Run(() => _engine.SetMuted(id, muted));
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
        var actions = new List<(string Id, bool Enabled)>();
        try
        {
            foreach (var row in _vm.Devices)
            {
                if (row.IsPresent)
                {
                    row.IsChecked = enabled;
                    actions.Add((row.Id, enabled));
                }
            }
        }
        finally
        {
            _suppressRowEvents = false;
        }
        if (actions.Count > 0)
        {
            _ = Task.Run(() =>
            {
                foreach (var (id, on) in actions)
                {
                    _engine.SetTargetEnabled(id, on);
                }
            });
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
