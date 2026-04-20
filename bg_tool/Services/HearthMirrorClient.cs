using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

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
    private static bool _debugDumped = false; // 每局只 dump 一次

    // 调试日志路径
    private static string DebugLogPath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "HearthstoneDeckTracker", "BGTracker");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "mirror_debug.log");
        }
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

            // 调试：反射 dump LobbyInfo 所有属性（每局只 dump 一次）
            if (!_debugDumped)
            {
                _debugDumped = true;
                DumpLobbyInfo(lobby);
            }

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

    /// <summary>
    /// 在新局开始时重置 dump 标志
    /// </summary>
    public static void ResetDumpFlag()
    {
        _debugDumped = false;
    }

    /// <summary>
    /// 反射 dump LobbyInfo 对象的所有属性，写入 mirror_debug.log
    /// </summary>
    static void DumpLobbyInfo(object lobby)
    {
        try
        {
            var path = DebugLogPath;
            using var sw = new StreamWriter(path, append: true);
            sw.WriteLine($"========== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==========");
            sw.WriteLine($"[LobbyInfo] Type = {lobby.GetType().FullName}");

            foreach (var prop in lobby.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                try
                {
                    var val = prop.GetValue(lobby);
                    var valStr = val switch
                    {
                        null => "(null)",
                        string s => $"\"{s}\"",
                        System.Collections.IEnumerable e when prop.Name != "GameUuid" => FormatCollection(e),
                        _ => val.ToString()
                    };
                    sw.WriteLine($"  {prop.Name} ({prop.PropertyType.Name}) = {valStr}");
                }
                catch (Exception ex)
                {
                    sw.WriteLine($"  {prop.Name} ({prop.PropertyType.Name}) = [读取失败: {ex.Message}]");
                }
            }

            // 逐个 Player 的详细属性
            try
            {
                var players = lobby.Players;
                if (players != null)
                {
                    sw.WriteLine($"\n  [Players] Count = {players.Count}");
                    for (int i = 0; i < players.Count; i++)
                    {
                        var p = players[i];
                        sw.WriteLine($"\n  --- Player[{i}] Type = {p.GetType().FullName}");
                        foreach (var prop in p.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                        {
                            try
                            {
                                var val = prop.GetValue(p);
                                var valStr = val?.ToString() ?? "(null)";
                                sw.WriteLine($"    {prop.Name} ({prop.PropertyType.Name}) = {valStr}");
                            }
                            catch (Exception ex)
                            {
                                sw.WriteLine($"    {prop.Name} ({prop.PropertyType.Name}) = [读取失败: {ex.Message}]");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                sw.WriteLine($"\n  [Players] 遍历失败: {ex.Message}");
            }

            sw.WriteLine();
            Console.WriteLine($"[HearthMirror] 📝 Debug dump → {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HearthMirror] ⚠️ Dump 失败: {ex.Message}");
        }
    }

    static string FormatCollection(System.Collections.IEnumerable e)
    {
        var items = new List<string>();
        foreach (var item in e)
        {
            items.Add(item?.ToString() ?? "(null)");
            if (items.Count > 20) { items.Add("..."); break; }
        }
        return $"[{string.Join(", ", items)}]";
    }
}
}
