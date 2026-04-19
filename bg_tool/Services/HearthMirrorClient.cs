using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

#nullable enable

namespace BgTool
{

/// <summary>
/// HearthMirror 集成（直接引用，单例 Reflection 实例）
/// 对齐 Python bg_parser 的 _mirror_reflection 全局单例模式
/// </summary>
public static class HearthMirrorClient
{
    private static bool _initAttempted;
    private static bool _available;
    private static HearthMirror.Reflection? _reflection;

    /// <summary>
    /// 注册 AssemblyResolve，从 HDT 目录加载 HearthMirror 的依赖 DLL
    /// （如 untapped-scry-dotnet.dll）
    /// </summary>
    static HearthMirrorClient()
    {
        AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
        {
            var hdtDir = Environment.GetEnvironmentVariable("HDT_PATH");
            if (string.IsNullOrEmpty(hdtDir)) return null;
            var name = new AssemblyName(args.Name).Name;
            var path = Path.Combine(hdtDir, name + ".dll");
            if (File.Exists(path))
            {
                Console.WriteLine($"[AssemblyResolve] 加载: {path}");
                return Assembly.LoadFrom(path);
            }
            Console.WriteLine($"[AssemblyResolve] 未找到: {path}");
            return null;
        };
    }

    public static bool TryInit()
    {
        if (_available) return true;
        if (_initAttempted) return _available;
        _initAttempted = true;

        try
        {
            _reflection = new HearthMirror.Reflection();
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

    public static List<LobbyPlayer> FetchLobbyPlayers()
    {
        if (!TryInit() || _reflection == null)
            return new List<LobbyPlayer>();

        try
        {
            var lobby = _reflection.GetBattlegroundsLobbyInfo();
            if (lobby?.Players == null || lobby.Players.Count == 0)
                return new List<LobbyPlayer>();

            var result = new List<LobbyPlayer>();
            foreach (var p in lobby.Players)
            {
                var lo = p.AccountId?.Lo ?? (ulong)0;
                var heroCardId = p.HeroCardId ?? "";
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
}
}
