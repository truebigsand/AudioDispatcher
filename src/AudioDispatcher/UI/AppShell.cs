using System;
using System.Linq;
using System.Windows.Forms;
using AudioDispatcher.Audio;
using AudioDispatcher.Logging;
using AudioDispatcher.Settings;
using Microsoft.Win32;
using Application = System.Windows.Application;
using MessageBox = System.Windows.Forms.MessageBox;

namespace AudioDispatcher.UI;

/// <summary>
/// 应用壳层:装配设备服务/分发引擎/托盘/主窗口,处理托盘菜单动作与退出流程。
/// </summary>
public sealed class AppShell
{
    private readonly MainWindow _window;
    private readonly TrayIcon _tray;
    private readonly AppSettings _settings;
    private readonly DeviceService _devices;
    private readonly DispatcherEngine _engine;
    private DateTime _lastBalloonUtc = DateTime.MinValue;

    public AppShell()
    {
        _settings = SettingsService.Load();
        ApplyStartWithWindows(_settings.StartWithWindows);

        _devices = new DeviceService();
        _engine = new DispatcherEngine(_devices, _settings);

        _tray = new TrayIcon();
        _tray.ShowMainRequested += ShowMainWindow;
        _tray.EnableAllRequested += () => DispatchUi(() => SetAllTargets(true));
        _tray.DisableAllRequested += () => DispatchUi(() => SetAllTargets(false));
        _tray.PauseToggleRequested += OnPauseToggle;
        _tray.AutoStartChanged += OnAutoStartChanged;
        _tray.ExitRequested += OnExitRequested;

        _engine.RunningChanged += () => DispatchUi(UpdateTrayState);
        _engine.TargetError += (id, msg) => DispatchUi(() => OnTargetError(msg));
        _engine.SourceLost += msg => DispatchUi(() => OnSourceLost(msg));

        _window = new MainWindow(_engine, _settings);
        Application.Current.MainWindow = _window;
        ApplyWindowBounds();
        _window.Show();

        UpdateTrayState();
        // 打开应用即自动开始分发(无源时引擎返回 false,UI 引导横幅会提示)
        _window.Dispatcher.BeginInvoke(() =>
        {
            if (_engine.SetRunning(true))
            {
                AppLog.Info("启动自动开始分发");
            }
            UpdateTrayState();
        });
    }

    public void ShowMainWindow() => _window.ActivateFromTray();

    private void UpdateTrayState()
    {
        _tray.IsPaused = !_engine.Running;
        _tray.SetStatusText(_engine.Running
            ? $"AudioDispatcher — 分发中({_engine.ActiveTargets.Count} 设备)"
            : "AudioDispatcher — 已暂停");
    }

    private void SetAllTargets(bool enabled)
    {
        // 启停涉及音频 COM,放后台线程(熄屏唤醒瞬间 Activate 可能挂起)
        var ids = _engine.Candidates.Where(c => c.Present).Select(c => c.Id).ToList();
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            foreach (var id in ids)
            {
                _engine.SetTargetEnabled(id, enabled);
            }
        });
        SettingsService.Save(_settings);
    }

    private void OnPauseToggle()
    {
        var next = !_engine.Running;
        AppLog.Info($"托盘操作:{(next ? "暂停分发" : "恢复分发")}");
        _engine.SetRunning(next);
        UpdateTrayState();
    }

    private void OnTargetError(string message)
    {
        BalloonThrottled("设备异常", message, ToolTipIcon.Warning);
    }

    private void OnSourceLost(string message)
    {
        _window.RefreshFromShell();
        BalloonThrottled("音频源丢失", message, ToolTipIcon.Error);
    }

    private void BalloonThrottled(string title, string message, ToolTipIcon icon)
    {
        var now = DateTime.UtcNow;
        if (now - _lastBalloonUtc < TimeSpan.FromSeconds(8))
        {
            return;
        }
        _lastBalloonUtc = now;
        _tray.Balloon(title, message, icon);
    }

    private void DispatchUi(Action action) => _window.Dispatcher.BeginInvoke(action);

    // ────────────── 窗口与退出 ──────────────

    private void ApplyWindowBounds()
    {
        if (_settings.WindowLeft is double left && _settings.WindowTop is double top)
        {
            _window.Left = left;
            _window.Top = top;
        }
        _window.Width = _settings.WindowWidth;
        _window.Height = _settings.WindowHeight;
    }

    private void OnExitRequested()
    {
        var msg = "确定退出 AudioDispatcher 吗?\n\n提示:若系统默认输出仍指向 CABLE Input,退出后将没有声音,请先切回原设备。";
        if (MessageBox.Show(msg, "退出 AudioDispatcher", MessageBoxButtons.OKCancel,
                            MessageBoxIcon.Question) != DialogResult.OK)
        {
            return;
        }
        AppLog.Info("用户退出应用");
        _window.AllowClose = true;
        _window.Close(); // Closing 内保存窗口位置与设置

        _engine.Dispose();
        _devices.Dispose();
        _tray.Dispose();
        Application.Current.Shutdown();
    }

    private void OnAutoStartChanged(bool enabled)
    {
        _settings.StartWithWindows = enabled;
        ApplyStartWithWindows(enabled);
        SettingsService.Save(_settings);
    }

    private static void ApplyStartWithWindows(bool enabled)
    {
        const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(runKey, writable: true);
            if (key == null)
            {
                return;
            }
            if (enabled)
            {
                var exe = Environment.ProcessPath ?? "";
                key.SetValue("AudioDispatcher", $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue("AudioDispatcher", throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "写入开机自启注册表失败");
        }
    }
}
