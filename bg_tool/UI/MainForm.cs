using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

#nullable enable

namespace BgTool
{

/// <summary>
/// WinForms 主窗口 — 深色主题联赛工具
/// </summary>
public class MainForm : Form
{
    // ── 颜色 ──
    static readonly Color C_BG        = Color.FromArgb(15, 23, 42);    // #0f172a
    static readonly Color C_CARD      = Color.FromArgb(30, 41, 59);    // #1e293b
    static readonly Color C_BORDER    = Color.FromArgb(51, 65, 85);    // #334155
    static readonly Color C_TEXT      = Color.FromArgb(226, 232, 240); // #e2e8f0
    static readonly Color C_TEXT_DIM  = Color.FromArgb(148, 163, 184); // #94a3b8
    static readonly Color C_TEXT_MUTED= Color.FromArgb(100, 116, 139); // #64748b
    static readonly Color C_GOLD      = Color.FromArgb(245, 158, 11);  // #f59e0b
    static readonly Color C_GREEN     = Color.FromArgb(34, 197, 94);   // #22c55e
    static readonly Color C_RED       = Color.FromArgb(239, 68, 68);   // #ef4444
    static readonly Color C_BLUE      = Color.FromArgb(56, 189, 248);  // #38bdf8
    static readonly Color C_ERR_BG    = Color.FromArgb(127, 29, 29);   // #7f1d1d

    // ── 控件 ──
    Label lblStatus       = null!;
    Label lblPlayerName   = null!;
    Label lblGameIcon     = null!;
    Label lblGameText     = null!;
    Label lblGameSub      = null!;
    Panel pnlRecent       = null!;
    Label lblStatGames    = null!;
    Label lblStatWinRate  = null!;
    Label lblStatAvgPlace = null!;
    Label lblStatPoints   = null!;
    Label lblVerifyCode   = null!;
    Button btnCopyCode    = null!;
    Panel pnlError        = null!;
    Label lblErrorMsg     = null!;
    Button btnErrorDetail = null!;

    // ── 状态 ──
    enum AppState { Waiting, InGame, Uploaded }
    AppState _state = AppState.Waiting;
    string _playerTag = "";
    string _verifyCode = "待接入";
    string _lastError = "";
    List<Game> _games = new List<Game>();
    Parser? _parser;

    // ═══════════════════════════════════════
    //  构造
    // ═══════════════════════════════════════

    public MainForm()
    {
        Text = "🍺 酒馆战棋联赛工具";
        Size = new Size(440, 520);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = C_BG;
        ForeColor = C_TEXT;
        Font = new Font("Microsoft YaHei UI", 9f);

        BuildUI();
        UpdateUI();

        // 后台线程启动日志监控
        var thread = new Thread(LogMonitorLoop) { IsBackground = true, Name = "LogMonitor" };
        thread.Start();
    }

    // ═══════════════════════════════════════
    //  UI 构建
    // ═══════════════════════════════════════

