using System;
using System.Collections.Generic;

namespace AudioDispatcher.Settings;

/// <summary>持久化设置模型,对应 settings.json。</summary>
public sealed class AppSettings
{
    /// <summary>设置版本。2 = 大窗口默认(1080×680),用于一次性迁移旧窗口尺寸。</summary>
    public int Version { get; set; } = 2;

    /// <summary>每设备环形缓冲大小(毫秒),10–500。</summary>
    public int BufferMs { get; set; } = 50;

    /// <summary>开机自启(注册表 Run 键)。</summary>
    public bool StartWithWindows { get; set; }

    public bool MinimizeToTray { get; set; } = true;

    /// <summary>null = 自动检测 CABLE Output。</summary>
    public string? SourceDeviceId { get; set; }

    /// <summary>防环过滤扩展点:追加排除的目标设备名。</summary>
    public List<string> BlockedDeviceNames { get; set; } = new();

    public List<TargetSetting> Targets { get; set; } = new();

    public double? WindowTop { get; set; }
    public double? WindowLeft { get; set; }
    public double WindowWidth { get; set; } = 1080;
    public double WindowHeight { get; set; } = 680;
}

/// <summary>单目标设备的持久化配置(是否参与分发)。音量/静音直接作用于系统端点,不在此持久化。</summary>
public sealed class TargetSetting
{
    public required string DeviceId { get; set; }
    public bool Enabled { get; set; }
}
