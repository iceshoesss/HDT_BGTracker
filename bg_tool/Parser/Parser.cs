using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace BgTool;

/// <summary>
/// 状态机解析器 — 逐行解析 Power.log
/// </summary>
public class Parser
{
    public Game Game { get; private set; } = new();
    public List<Game> Games { get; } = new();

    // CREATE_GAME 块状态
    private bool _inCreateBlock;
    private bool _createHasTurn;
    private Game? _pendingNewGame;

    // 投降检测
    private bool _concedePending;
    private string _concedeTag = "";

    // ═══════════════════════════════════════
    //  英雄卡牌过滤
    // ═══════════════════════════════════════

    private static readonly string[] HeroPrefixes =
    {
        "TB_BaconShop_HERO_",
        "BG20_HERO_", "BG21_HERO_", "BG22_HERO_", "BG23_HERO_",
        "BG24_HERO_", "BG25_HERO_", "BG26_HERO_", "BG27_HERO_",
        "BG28_HERO_", "BG29_HERO_", "BG30_HERO_", "BG31_HERO_",
        "BG32_HERO_", "BG33_HERO_", "BG34_HERO_", "BG35_HERO_",
    };

    private static readonly HashSet<string> HeroExclude = new()
    {
        "TB_BaconShop_HERO_PH"
    };

    public static bool IsHeroCard(string cardId)
    {
        if (HeroExclude.Contains(cardId)) return false;
        foreach (var prefix in HeroPrefixes)
        {
            if (!cardId.StartsWith(prefix)) continue;
            var suffix = cardId[prefix.Length..];
            return int.TryParse(suffix, out _);
        }
        return false;
    }

    // ═══════════════════════════════════════
    //  正则表达式（预编译）
    // ═══════════════════════════════════════

    private static readonly Regex ReCreateGame =
        new(@"GameState\.DebugPrintPower\(\) - CREATE_GAME$", RegexOptions.Compiled);

    private static readonly Regex ReGameType =
        new(@"GameType=(\w+)", RegexOptions.Compiled);

    private static readonly Regex ReGameSeed =
        new(@"tag=GAME_SEED value=(\d+)", RegexOptions.Compiled);

    private static readonly Regex RePlayerName =
        new(@"PlayerID=(\d+),\s*PlayerName=(.+?)$", RegexOptions.Compiled);

    private static readonly Regex ReAccountId =
        new(@"GameAccountId=\[hi=\d+ lo=(\d+)\]", RegexOptions.Compiled);

    private static readonly Regex ReHeroEntity =
        new(@"TAG_CHANGE Entity=(.+?) tag=HERO_ENTITY value=(\d+)", RegexOptions.Compiled);

    private static readonly Regex ReFullEntity =
        new(@"FULL_ENTITY - (?:Creating|Updating)\s+\[?entityName=(.+?)\s+id=(\d+)\s+zone=\w+(?:\s+zonePos=\d+)?"
            + @".*?cardId=(\w+).*?player=(\d+)\]?", RegexOptions.Compiled);

    private static readonly Regex ReLbEntity =
        new(@"TAG_CHANGE Entity=\[entityName=(.+?) id=(\d+) zone=\w+(?:\s+zonePos=\d+)?"
            + @".*?cardId=(\w+).*?player=(\d+)\]\s+tag=PLAYER_LEADERBOARD_PLACE value=(\d+)",
            RegexOptions.Compiled);

    private static readonly Regex ReLbTag =
        new(@"TAG_CHANGE Entity=(.+?) tag=PLAYER_LEADERBOARD_PLACE value=(\d+)\s*$", RegexOptions.Compiled);

    private static readonly Regex ReStep =
        new(@"TAG_CHANGE Entity=GameEntity tag=STEP value=(\w+)", RegexOptions.Compiled);

    private static readonly Regex ReConcedePlayerTag =
        new(@"TAG_CHANGE Entity=.+? tag=(3479|4356) value=1", RegexOptions.Compiled);

    private static readonly Regex ReConcedeGameTag =
        new(@"TAG_CHANGE Entity=GameEntity tag=4302 value=1", RegexOptions.Compiled);

    private static readonly Regex ReGameStateComplete =
        new(@"TAG_CHANGE Entity=GameEntity tag=STATE value=COMPLETE", RegexOptions.Compiled);

    // ═══════════════════════════════════════
    //  核心方法
    // ═══════════════════════════════════════