    void BuildUI()
    {
        // ── 顶部栏 ──
        var pnlHeader = MakePanel(0, 0, 440, 52);
        pnlHeader.BorderStyle = BorderStyle.FixedSingle;

        var lblTitle = MakeLabel("🍺 酒馆战棋联赛工具", 16, 8, 200, 20, 10f, FontStyle.Bold, C_TEXT);
        lblPlayerName = MakeLabel("", 16, 30, 250, 16, 9f, FontStyle.Regular, C_TEXT_DIM);
        lblPlayerName.Cursor = Cursors.Hand;
        lblPlayerName.Click += (s, e) =>
        {
            if (!string.IsNullOrEmpty(_playerTag))
            {
                try { Process.Start($"https://league.你的域名.com/player/{Uri.EscapeDataString(_playerTag)}"); }
                catch { }
            }
        };

        lblStatus = MakeLabel("● 已连接", 320, 16, 100, 20, 9f, FontStyle.Bold, C_GREEN);
        lblStatus.TextAlign = ContentAlignment.MiddleRight;

        pnlHeader.Controls.AddRange(new Control[] { lblTitle, lblPlayerName, lblStatus });
        Controls.Add(pnlHeader);

        // ── 对局状态区 ──
        var pnlGame = MakePanel(0, 52, 440, 76);
        pnlGame.BorderStyle = BorderStyle.FixedSingle;

        lblGameIcon = MakeLabel("⏳", 0, 10, 440, 32, 20f, FontStyle.Regular, C_TEXT);
        lblGameIcon.TextAlign = ContentAlignment.MiddleCenter;
        lblGameText = MakeLabel("等待对局中...", 0, 40, 440, 18, 10f, FontStyle.Regular, C_TEXT_DIM);
        lblGameText.TextAlign = ContentAlignment.MiddleCenter;
        lblGameSub = MakeLabel("联赛模式已启用", 0, 56, 440, 16, 8f, FontStyle.Regular, C_TEXT_MUTED);
        lblGameSub.TextAlign = ContentAlignment.MiddleCenter;

        pnlGame.Controls.AddRange(new Control[] { lblGameIcon, lblGameText, lblGameSub });
        Controls.Add(pnlGame);

        // ── 最近战绩 + 今日统计 ──
        var pnlSplit = MakePanel(0, 128, 440, 180);
        pnlSplit.BorderStyle = BorderStyle.FixedSingle;

        // 左侧：最近战绩
        var lblRecentTitle = MakeLabel("最近战绩", 16, 8, 100, 16, 8f, FontStyle.Bold, C_TEXT_MUTED);
        pnlSplit.Controls.Add(lblRecentTitle);

        pnlRecent = new Panel
        {
            Location = new Point(16, 26),
            Size = new Size(260, 150),
            BackColor = C_BG,
        };
        pnlSplit.Controls.Add(pnlRecent);

        // 分割线
        var divLine = new Panel
        {
            Location = new Point(290, 8),
            Size = new Size(1, 164),
            BackColor = C_BORDER,
        };
        pnlSplit.Controls.Add(divLine);

        // 右侧：今日统计
        var lblStatTitle = MakeLabel("今日统计", 306, 8, 100, 16, 8f, FontStyle.Bold, C_TEXT_MUTED);
        pnlSplit.Controls.Add(lblStatTitle);

        lblStatGames    = MakeStatLabel("总局数", "0", 306, 32);
        lblStatWinRate  = MakeStatLabel("前四", "0%", 306, 58);
        lblStatAvgPlace = MakeStatLabel("场均排名", "-", 306, 84);
        lblStatPoints   = MakeStatLabel("积分变动", "-", 306, 110);

        pnlSplit.Controls.AddRange(new Control[] { lblStatGames, lblStatWinRate, lblStatAvgPlace, lblStatPoints });
        Controls.Add(pnlSplit);

        // ── 验证码 ──
        var pnlVerify = MakePanel(0, 308, 440, 48);
        pnlVerify.BorderStyle = BorderStyle.FixedSingle;

        var lblVLabel = MakeLabel("验证码", 16, 6, 60, 14, 8f, FontStyle.Regular, C_TEXT_MUTED);
        lblVerifyCode = MakeLabel("待接入", 16, 22, 200, 20, 12f, FontStyle.Bold, C_BLUE);
        lblVerifyCode.Font = new Font("Consolas", 12f, FontStyle.Bold);

        btnCopyCode = new Button
        {
            Text = "📋 复制",
            Location = new Point(340, 12),
            Size = new Size(80, 28),
            FlatStyle = FlatStyle.Flat,
            BackColor = C_CARD,
            ForeColor = C_TEXT_DIM,
            Font = new Font("Microsoft YaHei UI", 8f),
            Cursor = Cursors.Hand,
        };
        btnCopyCode.FlatAppearance.BorderColor = C_BORDER;
        btnCopyCode.Click += (s, e) =>
        {
            if (!string.IsNullOrEmpty(_verifyCode) && _verifyCode != "待接入")
            {
                Clipboard.SetText(_verifyCode);
                btnCopyCode.Text = "✅ 已复制";
                btnCopyCode.BackColor = Color.FromArgb(22, 101, 52);
                btnCopyCode.ForeColor = Color.FromArgb(134, 239, 172);
                var timer = new System.Windows.Forms.Timer { Interval = 1500 };
                timer.Tick += (s2, e2) =>
                {
                    btnCopyCode.Text = "📋 复制";
                    btnCopyCode.BackColor = C_CARD;
                    btnCopyCode.ForeColor = C_TEXT_DIM;
                    timer.Stop();
                    timer.Dispose();
                };
                timer.Start();
            }
        };

        pnlVerify.Controls.AddRange(new Control[] { lblVLabel, lblVerifyCode, btnCopyCode });
        Controls.Add(pnlVerify);

        // ── 错误横幅 ──
        pnlError = MakePanel(0, 356, 440, 40);
        pnlError.BorderStyle = BorderStyle.FixedSingle;
        pnlError.BackColor = Color.FromArgb(30, 20, 20);
        pnlError.Visible = false;

        var lblErrIcon = MakeLabel("⚠️", 16, 8, 24, 24, 10f, FontStyle.Regular, C_RED);
        lblErrorMsg = MakeLabel("", 42, 10, 280, 18, 8f, FontStyle.Regular, Color.FromArgb(252, 165, 165));

        btnErrorDetail = new Button
        {
            Text = "详情",
            Location = new Point(360, 8),
            Size = new Size(60, 24),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.Transparent,
            ForeColor = C_RED,
            Font = new Font("Microsoft YaHei UI", 8f),
            Cursor = Cursors.Hand,
        };
        btnErrorDetail.FlatAppearance.BorderColor = Color.FromArgb(248, 113, 113, 68);
        btnErrorDetail.Click += (s, e) =>
        {
            if (!string.IsNullOrEmpty(_lastError))
                MessageBox.Show(_lastError, "错误详情", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };

        pnlError.Controls.AddRange(new Control[] { lblErrIcon, lblErrorMsg, btnErrorDetail });
        Controls.Add(pnlError);
    }

    // ── 辅助：创建 Label ──
    static Label MakeLabel(string text, int x, int y, int w, int h,
        float fontSize, FontStyle style, Color color)
    {
        return new Label
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(w, h),
            ForeColor = color,
            Font = new Font("Microsoft YaHei UI", fontSize, style),
            BackColor = Color.Transparent,
        };
    }

