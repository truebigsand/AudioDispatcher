using System;
using System.Runtime.InteropServices;
using System.Windows;
using Application = System.Windows.Application;

namespace AudioDispatcher;

public partial class App : Application
{
    /// <summary>壳层实例,供 Program(第二实例唤起)访问。</summary>
    public static UI.AppShell? Shell { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 注册稳定 AUMID:Win11 把托盘气泡转成系统 Toast 时按此身份归档。
        // 不注册时系统会用哈希生成的 NotifyIconGeneratedAumid_* 身份键,
        // 一旦该键在系统通知设置里被禁用,气泡会被静默丢弃。须在创建托盘/窗口前调用。
        const string aumid = "AudioDispatcher";
        var hr = SetCurrentProcessExplicitAppUserModelID(aumid);
        if (hr != 0)
        {
            Logging.AppLog.Warn($"AUMID 注册失败: 0x{hr:X8},托盘气泡将沿用系统生成的身份");
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Logging.AppLog.Error(args.Exception, "未处理的 UI 异常");
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                Logging.AppLog.Error(ex, "未处理的应用域异常");
            }
        };

        // 壳层(托盘 + 主窗口)由 AppShell 管理,不设置 StartupUri。
        Shell = new UI.AppShell();
    }

    [DllImport("shell32.dll", SetLastError = false)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string appID);
}
