using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
    RichTextBox rtbLog    = null!;

    // ── 状态 ──
    enum AppState { Waiting, InGame, LeagueGame, Uploaded }
    AppState _state = AppState.Waiting;
    string _playerTag = "";
    string _verifyCode = "待接入";
    Parser? _parser;
    Config _config = null!;
    bool _leagueChecked = false; // 当前对局是否已调过 check-league
    string _currentGameUuid = "";
    bool _scanning = false; // 扫描旧日志期间不触发 check_league

    // ═══════════════════════════════════════
    //  构造
    // ═══════════════════════════════════════

    public MainForm()
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        var verStr = version != null ? $"v{version.Major}.{version.Minor}.{version.Build}" : "";
        Text = $"🍺 酒馆战棋联赛工具 {verStr}";
        // 加载窗口图标
        try
        {
            var iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico");
            if (System.IO.File.Exists(iconPath))
                Icon = new System.Drawing.Icon(iconPath);
        }
        catch { }
        Size = new Size(440, 510);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = C_BG;
        ForeColor = C_TEXT;
        Font = new Font("Microsoft YaHei UI", 9f);

        // 加载配置并初始化 API
        _config = Config.Load();
        ApiClient.Init(_config.ApiBaseUrl);
        GameStore.Init();

        BuildUI();
        UpdateUI();

        // 获取玩家信息 + 验证码
        Task.Run(async () =>
        {
            // Phase 1: 等待炉石进程启动
            while (Process.GetProcessesByName("Hearthstone").Length == 0)
                await Task.Delay(1000);
            Console.WriteLine("[启动] ✅ 检测到炉石进程");

            // Phase 2: 获取 ID + 上传验证码（HearthMirror 失败时 5 秒重试）
            while (string.IsNullOrEmpty(ApiClient.VerificationCode))
            {
                var tagOk = HearthMirrorClient.FetchBattleTag();
                var loOk = HearthMirrorClient.FetchAccountId();

                if (tagOk && loOk && !string.IsNullOrEmpty(HearthMirrorClient.LocalPlayerBattleTag))
                {
                    _playerTag = HearthMirrorClient.LocalPlayerBattleTag;
                    Console.WriteLine("[启动] ✅ 玩家信息已获取: " + _playerTag + " Lo=" + HearthMirrorClient.LocalPlayerLo);
                    BeginInvoke(new Action(UpdateUI));

                    await ApiClient.UploadRatingAsync(
                        HearthMirrorClient.LocalPlayerBattleTag,
                        HearthMirrorClient.LocalPlayerLo,
                        0, _config.Region, _config.Mode);

                    if (!string.IsNullOrEmpty(ApiClient.VerificationCode))
                    {
                        _verifyCode = ApiClient.VerificationCode;
                        BeginInvoke(new Action(UpdateUI));
                    }
                    break;
                }

                await Task.Delay(5000);
            }
        });

        // 异步测试 API 连通性
        Task.Run(async () =>
        {
            var ok = await ApiClient.PingAsync();
            Console.WriteLine(ok ? "[API] ✅ 服务连接正常" : "[API] ❌ 服务连接异常");
            BeginInvoke(new Action(() =>
            {
                if (ok)
                {
                    lblStatus.Text = "● 服务正常";
                    lblStatus.ForeColor = C_GREEN;
                }
                else
                {
                    lblStatus.Text = "○ 服务异常";
                    lblStatus.ForeColor = C_RED;
                }
            }));
        });

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
                try { Process.Start($"{_config.ApiBaseUrl}/player/{Uri.EscapeDataString(_playerTag)}"); }
                catch { }
            }
        };

        lblStatus = MakeLabel("○ 检测中...", 290, 16, 130, 20, 9f, FontStyle.Bold, C_TEXT_MUTED);
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

        // ── 日志面板 ──
        var pnlLog = MakePanel(0, 356, 440, 130);
        pnlLog.BorderStyle = BorderStyle.FixedSingle;

        var lblLogTitle = MakeLabel("日志", 16, 4, 40, 14, 8f, FontStyle.Bold, C_TEXT_MUTED);
        pnlLog.Controls.Add(lblLogTitle);

        rtbLog = new RichTextBox
        {
            Location = new Point(0, 20),
            Size = new Size(440, 110),
            BackColor = Color.FromArgb(10, 15, 25),
            ForeColor = Color.FromArgb(148, 163, 184),
            Font = new Font("Consolas", 8f),
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            WordWrap = false,
        };
        pnlLog.Controls.Add(rtbLog);
        Controls.Add(pnlLog);

        // 重定向 Console 输出到日志面板 + 文件
        var fileWriter = Console.Out; // Program.cs 已经重定向到文件
        Console.SetOut(new UiTextWriter(rtbLog, fileWriter));
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
            case AppState.LeagueGame:
                var game = _parser?.Game;
                if (game != null && !string.IsNullOrEmpty(game.HeroName))
                {
                    lblGameIcon.Text = _state == AppState.LeagueGame ? "⚔️" : "🎮";
                    lblGameText.Text = $"{game.HeroName}";
                    lblGameSub.Text = _state == AppState.LeagueGame ? "🏆 联赛对局 · 等待结算" : "对局进行中";
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

        // 最近战绩（从持久化文件读取）
        pnlRecent.Controls.Clear();
        var recent = GameStore.GetRecent(5);
        for (int i = 0; i < recent.Count; i++)
        {
            var r = recent[i];
            var y = i * 28;
            var placement = r.Placement > 0 ? r.Placement.ToString() : "?";
            var heroName = string.IsNullOrEmpty(r.HeroName) ? "(未知英雄)" : r.HeroName;
            var ptsText = r.Points > 0 ? $"+{r.Points}" : r.Points.ToString();
            var ptsColor = r.Points >= 0 ? C_GREEN : C_RED;

            var badge = new Label
            {
                Text = placement,
                Location = new Point(0, y),
                Size = new Size(22, 22),
                Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold),
                ForeColor = r.Placement == 1 ? C_BG : C_TEXT_DIM,
                BackColor = GetPlacementColor(r.Placement),
                TextAlign = ContentAlignment.MiddleCenter,
            };
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

        // 今日统计（从持久化文件读取）
        var today = GameStore.GetToday();
        int totalToday = today.Count;
        int topFour = today.Count(r => r.Placement > 0 && r.Placement <= 4);
        double winRate = totalToday > 0 ? (double)topFour / totalToday * 100 : 0;
        double avgPlace = today.Count > 0 ? today.Where(r => r.Placement > 0).Average(r => r.Placement) : 0;
        int totalPoints = today.Sum(r => r.Points);

        SetStatValue(lblStatGames, totalToday.ToString());
        SetStatValue(lblStatWinRate, totalToday > 0 ? $"{winRate:F0}%" : "0%");
        SetStatValue(lblStatAvgPlace, today.Count > 0 ? avgPlace.ToString("F1") : "-");
        var pointsStr = totalPoints >= 0 ? $"+{totalPoints}" : totalPoints.ToString();
        SetStatValue(lblStatPoints, totalToday > 0 ? pointsStr : "-");

        // 验证码
        lblVerifyCode.Text = _verifyCode;
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



    // ═══════════════════════════════════════
    //  日志监控（后台线程）
    // ═══════════════════════════════════════

    void LogMonitorLoop()
    {
        // 等待日志出现
        string? logPath = null;
        var waitCount = 0;
        while (logPath == null)
        {
            logPath = LogPathFinder.Find(null);
            if (logPath == null)
            {
                waitCount++;
                if (waitCount % 5 == 1) // 每 15 秒打一次，避免刷屏
                    Console.WriteLine($"[日志] ⏳ 未找到 Power.log（已等待 {waitCount * 3} 秒），请确认炉石已启动");
                Thread.Sleep(3000);
            }
        }
        Console.WriteLine($"[日志] ✅ 找到: {logPath}");

        _parser = new Parser();
        long pos;

        try
        {
            _scanning = true;
            ScanExisting(logPath, out _parser, out pos);
            _scanning = false;

            // 检测空壳旧数据
            if (_parser.Game.IsActive
                && string.IsNullOrEmpty(_parser.Game.HeroName)
                && string.IsNullOrEmpty(_parser.Game.PlayerTag)
                && _parser.Game.AccountIdLo == 0)
            {
                Console.WriteLine("[日志] 📦 检测到旧数据（无有效信息），跳到文件尾等待新对局");
                _parser.Game.IsActive = false;
                pos = GetFileEnd(logPath);
            }
            else if (_parser.Game.IsActive)
            {
                Console.WriteLine($"[日志] 🔄 检测到进行中对局: {_parser.Game.PlayerTag} | 英雄={_parser.Game.HeroName} | Lo={_parser.Game.AccountIdLo}");
                _parser.ResetLobbyState();
                _state = AppState.InGame;
                _playerTag = _parser.Game.PlayerTag;
                TriggerCheckLeagueIfNeeded();
            }
            else
            {
                Console.WriteLine("[日志] 📭 无进行中对局，等待新游戏开始...");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[日志] ⚠️ 扫描异常: {ex.Message}，跳到文件尾");
            _parser = new Parser();
            pos = GetFileEnd(logPath);
        }

        UpdateUI();

        // 实时监控
        Console.WriteLine("[日志] 👁 开始实时监控，等待游戏事件...");
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
                        _scanning = true;
                        ScanExisting(currentPath, out _parser, out pos);
                        _scanning = false;

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
                            _parser.ResetLobbyState();
                            _state = AppState.InGame;
                            _playerTag = _parser.Game.PlayerTag;
                            TriggerCheckLeagueIfNeeded();
                        }
                        else
                        {
                            _state = AppState.Waiting;
                            pos = GetFileEnd(currentPath);
                        }
                        UpdateUI();
                    }
                }

                // 先检查文件是否有新数据，避免每 100ms 重复打开 FileStream
                long fileLen;
                try { fileLen = new FileInfo(currentPath).Length; }
                catch { Thread.Sleep(200); continue; }
                if (fileLen <= pos) { Thread.Sleep(100); continue; }

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
                    // 行前缀过滤（HDT 方案）：跳过 90% 的 PowerTaskList.DebugDump 冗余行
                    if (!line.Contains("GameState.") && !line.Contains("PowerTaskList.DebugPrintPower()")
                        && !line.Contains("PowerTaskList.DebugDump()"))
                        continue;

                    var evt = _parser.ProcessLine(line);
                    if (evt == null) continue;

                    switch (evt)
                    {
                        case "game_start":
                            _state = AppState.InGame;
                            _leagueChecked = false;
                            uiChanged = true;
                            break;
                        case "reconnect":
                            Console.WriteLine($"[游戏] 🔄 断线重连 {DateTime.Now:HH:mm:ss}");
                            break;
                        case "player_info":
                            _playerTag = _parser.Game.PlayerTag;
                            uiChanged = true;
                            break;
                        case "hero_entity":
                        case "hero_found":
                            uiChanged = true;
                            break;
                        case "check_league":
                            if (!_leagueChecked && !_scanning)
                            {
                                _leagueChecked = true;
                                var game = _parser.Game;

                                // HearthMirror 可能修正了 PlayerTag，同步到 UI
                                if (!string.IsNullOrEmpty(game.PlayerTag) && game.PlayerTag != _playerTag)
                                    _playerTag = game.PlayerTag;

                                Task.Run(async () =>
                                {
                                    // gameUuid 可能延迟加载，最多重试 3 次共 ~9 秒
                                    for (int attempt = 0; attempt < 3; attempt++)
                                    {
                                        if (!string.IsNullOrEmpty(game.GameUuid)) break;
                                        Console.WriteLine($"[MainForm] gameUuid 为空，等待 3 秒后重试...（第 {attempt + 1}/3 次）");
                                        await Task.Delay(3000);
                                        var freshUuid = HearthMirrorClient.LastGameUuid;
                                        if (!string.IsNullOrEmpty(freshUuid))
                                            game.GameUuid = freshUuid;
                                    }
                                    _currentGameUuid = !string.IsNullOrEmpty(game.GameUuid) ? game.GameUuid : Guid.NewGuid().ToString();

                                    var isLeague = await ApiClient.CheckLeagueAsync(
                                        _currentGameUuid,
                                        game.PlayerTag,
                                        game.AccountIdLo,
                                        game.LobbyPlayers,
                                        _config.Region,
                                        _config.Mode,
                                        DateTime.UtcNow.ToString("o"));

                                    if (_config.TestMode)
                                    {
                                        // 测试模式：无论服务端返回什么，都强制标记为联赛
                                        isLeague = true;
                                        Console.WriteLine("[API] [TEST] 强制标记为联赛对局（忽略 isLeague=false）");
                                    }

                                    // 验证码无论是否联赛都更新（check-league 总会返回）
                                    if (!string.IsNullOrEmpty(ApiClient.VerificationCode))
                                        _verifyCode = ApiClient.VerificationCode;

                                    if (isLeague)
                                    {
                                        _state = AppState.LeagueGame;
                                    }
                                    BeginInvoke(new Action(UpdateUI));
                                });
                            }
                            break;
                        case "game_end":
                        case "concede":
                            // 如果是联赛对局，上报排名
                            if (_state == AppState.LeagueGame && _parser.Game.HeroPlacement > 0)
                            {
                                var game = _parser.Game;
                                var placement = game.HeroPlacement;
                                var gameUuid = _currentGameUuid;
                                var playerTag = game.PlayerTag;
                                var accountIdLo = game.AccountIdLo;
                                Task.Run(async () =>
                                {
                                    var ok = await ApiClient.UpdatePlacementAsync(gameUuid, playerTag, accountIdLo, placement);
                                    if (ok)
                                    {
                                        var points = placement == 1 ? 9 : Math.Max(1, 9 - placement);
                                        GameStore.Save(new GameRecord
                                        {
                                            BattleTag = playerTag,
                                            HeroName = game.HeroName,
                                            HeroCardId = game.HeroCardId,
                                            Placement = placement,
                                            Points = points,
                                            GameUuid = gameUuid,
                                            Timestamp = DateTime.UtcNow.ToString("o"),
                                        });
                                    }
                                    BeginInvoke(new Action(() =>
                                    {
                                        _state = ok ? AppState.Uploaded : AppState.Waiting;
                                        UpdateUI();
                                    }));
                                });
                            }
                            else
                            {
                                _state = AppState.Waiting;
                                uiChanged = true;
                            }
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

    /// <summary>
    /// 扫描完成后补发 check-league（STEP 13 已过，不会在实时监控中再次触发）
    /// </summary>
    void TriggerCheckLeagueIfNeeded()
    {
        if (_leagueChecked) return;
        if (!_parser.Game.IsActive) return;

        _leagueChecked = true;
        var game = _parser.Game;

        if (!string.IsNullOrEmpty(game.PlayerTag) && game.PlayerTag != _playerTag)
            _playerTag = game.PlayerTag;

        Console.WriteLine("[日志] 📋 扫描完成，补发 check-league（STEP 13 已过）");

        Task.Run(async () =>
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (!string.IsNullOrEmpty(game.GameUuid)) break;
                Console.WriteLine($"[MainForm] gameUuid 为空，等待 3 秒后重试...（第 {attempt + 1}/3 次）");
                await Task.Delay(3000);
                var freshUuid = HearthMirrorClient.LastGameUuid;
                if (!string.IsNullOrEmpty(freshUuid))
                    game.GameUuid = freshUuid;
            }
            _currentGameUuid = !string.IsNullOrEmpty(game.GameUuid) ? game.GameUuid : Guid.NewGuid().ToString();

            var isLeague = await ApiClient.CheckLeagueAsync(
                _currentGameUuid,
                game.PlayerTag,
                game.AccountIdLo,
                game.LobbyPlayers,
                _config.Region,
                _config.Mode,
                DateTime.UtcNow.ToString("o"));

            if (_config.TestMode)
            {
                isLeague = true;
                Console.WriteLine("[API] [TEST] 强制标记为联赛对局（忽略 isLeague=false）");
            }

            if (!string.IsNullOrEmpty(ApiClient.VerificationCode))
                _verifyCode = ApiClient.VerificationCode;

            if (isLeague)
                _state = AppState.LeagueGame;

            BeginInvoke(new Action(UpdateUI));
        });
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

