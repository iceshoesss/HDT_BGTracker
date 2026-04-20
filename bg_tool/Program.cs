using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using BgTool;

#nullable enable

namespace BgTool
{

class Program
{
    static void Main(string[] args)
    {
        // Windows 控制台 UTF-8 输出
        Console.OutputEncoding = Encoding.UTF8;

        // 在任何 HearthMirror 代码被 JIT 之前注册依赖解析
        // HearthMirror.dll 依赖 untapped-scry-dotnet 等 DLL，需要从 HDT_PATH 加载
        AppDomain.CurrentDomain.AssemblyResolve += (sender, resolveArgs) =>
        {
            var hdtDir = Environment.GetEnvironmentVariable("HDT_PATH");
            if (string.IsNullOrEmpty(hdtDir)) return null;
            var name = new AssemblyName(resolveArgs.Name).Name;
            var path = Path.Combine(hdtDir, name + ".dll");
            if (File.Exists(path))
                return Assembly.LoadFrom(path);
            return null;
        };

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
            Console.WriteLine("⏳ 未找到 Power.log，等待游戏启动...");
            Console.WriteLine("   (Ctrl+C 退出)\n");
            while (logPath == null)
            {
                Thread.Sleep(3000);
                logPath = LogPathFinder.Find(customPath);
            }
        }

        if (parseMode)
            ParseFile(logPath);
        else
            TailLog(logPath);
    }

    // ═══════════════════════════════════════
    //  批量解析
    // ═══════════════════════════════════════

    static void ParseFile(string path)
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
            parser.Games.Add(parser.Game);

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

    static void TailLog(string path)
    {
        var currentPath = path;
        var running = true;
        var fileCheckCounter = 0;
        const int fileCheckEvery = 100;

        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true;
            running = false;
            Console.WriteLine("\n⏹ 停止监控");
        };

        Console.WriteLine($"👁 监控: {currentPath}");
        Console.WriteLine("   (Ctrl+C 停止)\n");

        var parser = new Parser();
        long pos;

        try
        {
            ScanExisting(currentPath, out parser, out pos);
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
                            ScanExisting(currentPath, out parser, out pos);
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

                string[] lines;
                using (var fs = new FileStream(currentPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(fs))
                {
                    fs.Seek(pos, SeekOrigin.Begin);
                    var list = new List<string>();
                    while (!reader.EndOfStream)
                        list.Add(reader.ReadLine());
                    pos = fs.Position;
                    lines = list.ToArray();
                }

                foreach (var line in lines)
                {
                    var evt = parser.ProcessLine(line);
                    if (evt == null) continue;

                    if ((evt == "game_end" || evt == "concede") && !parser.Game.IsActive)
                    {
                        PrintGameResult(parser.Games[parser.Games.Count - 1]);
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
                        ScanExisting(currentPath, out parser, out pos);
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
                    // 文件短暂不可访问（游戏切换），静默等待
                    Thread.Sleep(500);
                }
            }
            catch (IOException)
            {
                // 文件短暂锁定（游戏写入中），静默等待重试
                Thread.Sleep(200);
            }
            catch (Exception e)
            {
                Console.WriteLine($"⚠️ 错误: {e.Message}");
                Thread.Sleep(1000);
            }
        }
    }

