# HDT_BGTracker

炉石传说酒馆战棋分数记录插件，在每局结束后自动记录分数并上传到联赛网站。配套联赛网站用于排行榜、对局记录和报名。

## 项目结构

```
HDT_BGTracker/
├── HDT_BGTracker/          # C# HDT 插件（HTTP API 客户端）
│   ├── BGTrackerPlugin.cs  # 插件入口
│   ├── RatingTracker.cs    # 核心逻辑（联赛匹配 + 排名上传）
│   ├── LobbyOverlay.cs     # 游戏内浮动面板（已禁用）
│   └── HDT_BGTracker.csproj
├── league/                 # Flask 联赛网站 + 插件 API
│   ├── app.py              # 后端 API + 页面 + 插件端点
│   ├── templates/          # Jinja2 模板（Tailwind CSS + ECharts CDN）
│   ├── Dockerfile
│   └── requirements.txt
├── docker-compose.yml      # Docker 部署
├── API.md                  # 网站 API 文档
├── DEV_NOTES.md            # 开发文档（踩坑记录）
└── sync.ps1 / sync.sh      # 同步脚本（保护本地配置）
```

## 版本管理

### 版本号规则

采用 `v主版本.次版本.修订号`（如 `v0.1.0`）：

- **修订号 +1** — 修 bug（`v0.1.0` → `v0.1.1`）
- **次版本 +1** — 加新功能（`v0.1.0` → `v0.2.0`）
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

- Docker

### 构建

```bash
docker build -t league-web:latest ./league
```

### 启动

```bash
docker compose up -d
```

访问 http://localhost:5000

### 环境变量

| 变量 | 默认值 | 说明 |
|------|--------|------|
| `MONGO_URL` | `mongodb://mongo:27017` | MongoDB 连接地址 |
| `DB_NAME` | `hearthstone` | 数据库名 |
| `FLASK_SECRET_KEY` | 随机生成 | Session 签名密钥，生产环境建议固定设置 |
| `SITE_NAME` | `酒馆战棋联赛` | 网站名称（导航栏 + 页面标题） |
| `SITE_LOGO` | `🍺` | 网站 Logo，支持 emoji 或图片 URL（如 `https://example.com/logo.png`） |

### 常用命令

```bash
docker compose logs -f web     # 看日志
docker compose down            # 停止
docker compose restart web     # 重启
```

### 使用外部 MongoDB

删掉 `docker-compose.yml` 中的 `mongo` service 和 `depends_on`，改为：

```yaml
services:
  web:
    image: league-web:latest
    ports:
      - "5000:5000"
    environment:
      - MONGO_URL=mongodb://你的外部地址:27017
    restart: unless-stopped
```

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

MongoDB 数据库: `hearthstone`，集合: `bg_ratings`

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

### v0.5.2 (2026-04-13)
- 队列超时机制：报名队列 10 分钟自动踢出，等待队列 20 分钟自动解散
- 登出时自动退出所有队列
- 代码重构：验证码逻辑去重、print 替换为 logging、移除 league_players 冗余字段

### v0.5.1 (2026-04-13)
- 版本号更新，编译输出改用下划线分隔

详细开发记录见 [DEV_NOTES.md](DEV_NOTES.md)。