    // ── 辅助：创建 Panel ──
    static Panel MakePanel(int x, int y, int w, int h)
    {
        return new Panel
        {
            Location = new Point(x, y),
            Size = new Size(w, h),
            BackColor = C_BG,
        };
    }

    // ── 辅助：创建统计行 Label ──
    Label MakeStatLabel(string key, string value, int x, int y)
    {
        var lbl = MakeLabel($"{key}: {value}", x, y, 120, 20, 9f, FontStyle.Regular, C_TEXT);
        lbl.Tag = key;
        return lbl;
    }

    void SetStatValue(Label lbl, string value)
    {
        var key = lbl.Tag as string ?? "";
        lbl.Text = $"{key}: {value}";
    }

    // ═══════════════════════════════════════
    //  UI 更新
    // ═══════════════════════════════════════

    void UpdateUI()
    {
        if (InvokeRequired) { BeginInvoke(new Action(UpdateUI)); return; }

        // 玩家名
        lblPlayerName.Text = string.IsNullOrEmpty(_playerTag) ? "等待游戏启动..." : _playerTag;

        // 对局状态
        switch (_state)
        {
            case AppState.Waiting:
                lblGameIcon.Text = "⏳";
                lblGameText.Text = "等待对局中...";
                lblGameSub.Text = "联赛模式已启用";
                break;
            case AppState.InGame:
                var game = _parser?.Game;
                if (game != null && !string.IsNullOrEmpty(game.HeroName))
                {
                    lblGameIcon.Text = "🎮";
                    lblGameText.Text = $"{game.HeroName}";
                    lblGameSub.Text = "8 人联赛 · 等待结算";
                }
                else
                {
                    lblGameIcon.Text = "🎮";
                    lblGameText.Text = "对局进行中...";
                    lblGameSub.Text = "正在获取英雄信息";
                }
                break;
            case AppState.Uploaded:
                lblGameIcon.Text = "✅";
                lblGameText.Text = "已上传";
                lblGameSub.Text = "对局数据已提交";
                break;
        }

        // 最近战绩
        pnlRecent.Controls.Clear();
        var recent = _games.Where(g => !g.IsActive).Reverse().Take(5).ToList();
        for (int i = 0; i < recent.Count; i++)
        {
            var g = recent[i];
            var y = i * 28;
            var placement = g.HeroPlacement > 0 ? g.HeroPlacement.ToString() : "?";
            var heroName = string.IsNullOrEmpty(g.HeroName) ? "(未知英雄)" : g.HeroName;
            var points = GetPoints(g.HeroPlacement);
            var ptsText = points > 0 ? $"+{points}" : points.ToString();
            var ptsColor = points >= 0 ? C_GREEN : C_RED;

            var badge = new Label
            {
                Text = placement,
                Location = new Point(0, y),
                Size = new Size(22, 22),
                Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold),
                ForeColor = g.HeroPlacement == 1 ? C_BG : C_TEXT_DIM,
                BackColor = GetPlacementColor(g.HeroPlacement),
                TextAlign = ContentAlignment.MiddleCenter,
            };
            // 圆形 badge（用 Region 模拟）
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddEllipse(0, 0, 22, 22);
            badge.Region = new Region(path);

            var hero = new Label
            {
                Text = heroName,
                Location = new Point(30, y + 2),
                Size = new Size(180, 18),
                Font = new Font("Microsoft YaHei UI", 9f),
                ForeColor = C_TEXT,
            };

            var pts = new Label
            {
                Text = ptsText,
                Location = new Point(220, y + 2),
                Size = new Size(40, 18),
                Font = new Font("Consolas", 9f, FontStyle.Bold),
                ForeColor = ptsColor,
                TextAlign = ContentAlignment.MiddleRight,
            };

            pnlRecent.Controls.AddRange(new Control[] { badge, hero, pts });
        }

