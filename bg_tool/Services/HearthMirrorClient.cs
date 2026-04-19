using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace BgTool;

/// <summary>
/// HearthMirror 集成（可选）。通过反射加载 HearthMirror.dll，
/// 不硬编译依赖，找不到 DLL 时静默降级。
/// </summary>
public static class HearthMirrorClient
{
    private static object? _reflection;
    private static bool _initAttempted;
    private static bool _available;

    /// <summary>
    /// 尝试初始化，成功返回 true
    /// </summary>
    public static bool TryInit()
    {
        if (_reflection != null) return true;
        if (_initAttempted) return _available;
        _initAttempted = true;

        try
        {
            // 查找 HearthMirror.dll
            string? hmPath = FindDll();
            if (hmPath == null)
            {
                Console.WriteLine("[HearthMirror] ⚠️ 未找到 HearthMirror.dll");
                return false;
            }

            // 加载并实例化 Reflection
            var asm = Assembly.LoadFrom(hmPath);
            var type = asm.GetType("HearthMirror.Reflection");
            if (type == null)
            {
                Console.WriteLine("[HearthMirror] ⚠️ 未找到 Reflection 类");
                return false;
            }
            _reflection = Activator.CreateInstance(type)!;
            _available = true;
            Console.WriteLine("[HearthMirror] ✅ 已加载，可获取对手 Lo");
            return true;
        }
        catch (Exception e)
        {
            Console.WriteLine($"[HearthMirror] ⚠️ 不可用: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// 获取大厅 8 个玩家的 Lo + HeroCardId
    /// </summary>
    public static List<LobbyPlayer> FetchLobbyPlayers()
    {
        if (_reflection == null && !TryInit())
            return new List<LobbyPlayer>();

        try
        {
            // reflection.GetBattlegroundsLobbyInfo().Players
            var lobby = _reflection!.GetType()
                .GetMethod("GetBattlegroundsLobbyInfo")!
                .Invoke(_reflection, null);
            if (lobby == null) return new List<LobbyPlayer>();

            var players = lobby.GetType().GetProperty("Players")?.GetValue(lobby);
            if (players == null) return new List<LobbyPlayer>();

            var result = new List<LobbyPlayer>();
            foreach (var p in (System.Collections.IEnumerable)players)
            {
                var accountId = p.GetType().GetProperty("AccountId")?.GetValue(p);
                ulong lo = 0;
                if (accountId != null)
                    lo = Convert.ToUInt64(accountId.GetType().GetProperty("Lo")?.GetValue(accountId) ?? 0);

                var heroCardId = p.GetType().GetProperty("HeroCardId")?.GetValue(p)?.ToString() ?? "";

                result.Add(new LobbyPlayer { Lo = lo, HeroCardId = heroCardId });
            }
            return result;
        }
        catch (Exception e)
        {
            Console.WriteLine($"[HearthMirror] 读取失败: {e.Message}");
            return new List<LobbyPlayer>();
        }
    }

    private static string? FindDll()
    {
        // 环境变量 HDT_PATH
        var hdtDir = Environment.GetEnvironmentVariable("HDT_PATH");
        if (!string.IsNullOrEmpty(hdtDir))
        {
            var p = Path.Combine(hdtDir, "HearthMirror.dll");
            if (File.Exists(p)) return p;
        }

        // 同级 / 上级目录
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "HearthMirror.dll"),
            Path.Combine(baseDir, "..", "HearthMirror.dll"),
            Path.Combine(baseDir, "..", "..", "HearthMirror.dll"),
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c)) return Path.GetFullPath(c);
        }
        return null;
    }
}
