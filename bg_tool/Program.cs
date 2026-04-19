using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using BgTool;

// ═══════════════════════════════════════
//  命令行参数
// ═══════════════════════════════════════

string? customPath = null;
bool parseMode = false;

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--parse")
    {
        parseMode = true;
        if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
            customPath = args[i + 1];
    }
    else if (!args[i].StartsWith("--") && customPath == null)
    {
        customPath = args[i];
    }
}

var logPath = LogPathFinder.Find(customPath);
if (logPath == null)
{
    Console.WriteLine("❌ 未找到 Power.log");
    Console.WriteLine("用法: bg_tool [--parse] [Power.log路径]");
    return 1;
}

if (parseMode)
    ParseFile(logPath);
else
    TailLog(logPath);

return 0;

// ═══════════════════════════════════════
//  批量解析
// ═══════════════════════════════════════

void ParseFile(string path)
{
    var parser = new Parser();
    Console.WriteLine($"📖 读取: {path}");
    Console.WriteLine($"📏 大小: {new FileInfo(path).Length / 1024.0:F1} KB\n");

    foreach (var line in File.ReadLines(path))
    {
        var evt = parser.ProcessLine(line);
        if (evt != null) LogEvent(evt, parser.Game);
    }

    if (parser.Game.IsActive)
        parser.Games.Add(parser.Game); // 未结束的局也算上

    if (parser.Games.Count == 0)
    {
        Console.WriteLine("⚠️ 未发现战棋对局");
        return;
    }

    Console.WriteLine($"\n🎯 共 {parser.Games.Count} 局\n");
    for (int i = 0; i < parser.Games.Count; i++)
        PrintGameResult(parser.Games[i], i + 1);
    PrintSummary(parser.Games);
}

// ═══════════════════════════════════════
//  实时监控
// ═══════════════════════════════════════

void TailLog(string path)
{
    var currentPath = path;
    var running = true;
    var fileCheckCounter = 0;
    const int fileCheckEvery = 100;

    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        running = false;
        Console.WriteLine("\n⏹ 停止监控");
    };

    Console.WriteLine($"👁 监控: {currentPath}");
    Console.WriteLine("   (Ctrl+C 停止)\n");

    // 首次扫描：找最后一个 CREATE_GAME 开始
    var parser = new Parser();
    long pos;

    try
    {
        (parser, pos) = ScanExisting(currentPath);
        if (parser.Game.IsActive)
            PrintMidGame(parser.Game);
        else
        {
            pos = GetFileEnd(currentPath);
            Console.WriteLine("   等待游戏开始...");
        }
    }
    catch (Exception e)
    {
        Console.WriteLine($"⚠️ 首次扫描失败: {e.Message}");
        parser = new Parser();
        pos = GetFileEnd(currentPath);
    }

    while (running)
    {
        try
        {
            // 定期检查新日志文件
            fileCheckCounter++;
            if (fileCheckCounter >= fileCheckEvery)
            {
                fileCheckCounter = 0;
                var newPath = LogPathFinder.CheckNewLogFile(currentPath);
                if (newPath != null)
                {
                    if (parser.Game.IsActive)
                        Console.WriteLine("\n🔄 游戏重启，当前对局中断");
                    currentPath = newPath;
                    Console.WriteLine($"🔄 切换: {currentPath}");
                    try
                    {
                        (parser, pos) = ScanExisting(currentPath);
                        if (parser.Game.IsActive)
                            PrintMidGame(parser.Game);
                        else
                        {
                            pos = GetFileEnd(currentPath);
                            Console.WriteLine("   等待游戏开始...");
                        }
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"⚠️ 扫描失败: {e.Message}");
                        parser = new Parser();
                        pos = GetFileEnd(currentPath);
                    }
                }
            }

            // 读取新增内容
            string[] lines;
            using (var fs = new FileStream(currentPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(fs))
            {
                fs.Seek(pos, SeekOrigin.Begin);
                var list = new List<string>();
                while (!reader.EndOfStream)
                    list.Add(reader.ReadLine()!);
                pos = fs.Position;
                lines = list.ToArray();
            }

            foreach (var line in lines)
            {
                var evt = parser.ProcessLine(line);
                if (evt == null) continue;

                if (evt is "game_end" or "concede" && !parser.Game.IsActive)
                {
                    PrintGameResult(parser.Games[^1]);
                    Console.WriteLine("   等待下一局开始...\n");
                }
                else
                {
                    LogEvent(evt, parser.Game);
                }
            }

            Thread.Sleep(100);
        }
        catch (FileNotFoundException)
        {
            var newPath = LogPathFinder.CheckNewLogFile(currentPath);
            if (newPath != null)
            {
                currentPath = newPath;
                try
                {
                    (parser, pos) = ScanExisting(currentPath);
                    Console.WriteLine($"🔄 日志切换: {currentPath}");
                    if (parser.Game.IsActive)
                        PrintMidGame(parser.Game);
                    else
                        pos = GetFileEnd(currentPath);
                }
                catch
                {
                    parser = new Parser();
                    pos = GetFileEnd(currentPath);
                }
            }
            else
            {
                Console.WriteLine("❌ 日志消失，等待...");
                Thread.Sleep(3000);
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"⚠️ 错误: {e.Message}");
            Thread.Sleep(1000);
        }
    }
}