    static void ScanExisting(string filePath, out Parser parser, out long pos)
    {
        parser = new Parser();
        var cgPos = FindLastCreateGamePos(filePath);

        // 中途接入：从整个文件预加载 PlayerName（DebugPrintGame 可能在 CREATE_GAME 之前）
        PreloadPlayerInfo(filePath, parser);

        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(fs))
        {
            fs.Seek(cgPos, SeekOrigin.Begin);
            while (!reader.EndOfStream)
                parser.ProcessLine(reader.ReadLine());
            pos = fs.Position;
        }
    }

    /// <summary>
    /// 从文件中预加载玩家信息（用于中途接入时关键行在 CREATE_GAME 之前的情况）
    /// 加载 PlayerName、AccountIdLo、HeroEntityId
    /// </summary>
    static void PreloadPlayerInfo(string filePath, Parser parser)
    {
        try
        {
            bool foundName = false;
            bool foundLo = false;
            bool foundHero = false;

            foreach (var line in File.ReadLines(filePath))
            {
                // PlayerName
                if (!foundName && line.Contains("DebugPrintGame()") && line.Contains("PlayerName="))
                {
                    var m = Regex.Match(line, @"PlayerID=(\d+),\s*PlayerName=(.+?)$");
                    if (m.Success)
                    {
                        var name = m.Groups[2].Value.Trim();
                        if (name != "古怪之德鲁伊" && name != "惊魂之武僧")
                        {
                            parser.Game.PlayerTag = name;
                            parser.Game.PlayerDisplayName = name.Contains("#")
                                ? name.Substring(0, name.LastIndexOf("#"))
                                : name;
                            foundName = true;
                        }
                    }
                }

                // AccountIdLo（CREATE_GAME 块内的 GameAccountId）
                if (!foundLo && line.Contains("GameAccountId="))
                {
                    var m = Regex.Match(line, @"GameAccountId=\[hi=\d+ lo=(\d+)\]");
                    if (m.Success)
                    {
                        var lo = ulong.Parse(m.Groups[1].Value);
                        if (lo != 0)
                        {
                            parser.Game.AccountIdLo = lo;
                            foundLo = true;
                        }
                    }
                }

                // HeroEntityId（TAG_CHANGE ... tag=HERO_ENTITY）
                if (!foundHero && line.Contains("HERO_ENTITY") && !string.IsNullOrEmpty(parser.Game.PlayerTag))
                {
                    var m = Regex.Match(line, @"TAG_CHANGE Entity=(.+?) tag=HERO_ENTITY value=(\d+)");
                    if (m.Success && m.Groups[1].Value.Trim() == parser.Game.PlayerTag)
                    {
                        parser.Game.HeroEntityId = int.Parse(m.Groups[2].Value);
                        foundHero = true;
                    }
                }

                if (foundName && foundLo && foundHero)
                    break;
            }
        }
        catch { }
    }

    // ═══════════════════════════════════════
    //  查找最后一个 CREATE_GAME 的位置
    // ═══════════════════════════════════════

    static long FindLastCreateGamePos(string path)
    {
        // 用 FileStream 逐字节读取，避免 StreamReader 缓冲导致位置偏移
        // 对齐 Python bg_parser 的 open(path, 'rb') + f.tell() 方式
        long lastPos = 0;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var lineStart = 0L;
            var sb = new System.Text.StringBuilder();
            int b;
            while ((b = fs.ReadByte()) != -1)
            {
                if (b == '\n')
                {
                    var line = sb.ToString();
                    if (line.Length > 0 && line[line.Length - 1] == '\r')
                        line = line.Substring(0, line.Length - 1);
                    if (Regex.IsMatch(line, @"GameState\.DebugPrintPower\(\) - CREATE_GAME$"))
                        lastPos = lineStart;
                    sb.Clear();
                    lineStart = fs.Position;
                }
                else
                {
                    sb.Append((char)b);
                }
            }
        }
        return lastPos;
    }

    static long GetFileEnd(string path)
    {
        try
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                return fs.Length;
        }
        catch { return 0; }
    }

    // ═══════════════════════════════════════
    //  输出
    // ═══════════════════════════════════════

    static void LogEvent(string evt, Game game)
    {
        var ts = DateTime.Now.ToString("HH:mm:ss");
        switch (evt)
        {
            case "game_start":
                Console.WriteLine($"  [{ts}] 🎮 新局开始");
                break;
            case "reconnect":
                Console.WriteLine($"  [{ts}] 🔄 断线重连（忽略）");
                break;
            case "player_info":
                Console.WriteLine($"  [{ts}] 👤 {game.PlayerTag}");
                break;
            case "hero_entity":
                Console.WriteLine($"  [{ts}] 🦸 选定英雄: {(string.IsNullOrEmpty(game.HeroName) ? "(等待数据)" : game.HeroName)}");
                break;
            case "concede":
                Console.WriteLine($"  [{ts}] 🏳️ 投降");
                break;
            case "game_end":
                Console.WriteLine($"  [{ts}] 🏁 对局结束");
                break;
            case "not_bg":
                Console.WriteLine($"  [{ts}] ⚠️ 非战棋模式，跳过");
                break;
            default:
                Console.WriteLine($"  [{ts}] {evt}");
                break;
        }
    }

    static void PrintGameResult(Game game, int index = 0)
    {
        Console.WriteLine($"\n{new string('─', 50)}");
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

        if (game.PlacementConfirmed)
            Console.WriteLine($"🏆 排名: 第 {game.HeroPlacement} 名（确定）");
        else if (game.HeroPlacement > 0)
            Console.WriteLine($"🏆 排名: 第 {game.HeroPlacement} 名（不确定，游戏内最终观测值）");
        else
            Console.WriteLine("🏆 排名: 未知（未观测到最终排名）");

        var events = new List<string>();
        if (game.Reconnected) events.Add("断线重连");
        if (game.Conceded) events.Add("投降");
        else if (string.IsNullOrEmpty(game.EndTime)) events.Add("未正常结束");
        if (events.Count > 0)
            Console.WriteLine($"📊 {string.Join(" + ", events)}");

        var seen = new HashSet<string>();
        var others = new List<Hero>();
        foreach (var h in game.AllHeroes.Values)
        {
            if (!string.IsNullOrEmpty(h.HeroName) && h.HeroName != game.HeroName && seen.Add(h.HeroName))
                others.Add(h);
        }

        if (others.Count > 0)
        {
            Console.WriteLine("\n📊 其他英雄:");
            foreach (var h in others)
                Console.WriteLine($"   {h.HeroName} ({h.CardId})");
        }

        Console.WriteLine();
    }

    static void PrintSummary(List<Game> games)
    {
        if (games.Count == 0)
        {
            Console.WriteLine("\n⚠️ 没有对局");
            return;
        }
        var conceded = games.Count(g => g.Conceded);
        var reconnected = games.Count(g => g.Reconnected);
        Console.WriteLine($"\n{new string('=', 50)}");
        Console.WriteLine($"📈 汇总: {games.Count} 局");
        Console.WriteLine($"   投降: {conceded} | 断线重连: {reconnected}");
    }

    static void PrintMidGame(Game game)
    {
        Console.WriteLine($"{new string('─', 50)}");
        Console.WriteLine("🎮 进行中的对局");
        if (!string.IsNullOrEmpty(game.PlayerTag))
            Console.WriteLine($"👤 {game.PlayerTag}");
        if (!string.IsNullOrEmpty(game.HeroName))
            Console.WriteLine($"🦸 {game.HeroName} ({game.HeroCardId})");
        if (game.Reconnected)
            Console.WriteLine("🔄 已重连");
        Console.WriteLine();
    }
}
}
