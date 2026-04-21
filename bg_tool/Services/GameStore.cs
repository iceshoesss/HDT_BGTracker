using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

#nullable enable

namespace BgTool
{

/// <summary>
/// 对局记录持久化（games.json）
/// </summary>
public static class GameStore
{
    private static readonly object _lock = new object();
    private static string _path = "";

    /// <summary>
    /// 初始化（指定文件路径，通常在 exe 同目录）
    /// </summary>
    public static void Init(string? dir = null)
    {
        var baseDir = dir ?? AppDomain.CurrentDomain.BaseDirectory;
        _path = Path.Combine(baseDir, "games.json");
    }

    /// <summary>
    /// 加载所有记录
    /// </summary>
    public static List<GameRecord> Load()
    {
        if (string.IsNullOrEmpty(_path)) Init();
        if (!File.Exists(_path)) return new List<GameRecord>();

        try
        {
            var text = File.ReadAllText(_path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text)) return new List<GameRecord>();
            return ParseJsonArray(text);
        }
        catch (Exception e)
        {
            Console.WriteLine($"[GameStore] 加载失败: {e.Message}");
            return new List<GameRecord>();
        }
    }

    /// <summary>
    /// 追加一条记录
    /// </summary>
    public static void Save(GameRecord record)
    {
        if (string.IsNullOrEmpty(_path)) Init();

        lock (_lock)
        {
            var records = Load();
            records.Add(record);
            WriteAll(records);
        }

        Console.WriteLine($"[GameStore] 已保存: 第{record.Placement}名 {record.HeroName} {(record.Points >= 0 ? "+" : "")}{record.Points}");
    }

    /// <summary>
    /// 获取今天的记录
    /// </summary>
    public static List<GameRecord> GetToday()
    {
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var all = Load();
        var result = new List<GameRecord>();
        foreach (var r in all)
        {
            if (r.Timestamp.StartsWith(today))
                result.Add(r);
        }
        return result;
    }

    /// <summary>
    /// 获取最近 N 条记录（倒序）
    /// </summary>
    public static List<GameRecord> GetRecent(int count = 5)
    {
        var all = Load();
        var result = new List<GameRecord>();
        var start = Math.Max(0, all.Count - count);
        for (int i = all.Count - 1; i >= start; i--)
            result.Add(all[i]);
        return result;
    }

    // ── 极简 JSON 序列化（无第三方依赖） ──

    private static void WriteAll(List<GameRecord> records)
    {
        var sb = new StringBuilder();
        sb.Append("[\n");
        for (int i = 0; i < records.Count; i++)
        {
            var r = records[i];
            if (i > 0) sb.Append(",\n");
            sb.Append("  {");
            sb.Append($"\"battleTag\":\"{Esc(r.BattleTag)}\",");
            sb.Append($"\"heroName\":\"{Esc(r.HeroName)}\",");
            sb.Append($"\"heroCardId\":\"{Esc(r.HeroCardId)}\",");
            sb.Append($"\"placement\":{r.Placement},");
            sb.Append($"\"points\":{r.Points},");
            sb.Append($"\"gameUuid\":\"{Esc(r.GameUuid)}\",");
            sb.Append($"\"timestamp\":\"{Esc(r.Timestamp)}\"");
            sb.Append("}");
        }
        sb.Append("\n]\n");
        File.WriteAllText(_path, sb.ToString(), Encoding.UTF8);
    }

    private static List<GameRecord> ParseJsonArray(string text)
    {
        var result = new List<GameRecord>();
        // 极简解析：逐个 { ... } 块
        int i = 0;
        while (i < text.Length)
        {
            int start = text.IndexOf('{', i);
            if (start < 0) break;
            int end = text.IndexOf('}', start);
            if (end < 0) break;

            var block = text.Substring(start, end - start + 1);
            var rec = ParseRecord(block);
            if (rec != null) result.Add(rec);

            i = end + 1;
        }
        return result;
    }

    private static GameRecord? ParseRecord(string block)
    {
        try
        {
            var r = new GameRecord();
            r.BattleTag = Extract(block, "battleTag");
            r.HeroName = Extract(block, "heroName");
            r.HeroCardId = Extract(block, "heroCardId");
            r.Placement = ExtractInt(block, "placement");
            r.Points = ExtractInt(block, "points");
            r.GameUuid = Extract(block, "gameUuid");
            r.Timestamp = Extract(block, "timestamp");
            return r;
        }
        catch { return null; }
    }

    private static string Extract(string json, string key)
    {
        var search = $"\"{key}\":\"";
        var idx = json.IndexOf(search);
        if (idx < 0) return "";
        idx += search.Length;
        var end = json.IndexOf('"', idx);
        if (end < 0) return "";
        return json.Substring(idx, end - idx);
    }

    private static int ExtractInt(string json, string key)
    {
        var search = $"\"{key}\":";
        var idx = json.IndexOf(search);
        if (idx < 0) return 0;
        idx += search.Length;
        var end = idx;
        while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-'))
            end++;
        if (end == idx) return 0;
        int.TryParse(json.Substring(idx, end - idx), out var val);
        return val;
    }

    private static string Esc(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}

}
