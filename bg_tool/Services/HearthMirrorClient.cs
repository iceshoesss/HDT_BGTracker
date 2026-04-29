using System;
using System.Collections.Generic;
using System.Diagnostics;

#nullable enable

namespace BgTool
{

/// <summary>
/// HearthMirror 集成（直接引用，单例 Reflection 实例）
/// 注意：AssemblyResolve 在 Program.Main() 中注册，必须在任何 HearthMirror 代码 JIT 之前
/// </summary>
public static class HearthMirrorClient
{
    private static bool _available;
    private static HearthMirror.Reflection? _reflection;
    private static int _hsProcessId;  // 记录初始化时的炉石进程 ID

    /// <summary>
    /// 检测到炉石重启后为 true，MainForm 需重新获取玩家信息和验证码
    /// </summary>
    public static bool RestartDetected { get; private set; }

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
        if (_available)
        {
            // 检查炉石进程是否还活着，进程变了则重新初始化
            try
            {
                var procs = Process.GetProcessesByName("Hearthstone");
                if (procs.Length == 0)
                {
                    // 炉石已关闭，重置状态
                    Console.WriteLine("[HearthMirror] ⚠️ 炉石进程已退出，重置连接");
                    Reset();
                    return false;
                }
                var currentPid = procs[0].Id;
                if (currentPid != _hsProcessId)
                {
                    // 炉石重启了（进程 ID 变了），重新初始化
                    Console.WriteLine($"[HearthMirror] 🔄 炉石进程已重启（PID {_hsProcessId}→{currentPid}），重新初始化");
                    Reset();
                    RestartDetected = true;
                    // 继续往下走，重新创建 Reflection
                }
                else
                {
                    return true;
                }
            }
            catch
            {
                // 进程检查失败，保守重置
                Reset();
                return false;
            }
        }

        try
        {
            _reflection = new HearthMirror.Reflection();
            _available = true;
            // 记录当前炉石进程 ID
            try
            {
                var procs = Process.GetProcessesByName("Hearthstone");
                if (procs.Length > 0)
                    _hsProcessId = procs[0].Id;
            }
            catch { }
            Console.WriteLine("[HearthMirror] ✅ 已加载，可获取对手 Lo");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HearthMirror] ❌ 初始化失败: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 重置状态（炉石进程退出后调用，下次 TryInit 会重新初始化）
    /// </summary>
    public static void Reset()
    {
        _available = false;
        _reflection = null;
        _hsProcessId = 0;
        LocalPlayerBattleTag = "";
        LocalPlayerLo = 0;
        LastGameUuid = "";
    }

    /// <summary>
    /// 消费重启标记（MainForm 重新获取完玩家信息后调用）
    /// </summary>
    public static void AckRestart()
    {
        RestartDetected = false;
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
                Console.WriteLine("[HearthMirror] ✅ BattleTag=" + LocalPlayerBattleTag);
                return true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HearthMirror] ❌ FetchBattleTag 失败: {ex.GetType().Name}: {ex.Message}");
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
            if (accountId == null) return false;
            try
            {
                var lo = (ulong)accountId.Lo;
                if (lo != 0)
                {
                    LocalPlayerLo = lo;
                    Console.WriteLine("[HearthMirror] ✅ AccountId.Lo=" + LocalPlayerLo);
                    return true;
                }
            }
            catch (Exception)
            {
                Console.WriteLine("[HearthMirror] ⚠️ AccountId.Lo 绑定失败（可能未就绪）");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HearthMirror] ❌ FetchAccountId 失败: {ex.GetType().Name}: {ex.Message}");
        }
        return false;
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
                Console.WriteLine($"[HearthMirror] ✅ BattleTag={LocalPlayerBattleTag}");
                return true;
            }
            // BattleTag 为 null 时尝试 Name fallback
            if (matchInfo?.LocalPlayer?.Name != null)
            {
                LocalPlayerBattleTag = matchInfo.LocalPlayer.Name;
                Console.WriteLine($"[HearthMirror] ✅ BattleTag={LocalPlayerBattleTag}");
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
    /// 优先使用启动时 GetBattleTag() 获取的 LocalPlayerBattleTag。
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
                var hashIdx = tagSource!.IndexOf('#');
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
                    Console.WriteLine($"[HearthMirror] ✅ 本地玩家 Lo={lo}");
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