        // 今日统计
        var today = _games.Where(g => !g.IsActive && IsToday(g)).ToList();
        int totalToday = today.Count;
        int topFour = today.Count(g => g.HeroPlacement > 0 && g.HeroPlacement <= 4);
        double winRate = totalToday > 0 ? (double)topFour / totalToday * 100 : 0;
        double avgPlace = today.Count > 0 ? today.Where(g => g.HeroPlacement > 0).Average(g => g.HeroPlacement) : 0;
        int totalPoints = today.Sum(g => GetPoints(g.HeroPlacement));

        SetStatValue(lblStatGames, totalToday.ToString());
        SetStatValue(lblStatWinRate, totalToday > 0 ? $"{winRate:F0}%" : "0%");
        SetStatValue(lblStatAvgPlace, today.Count > 0 ? avgPlace.ToString("F1") : "-");
        var pointsStr = totalPoints >= 0 ? $"+{totalPoints}" : totalPoints.ToString();
        SetStatValue(lblStatPoints, totalToday > 0 ? pointsStr : "-");

        // 验证码
        lblVerifyCode.Text = _verifyCode;

        // 错误横幅
        pnlError.Visible = !string.IsNullOrEmpty(_lastError);
        lblErrorMsg.Text = _lastError;
    }

    static Color GetPlacementColor(int p)
    {
        switch (p)
        {
            case 1: return C_GOLD;
            case 2: return C_TEXT_DIM;
            case 3: return Color.FromArgb(161, 98, 7);
            default: return C_BORDER;
        }
    }

    static int GetPoints(int placement)
    {
        if (placement <= 0) return 0;
        return placement == 1 ? 9 : Math.Max(1, 9 - placement);
    }

    static bool IsToday(Game g)
    {
        // 用 StartTime 判断（HH:mm:ss 格式，无法判断日期）
        // Demo 中假设所有对局都是今天的
        return true;
    }

    // ═══════════════════════════════════════
    //  日志监控（后台线程）
    // ═══════════════════════════════════════

