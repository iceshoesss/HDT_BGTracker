using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

#nullable enable

namespace BgTool
{

/// <summary>
/// 自动查找炉石 Power.log 路径
/// </summary>
public static class LogPathFinder
{
    /// <summary>
    /// 查找最新的 Power.log，可指定自定义路径
    /// </summary>
    public static string? Find(string? customPath = null)
    {
        if (!string.IsNullOrEmpty(customPath))
        {
            if (File.Exists(customPath)) return customPath;
            Console.WriteLine($"❌ 文件不存在: {customPath}");
            return null;
        }

        // 注册表查找
        var installDir = FindHsInstallDir();
        if (installDir != null)
        {
            var logPath = FindLogInDir(Path.Combine(installDir, "Logs"));
            if (logPath != null) return logPath;
        }

        // Battle.net 配置文件查找
        installDir = FindHsFromBattleNetConfig();
        if (installDir != null)
        {
            var logPath = FindLogInDir(Path.Combine(installDir, "Logs"));
            if (logPath != null) return logPath;
        }

        // HDT_PATH 环境变量（编译时设置，运行时不一定有）
        var hdtPath = Environment.GetEnvironmentVariable("HDT_PATH");
        if (!string.IsNullOrEmpty(hdtPath))
        {
            var hdtLogs = Path.Combine(hdtPath, "Logs");
            if (Directory.Exists(hdtLogs))
            {
                var logPath = FindLogInDir(hdtLogs);
                if (logPath != null) return logPath;
            }
        }

        // 常见路径兜底
        foreach (var logsDir in new[]
        {
            @"D:\Battle.net\Hearthstone\Logs",
            @"C:\Program Files (x86)\Hearthstone\Logs",
            @"C:\Program Files\Hearthstone\Logs",
            @"D:\Hearthstone\Logs",
        })
        {
            var logPath = FindLogInDir(logsDir);
            if (logPath != null) return logPath;
        }

        // 诊断输出
        Console.WriteLine("搜索路径:");
        if (installDir != null)
            Console.WriteLine($"  注册表: {Path.Combine(installDir, "Logs")} {(Directory.Exists(Path.Combine(installDir, "Logs")) ? "（目录存在，无 Power.log）" : "（目录不存在）")}");
        else
            Console.WriteLine("  注册表: 未找到炉石安装路径");
        if (!string.IsNullOrEmpty(hdtPath))
            Console.WriteLine($"  HDT_PATH: {Path.Combine(hdtPath, "Logs")} {(Directory.Exists(Path.Combine(hdtPath, "Logs")) ? "（目录存在，无 Power.log）" : "（目录不存在）")}");
        else
            Console.WriteLine("  HDT_PATH: 未设置");

        return null;
    }

    /// <summary>
    /// 检查是否有更新的日志文件（游戏重启时切换）
    /// </summary>
    public static string? CheckNewLogFile(string currentPath)
    {
        var currentDir = Path.GetDirectoryName(currentPath)!;
        var parent = Path.GetDirectoryName(currentDir);
        var basename = Path.GetFileName(currentDir);
        var logsDir = basename?.StartsWith("Hearthstone_") == true ? parent! : currentDir;

        if (!Directory.Exists(logsDir)) return null;

        var candidates = new List<(DateTime mtime, string path)>();

        // Hearthstone_* 子目录
        foreach (var folder in Directory.GetDirectories(logsDir, "Hearthstone_*"))
        {
            var p = Path.Combine(folder, "Power.log");
            if (File.Exists(p) && !string.Equals(
                Path.GetFullPath(p), Path.GetFullPath(currentPath),
                StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add((File.GetLastWriteTimeUtc(p), p));
            }
        }

        // 根目录 Power.log
        var rootLog = Path.Combine(logsDir, "Power.log");
        if (File.Exists(rootLog) && !string.Equals(
            Path.GetFullPath(rootLog), Path.GetFullPath(currentPath),
            StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add((File.GetLastWriteTimeUtc(rootLog), rootLog));
        }

        if (candidates.Count == 0) return null;

        DateTime currentMtime;
        try { currentMtime = File.GetLastWriteTimeUtc(currentPath); }
        catch { currentMtime = DateTime.MinValue; }

        var newest = candidates.OrderByDescending(c => c.mtime).First();
        return newest.mtime > currentMtime ? newest.path : null;
    }

    private static string? FindHsInstallDir()
    {
        // x86 进程在 64 位 Windows 下访问注册表会被自动重定向到 WOW6432Node，
        // 导致显式指定 WOW6432Node 的路径读不到。需要用 RegistryView.Registry64
        // 强制读 64 位注册表视图。
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(@"SOFTWARE\WOW6432Node\Blizzard Entertainment\Hearthstone");
            var p = key?.GetValue("InstallPath") as string;
            if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) return p;
        }
        catch { }

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(@"SOFTWARE\Blizzard Entertainment\Hearthstone");
            var p = key?.GetValue("InstallPath") as string;
            if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) return p;
        }
        catch { }

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(@"SOFTWARE\Blizzard Entertainment\Hearthstone");
            var p = key?.GetValue("InstallPath") as string;
            if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) return p;
        }
        catch { }

        return null;
    }

    private static string? FindHsFromBattleNetConfig()
    {
        // Battle.net 在 ProgramData 下存储产品配置，从中可以找到游戏安装路径
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var agentDir = Path.Combine(programData, "Battle.net", "Agent");

        if (!Directory.Exists(agentDir)) return null;

        try
        {
            foreach (var file in Directory.GetFiles(agentDir, "*.*", SearchOption.AllDirectories))
            {
                try
                {
                    var content = File.ReadAllText(file);
                    if (content.IndexOf("Hearthstone", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    var match = System.Text.RegularExpressions.Regex.Match(content,
                        @"[A-Z]:\\[^""\s\\]+(?:\\[^""\s\\]+)*\\Hearthstone[^""\s\\]*",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (match.Success && Directory.Exists(match.Value))
                        return match.Value;
                }
                catch { }
            }
        }
        catch { }

        return null;
    }

    private static string? FindLogInDir(string logsDir)
    {
        if (!Directory.Exists(logsDir)) return null;

        var candidates = new List<(DateTime mtime, string path)>();

        foreach (var folder in Directory.GetDirectories(logsDir, "Hearthstone_*"))
        {
            var p = Path.Combine(folder, "Power.log");
            if (File.Exists(p))
                candidates.Add((File.GetLastWriteTimeUtc(p), p));
        }

        var rootLog = Path.Combine(logsDir, "Power.log");
        if (File.Exists(rootLog))
            candidates.Add((File.GetLastWriteTimeUtc(rootLog), rootLog));

        if (candidates.Count == 0) return null;
        return candidates.OrderByDescending(c => c.mtime).First().path;
    }
}
}
