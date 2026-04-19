using System;
using System.Collections.Generic;

#nullable enable

namespace BgTool
{

/// <summary>
/// HearthMirror 集成（直接引用，不再反射加载）
/// </summary>
public static class HearthMirrorClient
{
    private static bool _initAttempted;
    private static bool _available;

    public static bool TryInit()
    {
        if (_available) return true;
        if (_initAttempted) return _available;
        _initAttempted = true;

        try
        {
            // 尝试实例化验证 DLL 可用
            var r = new HearthMirror.Reflection();
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
        if (!TryInit())
            return new List<LobbyPlayer>();

        try
        {
            var r = new HearthMirror.Reflection();
            var lobby = r.GetBattlegroundsLobbyInfo();
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
