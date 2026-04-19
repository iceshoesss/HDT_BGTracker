namespace BgTool.Services;

using Microsoft.Win32;

/// <summary>自动查找 Power.log 路径</summary>
public static class LogPathFinder
{
    public static string? Find(string? customPath = null)
    {
        if (!string.IsNullOrEmpty(customPath))
        {
            if (File.Exists(customPath)) return customPath;
            Console.WriteLine($"❌ 文件不存在: {customPath}");
            return null;
        }

        // 注册表
        var installDir = FindHsInstallDir();
        if (installDir != null)
        {
            var logPath = FindLatestLog(Path.Combine(installDir, "Logs"));
            if (logPath != null) return logPath;
        }

        // 常见路径兜底
        foreach (var dir in new[]
        {
            @"D:\Battle.net\Hearthstone\Logs",
            @"C:\Program Files (x86)\Hearthstone\Logs",
            @"C:\Program Files\Hearthstone\Logs",
            @"D:\Hearthstone\Logs",
        })
        {
            var logPath = FindLatestLog(dir);
            if (logPath != null) return logPath;
        }
        return null;
    }

    static string? FindHsInstallDir()
    {
        try
        {
            foreach (var (hive, path) in new[]
            {
                (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Blizzard Entertainment\Hearthstone"),
                (RegistryHive.LocalMachine, @"SOFTWARE\Blizzard Entertainment\Hearthstone"),
                (RegistryHive.CurrentUser, @"SOFTWARE\Blizzard Entertainment\Hearthstone"),
            })
            {
                try
                {
                    using var key = RegistryKey.OpenBaseKey(hive, RegistryView.Default).OpenSubKey(path);
                    var p = key?.GetValue("InstallPath") as string;
                    if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) return p;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    static string? FindLatestLog(string logsDir)
    {
        if (!Directory.Exists(logsDir)) return null;
        var candidates = new List<(DateTime mtime, string path)>();

        foreach (var folder in Directory.GetDirectories(logsDir, "Hearthstone_*"))
        {
            var p = Path.Combine(folder, "Power.log");
            if (File.Exists(p))
                candidates.Add((File.GetLastWriteTime(p), p));
        }

        var root = Path.Combine(logsDir, "Power.log");
        if (File.Exists(root))
            candidates.Add((File.GetLastWriteTime(root), root));

        if (candidates.Count == 0) return null;
        candidates.Sort((a, b) => b.mtime.CompareTo(a.mtime));
        return candidates[0].path;
    }
}
