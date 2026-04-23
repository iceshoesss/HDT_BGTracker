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
            Console.WriteLine($"[LogPath] 注册表路径: {installDir}，Logs 下无 Power.log");
        }

        // 常见路径兜底
        var triedPaths = new List<string>();
        var candidateDirs = new List<string>
        {
            @"D:\Battle.net\Hearthstone\Logs",
            @"C:\Program Files (x86)\Hearthstone\Logs",
            @"C:\Program Files\Hearthstone\Logs",
            @"D:\Hearthstone\Logs",
            // 国服常见中文路径
            @"D:\暴雪战网\炉石传说\Hearthstone\Logs",
            @"C:\暴雪战网\炉石传说\Hearthstone\Logs",
            @"E:\暴雪战网\炉石传说\Hearthstone\Logs",
            @"D:\暴雪战网\Hearthstone\Logs",
            @"C:\暴雪战网\Hearthstone\Logs",
            @"E:\暴雪战网\Hearthstone\Logs",
        };

        // 扫描所有盘符下匹配 *Hearthstone* 或 *炉石* 的目录
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
            var root = drive.RootDirectory.FullName;
            foreach (var pattern in new[] { "*Hearthstone*", "*炉石*" })
            {
                try
                {
                    foreach (var dir in Directory.GetDirectories(root, pattern))
                    {
                        var logsPath = Path.Combine(dir, "Logs");
                        if (!candidateDirs.Contains(logsPath, StringComparer.OrdinalIgnoreCase))
                            candidateDirs.Add(logsPath);
                        // 也检查 Battle.net 子目录
                        var bnLogs = Path.Combine(dir, "Hearthstone", "Logs");
                        if (!candidateDirs.Contains(bnLogs, StringComparer.OrdinalIgnoreCase))
                            candidateDirs.Add(bnLogs);
                    }
                }
                catch { } // 跳过无权限的目录
            }
        }

        foreach (var logsDir in candidateDirs)
        {
            var logPath = FindLogInDir(logsDir);
            if (logPath != null) return logPath;
            if (Directory.Exists(logsDir))
                triedPaths.Add($"{logsDir}（存在但无 Power.log）");
            else
                triedPaths.Add($"{logsDir}（不存在）");
        }

        // 首次失败时输出诊断，避免每次重试都刷屏
        if (!_diagnosed)
        {
            _diagnosed = true;
            Console.WriteLine($"[LogPath] ❌ 未找到 Power.log，已检查: 注册表={installDir ?? "无"} | {string.Join(" | ", triedPaths)}");
        }
        return null;
    }

    private static bool _diagnosed;

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
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\WOW6432Node\Blizzard Entertainment\Hearthstone");
            var p = key?.GetValue("InstallPath") as string;
            if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) return p;
        }
        catch { }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Blizzard Entertainment\Hearthstone");
            var p = key?.GetValue("InstallPath") as string;
            if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) return p;
        }
        catch { }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Blizzard Entertainment\Hearthstone");
            var p = key?.GetValue("InstallPath") as string;
            if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) return p;
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
