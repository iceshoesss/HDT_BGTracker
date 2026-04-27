# HDT_BGTracker

炉石传说酒馆战棋联赛插件（HDT 插件），每局结束后自动检测联赛对局并上传排名。

**配套联赛网站**：[LeagueWeb](https://github.com/iceshoesss/LeagueWeb)（已拆分为独立仓库）

## 项目结构

```
HDT_BGTracker/
├── HDT_BGTracker/          # C# HDT 插件（HTTP API 客户端）
│   ├── BGTrackerPlugin.cs  # 插件入口
│   ├── RatingTracker.cs    # 核心逻辑（联赛匹配 + 排名上传）
│   ├── LobbyOverlay.cs     # 游戏内浮动面板（已禁用）
│   └── HDT_BGTracker.csproj
├── bg_tool/                # C# 日志解析器（独立可执行程序，替代 bg_parser）
│   ├── Program.cs          # 入口：批量解析 + 实时监控
│   ├── Parser/Parser.cs    # 核心状态机（预编译正则）
│   ├── Models/Game.cs      # 数据模型
│   ├── Services/           # 日志路径查找 + HearthMirror 集成
│   └── bg_tool.csproj
├── bg_parser/              # Python 日志解析器（参考实现）
│   ├── bg_parser.py        # Power.log 实时解析（含 HearthMirror 集成）
│   └── test_lobby_reader.py # HearthMirror 调试工具
├── API.md                  # 插件调用的 API 文档（与 LeagueWeb 共享）
├── DEV_NOTES.md            # 开发文档（踩坑记录）
└── sync.ps1 / sync.sh      # 同步脚本（保护本地配置）
```

## 版本管理

### 版本号规则

采用 `v主版本.次版本.修订号`（如 `v0.5.2`）：

- **修订号 +1** — 修 bug（`v0.5.2` → `v0.5.3`）
- **次版本 +1** — 加新功能（`v0.5.2` → `v0.6.0`）
- **主版本 +1** — 大改/重构/正式发布（`v0.x.x` → `v1.0.0`）

### 修改版本号的位置

两个地方必须同步修改：

| 文件 | 位置 | 用途 |
|------|------|------|
| `HDT_BGTracker/HDT_BGTracker.csproj` | `<Version>1.0.0</Version>` | 程序集版本 |
| `HDT_BGTracker/BGTrackerPlugin.cs` | `public Version Version => new Version(1, 0, 0);` | HDT 插件显示版本 |

### Docker 镜像 tag

构建时同时打版本号和 `latest`：

```bash
docker build -t league-web:v0.1.0 -t league-web:latest ./league
```

## Docker 部署（联赛网站）

### 前置条件

## 联赛网站

联赛网站已拆分为独立仓库：[LeagueWeb](https://github.com/iceshoesss/LeagueWeb)

- **main 分支**：积分赛（排行榜 + 报名队列 + 等待队列）
- **feat/knockout 分支**：淘汰赛（BO N 赛制 + 对阵图 + 自动晋级）

部署、配置、环境变量等详见 LeagueWeb 仓库。

> **淘汰赛不需要改插件。** 插件只上报 8 个 accountIdLo，判断逻辑全在 Flask 侧。check-league 先查 tournament_groups 匹配（淘汰赛），没匹配到再查 waiting_queue（积分赛）。

## C# 插件编译

### 前置条件

- .NET SDK
- 安装好的 HDT（Hearthstone Deck Tracker）

### 步骤

```powershell
cd HDT_BGTracker
$env:HDT_PATH = "你的HDT安装路径"
dotnet build -c Release
```

编译成功后，`bin\Release\net472\` 下只有一个 `HDT_BGTracker.dll`。

### 配置

`RatingTracker.cs` 中的 `ApiBaseUrl` 需要改成实际 API 地址：

```csharp
private const string ApiBaseUrl = "http://localhost:5000";  // 本地测试
// private const string ApiBaseUrl = "https://你的域名";     // 生产部署
```

### 打包安装

```powershell
cd bin\Release\net472
tar -a -cf HDT_BGTracker.zip HDT_BGTracker.dll
```

### 使用

- 插件启用后自动运行，无需手动操作
- 联赛对局自动识别并记录排名，非联赛对局不处理
- 点击插件设置中的「测试连接」按钮可验证 API 连接
- 日志在 `%AppData%\HearthstoneDeckTracker\BGTracker\tracker.log`

## C# 日志解析器（bg_tool）

独立可执行程序，直接读取游戏日志 Power.log 提取对局数据。替代 Python bg_parser，用户只需运行一个程序。

### 编译

```powershell
cd bg_tool
$env:HDT_PATH = "C:\...\HDT"  # HDT 安装目录，启用 HearthMirror 获取对手 Lo
dotnet build -c Release
```

编译产物：`bin\Release\net472\bg_tool.exe`（x86 32 位）

> **注意**：HearthMirror.dll 是 32 位程序集，bg_tool 必须以 x86 运行（csproj 已配置 `PlatformTarget=x86`）。

### 用法

```powershell
# 实时监控模式（默认）
$env:HDT_PATH = "C:\...\HDT"
.\bin\Release\net472\bg_tool.exe

# 解析已有日志
.\bin\Release\net472\bg_tool.exe --parse "D:\...\Power.log"

# 指定日志路径
.\bin\Release\net472\bg_tool.exe "D:\...\Power.log"
```

### 配置（config.json）

编译后在 exe 同目录创建 `config.json`：

```json
{
  "apiBaseUrl": "http://localhost:5000",
  "region": "CN",
  "mode": "solo",
  "testMode": true
}
```

| 字段 | 说明 |
|------|------|
| `apiBaseUrl` | Flask API 地址 |
| `region` | 服务器区域（bg_tool 无法从游戏获取，需手动配置） |
| `mode` | 游戏模式（bg_tool 无法从游戏获取，需手动配置） |
| `testMode` | 测试模式：跳过 check-league 判定，所有对局强制标记为联赛 |

> **注意**：`PLUGIN_API_KEY` 为编译时常量，已嵌入二进制文件中，不暴露在 config.json。发布前需替换 `ApiClient.cs` 中的 `const string ApiKey`。"

### 调试（mock_server.py）

用 mock 服务器验证 bg_tool 上报数据，不需要 MongoDB：

```bash
python bg_tool/mock_server.py       # 启动 mock 服务器（默认 5000 端口）
python bg_tool/mock_server.py 8080  # 指定端口
```

config.json 中 `apiBaseUrl` 设为 `http://localhost:5000`，打一局后 mock 服务器会打印完整请求数据。请求记录同时保存到 `mock_requests.log`。

### 功能

- **自动查找日志**：Windows 注册表 → 常见安装路径 → Logs 目录下最新文件夹
- **实时监控**：tail 模式，游戏中即时输出排名变化
- **自动切换**：玩家重启游戏时自动检测新日志文件夹并切换
- **中途接入**：游戏中启动脚本可显示已选英雄和玩家信息
- **断线重连**：自动识别并恢复对局状态
- **投降检测**：检测投降行为并记录排名
- **可提取数据**：本地玩家 BattleTag、accountIdLo、英雄名+cardId、排名 1-8
- **HearthMirror 集成**（需设置 `HDT_PATH`）：第一轮战斗结束时获取 8 个玩家的 AccountId.Lo + HeroCardId

### 输出示例

```
👁 监控: D:\Battle.net\Hearthstone\Logs\Hearthstone_2026_04_19\Power.log
   (Ctrl+C 停止)
   等待游戏开始...
  [21:29:19] 👤 南怀北瑾丨少头脑#5267
  [21:29:19] 🎮 新局开始
  [21:29:29] 🦸 选定英雄: 风暴之王托里姆
[HearthMirror] 📋 获取到 8 个玩家
   Lo=155147517, Hero=BG32_HERO_002
   Lo=80547085, Hero=BG34_HERO_004
   ...
──────────────────────────────────────────────────
🎮 对局
👤 南怀北瑾丨少头脑#5267
   账号ID: 1708070391
🦸 英雄: 风暴之王托里姆 (BG27_HERO_801)
🏆 排名: 第 3 名（不确定，游戏内最终观测值）
```

### Python 日志解析器（bg_parser）

bg_parser 是 Python 参考实现，bg_tool 基于它 1:1 翻译为 C#。功能完全一致，保留用于对比调试。

```bash
python bg_parser/bg_parser.py              # 实时监控
python bg_parser/bg_parser.py --parse log  # 解析已有日志
```

## 数据结构

MongoDB 数据库: `hearthstone`，集合: `player_records`

```json
{
  "playerId": "玩家名#1234",
  "accountIdLo": "1708070391",
  "rating": 6500,
  "lastRating": 6477,
  "ratingChange": 23,
  "gameCount": 42,
  "mode": "solo",
  "timestamp": "2026-04-07T09:15:00.0000000Z",
  "region": "CN",
  "verificationCode": "A1B2C3D4"
}
```

详细字段说明和 API 文档见 [API.md](API.md)。

## 当前开发状态

### bg_tool v0.4.0 (2026-04-24)
- **gameUuid 改为服务端生成**：修复同一局不同玩家生成不同 UUID 导致淘汰赛匹配失败
- 移除 `GenerateDeterministicUuid`，check-league 不再发送 gameUuid
- 使用服务端返回的 `gameUuid` 进行 update-placement

### HDT 插件 v0.7.0 (2026-04-24)
- **gameUuid 改为服务端生成**：同 bg_tool v0.4.0
- 移除本地 SHA256 UUID 计算，从 check-league 响应中提取服务端 gameUuid

### bg_tool v0.3.1 (2026-04-24)
- HttpClient 禁用系统代理：修复玩家开代理/加速器时 API 请求失败（浏览器能访问但 bg_tool 报"发送请求时出错"）
- 强制 TLS 1.2：兼容将来切 HTTPS

### HDT 插件 v0.6.1 (2026-04-24)
- HttpClient 禁用系统代理：同 bg_tool v0.3.1

### bg_tool v0.3.0 (2026-04-24)
- 确定性 gameUuid：用 Lo 集合 SHA256 生成，同一局所有人一致
- 移除 HearthMirror GameUuid 依赖，省去最多 9 秒等待
- 好友房 GameType 前缀匹配（支持 GT_BATTLEGROUNDS_FRIENDLY）
- 去掉等待 Power.log 刷屏日志

### bg_tool v0.2.9 (2026-04-24)
- 修复好友房 GameType 识别：精确匹配 `GT_BATTLEGROUNDS` 改为 `StartsWith` 前缀匹配，支持 `GT_BATTLEGROUNDS_FRIENDLY`
- 同步修复 bg_parser

### bg_tool v0.2.8 (2026-04-24)
- 移除 Diagnose() 诊断代码
- 启动先检测炉石进程，再获取 ID
- 玩家名立即显示（不等验证码上传）
- HearthMirror 失败重试间隔 30s → 5s

### bg_tool v0.2.6 (2026-04-23)
- 从炉石进程自动获取安装目录（HDT 同款方案：Process.MainModule.FileName）
- 国服常见中文路径兜底 + 盘符自动扫描
- 日志轮询优化：文件大小预检（减少 90% IO）+ 行前缀过滤（跳过 90% 冗余行）
- 修复中途启动 bg_tool 丢失联赛对局：扫描完成后补发 check-league
- DEV_NOTES 新增 HDT 日志读取方案对比分析（§16）

### bg_tool v0.2.3 (2026-04-23)
- check-league 接入 HearthDb 解析英雄名，POST 携带 heroName
- 修复 players dict 匿式对象 JSON 序列化 bug
- 不再过滤 Lo=0 玩家，本地玩家附带 battleTag + displayName + startedAt
- gameUuid 为空重试 3 次（共 ~9 秒），Lo 全为 0 时跳过

### bg_tool v0.2.2 (2026-04-21)
- 修复非联赛对局验证码不显示：check-league 回调中验证码更新与 isLeague 判断解耦

### bg_tool v0.2.1 (2026-04-21)
- 标题栏显示版本号
- 点击玩家名跳转 URL 改为 apiBaseUrl 拼接
- 断线重连时日志显示具体时间
- apiKey 改为编译时常量，不暴露在 config.json
- 启动扫描旧日志不触发 check_league，避免读到残留内存数据
- 日志优化：bg_tool.log 超 1MB 覆盖重写，移除 Lo=0 诊断日志

### bg_tool v0.2.0 (2026-04-21)
- WinForms 桌面应用（深色主题 UI）
- 对接 Flask API（check-league + update-placement）
- 联赛对局持久化到 games.json
- 启动 Ping 服务检测、日志面板
- 统一配置 shared_config.json
- 实时监控、中途接入、断线重连、自动切换日志
- HearthMirror 集成获取 8 个玩家 Lo + HeroCardId
- 批量解析 `--parse` 模式、mock_server.py 调试

### Web v0.4.2 (2026-04-17)
- 使用指南页面、管理员删除对局

### QQ Bot v0.1.1 (2026-04-17)
- 排行榜、选手查询、队列状态、最近对局、QQ 绑定/解绑
- 帮助命令、webhook 接收并转发到群通知

### 进行中
- bg_tool WinForms UI 细节优化
- QQ 机器人更多命令（报名/退出队列、管理员命令）
- ELO 评分系统（feature/elo 分支，待上线）
- 比赛定制规则：断线重连检测标记（将来按需实现）

## 更新日志

> Web 端更新日志已迁移至 [LeagueWeb](https://github.com/iceshoesss/LeagueWeb) 仓库。

### bg_tool v0.5.3 (2026-04-27)
- 新增断线重连追踪：ReconnectTimes 列表随 update-placement 上传
- 修复 update-placement playerTag 为空时回退到 HearthMirror 缓存
- 修复 TryCheckLeagueWithRetry 参数类型编译错误
- mock_server 新增 upload-rating 端点、返回服务端 gameUuid、展示断线信息

### bg_tool v0.5.2 (2026-04-26)
- check-league 失败后持续重试直到成功或对局结束

### bg_tool v0.5.1 (2026-04-26)
- 修复验证码日志去掉 upload-rating 后缀
- HearthMirror 未就绪时静默重试 30 秒，避免重复刷屏日志

### bg_tool v0.5.0 (2026-04-27)
- **线程安全**：`_parser` 引用加锁保护、`_state`/`_leagueChecked`/`_scanning` 加 volatile
- **Power.log 生命周期**：炉石退出时立即搜索新日志（不再卡死 200 秒）；初始等待循环追踪炉石进程 PID，重启时自动重置缓存并重新获取玩家信息
- **断线重连**：bg_tool 在重连后启动时正确追踪对局状态
- **炉石重启检测**：监控循环主动检测 PID 变化，立即刷新 BattleTag/AccountId/验证码（不依赖游戏事件触发）
- **日志优化**：去掉内部方法名、失败时输出诊断日志、UI 渲染异常写入文件日志

### bg_tool v0.4.1 (2026-04-26)
- 修复炉石重启后 bg_tool 不触发 check-league：HearthMirrorClient.TryInit() 检测进程 ID 变化，炉石重启后自动重新初始化 Reflection 连接
- 日志监控检测到文件截断时重置读取位置（炉石重启时 Power.log 重写导致 pos 越界）

### v0.2.2 (2026-04-21)
- 修复非联赛对局验证码不显示：check-league 回调中验证码更新与 isLeague 判断解耦

### v0.2.1 (2026-04-21)
- 标题栏显示版本号、点击玩家名跳转 URL 拼接、断线重连日志
- apiKey 改为编译时常量、启动扫描旧日志不触发 check_league
- 日志优化：超 1MB 覆盖重写

### v0.2.0 (2026-04-21)
- WinForms 桌面应用（深色主题 UI）
- 对接 Flask API（check-league + update-placement）
- 联赛对局持久化到 games.json
- 启动 Ping 服务检测、日志面板
- 统一配置 shared_config.json
- mock_server.py 支持状态追踪

### v0.6.1 (2026-04-24)
- HttpClient 禁用系统代理：修复玩家开代理/加速器时 API 请求失败

### v0.5.8 (2026-04-23)
- check-league LobbyInfo 延迟加载保护加强：gameUuid 为空重试 3 次（共 ~9 秒），accountIdLo 全为 0 时等 3 秒后重新读取 LobbyInfo

### v0.5.7 (2026-04-18)
- 修复日志刷屏：GetPlayerId/GetAccountIdLo 未找到时加 1 秒日志节流，避免 OnUpdate 每 100ms 写一条重复日志

### v0.5.6 (2026-04-14)
- 修复 GetPlayerId 失败导致 update-placement 静默丢失：三个缓存独立重试，accountIdLo 改用 Player.AccountId.Lo 优先，空值时有日志+重试
- 修复 409 误判为失败：已提交的 placement 返回 409 时不再重试

### v0.5.5 (2026-04-14)
- update-placement 网络失败时重试 3 次
- 插件认证：所有请求带 `Authorization: Bearer <key>` + 版本号 header，服务端双重校验

### v0.5.4 (2026-04-14)
- check-league 网络失败时重试 3 次

### v0.5.3 (2026-04-14)
- 修复淘汰玩家排名丢失：placement 为 null 时重试读取，最多 10 次

### v0.5.2 (2026-04-13)
- 队列超时机制：报名队列 10 分钟自动踢出，等待队列 20 分钟自动解散
- 登出时自动退出所有队列
- 代码重构：验证码逻辑去重、print 替换为 logging、移除 league_players 冗余字段

### v0.5.1 (2026-04-13)
- 版本号更新，编译输出改用下划线分隔

详细开发记录见 [DEV_NOTES.md](DEV_NOTES.md)。
