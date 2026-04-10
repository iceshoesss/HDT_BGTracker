# HDT_BGTracker

炉石传说酒馆战棋分数记录插件，在每局结束后自动记录分数并上传到 MongoDB。配套联赛网站用于排行榜、对局记录和报名。

## 项目结构

```
HDT_BGTracker/
├── HDT_BGTracker/          # C# HDT 插件
│   ├── BGTrackerPlugin.cs  # 插件入口
│   ├── RatingTracker.cs    # 核心逻辑（分数上传、联赛匹配、验证码）
│   ├── LobbyOverlay.cs     # 游戏内浮动面板（已禁用）
│   └── HDT_BGTracker.csproj
├── league/                 # Flask 联赛网站
│   ├── app.py              # 后端 API + 页面
│   ├── templates/          # Jinja2 模板
│   ├── Dockerfile
│   └── requirements.txt
├── docker-compose.yml      # Docker 部署
└── API.md                  # 网站 API 文档
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

### 常用命令

```bash
docker compose logs -f web     # 看日志
docker compose down            # 停止
docker compose restart web     # 重启
```

### 导入测试数据

```bash
cd league/mock-data
docker compose exec -T mongo mongoimport --db hearthstone --collection league_players --jsonArray < league_players.json
docker compose exec -T mongo mongoimport --db hearthstone --collection league_matches --jsonArray < league_matches.json
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

编译成功后，`bin\Release\net472\` 下会生成所有需要的 DLL。

### 打包安装

```powershell
cd bin\Release\net472
tar -a -cf HDT_BGTracker.zip HDT_BGTracker.dll MongoDB.Bson.dll MongoDB.Driver.dll MongoDB.Driver.Core.dll DnsClient.dll MongoDB.Libmongocrypt.dll SharpCompress.dll
```

### 使用

- 插件启用后自动运行，无需手动操作
- 每局酒馆战棋结束后（返回主菜单时），自动读取分数并上传
- 点击插件设置中的「测试连接」按钮可验证 MongoDB 连接
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
  "ratingChanges": [23, -15, 40, -30],
  "placements": [4, 1, 3, 6],
  "gameCount": 42,
  "mode": "solo",
  "timestamp": "2026-04-07T09:15:00.0000000Z",
  "region": "CN",
  "games": [...]
}
```

详细字段说明和 API 文档见 [API.md](API.md)。
