namespace BgTool.Models;

/// <summary>一个英雄实体</summary>
public class Hero
{
    public int EntityId { get; set; }
    public string HeroName { get; set; } = "";
    public string CardId { get; set; } = "";
    public int PlayerSlot { get; set; }
    public int Placement { get; set; }
}

/// <summary>HearthMirror 读取的玩家信息</summary>
public class LobbyPlayer
{
    public long Lo { get; set; }
    public string HeroCardId { get; set; } = "";
}

/// <summary>一局游戏的状态</summary>
public class Game
{
    // 玩家信息
    public string PlayerTag { get; set; } = "";            // 南怀北瑾丨少头脑#5267
    public string PlayerDisplayName { get; set; } = "";    // 南怀北瑾丨少头脑
    public long AccountIdLo { get; set; }

    // 对局标识
    public long GameSeed { get; set; }

    // 英雄信息
    public int HeroEntityId { get; set; }
    public string HeroName { get; set; } = "";
    public string HeroCardId { get; set; } = "";
    public int HeroPlacement { get; set; }

    // 所有英雄（用 (card_id, player_slot) 去重）
    public Dictionary<(string, int), Hero> AllHeroes { get; set; } = new();

    // HearthMirror 对手信息
    public List<LobbyPlayer> LobbyPlayers { get; set; } = new();

    // 游戏状态
    public bool IsActive { get; set; }
    public bool Reconnected { get; set; }
    public bool Conceded { get; set; }
    public bool PlacementConfirmed { get; set; }
    public string StartTime { get; set; } = "";
    public string EndTime { get; set; } = "";

    public Hero? FindHeroByEntity(int entityId)
    {
        foreach (var h in AllHeroes.Values)
            if (h.EntityId == entityId) return h;
        return null;
    }
}
