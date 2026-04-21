using System;
using System.Collections.Generic;

#nullable enable

namespace BgTool
{

/// <summary>
/// 持久化的对局记录（写入 games.json）
/// </summary>
public class GameRecord
{
    public string BattleTag { get; set; } = "";
    public string HeroName { get; set; } = "";
    public string HeroCardId { get; set; } = "";
    public int Placement { get; set; }
    public int Points { get; set; }
    public string GameUuid { get; set; } = "";
    public string Timestamp { get; set; } = "";  // ISO 8601 UTC
}

}
