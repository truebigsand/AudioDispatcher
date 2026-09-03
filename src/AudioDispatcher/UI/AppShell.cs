using System;
using System.Windows;
using AudioDispatcher.Settings;
using Microsoft.Win32;

namespace AudioDispatcher.UI;

/// <summary>
/// 应用壳层:装配托盘、主窗口、设置加载与保存、开机自启注册表项。
/// 引擎(DispatcherEngine)接入点在音频核心落地后于此处接线。
/// </summary>
public sealed class AppShell
{
    private readonly MainWindow _window;
    private readonly TrayIcon _tray;
    private readonly AppSettings _settings;
    private bool _exiting;

    public AppShell()
    {
        _settings = SettingsService.Load();
        ApplyStartWithWindows(_settings.StartWithWindows);

        _tray = new TrayIcon();
        _tray.ShowMainRequested += ShowMainWindow;
        _tray.EnableAllRequested += () => { };
        _tray.DisableAllRequested += () => { };
        _tray.PauseToggleRequested += () => { };
        _tray.AutoStartChanged += OnAutoStartChanged;
        _tray.ExitRequested += OnExitRequested;

        _window = new MainWindow();
        Application.Current.MainWindow = _window;
        _window.Closing += OnWindowClosing;

        if (_settings.WindowLeft is double left && _settings.WindowTop is double top)
        {
            _window.Left = left;
            _window.Top = top;
        }
        _window.Width = _settings.WindowWidth;
        _window.Height = _settings.WindowHeight;
        _window.Show();
    }

    public void ShowMainWindow() => _window.ActivateFromTray();

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting || !_settings.MinimizeToTray)
        {
            SaveWindowBounds();
            return;
        }
        // 关闭 = 最小化到托盘
        e.Cancel = true;
        _window.Hide();
    }

    private void SaveWindowBounds()
    {
        if (_window.WindowState == WindowState.Normal)
        {
            _settings.WindowLeft = _window.Left;
            _settings.WindowTop = _window.Top;
        }
        _settings.WindowWidth = _window.Width;
        _settings.WindowHeight = _window.Height;
        SettingsService.Save(_settings);
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
        catch (Exception)
        {
            // 注册表写入失败不阻断使用。
        }
    }

    private void OnExitRequested()
    {
        if (MessageBox.Show("确定退出 AudioDispatcher 吗?\n\n提示:若系统默认输出仍指向 CABLE Input,退出后将没有声音,请先切回原设备。",
                            "退出 AudioDispatcher",
                            MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }
        _exiting = true;
        SaveWindowBounds();
        _tray.Dispose();
        Application.Current.Shutdown();
    }
}
