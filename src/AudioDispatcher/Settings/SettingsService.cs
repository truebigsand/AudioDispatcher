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

    public static void Save(AppSettings settings)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOptions));
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
        foreach (var t in s.Targets)
        {
            if (t.Volume < 0)
            {
                t.Volume = 0;
            }
            if (t.Volume > 1.5)
            {
                t.Volume = 1.5;
            }
        }
    }
}
