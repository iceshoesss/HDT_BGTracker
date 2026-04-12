# HDT_BGTracker 开发文档

> 给 AI 的提示：本文档是开发参考，不是用户手册。所有"踩坑记录"和"已验证结论"都是实际测试得出的，直接使用即可，不要重复实验。

---

## 1. 项目架构

```
C# 插件 (HDT_BGTracker/)          Flask 网站 (league/)
┌─────────────────────┐           ┌─────────────────────┐
│ HDT 插件生命周期     │           │ app.py              │
│  ├ OnUpdate (~100ms) │  HTTP     │  ├ 页面路由          │
│  ├ 分数读取          │  POST     │  ├ REST API          │
│  ├ STEP 13 检测      │ ───────→ │  ├ 插件 API          │
│  ├ 联赛匹配          │  JSON     │  └ SSE 推送          │
│  └ HttpClient 上传   │           │     │               │
└─────────────────────┘           │     ↓ 写入           │
                                  │  MongoDB 聚合管道     │
                                  └─────────────────────┘
                                          ↓
                                   MongoDB (hearthstone)
                                   ├ bg_ratings
                                   ├ league_matches
                                   ├ league_queue
                                   ├ league_waiting_queue
                                   └ league_players
```

### 数据流

```
玩家打完一局 → 插件 OnUpdate 检测游戏结束
  → 读取 rating + placement + opponents
  → HTTP POST /api/plugin/upload-rating → Flask 写入 bg_ratings
  → STEP 13: POST /api/plugin/check-league → 匹配等待组 → 创建 league_matches
  → 游戏结束: POST /api/plugin/update-placement → 更新排名 + 积分
  → 验证码：服务端基于 ObjectId 生成，首次上传时返回

网站请求 → Flask → MongoDB 聚合管道 → 返回 JSON/HTML
  → 排行榜 = league_matches $unwind + $group（30s 缓存）
  → 选手页 = league_matches 聚合（单个 battleTag）
  → SSE 端点 = 内部轮询 MongoDB，有变化才推送
```

### 插件 API 端点

| 端点 | 认证 | 时机 | 说明 |
|------|------|------|------|
| `POST /api/plugin/upload-rating` | 无 | 游戏结束 | 上传分数 + 签发 token |
| `POST /api/plugin/check-league` | 无 | STEP 13 | 检查联赛匹配 |
| `POST /api/plugin/update-placement` | Bearer token | 游戏结束 | 更新排名 |

认证流程：`upload-rating` 首次返回 token → 后续 `update-placement` 携带 token。
`check-league` 不需要认证（STEP 13 时 token 尚未签发）。

---

## 2. HDT API 关键发现

> 给 AI 的提示：以下全部经过实测验证，不要怀疑或重复尝试"更优方案"。

### 2.1 玩家 ID 获取

| 数据源 | 格式 | 有 #tag？ | 唯一标识 |
|--------|------|----------|---------|
| `Core.Game.Player.Name` | `南怀北瑾丨少头脑#5267` | ✅ | ❌ 游戏结束后变空 |
| `LobbyPlayer.Name` | `南怀北瑾丨少头脑` | ❌ | 通过 `AccountId.Lo` |
| `AccountId.Lo` | 数字 (ulong) | N/A | ✅ 跨局稳定不变 |

**关键踩坑**：
- `Player.Name` 在**游戏结束后被重置为空**，必须在游戏进行中缓存
- 进入游戏后需**延迟 3 秒**再读取，否则 Player.Name 还没初始化
- `Config.Instance.BattleTag` **不存在**（这个版本的 HDT）
- `Core.Game.AccountInfo` **不存在**
- `AccountId.Lo` 无法反推 BattleTag（暴雪两套独立 ID 体系）
- `lobbyInfo.heroCardId` 通常为空（LobbyInfo 不含英雄选择信息，STEP 13 后才有值）

**当前方案**：游戏开始 → 等 3 秒 → 读 `Player.Name` 缓存 → 游戏结束后用缓存值上传

### 2.2 对手信息

