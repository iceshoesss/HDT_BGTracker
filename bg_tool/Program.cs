using BgTool.Models;
using BgTool.Parser;
using BgTool.Services;

namespace BgTool;

class Program
{
    // ── 增量读取状态 ─────────────────────────────────────
    static long _filePosition;
    static byte[] _lineBuffer = [];
    static DateTime _lastMtime = DateTime.MinValue;

    static void Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("bg_tool — 炉石酒馆战棋 Power.log 解析器 (C#)\n");

        string? customPath = null;
        string? hdtDir = Environment.GetEnvironmentVariable("HDT_PATH");

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--parse" && i + 1 < args.Length) customPath = args[i + 1];
            if (args[i] == "--hdt" && i + 1 < args.Length) hdtDir = args[i + 1];
        }

        if (!string.IsNullOrEmpty(hdtDir))
            LobbyReader.Init(hdtDir);

        var logPath = LogPathFinder.Find(customPath);
        if (logPath == null)
        {
            Console.WriteLine("❌ 未找到 Power.log");
            Console.WriteLine("用法: bg_tool [Power.log路径] 或 设置 HDT_PATH 环境变量");
            return;
        }

        Console.CancelKeyPress += (_, e) => { e.Cancel = true; Console.WriteLine("\n⏹ 停止监控"); Environment.Exit(0); };

        // ── 扫描已有内容（对齐 Python scan_existing）──────
        Console.WriteLine($"👁 监控: {logPath}");
        Console.WriteLine("   (Ctrl+C 停止)\n");

        var parser = new Parser();
        string currentPath = logPath;

        try
        {
            var events = parser.ScanFromLastCreateGame(currentPath);
            foreach (var ev in events) LogEvent(ev, parser.Game);

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

        // 初始化增量读取位置
        try { _lastMtime = File.GetLastWriteTime(currentPath); } catch { }
        _filePosition = 0;
        _lineBuffer = [];

        // ── 实时监控（对齐 Python tail_log）──────────────
        int fileCheckCounter = 0;
        const int fileCheckEvery = 100; // 100ms × 100 = 每 10 秒检查新文件

        while (true)
        {
            try
            {
                // 定期检查新日志文件夹
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
                        parser = new Parser();
                        _filePosition = 0;
                        _lineBuffer = [];
                        try
                        {
                            _lastMtime = File.GetLastWriteTime(currentPath);
                            var evts = parser.ScanFromLastCreateGame(currentPath);
                            foreach (var ev in evts) LogEvent(ev, parser.Game);
                            if (parser.Game.IsActive)
                                PrintMidGame(parser.Game);
                            else
                                Console.WriteLine("   等待游戏开始...");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"⚠️ 扫描失败: {ex.Message}");
                        }
                        // 重置增量读取（scan 已经读完所有内容）
                        _filePosition = 0;
                        _lineBuffer = [];
                        try { _lastMtime = File.GetLastWriteTime(currentPath); } catch { }
                        continue;
                    }
                }

                // 检查文件变化（mtime）
                DateTime mtime;
                try { mtime = File.GetLastWriteTime(currentPath); } catch { mtime = DateTime.MinValue; }

                if (mtime > _lastMtime)
                {
                    _lastMtime = mtime;
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

    // ═══════════════════════════════════════════════════
    //  增量读取（用 FileStream 原始字节，避免 StreamReader 缓冲问题）
    // ═══════════════════════════════════════════════════

    static List<string> ReadNewLines(string path)
    {
        var lines = new List<string>();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            // 文件重建（长度回退）
            if (fs.Length < _filePosition)
            {
                _filePosition = 0;
                _lineBuffer = [];
            }

            if (fs.Length <= _filePosition)
                return lines; // 无新内容

            fs.Seek(_filePosition, SeekOrigin.Begin);
            var buf = new byte[fs.Length - _filePosition];
            var bytesRead = fs.Read(buf, 0, buf.Length);
            _filePosition = fs.Position;

            // 拼接上次残留的半行
            var combined = new byte[_lineBuffer.Length + bytesRead];
            Buffer.BlockCopy(_lineBuffer, 0, combined, 0, _lineBuffer.Length);
            Buffer.BlockCopy(buf, 0, combined, _lineBuffer.Length, bytesRead);

            // 按换行符分割
            int lineStart = 0;
            for (int i = 0; i < combined.Length; i++)
            {
                if (combined[i] == (byte)'\n')
                {
                    var lineEnd = i > 0 && combined[i - 1] == (byte)'\r' ? i - 1 : i;
                    var lineLen = lineEnd - lineStart;
                    if (lineLen > 0)
                    {
                        var line = System.Text.Encoding.UTF8.GetString(combined, lineStart, lineLen);
                        lines.Add(line);
                    }
                    lineStart = i + 1;
                }
            }

            // 保存残留的半行（最后一个 \n 之后的内容）
            if (lineStart < combined.Length)
            {
                _lineBuffer = new byte[combined.Length - lineStart];
                Buffer.BlockCopy(combined, lineStart, _lineBuffer, 0, _lineBuffer.Length);
            }
            else
            {
                _lineBuffer = [];
            }
        }
        catch { }
        return lines;
    }

    // ═══════════════════════════════════════════════════
    //  文件检查
    // ═══════════════════════════════════════════════════

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
                var mt = File.GetLastWriteTime(p);
                if (mt > newestMtime) { newestMtime = mt; newestPath = p; }
            }
        }
        return newestPath;
    }

    // ═══════════════════════════════════════════════════
    //  输出（对齐 Python _log_event / _print_mid_game / print_game_result）
    // ═══════════════════════════════════════════════════

    static void LogEvent(string ev, Game game)
    {
        var ts = DateTime.Now.ToString("HH:mm:ss");
        switch (ev)
        {
            case "game_start": Console.WriteLine($"  [{ts}] 🎮 新局开始"); break;
            case "reconnect": Console.WriteLine($"  [{ts}] 🔄 断线重连（忽略）"); break;
            case "player_info": Console.WriteLine($"  [{ts}] 👤 {game.PlayerTag}"); break;
            case "hero_entity": Console.WriteLine($"  [{ts}] 🦸 选定英雄: {(string.IsNullOrEmpty(game.HeroName) ? "(等待数据)" : game.HeroName)}"); break;
            case "concede": Console.WriteLine($"  [{ts}] 🏳️ 投降"); break;
            case "game_end": Console.WriteLine($"  [{ts}] 🏁 对局结束"); break;
            case "not_bg": Console.WriteLine($"  [{ts}] ⚠️ 非战棋模式，跳过"); break;
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
