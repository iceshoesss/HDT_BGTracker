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

                // 诊断：Lo=0 时输出更多信息
                if (lo == 0)
                {
                    var accountIdNull = p.AccountId == null;
                    var hi = p.AccountId?.Hi ?? 0;
                    var name = p.Name ?? "(null)";
                    Console.WriteLine($"[HearthMirror] ⚠️ Lo=0 诊断: index={i}, name=\"{name}\", AccountId is null={accountIdNull}, Hi={hi}, Hero={heroCardId}");
                }

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
