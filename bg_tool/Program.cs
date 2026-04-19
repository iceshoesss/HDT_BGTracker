using BgTool.Models;
using BgTool.Parser;
using BgTool.Services;

namespace BgTool;

class Program
{
    static void Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("bg_tool — 炉石酒馆战棋 Power.log 解析器 (C#)\n");

        // 解析参数
        string? customPath = null;
        string? hdtDir = Environment.GetEnvironmentVariable("HDT_PATH");

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--parse" && i + 1 < args.Length)
                customPath = args[i + 1];
            if (args[i] == "--hdt" && i + 1 < args.Length)
                hdtDir = args[i + 1];
        }

        // 初始化 HearthMirror
        if (!string.IsNullOrEmpty(hdtDir))
            LobbyReader.Init(hdtDir);

        // 查找日志
        var logPath = LogPathFinder.Find(customPath);
        if (logPath == null)
        {
            Console.WriteLine("❌ 未找到 Power.log");
            Console.WriteLine("用法: bg_tool [Power.log路径] 或 设置 HDT_PATH 环境变量");
            return;
        }

        Console.WriteLine($"👁 监控: {logPath}");
        Console.WriteLine("   (Ctrl+C 停止)\n");

        // Ctrl+C 处理
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\n⏹ 停止监控");
            Environment.Exit(0);
        };

        // 启动：扫描已有内容（对齐 Python scan_existing）
        var parser = new LogParser();
        string currentPath = logPath;

        try
        {
            var events = parser.ScanFromLastCreateGame(currentPath);
            foreach (var ev in events)
                LogEvent(ev, parser.Game);

            if (parser.Game.IsActive)
                PrintMidGame(parser.Game);
            else
                Console.WriteLine("   等待游戏开始...");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ 首次扫描失败: {ex.Message}");
            Console.WriteLine("   等待游戏开始...");
        }

        // 实时监控（对齐 Python tail_log）
        var lastMtime = GetMtime(currentPath);
        int fileCheckCounter = 0;
        const int fileCheckEvery = 100; // 100ms × 100 = 每 10 秒检查一次

        while (true)
        {
            try
            {
                // 定期检查新日志文件
                fileCheckCounter++;
                if (fileCheckCounter >= fileCheckEvery)
                {
                    fileCheckCounter = 0;
                    var newPath = CheckNewLogFile(currentPath);
                    if (newPath != null)
                    {
                        if (parser.Game.IsActive)
                            Console.WriteLine("\n🔄 游戏重启，当前对局中断");
                        Console.WriteLine($"🔄 切换: {newPath}");
                        currentPath = newPath;
                        parser = new LogParser();
                        try
                        {
                            var evts = parser.ScanFromLastCreateGame(currentPath);
                            foreach (var ev in evts)
                                LogEvent(ev, parser.Game);
                            if (parser.Game.IsActive)
                                PrintMidGame(parser.Game);
                            else
                                Console.WriteLine("   等待游戏开始...");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"⚠️ 扫描失败: {ex.Message}");
                        }
                        lastMtime = GetMtime(currentPath);
                        continue;
                    }
                }

                // 检查文件是否有新内容（mtime 变化）
                var mtime = GetMtime(currentPath);
                if (mtime > lastMtime)
                {
                    lastMtime = mtime;
                    // 读取新增行
                    var newLines = ReadNewLines(currentPath);
                    foreach (var line in newLines)
                    {
                        var ev = parser.ProcessLine(line);
                        if (ev != null)
                        {
                            if ((ev == "game_end" || ev == "concede") && !parser.Game.IsActive && parser.Games.Count > 0)
                            {
                                PrintGameResult(parser.Games[^1]);
                                Console.WriteLine("   等待下一局开始...\n");
                            }
                            else
                            {
                                LogEvent(ev, parser.Game);
                            }
                        }
                    }
                }

                Thread.Sleep(100);
            }
            catch (FileNotFoundException)
            {
                Console.WriteLine("❌ 日志消失，等待...");
                Thread.Sleep(3000);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ 错误: {ex.Message}");
                Thread.Sleep(1000);
            }
        }
    }

    // ── 增量读取（简易实现）────────────────────────────

    private static long _readPosition;

    /// <summary>读取文件新增行（从上次位置继续）</summary>
    static List<string> ReadNewLines(string path)
    {
        var lines = new List<string>();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < _readPosition)
                _readPosition = 0; // 文件重建
            fs.Seek(_readPosition, SeekOrigin.Begin);
            using var sr = new StreamReader(fs, System.Text.Encoding.UTF8);
            string? line;
            while ((line = sr.ReadLine()) != null)
                lines.Add(line);
            _readPosition = fs.Position;
        }
        catch { }
        return lines;
    }

    // ── 文件检查 ──────────────────────────────────────

    static DateTime GetMtime(string path)
    {
        try { return File.GetLastWriteTime(path); } catch { return DateTime.MinValue; }
    }

    static string? CheckNewLogFile(string currentPath)
    {
        var currentDir = Path.GetDirectoryName(currentPath)!;
        var parent = Path.GetDirectoryName(currentDir);
        var basename = Path.GetFileName(currentDir);
        var logsDir = basename.StartsWith("Hearthstone_") ? parent! : currentDir;

        if (!Directory.Exists(logsDir)) return null;

        string? newestPath = null;
        DateTime newestMtime = DateTime.MinValue;

        try { newestMtime = File.GetLastWriteTime(currentPath); } catch { }

        foreach (var folder in Directory.GetDirectories(logsDir, "Hearthstone_*"))
        {
            var p = Path.Combine(folder, "Power.log");
            if (File.Exists(p) && Path.GetFullPath(p) != Path.GetFullPath(currentPath))
            {
                var mtime = File.GetLastWriteTime(p);
                if (mtime > newestMtime) { newestMtime = mtime; newestPath = p; }
            }
        }

        return newestPath;
    }

    // ── 输出 ─────────────────────────────────────────

    static void LogEvent(string ev, Game game)
    {
        var ts = DateTime.Now.ToString("HH:mm:ss");
        switch (ev)
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
        }
    }

    static void PrintMidGame(Game game)
    {
        Console.WriteLine(new string('─', 50));
        Console.WriteLine("🎮 进行中的对局");
        if (!string.IsNullOrEmpty(game.PlayerTag))
            Console.WriteLine($"👤 {game.PlayerTag}");
        if (!string.IsNullOrEmpty(game.HeroName))
            Console.WriteLine($"🦸 {game.HeroName} ({game.HeroCardId})");
        if (game.Reconnected)
            Console.WriteLine("🔄 已重连");
        Console.WriteLine();
    }

    static void PrintGameResult(Game game)
    {
        Console.WriteLine(new string('─', 50));
        Console.WriteLine("🎮 对局");

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
}
