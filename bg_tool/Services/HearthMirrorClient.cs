using System;
using System.Collections.Generic;

#nullable enable

namespace BgTool
{

/// <summary>
/// HearthMirror 集成（直接引用，单例 Reflection 实例）
/// 注意：AssemblyResolve 在 Program.Main() 中注册，必须在任何 HearthMirror 代码 JIT 之前
/// </summary>
public static class HearthMirrorClient
{
    private static bool _initAttempted;
    private static bool _available;
    private static HearthMirror.Reflection? _reflection;

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

            // 尝试读取 GameUuid
            try
            {
                var gameUuid = lobby.GameUuid;
                if (!string.IsNullOrEmpty(gameUuid))
                    Console.WriteLine($"[HearthMirror] GameUuid: {gameUuid}");
                else
                    Console.WriteLine("[HearthMirror] GameUuid: (null)");
            }
            catch { Console.WriteLine("[HearthMirror] GameUuid: (不可访问)"); }

            var result = new List<LobbyPlayer>();
            for (int i = 0; i < lobby.Players.Count; i++)
            {
                var p = lobby.Players[i];
                var lo = p.AccountId?.Lo ?? (ulong)0;
                var heroCardId = p.HeroCardId ?? "";
                var heroName = ResolveHeroName(heroCardId);
                result.Add(new LobbyPlayer { Lo = lo, HeroCardId = heroCardId, HeroName = heroName });

                // Lo=0 时醒目标注
                if (lo == 0)
                    Console.WriteLine($"[HearthMirror] ⚠️ Lo=0: index={i}, 英雄={heroName}({heroCardId}) ← 观察此人是否为机器人");
            }
            return result;
        }
        catch (Exception e)
        {
            Console.WriteLine($"[HearthMirror] 读取失败: {e.Message}");
            return new List<LobbyPlayer>();
        }
    }

    private static string ResolveHeroName(string cardId)
    {
        if (string.IsNullOrEmpty(cardId)) return "(未知)";
        return cardId;
    }
}
}
