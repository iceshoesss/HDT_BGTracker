using System.Text.RegularExpressions;
using BgTool.Models;
using BgTool.Services;

namespace BgTool.Parser;

/// <summary>Power.log 正则状态机解析器（从 Python bg_parser 移植）</summary>
public class Parser
{
    // ── 正则（预编译，与 Python 完全一致）────────────────
    static readonly Regex ReCreateGame = new(@"GameState\.DebugPrintPower\(\) - CREATE_GAME$", RegexOptions.Compiled);
    static readonly Regex ReGameType = new(@"GameType=(\w+)", RegexOptions.Compiled);
    static readonly Regex ReGameSeed = new(@"tag=GAME_SEED value=(\d+)", RegexOptions.Compiled);
    static readonly Regex RePlayerName = new(@"PlayerID=(\d+),\s*PlayerName=(.+?)$", RegexOptions.Compiled);
    static readonly Regex ReAccountId = new(@"GameAccountId=\[hi=\d+ lo=(\d+)\]", RegexOptions.Compiled);
    static readonly Regex ReHeroEntity = new(@"TAG_CHANGE Entity=(.+?) tag=HERO_ENTITY value=(\d+)", RegexOptions.Compiled);
    static readonly Regex ReFullEntity = new(
        @"FULL_ENTITY - (?:Creating|Updating)\s+\[?entityName=(.+?)\s+id=(\d+)\s+zone=\w+(?:\s+zonePos=\d+)?.*?cardId=(\w+).*?player=(\d+)\]?",
        RegexOptions.Compiled);
    static readonly Regex ReLbEntity = new(
        @"TAG_CHANGE Entity=\[entityName=(.+?) id=(\d+) zone=\w+(?:\s+zonePos=\d+)?.*?cardId=(\w+).*?player=(\d+)\]\s+tag=PLAYER_LEADERBOARD_PLACE value=(\d+)",
        RegexOptions.Compiled);
    static readonly Regex ReLbTag = new(@"TAG_CHANGE Entity=(.+?) tag=PLAYER_LEADERBOARD_PLACE value=(\d+)\s*$", RegexOptions.Compiled);
    static readonly Regex ReStep = new(@"TAG_CHANGE Entity=GameEntity tag=STEP value=(\w+)", RegexOptions.Compiled);
    static readonly Regex ReConcedePlayerTag = new(@"TAG_CHANGE Entity=.+? tag=(3479|4356) value=1", RegexOptions.Compiled);
    static readonly Regex ReConcedeGameTag = new(@"TAG_CHANGE Entity=GameEntity tag=4302 value=1", RegexOptions.Compiled);
    static readonly Regex ReGameStateComplete = new(@"TAG_CHANGE Entity=GameEntity tag=STATE value=COMPLETE", RegexOptions.Compiled);

    // ── 英雄卡牌前缀 ────────────────────────────────────
    static readonly string[] HeroPrefixes =
    {
        "TB_BaconShop_HERO_",
        "BG20_HERO_", "BG21_HERO_", "BG22_HERO_", "BG23_HERO_",
        "BG24_HERO_", "BG25_HERO_", "BG26_HERO_", "BG27_HERO_",
        "BG28_HERO_", "BG29_HERO_", "BG30_HERO_", "BG31_HERO_",
        "BG32_HERO_", "BG33_HERO_", "BG34_HERO_", "BG35_HERO_",
    };
    static readonly HashSet<string> HeroExclude = new() { "TB_BaconShop_HERO_PH" };

    // ── 公开状态 ────────────────────────────────────────
    public Game Game { get; private set; } = new();
    public List<Game> Games { get; } = [];

    // ── 私有状态 ────────────────────────────────────────
    bool _inCreateBlock;
    bool _createHasTurn;
    Game? _pendingNewGame;
    bool _concedePending;
    string _concedeTag = "";

    // ── 公开方法 ────────────────────────────────────────