    /// <summary>
    /// 处理一行日志，返回事件类型或 null。
    /// 事件: game_start / reconnect / player_info / account_info /
    ///       hero_entity / hero_found / phase_change / concede / game_end / not_bg
    /// </summary>
    public string? ProcessLine(string line)
    {
        // ── CREATE_GAME ──
        if (ReCreateGame.IsMatch(line))
        {
            _inCreateBlock = true;
            _createHasTurn = false;
            _pendingNewGame = Game.IsActive ? Game : null;
            ResetGame();
            return null;
        }

        // ── CREATE_GAME 块内 ──
        if (_inCreateBlock)
        {
            if (line.Contains("tag=TURN value="))
            {
                // 断线重连！回滚到旧局
                _createHasTurn = true;
                if (_pendingNewGame != null)
                {
                    if (Games.Count > 0 && Games[^1] == Game)
                        Games.RemoveAt(Games.Count - 1);
                    Game = _pendingNewGame;
                    Game.Reconnected = true;
                    _pendingNewGame = null;
                }
                return null;
            }

            if (line.Contains("GameAccountId="))
            {
                var m = ReAccountId.Match(line);
                if (m.Success)
                {
                    var lo = ulong.Parse(m.Groups[1].Value);
                    if (lo != 0 && Game.AccountIdLo == 0)
                        Game.AccountIdLo = lo;
                }
            }

            if (line.Contains("GAME_SEED"))
            {
                var m = ReGameSeed.Match(line);
                if (m.Success)
                    Game.GameSeed = long.Parse(m.Groups[1].Value);
            }

            if (line.Contains("PowerTaskList.DebugDump()"))
            {
                _inCreateBlock = false;
                if (!_createHasTurn && _pendingNewGame != null)
                {
                    var old = _pendingNewGame;
                    old.IsActive = false;
                    old.EndTime = DateTime.Now.ToString("HH:mm:ss");
                    Games.Add(old);
                    _pendingNewGame = null;
                }
                return _createHasTurn ? "reconnect" : "game_start";
            }

            // 块内其他行跳过（等待块结束）
            if (!line.Contains("PowerTaskList.DebugPrintPower()"))
                return null;
        }

        // ── 只处理 GameState + PowerTaskList ──
        if (!line.Contains("GameState.") && !line.Contains("PowerTaskList.DebugPrintPower()"))
            return null;

        if (!Game.IsActive)
            return null;

        // ── PowerTaskList：只处理 FULL_ENTITY ──
        if (line.Contains("PowerTaskList."))
            return HandlePowerTaskList(line);

        return HandleGameState(line);
    }

    // ═══════════════════════════════════════
    //  GameState 处理
    // ═══════════════════════════════════════