    void LogMonitorLoop()
    {
        // 等待日志出现
        string? logPath = null;
        while (logPath == null)
        {
            logPath = LogPathFinder.Find(null);
            if (logPath == null)
                Thread.Sleep(3000);
        }

        BeginInvoke(new Action(() => lblStatus.Text = "● 已连接"));

        _parser = new Parser();
        long pos;

        try
        {
            ScanExisting(logPath, out _parser, out pos);

            // 检测空壳旧数据
            if (_parser.Game.IsActive
                && string.IsNullOrEmpty(_parser.Game.HeroName)
                && string.IsNullOrEmpty(_parser.Game.PlayerTag)
                && _parser.Game.AccountIdLo == 0)
            {
                _parser.Game.IsActive = false;
                pos = GetFileEnd(logPath);
            }
            else if (_parser.Game.IsActive)
            {
                _state = AppState.InGame;
                _playerTag = _parser.Game.PlayerTag;
            }

            _games = new List<Game>(_parser.Games);
        }
        catch
        {
            _parser = new Parser();
            pos = GetFileEnd(logPath);
        }

        UpdateUI();

        // 实时监控
        var currentPath = logPath;
        var fileCheckCounter = 0;

        while (true)
        {
            try
            {
                fileCheckCounter++;
                if (fileCheckCounter >= 100)
                {
                    fileCheckCounter = 0;
                    var newPath = LogPathFinder.CheckNewLogFile(currentPath);
                    if (newPath != null)
                    {
                        currentPath = newPath;
                        ScanExisting(currentPath, out _parser, out pos);

                        if (_parser.Game.IsActive
                            && string.IsNullOrEmpty(_parser.Game.HeroName)
                            && string.IsNullOrEmpty(_parser.Game.PlayerTag)
                            && _parser.Game.AccountIdLo == 0)
                        {
                            _parser.Game.IsActive = false;
                            pos = GetFileEnd(currentPath);
                        }
                        else if (_parser.Game.IsActive)
                        {
                            _state = AppState.InGame;
                            _playerTag = _parser.Game.PlayerTag;
                        }
                        else
                        {
                            _state = AppState.Waiting;
                            pos = GetFileEnd(currentPath);
                        }
                        UpdateUI();
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

                bool uiChanged = false;
                foreach (var line in lines)
                {
                    var evt = _parser.ProcessLine(line);
                    if (evt == null) continue;

                    switch (evt)
                    {
                        case "game_start":
                            _state = AppState.InGame;
                            uiChanged = true;
                            break;
                        case "player_info":
                            _playerTag = _parser.Game.PlayerTag;
                            uiChanged = true;
                            break;
                        case "hero_entity":
                        case "hero_found":
                            uiChanged = true;
                            break;
                        case "game_end":
                        case "concede":
                            _state = AppState.Waiting;
                            _games = new List<Game>(_parser.Games);
                            uiChanged = true;
                            break;
                    }
                }

                if (uiChanged) UpdateUI();
                Thread.Sleep(100);
            }
            catch (FileNotFoundException)
            {
                Thread.Sleep(2000);
            }
            catch (IOException)
            {
                Thread.Sleep(200);
            }
            catch
            {
                Thread.Sleep(1000);
            }
        }
    }

    static void ScanExisting(string filePath, out Parser parser, out long pos)
    {
        parser = new Parser();
        var cgPos = FindLastCreateGamePos(filePath);
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

    static void PreloadPlayerInfo(string filePath, Parser parser)
    {
        try
        {
            bool foundName = false;
            bool foundLo = false;
            bool foundHero = false;

            foreach (var line in File.ReadLines(filePath))
            {
                if (!foundName && line.Contains("DebugPrintGame()") && line.Contains("PlayerName="))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(line, @"PlayerID=(\d+),\s*PlayerName=(.+?)$");
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
                if (!foundLo && line.Contains("GameAccountId="))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(line, @"GameAccountId=\[hi=\d+ lo=(\d+)\]");
                    if (m.Success)
                    {
                        var lo = ulong.Parse(m.Groups[1].Value);
                        if (lo != 0) { parser.Game.AccountIdLo = lo; foundLo = true; }
                    }
                }
                if (!foundHero && line.Contains("HERO_ENTITY") && !string.IsNullOrEmpty(parser.Game.PlayerTag))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(line, @"TAG_CHANGE Entity=(.+?) tag=HERO_ENTITY value=(\d+)");
                    if (m.Success && m.Groups[1].Value.Trim() == parser.Game.PlayerTag)
                    {
                        parser.Game.HeroEntityId = int.Parse(m.Groups[2].Value);
                        foundHero = true;
                    }
                }
                if (foundName && foundLo && foundHero) break;
            }
        }
        catch { }
    }

    static long FindLastCreateGamePos(string path)
    {
        long lastPos = 0;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var lineStart = 0L;
            var sb = new StringBuilder();
            int b;
            while ((b = fs.ReadByte()) != -1)
            {
                if (b == '\n')
                {
                    var line = sb.ToString();
                    if (line.Length > 0 && line[line.Length - 1] == '\r')
                        line = line.Substring(0, line.Length - 1);
                    if (System.Text.RegularExpressions.Regex.IsMatch(line, @"GameState\.DebugPrintPower\(\) - CREATE_GAME$"))
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
}

}