    /// <summary>处理一行日志，返回事件类型或 null</summary>
    /// <remarks>
    /// 事件: game_start / reconnect / player_info / hero_entity /
    ///        hero_found / phase_change / concede / game_end / not_bg
    /// </remarks>
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
                // 断线重连：回滚到旧局
                _createHasTurn = true;
                if (_pendingNewGame != null)
                {
                    Games.Remove(Game);
                    Game = _pendingNewGame;
                    Game.Reconnected = true;
                    _pendingNewGame = null;
                }
                return null;
            }

            if (line.Contains("GameAccountId="))
            {
                var m = ReAccountId.Match(line);
                if (m.Success && Game.AccountIdLo == 0)
                {
                    var lo = long.Parse(m.Groups[1].Value);
                    if (lo != 0) Game.AccountIdLo = lo;
                }
            }

            if (line.Contains("GAME_SEED"))
            {
                var m = ReGameSeed.Match(line);
                if (m.Success) Game.GameSeed = long.Parse(m.Groups[1].Value);
            }

            if (line.Contains("PowerTaskList.DebugDump()"))
            {
                _inCreateBlock = false;
                // 非重连 → 旧局结束，存入 history
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

            // DebugPrintGame / 其他块内行 → 跳过
            return null;
        }

        // ── 只处理 GameState + PowerTaskList ──
        if (!line.Contains("GameState.") && !line.Contains("PowerTaskList.DebugPrintPower()"))
            return null;
        if (!Game.IsActive) return null;

        // PowerTaskList → 只处理 FULL_ENTITY
        if (line.Contains("PowerTaskList."))
            return HandlePowerTaskList(line);

        return HandleGameState(line);
    }

    // ── 扫描（对齐 Python scan_existing）────────────────

    /// <summary>从最后一行 CREATE_GAME 开始扫描，逐行处理事件</summary>
    public List<string> ScanFromLastCreateGame(string path)
    {
        var lines = ReadAllLines(path);
        var startIdx = FindLastCreateGameIndex(lines);
        var events = new List<string>();
        for (int i = startIdx; i < lines.Count; i++)
        {
            var ev = ProcessLine(lines[i]);
            if (ev != null) events.Add(ev);
        }
        return events;
    }

    static List<string> ReadAllLines(string path)
    {
        var lines = new List<string>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs, System.Text.Encoding.UTF8);
        string? line;
        while ((line = sr.ReadLine()) != null)
            lines.Add(line);
        return lines;
    }

    static int FindLastCreateGameIndex(List<string> lines)
    {
        int last = 0;
        for (int i = 0; i < lines.Count; i++)
            if (ReCreateGame.IsMatch(lines[i])) last = i;
        return last;
    }

    // ── 内部 ──────────────────────────────────────────

    void ResetGame()
    {
        Game = new Game { IsActive = true, StartTime = DateTime.Now.ToString("HH:mm:ss") };
        _concedePending = false;
        _concedeTag = "";
    }

    void EndGame()
    {
        if (!Game.IsActive) return;
        Game.IsActive = false;
        Game.EndTime = DateTime.Now.ToString("HH:mm:ss");
        Games.Add(Game);
    }

    static bool IsHeroCard(string cardId)
    {
        if (HeroExclude.Contains(cardId)) return false;
        foreach (var p in HeroPrefixes)
            if (cardId.StartsWith(p) && int.TryParse(cardId[p.Length..], out _))
                return true;
        return false;
    }

    string? HandleGameState(string line)
    {
        // GameType
        var m = ReGameType.Match(line);
        if (m.Success && line.Contains("DebugPrintGame()"))
        {
            if (m.Groups[1].Value != "GT_BATTLEGROUNDS") { EndGame(); return "not_bg"; }
            return null;
        }

        // PlayerName
        m = RePlayerName.Match(line);
        if (m.Success && line.Contains("DebugPrintGame()") && string.IsNullOrEmpty(Game.PlayerTag))
        {
            var name = m.Groups[2].Value.Trim();
            if (name is "古怪之德鲁伊" or "惊魂之武僧") return null;
            Game.PlayerTag = name;
            Game.PlayerDisplayName = name.Contains('#') ? name[..name.LastIndexOf('#')] : name;
            return "player_info";
        }

        // HERO_ENTITY
        m = ReHeroEntity.Match(line);
        if (m.Success)
        {
            var entityName = m.Groups[1].Value.Trim();
            var heroEntityId = int.Parse(m.Groups[2].Value);
            if (entityName == Game.PlayerTag)
            {
                Game.HeroEntityId = heroEntityId;
                var hero = Game.FindHeroByEntity(heroEntityId);
                if (hero != null)
                {
                    Game.HeroName = hero.HeroName;
                    Game.HeroCardId = hero.CardId;
                }
                return "hero_entity";
            }
            return null;
        }

        // FULL_ENTITY
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
                    Game.AllHeroes[key] = new Hero
                    {
                        EntityId = entityId, HeroName = heroName,
                        CardId = cardId, PlayerSlot = playerSlot
                    };
                    // 回填本地英雄
                    if (entityId == Game.HeroEntityId ||
                        (!string.IsNullOrEmpty(Game.HeroCardId) && cardId == Game.HeroCardId))
                    {
                        Game.HeroName = heroName;
                        Game.HeroCardId = cardId;
                    }
                }
                else if ((entityId == Game.HeroEntityId ||
                          (!string.IsNullOrEmpty(Game.HeroCardId) && cardId == Game.HeroCardId))
                         && string.IsNullOrEmpty(Game.HeroName))
                {
                    Game.HeroName = Game.AllHeroes[key].HeroName;
                    Game.HeroCardId = Game.AllHeroes[key].CardId;
                }
            }
            return null;
        }

        // STEP
        m = ReStep.Match(line);
        if (m.Success)
        {
            var step = m.Groups[1].Value;
            if (step is "MAIN_READY" or "MAIN_ACTION") return "phase_change";
            if (step == "MAIN_CLEANUP")
            {
                Game.LobbyPlayers = LobbyReader.GetLobbyPlayers(Game);
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

        // LEADERBOARD_PLACE（带 entity 信息）
        m = ReLbEntity.Match(line);
        if (m.Success)
        {
            var cardId = m.Groups[3].Value;
            var placement = int.Parse(m.Groups[5].Value);
            if (IsHeroCard(cardId))
            {
                // 更新 all_heroes 和 hero_placements
                var key = (cardId, int.Parse(m.Groups[4].Value));
                if (Game.AllHeroes.TryGetValue(key, out var hero))
                    hero.Placement = placement;
                // 本地英雄排名
                if (cardId == Game.HeroCardId)
                    Game.HeroPlacement = placement;
            }
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

        // LEADERBOARD_PLACE 简写（仅 BattleTag）
        m = ReLbTag.Match(line);
        if (m.Success)
        {
            var tag = m.Groups[1].Value.Trim();
            var placement = int.Parse(m.Groups[2].Value);
            if (tag == Game.PlayerTag) Game.HeroPlacement = placement;
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

        // 投降信号前兆
        if (ReConcedePlayerTag.IsMatch(line))
        {
            var tm = Regex.Match(line, @"Entity=(.+?) tag=(?:3479|4356)");
            if (tm.Success) _concedeTag = tm.Groups[1].Value.Trim();
            _concedePending = true;
            return null;
        }

        // 投降信号
        if (_concedePending && ReConcedeGameTag.IsMatch(line))
            return null;

        // 游戏结束
        if (ReGameStateComplete.IsMatch(line))
        {
            if (!Game.Conceded && Game.HeroPlacement > 0)
                Game.PlacementConfirmed = true;
            EndGame();
            return "game_end";
        }

        return null;
    }

    string? HandlePowerTaskList(string line)
    {
        var m = ReFullEntity.Match(line);
        if (!m.Success) return null;

        var heroName = m.Groups[1].Value;
        var entityId = int.Parse(m.Groups[2].Value);
        var cardId = m.Groups[3].Value;
        var playerSlot = int.Parse(m.Groups[4].Value);

        if (!IsHeroCard(cardId)) return null;

        var key = (cardId, playerSlot);
        if (!Game.AllHeroes.ContainsKey(key))
        {
            Game.AllHeroes[key] = new Hero
            {
                EntityId = entityId, HeroName = heroName,
                CardId = cardId, PlayerSlot = playerSlot
            };
            // 回填本地英雄
            if (entityId == Game.HeroEntityId ||
                (!string.IsNullOrEmpty(Game.HeroCardId) && cardId == Game.HeroCardId))
            {
                Game.HeroName = heroName;
                Game.HeroCardId = cardId;
            }
            return "hero_found";
        }
        return null;
    }
}
