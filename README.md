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

部署、配置、环境变量等详见 LeagueWeb 仓库。

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

## 更新日志

> Web 端更新日志已迁移至 [LeagueWeb](https://github.com/iceshoesss/LeagueWeb) 仓库。

### v0.5.5 (2026-04-14)
- update-placement 网络失败时重试 3 次

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
