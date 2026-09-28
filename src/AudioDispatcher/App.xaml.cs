using System;
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

        // 注册稳定通知身份(AUMID + 开始菜单快捷方式):Win11 把托盘气泡转成系统
        // Toast 时按 AUMID 归档并解析显示信息;解析不到身份的通知会被记录但不显示。
        // 须在创建托盘/窗口前调用。
        ToastAppIdentity.Register();

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
}
