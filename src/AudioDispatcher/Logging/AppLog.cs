using System;
using System.IO;
using System.Text;

namespace AudioDispatcher.Logging;

/// <summary>
/// 滚动文件日志:%AppData%\AudioDispatcher\logs\app-yyyyMMdd.log,按天切分,保留 7 天。
/// </summary>
public static class AppLog
{
    private static readonly object Sync = new();
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "AudioDispatcher", "logs");

    private static StreamWriter? _writer;
    private static DateTime _currentDay;

    static AppLog()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            DeleteOld();
        }
        catch
        {
            // 日志不可用时静默降级,不影响主功能。
        }
    }

    public static void Info(string message) => Write("INF", message, null);

    public static void Warn(string message) => Write("WRN", message, null);

    public static void Error(Exception ex, string context) => Write("ERR", context, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            lock (Sync)
            {
                var now = DateTime.Now;
                if (_writer == null || now.Date != _currentDay)
                {
                    _writer?.Dispose();
                    _currentDay = now.Date;
                    _writer = new StreamWriter(Path.Combine(Dir, $"app-{now:yyyyMMdd}.log"),
                                               append: true, new UTF8Encoding(false))
                    { AutoFlush = true };
                    DeleteOld();
                }

                var sb = new StringBuilder();
                sb.Append(now.ToString("HH:mm:ss.fff")).Append(" [").Append(level).Append("] ").Append(message);
                if (ex != null)
                {
                    sb.Append(" :: ").Append(ex);
                }
                _writer.WriteLine(sb.ToString());
            }
        }
        catch
        {
            // 静默。
        }
    }

    private static void DeleteOld()
    {
        try
        {
            var cutoff = DateTime.Today.AddDays(-7);
            foreach (var f in Directory.EnumerateFiles(Dir, "app-*.log"))
            {
                if (File.GetLastWriteTime(f) < cutoff)
                {
                    File.Delete(f);
                }
            }
        }
        catch
        {
            // 静默。
        }
    }
}
