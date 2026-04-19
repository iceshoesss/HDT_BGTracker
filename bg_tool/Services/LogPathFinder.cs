namespace BgTool.Services;

using Microsoft.Win32;

/// <summary>自动查找 Power.log 路径</summary>
public static class LogPathFinder
{
    /// <summary>查找最新的 Power.log</summary>
    public static string? Find(string? customPath = null)
    {
        if (!string.IsNullOrEmpty(customPath))
        {
            if (File.Exists(customPath)) return customPath;
            Console.WriteLine($"❌ 文件不存在: {customPath}");
            return null;
        }

        // 1. 注册表
        var installDir = FindHsInstallDir();
        if (installDir != null)
        {
            var logPath = FindLatestLog(Path.Combine(installDir, "Logs"));
            if (logPath != null) return logPath;
        }

        // 2. 常见路径兜底
        foreach (var logsDir in new[]
        {
            @"D:\Battle.net\Hearthstone\Logs",
            @"C:\Program Files (x86)\Hearthstone\Logs",
            @"C:\Program Files\Hearthstone\Logs",
            @"D:\Hearthstone\Logs",
        })
        {
            var logPath = FindLatestLog(logsDir);
            if (logPath != null) return logPath;
        }

        return null;
    }

    private static string? FindHsInstallDir()
    {
        try
        {
            var paths = new[]
            {
                (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Blizzard Entertainment\Hearthstone"),
                (RegistryHive.LocalMachine, @"SOFTWARE\Blizzard Entertainment\Hearthstone"),
                (RegistryHive.CurrentUser, @"SOFTWARE\Blizzard Entertainment\Hearthstone"),
            };

            foreach (var (hive, path) in paths)
            {
                try
                {
                    using var key = RegistryKey.OpenBaseKey(hive, RegistryView.Default)
                        .OpenSubKey(path);
                    var installPath = key?.GetValue("InstallPath") as string;
                    if (!string.IsNullOrEmpty(installPath) && Directory.Exists(installPath))
                        return installPath;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    private static string? FindLatestLog(string logsDir)
    {
        if (!Directory.Exists(logsDir)) return null;

        var candidates = new List<(DateTime mtime, string path)>();

        // Hearthstone_* 文件夹
        foreach (var folder in Directory.GetDirectories(logsDir, "Hearthstone_*"))
        {
            var powerLog = Path.Combine(folder, "Power.log");
            if (File.Exists(powerLog))
                candidates.Add((File.GetLastWriteTime(powerLog), powerLog));
        }

        // 根目录 Power.log
        var rootLog = Path.Combine(logsDir, "Power.log");
        if (File.Exists(rootLog))
            candidates.Add((File.GetLastWriteTime(rootLog), rootLog));

        if (candidates.Count == 0) return null;
        candidates.Sort((a, b) => b.mtime.CompareTo(a.mtime));
        return candidates[0].path;
    }
}
