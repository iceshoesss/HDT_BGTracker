using System;
using System.Collections.Generic;
using System.Linq;

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

    /// <summary>
    /// 最后一次获取到的 GameUuid
    /// </summary>
    public static string LastGameUuid { get; private set; } = "";

    /// <summary>
    /// 从 MatchInfo 获取的本地玩家完整 BattleTag（含 #tag）
    /// </summary>
    public static string LocalPlayerBattleTag { get; private set; } = "";

    /// <summary>
    /// 从 LobbyInfo 中通过名字匹配获取的本地玩家 AccountId.Lo
    /// </summary>
    public static ulong LocalPlayerLo { get; private set; }

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

    /// <summary>
    /// 从 HearthMirror 获取本地玩家 BattleTag（主菜单即可调用，无需进入对局）
    /// </summary>
    public static bool FetchBattleTag()
    {
        if (!TryInit() || _reflection == null) return false;
        try
        {
            var bt = _reflection.GetBattleTag();
            if (bt != null)
            {
                LocalPlayerBattleTag = bt.Name + "#" + bt.Number;
                Console.WriteLine("[HearthMirror] ✅ BattleTag=" + LocalPlayerBattleTag + "（GetBattleTag）");
                return true;
            }
        }
        catch (Exception e)
        {
            Console.WriteLine("[HearthMirror] GetBattleTag 失败: " + e.Message);
        }
        return false;
    }

    /// <summary>
    /// 从 HearthMirror 获取本地玩家 AccountId.Lo（主菜单即可调用，无需进入对局）
    /// </summary>
    public static bool FetchAccountId()
    {
        if (!TryInit() || _reflection == null) return false;
        try
        {
            var accountId = _reflection.GetAccountId();
            if (accountId != null && accountId.Lo != 0)
            {
                LocalPlayerLo = accountId.Lo;
                Console.WriteLine("[HearthMirror] ✅ AccountId.Lo=" + LocalPlayerLo + "（GetAccountId）");
                return true;
            }
        }
        catch (Exception e)
        {
            Console.WriteLine("[HearthMirror] GetAccountId 失败: " + e.Message);
        }
        return false;
    }

    /// <summary>
    /// 诊断：尝试从 HearthMirror 读取所有可用数据（主菜单/对局中均可调用）
    /// 用于测试主菜单时能否获取 BattleTag + AccountId.Lo
    /// </summary>
    public static void Diagnose()
    {
        if (!TryInit() || _reflection == null)
        {
            Console.WriteLine("[诊断] HearthMirror 不可用");
            return;
        }

        Console.WriteLine("═══ HearthMirror 诊断开始 ═══");

        // 1. MatchInfo
        try
        {
            var matchInfo = _reflection.GetMatchInfo();
            if (matchInfo != null)
            {
                Console.WriteLine("[诊断] MatchInfo: 非空");
                try { var n = matchInfo.LocalPlayer?.Name; Console.WriteLine("[诊断]   LocalPlayer.Name = " + (n ?? "(null)")); } catch { }
                try
                {
                    var bt = matchInfo.LocalPlayer?.BattleTag;
                    Console.WriteLine("[诊断]   LocalPlayer.BattleTag = " + (bt != null ? bt.Name + "#" + bt.Number : "(null)"));
                }
                catch { }
                try { var h = matchInfo.LocalPlayer?.AccountId?.Hi; Console.WriteLine("[诊断]   LocalPlayer.AccountId.Hi = " + (h ?? 0)); } catch { }
                try { var l = matchInfo.LocalPlayer?.AccountId?.Lo; Console.WriteLine("[诊断]   LocalPlayer.AccountId.Lo = " + (l ?? 0)); } catch { }
            }
            else
            {
                Console.WriteLine("[诊断] MatchInfo: null（不在对局中？）");
            }
        }
        catch (Exception e)
        {
            Console.WriteLine("[诊断] MatchInfo 异常: " + e.Message);
        }

        // 2. BattlegroundsLobbyInfo
        try
        {
            var lobby = _reflection.GetBattlegroundsLobbyInfo();
            if (lobby != null)
            {
                var cnt = lobby.Players?.Count ?? 0;
                Console.WriteLine("[诊断] LobbyInfo: 非空, Players=" + cnt);
                try { var g = lobby.GameUuid; Console.WriteLine("[诊断]   GameUuid = " + (g ?? "(null)")); } catch { }
            }
            else
            {
                Console.WriteLine("[诊断] LobbyInfo: null（不在对局中？）");
            }
        }
        catch (Exception e)
        {
            Console.WriteLine("[诊断] LobbyInfo 异常: " + e.Message);
        }

        // 3. 尝试反射列出 Reflection 类的所有公共方法
        try
        {
            var methods = typeof(HearthMirror.Reflection).GetMethods(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            Console.WriteLine("[诊断] Reflection 公共方法 (" + methods.Length + " 个):");
            foreach (var m in methods)
            {
                var parms = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name));
                Console.WriteLine("[诊断]   " + m.ReturnType.Name + " " + m.Name + "(" + parms + ")");
            }
        }
        catch (Exception e)
        {
            Console.WriteLine("[诊断] 反射列出方法失败: " + e.Message);
        }

        Console.WriteLine("═══ HearthMirror 诊断结束 ═══");
    }

    /// <summary>
    /// 从 MatchInfo 获取本地玩家完整 BattleTag（Name#Number）
    /// </summary>
    public static bool FetchMatchInfo()
    {
        if (!TryInit() || _reflection == null)
            return false;

        try
        {
            var matchInfo = _reflection.GetMatchInfo();
            if (matchInfo?.LocalPlayer?.BattleTag != null)
            {
                var bt = matchInfo.LocalPlayer.BattleTag;
                LocalPlayerBattleTag = $"{bt.Name}#{bt.Number}";
                Console.WriteLine($"[HearthMirror] ✅ BattleTag={LocalPlayerBattleTag}（MatchInfo）");
                return true;
            }
            // BattleTag 为 null 时尝试 Name fallback
            if (matchInfo?.LocalPlayer?.Name != null)
            {
                LocalPlayerBattleTag = matchInfo.LocalPlayer.Name;
                Console.WriteLine($"[HearthMirror] ✅ BattleTag={LocalPlayerBattleTag}（MatchInfo.Name fallback）");
                return true;
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"[HearthMirror] MatchInfo 读取失败: {e.Message}");
        }
        return false;
    }

    /// <summary>
    /// 获取大厅玩家列表 + 本地玩家的 AccountId.Lo
    /// </summary>
    /// <param name="localPlayerName">
    /// 本地玩家 BattleTag（含 #tag），用于在 LobbyInfo 中名字匹配。
    /// 优先使用 FetchMatchInfo() 获取的 LocalPlayerBattleTag。
    /// </param>
    public static List<LobbyPlayer> FetchLobbyPlayers(string? localPlayerName = null)
    {
        LocalPlayerLo = 0;

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
                    LastGameUuid = gameUuid;
            }
            catch { }

            // 提取本地玩家显示名：优先用 MatchInfo 的 BattleTag，fallback 用参数
            string? localDisplayName = null;
            var tagSource = !string.IsNullOrEmpty(LocalPlayerBattleTag)
                ? LocalPlayerBattleTag
                : localPlayerName;

            if (!string.IsNullOrEmpty(tagSource))
            {
                var hashIdx = tagSource.IndexOf('#');
                localDisplayName = hashIdx > 0
                    ? tagSource.Substring(0, hashIdx)
                    : tagSource;
            }

            var result = new List<LobbyPlayer>();
            for (int i = 0; i < lobby.Players.Count; i++)
            {
                var p = lobby.Players[i];
                var lo = p.AccountId?.Lo ?? (ulong)0;
                var heroCardId = p.HeroCardId ?? "";
                var heroName = HeroNameResolver.Resolve(heroCardId);

                // 通过名字匹配识别本地玩家（和 HDT 插件 GetAccountIdLo 同逻辑）
                if (LocalPlayerLo == 0 && lo != 0
                    && !string.IsNullOrEmpty(localDisplayName)
                    && p.Name == localDisplayName)
                {
                    LocalPlayerLo = lo;
                    Console.WriteLine($"[HearthMirror] ✅ 本地玩家 Lo={lo}（名字匹配: {p.Name}）");
                }

                result.Add(new LobbyPlayer { Lo = lo, HeroCardId = heroCardId, HeroName = heroName });
            }

            // 未匹配到时输出诊断
            if (LocalPlayerLo == 0 && !string.IsNullOrEmpty(localDisplayName))
            {
                Console.WriteLine($"[HearthMirror] ⚠️ 未通过名字匹配到本地玩家（期望: {localDisplayName}）");
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
