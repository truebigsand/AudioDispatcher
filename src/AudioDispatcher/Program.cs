using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace AudioDispatcher;

/// <summary>自定义入口:单实例检查后启动 WPF 应用。</summary>
public static class Program
{
    private const string MutexName = "AudioDispatcher_SingleInstance";
    private const string ShowEventName = "AudioDispatcher_ShowRequest";

    [STAThread]
    public static int Main(string[] args)
    {
        using var mutex = new Mutex(true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            // 第二实例:通知已运行实例显示主窗口后退出。
            try
            {
                using var evt = EventWaitHandle.OpenExisting(ShowEventName);
                evt.Set();
            }
            catch (Exception)
            {
                // 主实例可能尚未创建事件句柄,忽略。
            }
            return 0;
        }

        var showRequested = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _ = Task.Run(() =>
        {
            while (true)
            {
                try
                {
                    showRequested.WaitOne();
                }
                catch (AbandonedMutexException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    if (Application.Current is App)
                    {
                        App.Shell?.ShowMainWindow();
                    }
                });
            }
        });

        var app = new App();
        return app.Run();
    }
}
