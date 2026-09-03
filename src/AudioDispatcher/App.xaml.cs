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