- 来源：`Core.Game.MetaData.BattlegroundsLobbyInfo.Players`（8 个玩家，含自己）
- 每个玩家有：`Name`（不带 #tag）、`AccountId.Hi`、`AccountId.Lo`、`HeroCardId`
- 需要 `using HearthMirror.Objects;` 引用
- csproj 必须添加 `HearthMirror.dll` 引用

### 2.3 排名获取

- 来源：`Core.Game.CurrentGameStats.BattlegroundsDetails.FinalPlacement`
- 返回 `int?`，值 1-8，null 表示未获取到
- **时机**：游戏结束后等 2 秒再读取，太早可能为 null
- 其他有用字段：
  - `FriendlyRawHeroDbfId` — 自己选的英雄 DBF ID
  - `LobbyRawHeroDbfIds` — 大厅可用英雄列表
  - `AnomalyDbfId` — 异常模式

### 2.4 STEP Tag 检测

- `GameEntity` 上的 `STEP` tag（tag ID 198）标记游戏阶段
- BG 模式 step 流转：`INVALID(0) → BEGIN_MULLIGAN(4) → MAIN_CLEANUP(13) → MAIN_START(9) → ...`
- **STEP 13 (MAIN_CLEANUP)** ≈ 第一轮战斗结束，替代了原来的 63s 固定延迟
- 读取：`gameEntity.GetTag(GameTag.STEP)` 返回 int

### 2.5 HeroDb → 英雄名

- **方案**：`HearthDb.Cards.All.TryGetValue(heroCardId, out var card)` → `card.GetLocName(Locale.zhCN)`
- HDT 自带卡牌数据库，无需外部请求，自动同步更新
- csproj 需添加 `HearthDb.dll` 引用

### 2.6 Overlay API

