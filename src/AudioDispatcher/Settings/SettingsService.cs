using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AudioDispatcher.Logging;

namespace AudioDispatcher.Settings;

public static class SettingsService
{
    private static readonly string FilePath =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                               "AudioDispatcher", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Path => FilePath;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded != null)
                {
                    Sanitize(loaded);
                    Migrate(loaded);
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "读取设置失败,使用默认设置");
        }
        return NewDefault();
    }

    /// <summary>旧版本迁移:v1 默认窗口 720 宽放不下设备行,升级到 v2 的大窗口默认。</summary>
    private static void Migrate(AppSettings s)
    {
        if (s.Version < 2)
        {
            s.Version = 2;
            if (s.WindowWidth < 960)
            {
                s.WindowWidth = 1080;
            }
            if (s.WindowHeight < 640)
            {
                s.WindowHeight = 680;
            }
            AppLog.Info("设置迁移 v1→v2:窗口默认尺寸调整为 1080×680");
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            // 临时文件 + 原子替换:中途崩溃/断电不会留下截断的 settings.json
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "保存设置失败");
        }
    }

    public static AppSettings NewDefault() => new();

    private static void Sanitize(AppSettings s)
    {
        if (s.BufferMs is < 10 or > 500)
        {
            s.BufferMs = 50;
        }
        s.Targets ??= new();
        s.BlockedDeviceNames ??= new();
        // 空串会命中所有设备名的 Contains,把过滤规则变成"排除一切"
        s.BlockedDeviceNames.RemoveAll(string.IsNullOrWhiteSpace);
    }
}
