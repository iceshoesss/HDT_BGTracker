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
    volatile AppState _state = AppState.Waiting;
    string _playerTag = "";
    string _verifyCode = "待接入";
    Parser? _parser;
    readonly object _parserLock = new object(); // 保护 _parser 引用的读写
    Config _config = null!;
    volatile bool _leagueChecked = false; // 当前对局是否已调过 check-league
    string _currentGameUuid = "";
    volatile bool _scanning = false; // 扫描旧日志期间不触发 check_league

    // check-league 持续重试（初始 3 次失败后，每 15 秒重试直到成功或对局结束）
    System.Threading.Timer? _checkLeagueRetryTimer;
    string? _pendingPlayerTag;
    ulong? _pendingAccountIdLo;
    List<LobbyPlayer>? _pendingLobbyPlayers;
    string? _pendingRegion;
    string? _pendingMode;

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
                Game? game;
                lock (_parserLock) { game = _parser?.Game; }
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
        // 等待 Power.log 出现（进入酒馆战棋时生成）
        string? logPath = null;
        var initHsPid = 0;
        var hmFetchStart = DateTime.MinValue; // 开始尝试获取玩家信息的时间
        try { var p = Process.GetProcessesByName("Hearthstone"); if (p.Length > 0) initHsPid = p[0].Id; } catch { }

        while (logPath == null)
        {
            // 检测炉石进程变化（重启/关闭），重置缓存重新搜索
            var curPid = 0;
            try { var procs = Process.GetProcessesByName("Hearthstone"); if (procs.Length > 0) curPid = procs[0].Id; } catch { }
            if (curPid != initHsPid)
            {
                if (curPid == 0)
                {
                    Console.WriteLine("[日志] ⚠️ 炉石进程已退出");
                    initHsPid = curPid;
                    LogPathFinder.ResetProcessDirCache();
                    HearthMirrorClient.Reset();
                }
                else if (initHsPid == 0)
                {
                    if (hmFetchStart == DateTime.MinValue)
                    {
                        Console.WriteLine($"[日志] 🔄 炉石已启动（PID {curPid}）");
                        LogPathFinder.ResetProcessDirCache();
                        HearthMirrorClient.Reset();
                        hmFetchStart = DateTime.UtcNow;
                    }

                    // 尝试获取玩家信息，失败则下轮静默重试
                    var tagOk = HearthMirrorClient.FetchBattleTag();
                    var loOk = HearthMirrorClient.FetchAccountId();
                    if (tagOk && loOk && !string.IsNullOrEmpty(HearthMirrorClient.LocalPlayerBattleTag))
                    {
                        _playerTag = HearthMirrorClient.LocalPlayerBattleTag;
                        Console.WriteLine("[启动] ✅ 玩家信息已获取: " + _playerTag);
                        try
                        {
                            ApiClient.UploadRatingAsync(
                                HearthMirrorClient.LocalPlayerBattleTag,
                                HearthMirrorClient.LocalPlayerLo,
                                0, _config.Region, _config.Mode).GetAwaiter().GetResult();
                            if (!string.IsNullOrEmpty(ApiClient.VerificationCode))
                            {
                                _verifyCode = ApiClient.VerificationCode;
                                Console.WriteLine("[启动] ✅ 验证码: " + _verifyCode);
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("[启动] ⚠️ upload-rating 失败: " + ex.Message);
                        }
                        initHsPid = curPid; // 成功后才更新，避免重复获取
                        hmFetchStart = DateTime.MinValue;
                        BeginInvoke(new Action(UpdateUI));
                    }
                    else if ((DateTime.UtcNow - hmFetchStart).TotalSeconds > 30)
                    {
                        Console.WriteLine("[启动] ⏳ HearthMirror 未就绪，稍后重试...");
                        // 不更新 initHsPid，下轮循环重试
                    }
                    // 30 秒内静默重试，不打印
                }
                else
                {
                    Console.WriteLine($"[日志] 🔄 炉石进程已重启（PID {initHsPid}→{curPid}）");
                    initHsPid = curPid;
                    LogPathFinder.ResetProcessDirCache();
                    HearthMirrorClient.Reset();
                }
            }

            logPath = LogPathFinder.Find(null);
            if (logPath == null)
                Thread.Sleep(3000);
        }
        Console.WriteLine("[日志] ✅ 已定位 Power.log");

        // 单次获取玩家信息 + 验证码（Find() 返回后、ScanExisting 之前）
        if (string.IsNullOrEmpty(HearthMirrorClient.LocalPlayerBattleTag))
        {
            Console.WriteLine("[启动] ⏳ 尝试获取玩家信息...");
            var tagOk = HearthMirrorClient.FetchBattleTag();
            var loOk = HearthMirrorClient.FetchAccountId();
            if (tagOk && loOk && !string.IsNullOrEmpty(HearthMirrorClient.LocalPlayerBattleTag))
            {
                _playerTag = HearthMirrorClient.LocalPlayerBattleTag;
                Console.WriteLine("[启动] ✅ 玩家信息已获取: " + _playerTag + " Lo=" + HearthMirrorClient.LocalPlayerLo);
                BeginInvoke(new Action(UpdateUI));
                try
                {
                    ApiClient.UploadRatingAsync(
                        HearthMirrorClient.LocalPlayerBattleTag,
                        HearthMirrorClient.LocalPlayerLo,
                        0, _config.Region, _config.Mode).GetAwaiter().GetResult();
                    if (!string.IsNullOrEmpty(ApiClient.VerificationCode))
                    {
                        _verifyCode = ApiClient.VerificationCode;
                        BeginInvoke(new Action(UpdateUI));
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[启动] ⚠️ upload-rating 失败: " + ex.Message);
                }
            }
            else
            {
                Console.WriteLine("[启动] ⏳ 玩家信息暂不可用，等待游戏中自动获取");
            }
        }

        Parser parser;
        long pos;

        try
        {
            _scanning = true;
            lock (_parserLock) { ScanExisting(logPath, out _parser, out pos); parser = _parser; }
            _scanning = false;

            HandleScannedGameState(parser);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[日志] ⚠️ 扫描异常: {ex.Message}，跳到文件尾");
            lock (_parserLock) { _parser = new Parser(); parser = _parser; }
            pos = GetFileEnd(logPath);
        }

        UpdateUI();

        // 实时监控
        Console.WriteLine("[日志] 👁 开始实时监控，等待游戏事件...");
        var currentPath = logPath;
        var fileCheckCounter = 0;
        var consecutiveFileErrors = 0; // 连续文件不存在计数

        // 追踪炉石进程 PID，主动检测重启（不依赖 TryInit 被调用）
        var hsPid = 0;
        var hmRestartFetchStart = DateTime.MinValue; // 重启后开始尝试获取的时间
        try { var p = Process.GetProcessesByName("Hearthstone"); if (p.Length > 0) hsPid = p[0].Id; } catch { }

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
                        Console.WriteLine($"[日志] 🔄 发现新日志文件: {newPath}");
                        currentPath = newPath;
                        if (TryScanAndSwitch(currentPath, ref parser, ref pos))
                            UpdateUI();
                    }
                }

                // 主动检测炉石进程重启（通过 PID 变化，不依赖 TryInit）
                var currentHsPid = 0;
                try { var procs = Process.GetProcessesByName("Hearthstone"); if (procs.Length > 0) currentHsPid = procs[0].Id; } catch { }
                if (currentHsPid != 0 && currentHsPid != hsPid)
                {
                    if (hmRestartFetchStart == DateTime.MinValue)
                    {
                        Console.WriteLine($"[重启] 🔄 检测到炉石进程变化（PID {hsPid}→{currentHsPid}），重新获取玩家信息...");
                        HearthMirrorClient.Reset();
                        LogPathFinder.ResetProcessDirCache();
                        hmRestartFetchStart = DateTime.UtcNow;
                    }

                    var tagOk = HearthMirrorClient.FetchBattleTag();
                    var loOk = HearthMirrorClient.FetchAccountId();
                    if (tagOk && loOk && !string.IsNullOrEmpty(HearthMirrorClient.LocalPlayerBattleTag))
                    {
                        hsPid = currentHsPid; // 成功后才更新，失败时下轮继续重试
                        hmRestartFetchStart = DateTime.MinValue;
                        _playerTag = HearthMirrorClient.LocalPlayerBattleTag;
                        Console.WriteLine("[重启] ✅ 新玩家: " + _playerTag + " Lo=" + HearthMirrorClient.LocalPlayerLo);

                        try
                        {
                            ApiClient.UploadRatingAsync(
                                HearthMirrorClient.LocalPlayerBattleTag,
                                HearthMirrorClient.LocalPlayerLo,
                                0, _config.Region, _config.Mode).GetAwaiter().GetResult();

                            if (!string.IsNullOrEmpty(ApiClient.VerificationCode))
                            {
                                _verifyCode = ApiClient.VerificationCode;
                                Console.WriteLine("[重启] ✅ 新验证码: " + _verifyCode);
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("[重启] ⚠️ upload-rating 失败: " + ex.Message);
                        }

                        _state = AppState.Waiting;
                        BeginInvoke(new Action(UpdateUI));
                    }
                    else if ((DateTime.UtcNow - hmRestartFetchStart).TotalSeconds > 30)
                    {
                        Console.WriteLine("[重启] ⏳ HearthMirror 未就绪，稍后重试...");
                        // 不更新 hsPid，下轮循环继续重试
                    }
                    // 30 秒内静默重试，不打印
                }
                else if (currentHsPid != 0)
                {
                    hsPid = currentHsPid; // 更新缓存（处理首次获取或进程列表变化）
                }
                else if (currentHsPid == 0 && hsPid != 0)
                {
                    Console.WriteLine("[日志] ⚠️ 炉石进程已退出");
                    hsPid = 0;
                    LogPathFinder.ResetProcessDirCache();
                }

                // 先检查文件是否有新数据，避免每 100ms 重复打开 FileStream
                long fileLen;
                try { fileLen = new FileInfo(currentPath).Length; }
                catch (FileNotFoundException)
                {
                    // Power.log 被删除（炉石退出），立即搜索新日志
                    consecutiveFileErrors++;
                    if (consecutiveFileErrors == 1)
                        Console.WriteLine("[日志] ⚠️ Power.log 已删除（炉石可能已退出），搜索新日志...");

                    var newPath = LogPathFinder.Find(null);
                    if (newPath != null && !string.Equals(
                        Path.GetFullPath(newPath), Path.GetFullPath(currentPath),
                        StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"[日志] ✅ 找到新日志: {newPath}");
                        currentPath = newPath;
                        consecutiveFileErrors = 0;
                        if (TryScanAndSwitch(currentPath, ref parser, ref pos))
                            UpdateUI();
                    }
                    else
                    {
                        // 没找到新日志，等一会再找（炉石可能还在启动中）
                        Thread.Sleep(2000);
                    }
                    continue;
                }
                catch
                {
                    Thread.Sleep(200);
                    continue;
                }
                consecutiveFileErrors = 0;
                if (fileLen < pos)
                {
                    // 文件被截断或切换了新文件，重置位置
                    Console.WriteLine($"[日志] 🔄 检测到日志文件变化（{pos}→{fileLen}），重置读取位置");
                    pos = 0;
                }
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

                    var evt = parser.ProcessLine(line);
                    if (evt == null) continue;

                    switch (evt)
                    {
                        case "game_start":
                            StopCheckLeagueRetry();
                            _state = AppState.InGame;
                            _leagueChecked = false;
                            _currentGameUuid = "";
                            uiChanged = true;
                            break;
                        case "reconnect":
                            Console.WriteLine($"[游戏] 🔄 断线重连 {DateTime.Now:HH:mm:ss}");
                            if (_state == AppState.Waiting)
                                _state = AppState.InGame;
                            break;
                        case "player_info":
                            _playerTag = parser.Game.PlayerTag;
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
                                var game = parser.Game;

                                // HearthMirror 可能修正了 PlayerTag，同步到 UI
                                if (!string.IsNullOrEmpty(game.PlayerTag) && game.PlayerTag != _playerTag)
                                    _playerTag = game.PlayerTag;

                                if (game.LobbyPlayers == null || game.LobbyPlayers.Count == 0)
                                {
                                    Console.WriteLine("[MainForm] ⚠️ LobbyPlayers 为空，跳过 check-league");
                                    _leagueChecked = false;
                                    break;
                                }

                                TryCheckLeagueWithRetry(
                                    game.PlayerTag, game.AccountIdLo, game.LobbyPlayers,
                                    _config.Region, _config.Mode);
                            }
                            break;
                        case "game_end":
                        case "concede":
                            StopCheckLeagueRetry();
                            // 如果是联赛对局，上报排名
                            if (_state == AppState.LeagueGame && parser.Game.HeroPlacement > 0)
                            {
                                var game = parser.Game;
                                var placement = game.HeroPlacement;
                                var gameUuid = _currentGameUuid;
                                var playerTag = !string.IsNullOrEmpty(game.PlayerTag) ? game.PlayerTag : _playerTag;
                                var accountIdLo = game.AccountIdLo;
                                Task.Run(async () =>
                                {
                                    var ok = await ApiClient.UpdatePlacementAsync(gameUuid, playerTag, accountIdLo, placement, game.ReconnectTimes);
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
                // 文件在 FileInfo 检查后、FileStream 读取前被删除（竞态）
                // 下一轮循环的 FileInfo 检查会处理，这里只做短暂等待
                Console.WriteLine("[日志] ⚠️ 读取时日志文件消失，下轮重试");
                Thread.Sleep(500);
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
        Game game;
        lock (_parserLock)
        {
            if (_parser == null || !_parser.Game.IsActive) return;
            game = _parser.Game;
        }

        // 扫描阶段如果没读到 STEP 13（英雄选择中才打开工具），数据不完整
        // 不锁 _leagueChecked，等真正的 STEP 13 触发
        if (string.IsNullOrEmpty(game.HeroName) || game.LobbyPlayers == null || game.LobbyPlayers.Count == 0)
        {
            Console.WriteLine("[日志] 📋 扫描完成，但 STEP 13 数据未就绪（可能中途启动），等待实时 STEP 13 触发");
            return;
        }

        _leagueChecked = true;

        if (!string.IsNullOrEmpty(game.PlayerTag) && game.PlayerTag != _playerTag)
            _playerTag = game.PlayerTag;

        // 数据不可用 → 解锁 _leagueChecked，等真正的 STEP 13 重试
        var hasValidLo = game.LobbyPlayers != null && game.LobbyPlayers.Any(p => p.Lo != 0);
        if (game.AccountIdLo == 0 && !hasValidLo)
        {
            Console.WriteLine("[MainForm] ⚠️ 补发 check-league 数据不可用（Lo 全 0），等待实时 STEP 13 重试");
            _leagueChecked = false;
            return;
        }

        Console.WriteLine("[日志] 📋 扫描完成，补发 check-league（STEP 13 已过）");

        TryCheckLeagueWithRetry(
            game.PlayerTag, game.AccountIdLo, game.LobbyPlayers!,
            _config.Region, _config.Mode);
    }

    /// <summary>
    /// 尝试 check-league（初始 3 次快速重试）。错误则启动 15 秒周期重试，非联赛对局直接停止。
    /// </summary>
    void TryCheckLeagueWithRetry(string playerTag, ulong accountIdLo,
        List<LobbyPlayer> lobbyPlayers, string region, string mode)
    {
        Task.Run(async () =>
        {
            // 初始 3 次快速重试（仅在错误/异常时重试）
            for (int retry = 0; retry < 3 && _state == AppState.InGame; retry++)
            {
                var ok = await ApiClient.CheckLeagueAsync(playerTag, accountIdLo, lobbyPlayers, region, mode, DateTime.UtcNow.ToString("o"));
                if (ok == true)
                {
                    HandleCheckLeagueResult();
                    return;
                }
                else if (ok == false)
                {
                    Console.WriteLine("[API] 非联赛对局，停止 check-league");
                    return;
                }
                // ok == null（错误），继续重试
                if (retry < 2) await Task.Delay(1000 * (retry + 1));
            }

            // 3 次全错误 → 启动 15 秒周期重试
            Console.WriteLine("[API] ⚠️ check-league 初始重试失败，启动 15 秒周期重试...");
            _pendingPlayerTag = playerTag;
            _pendingAccountIdLo = accountIdLo;
            _pendingLobbyPlayers = lobbyPlayers;
            _pendingRegion = region;
            _pendingMode = mode;

            _checkLeagueRetryTimer?.Dispose();
            _checkLeagueRetryTimer = new System.Threading.Timer(async _ =>
            {
                if (_state != AppState.InGame)
                {
                    StopCheckLeagueRetry();
                    return;
                }

                var retryOk = await ApiClient.CheckLeagueAsync(
                    _pendingPlayerTag!, _pendingAccountIdLo!.Value, _pendingLobbyPlayers!,
                    _pendingRegion!, _pendingMode!, DateTime.UtcNow.ToString("o"));

                if (retryOk == true)
                {
                    HandleCheckLeagueResult();
                    StopCheckLeagueRetry();
                    Console.WriteLine("[API] ✅ check-league 周期重试成功");
                }
                else if (retryOk == false)
                {
                    // 非联赛对局，停止重试
                    StopCheckLeagueRetry();
                    Console.WriteLine("[API] 非联赛对局，停止 check-league 周期重试");
                }
                else
                {
                    // 错误，继续重试
                    Console.WriteLine("[API] ⏳ check-league 重试失败，15 秒后继续...");
                }
            }, null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        });
    }

    /// <summary>
    /// 处理 check-league 成功响应（提取 gameUuid、验证码、设置状态）
    /// </summary>
    void HandleCheckLeagueResult()
    {
        if (!string.IsNullOrEmpty(ApiClient.ServerGameUuid))
            _currentGameUuid = ApiClient.ServerGameUuid;

        if (_config.TestMode)
        {
            Console.WriteLine("[API] [TEST] 强制标记为联赛对局（忽略 isLeague=false）");
            _state = AppState.LeagueGame;
        }
        else if (ApiClient.LastLeagueResult)
        {
            _state = AppState.LeagueGame;
        }

        if (!string.IsNullOrEmpty(ApiClient.VerificationCode))
            _verifyCode = ApiClient.VerificationCode;

        BeginInvoke(new Action(UpdateUI));
    }

    /// <summary>
    /// 停止 check-league 周期重试
    /// </summary>
    void StopCheckLeagueRetry()
    {
        _checkLeagueRetryTimer?.Dispose();
        _checkLeagueRetryTimer = null;
        _pendingPlayerTag = null;
        _pendingAccountIdLo = null;
        _pendingLobbyPlayers = null;
        _pendingRegion = null;
        _pendingMode = null;
    }

    /// <summary>
    /// 扫描新日志文件并切换状态（消除三处重复代码）
    /// </summary>
    /// <returns>true=成功扫描并切换，false=扫描失败或未找到文件</returns>
    bool TryScanAndSwitch(string filePath, ref Parser parser, ref long pos)
    {
        try
        {
            _scanning = true;
            lock (_parserLock) { ScanExisting(filePath, out _parser, out pos); parser = _parser; }
            _scanning = false;

            HandleScannedGameState(parser);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[日志] ⚠️ 扫描新日志异常: {ex.Message}，跳到文件尾");
            _scanning = false;
            lock (_parserLock) { _parser = new Parser(); parser = _parser; }
            pos = GetFileEnd(filePath);
            return true; // 仍然算成功切换，只是数据丢了
        }
    }

    /// <summary>
    /// 扫描完成后根据游戏状态设置 UI（消除三处重复代码）
    /// </summary>
    void HandleScannedGameState(Parser parser)
    {
        if (parser.Game.IsActive
            && string.IsNullOrEmpty(parser.Game.HeroName)
            && string.IsNullOrEmpty(parser.Game.PlayerTag)
            && parser.Game.AccountIdLo == 0)
        {
            Console.WriteLine("[日志] 📦 检测到旧数据（无有效信息），跳到文件尾等待新对局");
            parser.Game.IsActive = false;
        }
        else if (parser.Game.IsActive)
        {
            Console.WriteLine($"[日志] 🔄 检测到进行中对局: {parser.Game.PlayerTag} | 英雄={parser.Game.HeroName} | Lo={parser.Game.AccountIdLo}");
            parser.ResetLobbyState();
            _state = AppState.InGame;
            _leagueChecked = false; // 新对局必须重新 check-league（上一局的标记不能带到下一局）
            _currentGameUuid = "";  // 清空上一局的 gameUuid，等 check-league 返回新的
            _playerTag = parser.Game.PlayerTag;

            // 扫描阶段跳过了 STEP 13 的 HearthMirror 调用（IsScanning=true），
            // 如果 LobbyPlayers 为空但 PlayerTag 有效，主动获取
            var game = parser.Game;
            if (!string.IsNullOrEmpty(game.PlayerTag)
                && (game.LobbyPlayers == null || game.LobbyPlayers.Count == 0))
            {
                Console.WriteLine("[日志] 📋 中途启动，主动获取 LobbyPlayers...");
                game.LobbyPlayers = HearthMirrorClient.FetchLobbyPlayers(game.PlayerTag);
                game.GameUuid = HearthMirrorClient.LastGameUuid;
                if (HearthMirrorClient.LocalPlayerLo != 0)
                    game.AccountIdLo = HearthMirrorClient.LocalPlayerLo;
                Console.WriteLine($"[日志] 📋 获取到 {game.LobbyPlayers.Count} 个玩家 | Lo={game.AccountIdLo}");
            }

            TriggerCheckLeagueIfNeeded();
        }
        else
        {
            Console.WriteLine("[日志] 📭 无进行中对局，等待新游戏开始...");
        }
    }

    static void ScanExisting(string filePath, out Parser parser, out long pos)
    {
        parser = new Parser();
        parser.IsScanning = true;  // 扫描阶段不做 HearthMirror 调用（游戏未就绪），留给实时阶段
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
        parser.IsScanning = false;
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
        catch (Exception ex)
        {
            // UI 日志渲染失败时写入文件日志，避免静默丢失
            try { _inner.WriteLine($"[UI日志异常] {ex.GetType().Name}: {ex.Message}"); } catch { }
        }
    }
}

}