    private string? HandleGameState(string line)
    {
        // GameType
        var m = ReGameType.Match(line);
        if (m.Success && line.Contains("DebugPrintGame()"))
        {
            if (m.Groups[1].Value != "GT_BATTLEGROUNDS")
            {
                EndGame();
                return "not_bg";
            }
            return null;
        }

        // PlayerName
        m = RePlayerName.Match(line);
        if (m.Success && line.Contains("DebugPrintGame()") && string.IsNullOrEmpty(Game.PlayerTag))
        {
            var name = m.Groups[2].Value.Trim();
            if (name is "古怪之德鲁伊" or "惊魂之武僧")
                return null;
            Game.PlayerTag = name;
            Game.PlayerDisplayName = name.Contains('#')
                ? name[..name.LastIndexOf('#')]
                : name;
            return "player_info";
        }

        // HERO_ENTITY（本地玩家选英雄）
        m = ReHeroEntity.Match(line);
        if (m.Success)
        {
            var entityName = m.Groups[1].Value.Trim();
            var heroEntityId = int.Parse(m.Groups[2].Value);
            if (entityName == Game.PlayerTag)
            {
                Game.HeroEntityId = heroEntityId;
                var hero = FindHeroByEntity(heroEntityId);
                if (hero != null)
                {
                    Game.HeroName = hero.HeroName;
                    Game.HeroCardId = hero.CardId;
                }
                return "hero_entity";
            }
        }

        // FULL_ENTITY（GameState 中的）
        m = ReFullEntity.Match(line);
        if (m.Success)
        {
            var heroName = m.Groups[1].Value;
            var entityId = int.Parse(m.Groups[2].Value);
            var cardId = m.Groups[3].Value;
            var playerSlot = int.Parse(m.Groups[4].Value);

            if (IsHeroCard(cardId))
            {
                var key = (cardId, playerSlot);
                if (!Game.AllHeroes.ContainsKey(key))
                {
                    var hero = new Hero
                    {
                        EntityId = entityId,
                        HeroName = heroName,
                        CardId = cardId,
                        PlayerSlot = playerSlot,
                    };
                    Game.AllHeroes[key] = hero;
                    if (entityId == Game.HeroEntityId)
                    {
                        Game.HeroName = heroName;
                        Game.HeroCardId = cardId;
                    }
                }
                else if (entityId == Game.HeroEntityId && string.IsNullOrEmpty(Game.HeroName))
                {
                    var existing = Game.AllHeroes[key];
                    Game.HeroName = existing.HeroName;
                    Game.HeroCardId = existing.CardId;
                }
            }
            return null;
        }

        // STEP
        m = ReStep.Match(line);
        if (m.Success)
        {
            var step = m.Groups[1].Value;
            if (step is "MAIN_READY" or "MAIN_ACTION")
                return "phase_change";
            if (step == "MAIN_CLEANUP")
            {
                Game.LobbyPlayers = HearthMirrorClient.FetchLobbyPlayers();
                if (Game.LobbyPlayers.Count > 0)
                {
                    Console.WriteLine($"[HearthMirror] 📋 获取到 {Game.LobbyPlayers.Count} 个玩家");
                    foreach (var lp in Game.LobbyPlayers)
                        Console.WriteLine($"   Lo={lp.Lo}, Hero={lp.HeroCardId}");
                }
                return "phase_change";
            }
            return null;
        }

        // LEADERBOARD_PLACE（追踪本地英雄排名变化）
        m = ReLbEntity.Match(line);
        if (m.Success)
        {
            var cardId = m.Groups[3].Value;
            var placement = int.Parse(m.Groups[5].Value);

            if (IsHeroCard(cardId) && !string.IsNullOrEmpty(Game.HeroCardId) && cardId == Game.HeroCardId)
                Game.HeroPlacement = placement;

            // 投降检测
            if (_concedePending && placement == 8)
            {
                Game.Conceded = true;
                Game.PlacementConfirmed = true;
                Game.HeroPlacement = 8;
                EndGame();
                return "concede";
            }
            return null;
        }

        // LEADERBOARD_PLACE 简写格式（仅 BattleTag）
        m = ReLbTag.Match(line);
        if (m.Success)
        {
            var tag = m.Groups[1].Value.Trim();
            var placement = int.Parse(m.Groups[2].Value);

            if (tag == Game.PlayerTag)
                Game.HeroPlacement = placement;

            if (_concedePending && placement == 8)
            {
                Game.Conceded = true;
                Game.PlacementConfirmed = true;
                Game.HeroPlacement = 8;
                EndGame();
                return "concede";
            }
            return null;
        }

        // 投降信号前兆：tag=3479/4356
        if (ReConcedePlayerTag.IsMatch(line))
        {
            var tagMatch = Regex.Match(line, @"Entity=(.+?) tag=(?:3479|4356)");
            if (tagMatch.Success)
                _concedeTag = tagMatch.Groups[1].Value.Trim();
            _concedePending = true;
            return null;
        }

        // 投降信号：tag=4302
        if (_concedePending && ReConcedeGameTag.IsMatch(line))
            return null;

        // 游戏结束：STATE=COMPLETE
        if (ReGameStateComplete.IsMatch(line))
        {
            if (!Game.Conceded && Game.HeroPlacement > 0)
                Game.PlacementConfirmed = true;
            EndGame();
            return "game_end";
        }

        return null;
    }

    // ═══════════════════════════════════════
    //  PowerTaskList 处理
    // ═══════════════════════════════════════

    private string? HandlePowerTaskList(string line)
    {
        var m = ReFullEntity.Match(line);
        if (!m.Success) return null;

        var heroName = m.Groups[1].Value;
        var entityId = int.Parse(m.Groups[2].Value);
        var cardId = m.Groups[3].Value;
        var playerSlot = int.Parse(m.Groups[4].Value);

        if (!IsHeroCard(cardId)) return null;

        var key = (cardId, playerSlot);
        if (Game.AllHeroes.ContainsKey(key)) return null;

        var hero = new Hero
        {
            EntityId = entityId,
            HeroName = heroName,
            CardId = cardId,
            PlayerSlot = playerSlot,
        };
        Game.AllHeroes[key] = hero;

        if (entityId == Game.HeroEntityId)
        {
            Game.HeroName = heroName;
            Game.HeroCardId = cardId;
        }
        return "hero_found";
    }

    // ═══════════════════════════════════════
    //  辅助方法
    // ═══════════════════════════════════════

    private void ResetGame()
    {
        Game = new Game
        {
            IsActive = true,
            StartTime = DateTime.Now.ToString("HH:mm:ss"),
        };
        _concedePending = false;
        _concedeTag = "";
    }

    private void EndGame()
    {
        if (!Game.IsActive) return;
        Game.IsActive = false;
        Game.EndTime = DateTime.Now.ToString("HH:mm:ss");
        Games.Add(Game);
    }

    private Hero? FindHeroByEntity(int entityId)
    {
        foreach (var hero in Game.AllHeroes.Values)
        {
            if (hero.EntityId == entityId)
                return hero;
        }
        return null;
    }
}
