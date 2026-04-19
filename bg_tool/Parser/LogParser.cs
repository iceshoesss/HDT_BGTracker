using System.Text.RegularExpressions;
using BgTool.Models;
using BgTool.Services;

namespace BgTool.Parser;

/// <summary>Power.log 正则状态机解析器（从 Python bg_parser 移植）</summary>
public class LogParser
{
    // ── 正则表达式（预编译）──────────────────────────────
    private static readonly Regex ReCreateGame = new(@"GameState\.DebugPrintPower\(\) - CREATE_GAME$", RegexOptions.Compiled);
    private static readonly Regex ReGameType = new(@"GameType=(\w+)", RegexOptions.Compiled);
    private static readonly Regex ReGameSeed = new(@"tag=GAME_SEED value=(\d+)", RegexOptions.Compiled);
    private static readonly Regex RePlayerName = new(@"PlayerID=(\d+),\s*PlayerName=(.+?)$", RegexOptions.Compiled);
    private static readonly Regex ReAccountId = new(@"GameAccountId=\[hi=\d+ lo=(\d+)\]", RegexOptions.Compiled);
    private static readonly Regex ReHeroEntity = new(@"TAG_CHANGE Entity=(.+?) tag=HERO_ENTITY value=(\d+)", RegexOptions.Compiled);
    private static readonly Regex ReFullEntity = new(
        @"FULL_ENTITY - (?:Creating|Updating)\s+\[?entityName=(.+?)\s+id=(\d+)\s+zone=\w+.*?cardId=(\w+)\s+player=(\d+)\]",
        RegexOptions.Compiled);
    private static readonly Regex ReLbEntity = new(
        @"TAG_CHANGE Entity=\[entityName=(.+?) id=(\d+) zone=\w+.*?cardId=(\w+).*?player=(\d+)\]\s+tag=PLAYER_LEADERBOARD_PLACE value=(\d+)",
        RegexOptions.Compiled);
    private static readonly Regex ReLbTag = new(@"TAG_CHANGE Entity=(.+?) tag=PLAYER_LEADERBOARD_PLACE value=(\d+)\s*$", RegexOptions.Compiled);
    private static readonly Regex ReStep = new(@"TAG_CHANGE Entity=GameEntity tag=STEP value=(\w+)", RegexOptions.Compiled);
    private static readonly Regex ReConcedePlayerTag = new(@"TAG_CHANGE Entity=.+? tag=(3479|4356) value=1", RegexOptions.Compiled);
    private static readonly Regex ReConcedeGameTag = new(@"TAG_CHANGE Entity=GameEntity tag=4302 value=1", RegexOptions.Compiled);
    private static readonly Regex ReGameStateComplete = new(@"TAG_CHANGE Entity=GameEntity tag=STATE value=COMPLETE", RegexOptions.Compiled);

    // ── 英雄卡牌前缀 ────────────────────────────────────
    private static readonly string[] HeroPrefixes =
    {
        "TB_BaconShop_HERO_",
        "BG20_HERO_", "BG21_HERO_", "BG22_HERO_", "BG23_HERO_",
        "BG24_HERO_", "BG25_HERO_", "BG26_HERO_", "BG27_HERO_",
        "BG28_HERO_", "BG29_HERO_", "BG30_HERO_", "BG31_HERO_",
        "BG32_HERO_", "BG33_HERO_", "BG34_HERO_", "BG35_HERO_",
    };
    private static readonly HashSet<string> HeroExclude = new() { "TB_BaconShop_HERO_PH" };

    // ── 状态 ────────────────────────────────────────────
    public Game Game { get; private set; } = new() { IsActive = true, StartTime = DateTime.Now.ToString("HH:mm:ss") };
    public List<Game> Games { get; } = [];

    private bool _inCreateBlock;
    private bool _createHasTurn;
    private Game? _pendingNewGame;
    private bool _concedePending;
    private string _concedeTag = "";

    // ── 公共方法 ────────────────────────────────────────