/// <summary>
/// 双写 TextWriter：同时写入 RichTextBox（UI 线程安全）和底层文件 writer
/// </summary>
class UiTextWriter : TextWriter
{
    private readonly RichTextBox _rtb;
    private readonly TextWriter _inner;
    private readonly int _maxLines = 200;

    public UiTextWriter(RichTextBox rtb, TextWriter inner)
    {
        _rtb = rtb;
        _inner = inner;
    }

    public override Encoding Encoding => Encoding.UTF8;

    public override void WriteLine(string? value)
    {
        _inner.WriteLine(value); // 写文件（后台线程安全）
        AppendToUi(value ?? "");
    }

    public override void Write(string? value)
    {
        _inner.Write(value);
        if (!string.IsNullOrEmpty(value) && value.Contains('\n'))
            AppendToUi(value!);
    }

    private void AppendToUi(string text)
    {
        if (_rtb.IsDisposed) return;
        try
        {
            if (_rtb.InvokeRequired)
                _rtb.BeginInvoke(new Action<string>(AppendToUi), text);
            else
            {
                // 自动裁剪：超过 maxLines 删前半
                if (_rtb.Lines.Length > _maxLines)
                {
                    var keep = _maxLines / 2;
                    var lines = _rtb.Lines;
                    var sb = new StringBuilder();
                    for (int i = lines.Length - keep; i < lines.Length; i++)
                        sb.AppendLine(lines[i]);
                    _rtb.Text = sb.ToString();
                }
                _rtb.AppendText(text + "\n");
                _rtb.ScrollToCaret();
            }
        }
        catch { }
    }
}

}