- 添加：`Core.OverlayCanvas.Children.Add(element)`（`Canvas` 类型）
- 移除：`Core.OverlayCanvas.Children.Remove(element)`
- 鼠标穿透：`OverlayExtensions.SetIsOverlayHitTestVisible(element, true)`
- `Core.OverlayWindow` 是 `Window` 类型，**没有** `Children` 属性
- 参考项目：[HDT_BGrank](https://github.com/IBM5100o/HDT_BGrank)

### 2.7 查找 HDT API

- 源码：`https://github.com/HearthSim/Hearthstone-Deck-Tracker`
- 关键文件：
  - `Hearthstone Deck Tracker/Hearthstone/GameV2.cs` — 主游戏类
  - `Hearthstone Deck Tracker/Stats/GameStats.cs` — 含 `BattlegroundsLobbyDetails`
  - `Hearthstone Deck Tracker/Hearthstone/Player.cs` — 玩家类
- Raw 文件 URL：`https://raw.githubusercontent.com/HearthSim/Hearthstone-Deck-Tracker/refs/heads/master/Hearthstone%20Deck%20Tracker/Hearthstone/GameV2.cs`（空格 → `%20`）

---

## 3. C# 插件开发

### 3.1 编译

```powershell
cd HDT_BGTracker
$env:HDT_PATH = "HDT安装路径"
dotnet build -c Release
```

### 3.2 net472 WPF 限制

- SDK-style 项目**不支持** `<UseWPF>true</UseWPF>`（仅 .NET Core 3.0+）
- XAML 编译在 net472 SDK 项目中**不可用**
- **解决方案**：纯 C# 代码创建 UI（`new TextBlock()`, `new Grid()`）
- 需手动添加 `<Reference Include="System.Xaml">`

### 3.3 ~~MongoDB.Driver 兼容性~~ ⛔ 已移除

> MongoDB 驱动已从 C# 插件移除，改为 HTTP API。以下仅作归档。

<details>
<summary>展开历史内容</summary>

项目曾锁定 MongoDB.Driver 2.19.2，使用 `BsonDocument` 风格操作。
所有 MongoDB 操作已迁移至 Flask 服务端（`app.py` 中的 `/api/plugin/*` 端点）。

</details>

### 3.4 HTTP API 客户端开发

插件通过 `HttpClient` + `JavaScriptSerializer`（`System.Web.Extensions`）与 Flask 通信。

**csproj 必须添加的引用：**
```xml
<Reference Include="System.Net.Http"><Private>False</Private></Reference>
<Reference Include="System.Web.Extensions"><Private>False</Private></Reference>
```

**using：**
```csharp
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
```

**请求模式：**
```csharp
private static readonly JavaScriptSerializer _json = new JavaScriptSerializer();

// POST JSON
var content = new StringContent(_json.Serialize(data), Encoding.UTF8, "application/json");
var response = _httpClient.PostAsync($"{ApiBaseUrl}/api/plugin/upload-rating", content).Result;
string body = response.Content.ReadAsStringAsync().Result;
var result = _json.Deserialize<Dictionary<string, object>>(body);
```

**注意事项：**
- `HttpClient` 应为单例复用（不要每次请求 new）
- 所有 HTTP 调用在 `Task.Run` 中执行（不阻塞 HDT 主线程）
- token 过期（401）时清除本地 token 并重试
- 插件请求必须带 `X-HDT-Plugin: v1` header（CF WAF 要求）

### 3.5 验证码生成

- **已迁移至服务端**，插件不再生成验证码
- 服务端逻辑：基于 MongoDB `ObjectId`，`SHA256("bgtracker:" + objectId.ToString())` 前 8 位大写
- 首次 `upload-rating` 时由 Flask 生成并返回，插件打印到日志
- 后续上传时服务端返回已有验证码，不再重新生成

---

## 4. Flask 网站开发

### 4.1 启动方式

- 本地开发：`python app.py`（werkzeug，单线程）
- 生产部署：`gunicorn -c gunicorn.conf.py app:app`（gevent worker，不支持 Windows）

### 4.2 SSE 推送

三个 SSE 端点，替换前端轮询：

| 端点 | 数据源 | 用途 |
|------|--------|------|
| `/api/events/active-games` | `get_active_games()` | 进行中对局 |
| `/api/events/queue` | `league_queue` | 报名队列 |
| `/api/events/waiting-queue` | `league_waiting_queue` | 等待队列 |

实现要点：
- 通用 `_sse_generate(fetch_fn, poll_interval, max_lifetime)` 生成器
- JSON 指纹比对，有变化才 `yield`
- 每 30s 心跳注释行 `: heartbeat`
- `max_lifetime=120s` 后主动断开，客户端自动重连（防僵尸连接）
- 前端 `visibilitychange` 事件：切 tab 关 SSE，回来重连
- 原 REST API 保留，POST 操作仍用 `fetch`

### 4.3 时间处理

C# 插件存 `DateTime.UtcNow.ToString("o")`，MongoDB 可能存为 BSON datetime 或字符串。必须统一处理：

```python
# 安全转 epoch 秒数
def to_epoch(dt_val):
    if isinstance(dt_val, (datetime, bson_datetime.datetime)):
        dt_val = dt_val.replace(tzinfo=timezone.utc) if dt_val.tzinfo is None else dt_val
        return int(dt_val.timestamp())
    # 字符串 fallback ...

# 安全转 ISO 字符串
def to_iso_str(dt_val):
    if isinstance(dt_val, (datetime, bson_datetime.datetime)):
        return dt_val.strftime("%Y-%m-%dT%H:%M:%S")
    return str(dt_val)

# 转北京时间
def to_cst_str(dt_val):
    # +8 hours ...
    return cst.strftime("%Y-%m-%d %H:%M")

app.jinja_env.filters['cst'] = to_cst_str
```

### 4.4 active games 时间比较

`startedAt` 存为字符串格式，Python 查询必须用字符串比较（不是 datetime 对象）：

```python
# ✅ 正确
cutoff_str = (datetime.utcnow() - timedelta(minutes=80)).strftime("%Y-%m-%dT%H:%M:%S")
query = {"startedAt": {"$gte": cutoff_str}}

# ❌ 错误 — MongoDB BSON datetime 和 String 排序不同
cutoff_dt = datetime.utcnow() - timedelta(minutes=80)
query = {"startedAt": {"$gte": cutoff_dt}}  # 永远 false
```

### 4.5 排行榜聚合管道

从 `league_matches` 计算，不依赖 `league_players`：

```python
pipeline = [
    {"$match": {"endedAt": {"$ne": None}}},
    {"$unwind": "$players"},
    {"$match": {"players.points": {"$ne": None}}},
    {"$group": {
        "_id": "$players.battleTag",
        "totalPoints": {"$sum": "$players.points"},
        "leagueGames": {"$sum": 1},
        "wins": {"$sum": {"$cond": [{"$lte": ["$players.placement", 4]}, 1, 0]}},
        "chickens": {"$sum": {"$cond": [{"$eq": ["$players.placement", 1]}, 1, 0]}},
        ...
    }},
]
```

### 4.6 最近对局过滤

排除不完整对局必须用 `$not + $elemMatch`：

```python
# ✅ 正确：确保没有 placement 为 null 的玩家
{"players": {"$not": {"$elemMatch": {"placement": None}}}}

# ❌ 错误：对数组含义是"至少一个 ≠ null"，不是"全部 ≠ null"
{"players.placement": {"$ne": None}}
```

### 4.7 ECharts 图表（玩家页）

玩家页使用 ECharts 5.x 渲染对局分析图表，CDN 引用：

```html
<script src="https://cdn.jsdelivr.net/npm/echarts@5.5.0/dist/echarts.min.js"></script>
```

**折线图（近 20 局排名走势）**：
- y 轴反转（1 在上，8 在下）
- 每个点按排名着色，8 色梯度：`#fbbf24` → `#4ade80` → `#34d399` → `#2dd4bf` → `#fb923c` → `#f87171` → `#ef4444` → `#991b1b`
- 无面积填充，纯线条 + 圆点
- tooltip 显示 `1st` / `2nd` / `3rd` 格式

**饼图（排名分布）**：
- 环形饼图（`radius: ['30%', '60%']`）
- 与折线图相同的 8 色
- 标签格式：`{b}\n{d}%`（排名 + 百分比）

**布局**：左侧 3 : 右侧 2 分栏，无分隔线，参考 amae-kororo 风格。

> 数据来源：前端从 Jinja2 注入的 `matches_json` 中提取 placement 数组，纯客户端计算分布统计，无额外 API 请求。

---

## 5. 数据库结构

### 集合一览

| 集合 | 写入方 | 说明 |
|------|--------|------|
| `bg_ratings` | Flask API（`/api/plugin/upload-rating`） | 玩家分数记录（含验证码） |
| `league_matches` | Flask API（`/api/plugin/check-league` + `/api/plugin/update-placement`） | 联赛对局（8人完整数据） |
| `league_queue` | Flask 网站 | 报名队列 |
| `league_waiting_queue` | Flask 网站 + `/api/plugin/check-league` | 等待组（满 N 人自动创建） |
| `league_players` | Flask 网站 | 已注册选手 |

> C# 插件不再直接操作 MongoDB，所有写入通过 Flask API 中转。

### 积分规则

| 排名 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 |
|------|---|---|---|---|---|---|---|---|
| 积分 | 9 | 7 | 6 | 5 | 4 | 3 | 2 | 1 |

公式：`points = placement == 1 ? 9 : max(1, 9 - placement)`

### 对局 status 字段

| 值 | 含义 |
|----|------|
| （不存在） | 正常完成 |
| `"timeout"` | 超时（80分钟） |
| `"abandoned"` | 部分玩家掉线 |

---

## 6. 部署

### Docker

```bash
# 构建
docker build -t league-web:latest ./league

# 启动
docker compose up -d

# 访问 http://localhost:5000
```

### 环境变量

| 变量 | 默认值 | 说明 |
|------|--------|------|
| `MONGO_URL` | `mongodb://mongo:27017` | MongoDB 地址 |
| `DB_NAME` | `hearthstone` | 数据库名 |
| `FLASK_SECRET_KEY` | 内置默认值 | Session 签名密钥 |

### Git 操作

- 仓库：`https://github.com/iceshoesss/HDT_BGTracker`（claw_version 分支）
- 需要 Fine-grained PAT（Contents Read and write）才能 push
- 本地 `app.py` 用 `skip-worktree` 保护数据库地址

### 本地开发（Windows）

- C# 插件：`$env:HDT_PATH = "路径"` + `dotnet build -c Release`
- Flask 网站：`python app.py`（gunicorn 不支持 Windows）
- MongoDB 地址通过环境变量 `MONGO_URL` 覆盖，默认连接 `localhost:27017`

---

## 7. 待办

### 高优先级
- [x] 编译验证 HearthDb 引用是否可用（`Cards.All` 查英雄名）
- [x] SSE 连接 120 秒自动断开 + 客户端重连，防僵尸连接堆积
- [ ] 问题对局页面添加入口链接
- [x] **bg_ratings 精简：移除 `games`、`ratingChanges`、`placements` 数组** ✅ 已完成
  - 联赛所有数据（排名、英雄、时间戳）已完整存储在 `league_matches` 中，`bg_ratings` 的历史数组纯属冗余
  - 移除后单条从 ~14.5KB 降到 ~1.5KB，节省 90%
  - 保留字段：`playerId`、`accountIdLo`、`displayName`、`rating`、`lastRating`、`ratingChange`、`gameCount`、`mode`、`region`、`timestamp`、`verificationCode`
  - 涉及修改：`RatingTracker.cs`（C# 插件上传逻辑）、`app.py`（无需修改，无依赖）
  - 已同步更新 `README.md`、`API.md` 数据结构文档
- [x] **插件架构改造：直连 MongoDB → HTTP API（通过 CF Tunnel）** ✅ 已完成
  - 背景：CF Tunnel 只能穿透 HTTP，无法暴露 MongoDB 端口
  - C# 插件：`MongoDB.Driver` → `HttpClient` + `JavaScriptSerializer`
  - Flask 端：3 个 `/api/plugin/*` 端点，由插件 HTTP 调用
  - 认证：`upload-rating` 签发 token → `update-placement` 携带 token
  - 插件体积：仅 1 个 DLL（移除 MongoDB 全套依赖）
  - `ApiBaseUrl` 配置项支持本地/生产切换

### 中优先级
- [ ] 验证 FinalPlacement 在单人/双人/掉线重连等场景的可靠性
- [ ] CheckAndFinalizeMatch 写入竞争优化（8 人并行写 endedAt）
- [ ] 赛季功能（`seasonId` 字段隔离不同届联赛）

### 已完成
- [x] **白名单版本移除报名队列和等待队列** — 只保留白名单匹配，移除 `league_queue` / `league_waiting_queue` 相关 API、SSE 和前端 UI

### 低优先级
- [ ] 静态资源上 CDN（Tailwind CSS）
- [ ] 如流量到数千人：考虑迁移到 Quart（Flask async 版）
- [x] **玩家页对局分析模块：折线图 + 饼图** ✅ 已完成
  - 使用 ECharts 5.x（CDN 加载，无后端依赖）
  - 左侧折线图：近 20 局排名走势，按排名着色（8 色梯度）
  - 右侧环形饼图：排名分布占比
  - 颜色统一：金→绿→浅绿→蓝绿→橙→浅红→红→深红（对应第 1-8 名）
  - 数据来自前端 `matches_json`，纯客户端计算，零额外请求
- [x] **正在进行对局分页** ✅ 已完成
  - 每页最多显示 4 个对局，带分页按钮
  - 分页样式与排行榜、玩家页一致
  - SSE 推送时保留当前页码
- [ ] **公网生产部署：Nginx + HTTPS**（待有公网 IP + 域名时启用）
  - 已准备 `docker-compose.prod.yml`（Flask + Nginx + Certbot 三件套）
  - 已准备 `nginx.conf`（反向代理 + SSL 终止 + SSE 长连接支持）
  - CF Tunnel 延迟较高，公网部署后切直连
  - 上线步骤：域名解析 → nginx.conf 替换 YOUR_DOMAIN → certbot 签证 → docker compose up