    /// <summary>处理一行日志，返回事件类型或 null</summary>
    public string? ProcessLine(string line)
    {
        // CREATE_GAME
        if (ReCreateGame.IsMatch(line))
        {
            _inCreateBlock = true;
            _createHasTurn = false;
            if (Game.IsActive)
                _pendingNewGame = Game;
            else
                _pendingNewGame = null;
            ResetGame();
            return null;
        }

        // CREATE_GAME 块内
        if (_inCreateBlock)
        {
            if (line.Contains("tag=TURN value="))
            {
                // 断线重连
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
                    Game.AccountIdLo = long.Parse(m.Groups[1].Value);
            }
            if (line.Contains("GAME_SEED"))
            {
                var m = ReGameSeed.Match(line);
                if (m.Success) Game.GameSeed = long.Parse(m.Groups[1].Value);
            }
            if (line.Contains("PowerTaskList.DebugDump()"))
            {
                _inCreateBlock = false;
                if (!_createHasTurn && _pendingNewGame != null)
                {
                    _pendingNewGame.IsActive = false;
                    _pendingNewGame.EndTime = DateTime.Now.ToString("HH:mm:ss");
                    Games.Add(_pendingNewGame);
                    _pendingNewGame = null;
                }
                return _createHasTurn ? "reconnect" : "game_start";
            }
            return null;
        }

        // 只处理 GameState + PowerTaskList
        if (!line.Contains("GameState.") && !line.Contains("PowerTaskList.DebugPrintPower()"))
            return null;
        if (!Game.IsActive) return null;

        // PowerTaskList：只处理 FULL_ENTITY
        if (line.Contains("PowerTaskList."))
            return HandlePowerTaskList(line);

        return HandleGameState(line);
    }

    // ── 内部处理 ────────────────────────────────────────

    private void ResetGame()
    {
        Game = new Game { IsActive = true, StartTime = DateTime.Now.ToString("HH:mm:ss") };
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

    private static bool IsHeroCard(string cardId)
    {
        if (HeroExclude.Contains(cardId)) return false;
        foreach (var prefix in HeroPrefixes)
        {
            if (cardId.StartsWith(prefix))
            {
                var suffix = cardId[prefix.Length..];
                return int.TryParse(suffix, out _);
            }
        }
        return false;
    }

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
            }
            return "hero_entity";
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
                    if (entityId == Game.HeroEntityId)
                    {
                        Game.HeroName = heroName;
                        Game.HeroCardId = cardId;
                    }
                }
                else if (entityId == Game.HeroEntityId && string.IsNullOrEmpty(Game.HeroName))
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
            if (step is "MAIN_READY" or "MAIN_ACTION")
                return "phase_change";
            if (step == "MAIN_CLEANUP")
            {
                // 英雄选定完毕，尝试获取对手 Lo
                Game.LobbyPlayers = LobbyReader.GetLobbyPlayers();
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
            if (IsHeroCard(cardId) && cardId == Game.HeroCardId)
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
            var tagMatch = Regex.Match(line, @"Entity=(.+?) tag=(?:3479|4356)");
            if (tagMatch.Success) _concedeTag = tagMatch.Groups[1].Value.Trim();
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
        if (!Game.AllHeroes.ContainsKey(key))
        {
            Game.AllHeroes[key] = new Hero
            {
                EntityId = entityId, HeroName = heroName,
                CardId = cardId, PlayerSlot = playerSlot
            };
            if (entityId == Game.HeroEntityId)
            {
                Game.HeroName = heroName;
                Game.HeroCardId = cardId;
            }
            return "hero_found";
        }
        return null;
    }

    // ── 工具方法 ────────────────────────────────────────

    public static long FindLastCreateGamePos(string path)
    {
        long pos = 0;
        long lastPos = 0;
        using var reader = new StreamReader(path, System.Text.Encoding.UTF8);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (ReCreateGame.IsMatch(line))
                lastPos = pos;
            pos += System.Text.Encoding.UTF8.GetByteCount(line) + 1; // +1 for newline
        }
        return lastPos;
    }
}
