using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AudioDispatcher.UI;

/// <summary>
/// 托盘图标封装:双态图标(绿=分发中/灰=暂停)在运行时用 System.Drawing 绘制,
/// 避免维护 .ico 资源文件。菜单行为通过回调交给 AppShell。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _notify;
    private readonly Icon _iconActive;
    private readonly Icon _iconIdle;
    private readonly List<IntPtr> _ownedHandles = new();
    private bool _isPaused = true;
    private bool _autoStartChecked;
    private ToolStripMenuItem? _pauseItem;
    private ToolStripMenuItem? _autoStartItem;
    private bool _disposed;

    public event Action? ShowMainRequested;
    public event Action? EnableAllRequested;
    public event Action? DisableAllRequested;
    public event Action? PauseToggleRequested;
    public event Action<bool>? AutoStartChanged;
    public event Action? ExitRequested;

    public TrayIcon()
    {
        _iconActive = CreateStateIcon(Color.FromArgb(46, 160, 67));
        _iconIdle = CreateStateIcon(Color.FromArgb(150, 150, 150));

        _notify = new NotifyIcon { Icon = _iconIdle, Visible = true, Text = "AudioDispatcher" };

        var menu = new ContextMenuStrip();
        menu.Items.Add("打开主窗口", null, (_, _) => ShowMainRequested?.Invoke());

        var enableAll = new ToolStripMenuItem("启用全部");
        enableAll.Click += (_, _) => EnableAllRequested?.Invoke();
        menu.Items.Add(enableAll);

        var disableAll = new ToolStripMenuItem("停用全部");
        disableAll.Click += (_, _) => DisableAllRequested?.Invoke();
        menu.Items.Add(disableAll);

        _pauseItem = new ToolStripMenuItem("暂停分发");
        _pauseItem.Click += (_, _) => PauseToggleRequested?.Invoke();
        menu.Items.Add(_pauseItem);

        menu.Items.Add(new ToolStripSeparator());

        _autoStartItem = new ToolStripMenuItem("开机自启") { Checked = _autoStartChecked };
        _autoStartItem.Click += (_, _) => AutoStartChanged?.Invoke(_autoStartItem.Checked);
        menu.Items.Add(_autoStartItem);

        menu.Items.Add(new ToolStripSeparator());

        var exit = new ToolStripMenuItem("退出");
        exit.Click += (_, _) => ExitRequested?.Invoke();
        menu.Items.Add(exit);

        _notify.ContextMenuStrip = menu;
        _notify.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowMainRequested?.Invoke();
            }
        };
    }

    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            _isPaused = value;
            _notify.Icon = value ? _iconIdle : _iconActive;
            if (_pauseItem != null)
            {
                _pauseItem.Text = value ? "恢复分发" : "暂停分发";
            }
        }
    }

    public bool AutoStartChecked
    {
        get => _autoStartChecked;
        set
        {
            _autoStartChecked = value;
            if (_autoStartItem != null)
            {
                _autoStartItem.Checked = value;
            }
        }
    }

    public void SetStatusText(string text) => _notify.Text = text;

    /// <summary>弹一条一次性气泡提示。</summary>
    public void Balloon(string title, string message, ToolTipIcon icon)
    {
        _notify.BalloonTipTitle = title;
        _notify.BalloonTipText = message;
        _notify.BalloonTipIcon = icon;
        _notify.ShowBalloonTip(3000);
    }

    private Icon CreateStateIcon(Color baseColor)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var bg = new SolidBrush(baseColor);
            g.FillEllipse(bg, 2, 2, 28, 28);

            using var pen = new Pen(Color.White, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            // 三条声波弧线(从圆心向右扩散)
            for (var i = 0; i < 3; i++)
            {
                var inset = 4 + i * 4;
                g.DrawArc(pen, 2 + inset, 2 + inset, 28 - inset * 2, 28 - inset * 2, -50, 100);
            }
        }

        var handle = bmp.GetHicon();
        _ownedHandles.Add(handle);
        return Icon.FromHandle(handle);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _notify.Visible = false;
        _notify.Dispose();
        foreach (var h in _ownedHandles)
        {
            NativeMethods.DestroyIcon(h);
        }
        _ownedHandles.Clear();
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr hIcon);
    }
}