(Parser parser, long pos) ScanExisting(string filePath)
{
    var p = new Parser();
    var cgPos = FindLastCreateGamePos(filePath);

    using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var reader = new StreamReader(fs);
    fs.Seek(cgPos, SeekOrigin.Begin);
    while (!reader.EndOfStream)
        p.ProcessLine(reader.ReadLine()!);
    return (p, fs.Position);
}

// ═══════════════════════════════════════
//  查找最后一个 CREATE_GAME 的位置
// ═══════════════════════════════════════

long FindLastCreateGamePos(string path)
{
    long lastPos = 0;
    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var reader = new StreamReader(fs);
    while (!reader.EndOfStream)
    {
        var pos = fs.Position; // 每行读取前记录位置（避免 StreamReader 缓冲超前）
        var line = reader.ReadLine();
        if (line != null && Regex.IsMatch(line, @"GameState\.DebugPrintPower\(\) - CREATE_GAME$"))
            lastPos = pos;
    }
    return lastPos;
}

long GetFileEnd(string path)
{
    try
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return fs.Length;
    }
    catch { return 0; }
}

// ═══════════════════════════════════════
//  输出
// ═══════════════════════════════════════

void LogEvent(string evt, Game game)
{
    var ts = DateTime.Now.ToString("HH:mm:ss");
    Console.WriteLine(evt switch
    {
        "game_start"  => $"  [{ts}] 🎮 新局开始",
        "reconnect"   => $"  [{ts}] 🔄 断线重连（忽略）",
        "player_info" => $"  [{ts}] 👤 {game.PlayerTag}",
        "hero_entity" => $"  [{ts}] 🦸 选定英雄: {(string.IsNullOrEmpty(game.HeroName) ? "(等待数据)" : game.HeroName)}",
        "concede"     => $"  [{ts}] 🏳️ 投降",
        "game_end"    => $"  [{ts}] 🏁 对局结束",
        "not_bg"      => $"  [{ts}] ⚠️ 非战棋模式，跳过",
        _ => $"  [{ts}] {evt}",
    });
}

void PrintGameResult(Game game, int index = 0)
{
    Console.WriteLine($"\n{"─",50}");
    var prefix = index > 0 ? $"第 {index} 局" : "对局";
    Console.WriteLine($"🎮 {prefix}");

    if (!string.IsNullOrEmpty(game.PlayerTag))
        Console.WriteLine($"👤 {game.PlayerTag}");
    if (game.AccountIdLo != 0)
        Console.WriteLine($"   账号ID: {game.AccountIdLo}");
    if (game.GameSeed != 0)
        Console.WriteLine($"   GAME_SEED: {game.GameSeed}");
    if (!string.IsNullOrEmpty(game.HeroName))
        Console.WriteLine($"🦸 英雄: {game.HeroName} ({game.HeroCardId})");

    // 排名
    if (game.PlacementConfirmed)
        Console.WriteLine($"🏆 排名: 第 {game.HeroPlacement} 名（确定）");
    else if (game.HeroPlacement > 0)
        Console.WriteLine($"🏆 排名: 第 {game.HeroPlacement} 名（不确定，游戏内最终观测值）");
    else
        Console.WriteLine("🏆 排名: 未知（未观测到最终排名）");

    // 状态
    var events = new List<string>();
    if (game.Reconnected) events.Add("断线重连");
    if (game.Conceded) events.Add("投降");
    else if (string.IsNullOrEmpty(game.EndTime)) events.Add("未正常结束");
    if (events.Count > 0)
        Console.WriteLine($"📊 {string.Join(" + ", events)}");

    // 其他英雄
    var seen = new HashSet<string>();
    var others = game.AllHeroes.Values
        .Where(h => !string.IsNullOrEmpty(h.HeroName) && h.HeroName != game.HeroName)
        .Where(h => seen.Add(h.HeroName))
        .ToList();

    if (others.Count > 0)
    {
        Console.WriteLine("\n📊 其他英雄:");
        foreach (var h in others)
            Console.WriteLine($"   {h.HeroName} ({h.CardId})");
    }

    Console.WriteLine();
}

void PrintSummary(List<Game> games)
{
    if (games.Count == 0)
    {
        Console.WriteLine("\n⚠️ 没有对局");
        return;
    }
    var conceded = games.Count(g => g.Conceded);
    var reconnected = games.Count(g => g.Reconnected);
    Console.WriteLine($"\n{"=",50}");
    Console.WriteLine($"📈 汇总: {games.Count} 局");
    Console.WriteLine($"   投降: {conceded} | 断线重连: {reconnected}");
}

void PrintMidGame(Game game)
{
    Console.WriteLine($"{"─",50}");
    Console.WriteLine("🎮 进行中的对局");
    if (!string.IsNullOrEmpty(game.PlayerTag))
        Console.WriteLine($"👤 {game.PlayerTag}");
    if (!string.IsNullOrEmpty(game.HeroName))
        Console.WriteLine($"🦸 {game.HeroName} ({game.HeroCardId})");
    if (game.Reconnected)
        Console.WriteLine("🔄 已重连");
    Console.WriteLine();
}
