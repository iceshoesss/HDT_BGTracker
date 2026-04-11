# HDT_BGTracker 开发文档

> 给 AI 的提示：本文档是开发参考，不是用户手册。所有"踩坑记录"和"已验证结论"都是实际测试得出的，直接使用即可，不要重复实验。

---

## 1. 项目架构

```
C# 插件 (HDT_BGTracker/)          Flask 网站 (league/)
┌─────────────────────┐           ┌─────────────────────┐
│ HDT 插件生命周期     │           │ app.py              │
│  ├ OnUpdate (~100ms) │           │  ├ 页面路由          │
│  ├ 分数读取          │           │  ├ REST API          │
│  ├ STEP 13 检测      │  写入     │  └ SSE 推送          │
│  ├ 联赛匹配          │ ──────→  │                     │
│  └ MongoDB 上传      │  MongoDB  │  ← 聚合管道读取      │
└─────────────────────┘  ←──────  └─────────────────────┘
        ↓                               ↓
   MongoDB (hearthstone)          Docker 部署
   ├ bg_ratings                   ├ league-web 镜像
   ├ league_matches               └ mongo:7 容器
   ├ league_queue
   ├ league_waiting_queue
   └ league_players
```

### 数据流

```
玩家打完一局 → 插件 OnUpdate 检测游戏结束
  → 读取 rating + placement + opponents
  → 聚合管道原子写入 bg_ratings（ratingChanges + games 数组）
  → 如果是联赛对局：UpdateLeaguePlacement 写入 league_matches
  → 验证码：首次上传后基于 _id 生成，后续打印到日志

网站请求 → Flask → MongoDB 聚合管道 → 返回 JSON/HTML
  → 排行榜 = league_matches $unwind + $group（30s 缓存）
  → 选手页 = league_matches 聚合（单个 battleTag）
  → SSE 端点 = 内部轮询 MongoDB，有变化才推送
```

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

### 3.3 MongoDB.Driver 2.19.2 兼容性 ⚠️

> 给 AI 的提示：项目锁定 2.19.2，不要尝试使用新版 API。

| 新版 API (2.21+) | 2.19.2 替代写法 |
|---|---|
| `Update.Set("field", val)` 链式 | `new BsonDocument("$set", new BsonDocument {...})` |
| `Update.SetOnInsert(...)` | `new BsonDocument("$setOnInsert", ...)` |
| `Builders<>.Filter.Eq()` + `.Update.Set()` 链式 | 直接用 `BsonDocument` |

**原则：新建代码统一用 `BsonDocument` 风格。**

必须加 `using MongoDB.Driver;`，否则 `Find()` 扩展方法不可见。

### 3.4 MongoDB 聚合管道模式

```csharp
// ✅ 追加数组元素（$push 不能作为管道 stage）
{ "$set", { "array", { "$concatArrays",
    [{ "$ifNull", ["$array", []] }, [newItem]]
} } }

// ✅ 引用管道内计算值
// Stage 1: $set ratingChange = ...
// Stage 3: { "games", { "$concatArrays", [..., { "ratingChange", "$ratingChange" }] } }

// ✅ upsert
var filter = new BsonDocument("gameUuid", gameUuid);
var update = new BsonDocument("$setOnInsert", new BsonDocument {...});
_collection.UpdateOne(filter, update, new UpdateOptions { IsUpsert = true });

// ✅ 数组内定位更新
var filter = new BsonDocument {
    { "gameUuid", gameUuid }, { "players.accountIdLo", accountIdLo }
};
var update = new BsonDocument("$set", new BsonDocument {
    { "players.$.placement", 3 }, { "players.$.points", 6 }
});
```

### 3.5 验证码生成

- 基于 MongoDB `ObjectId`：`SHA256("bgtracker:" + objectId.ToString())` 前 8 位大写
- ObjectId 仅存在于服务端，游戏内不可见，无法被盗用
- 首次上传后读回 `_id` 生成，后续不再生成

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

---

## 5. 数据库结构

### 集合一览

| 集合 | 写入方 | 说明 |
|------|--------|------|
| `bg_ratings` | C# 插件 | 玩家分数记录（含验证码） |
| `league_matches` | C# 插件 | 联赛对局（8人完整数据） |
| `league_queue` | Flask 网站 | 报名队列 |
| `league_waiting_queue` | Flask 网站 | 等待组（满 N 人自动创建） |
| `league_players` | Flask 网站 | 已注册选手 |

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
- [ ] **bg_ratings 精简：移除 `games`、`ratingChanges`、`placements` 数组**
  - 联赛所有数据（排名、英雄、时间戳）已完整存储在 `league_matches` 中，`bg_ratings` 的历史数组纯属冗余
  - 移除后单条从 ~14.5KB 降到 ~1.5KB，节省 90%
  - 保留字段：`playerId`、`accountIdLo`、`displayName`、`rating`、`lastRating`、`ratingChange`、`gameCount`、`mode`、`region`、`timestamp`、`verificationCode`
  - 涉及修改：`RatingTracker.cs`（C# 插件上传逻辑）、`app.py`（如有读取这些字段的地方）
  - 需同步更新 `API.md` 数据结构文档
- [ ] **插件架构改造：直连 MongoDB → HTTP API（通过 CF Tunnel）**
  - 背景：CF Tunnel 只能穿透 HTTP，无法暴露 MongoDB 端口；用户无公网 IP
  - 现状：插件用 MongoDB.Driver 直连 Atlas，连接串硬编码在 C# 中（反编译可泄露）
  - 目标：插件改为 HTTP POST 到 Flask API，Flask 负责写 MongoDB

  #### 改造方案

  **新增 Flask API 端点（`app.py`）：**

  1. `POST /api/plugin/upload-rating`
     - 合并原 UploadToMongo + IncrementLeagueCount 逻辑
     - 请求体：`{ playerId, accountIdLo, rating, mode, placement, gameUuid, region }`
     - 服务端：upsert bg_ratings（含 leagueCount $inc）、生成验证码
     - 返回：`{ ok, verificationCode? }`

  2. `POST /api/plugin/check-league`
     - 合并原 CheckLeagueQueue 逻辑
     - 请求体：`{ gameUuid, accountIdLoList: [...] }`
     - 服务端：查 waiting_queue → 完全匹配 → 创建 league_matches 文档 → 删除等待组
     - 返回：`{ isLeague: true/false }`

  3. `POST /api/plugin/update-placement`
     - 原 UpdateLeaguePlacement + CheckAndFinalizeMatch 逻辑
     - 请求体：`{ gameUuid, accountIdLo, placement }`
     - 服务端：更新 players.$.placement + points，检查是否 8 人都提交 → 写 endedAt
     - 返回：`{ ok, finalized: true/false }`

  **认证方案（多层防御）：**

  插件端：
  - 首次上传成功后，服务端返回 JWT token（签发给该 playerId）
  - 后续请求 Header 携带 `Authorization: Bearer <token>`
  - token 有效期 7 天，插件自动续期

  服务端校验（每个 POST 端点必做）：
  1. **身份校验**：JWT 解码后 playerId 必须和请求体一致 → 防止篡改他人数据
  2. **数据合理性**：rating 变化幅度 ≤ ±200、placement 1-8、gameUuid 格式合法
  3. **速率限制**：同一 playerId 每分钟最多 3 次上传，异常频率封禁
  4. **联赛匹配验证**：update-placement 的 gameUuid 必须存在于 league_matches 且该玩家在 players 数组中
  5. **幂等性**：同 gameUuid + playerId 的 placement 只接受首次提交，后续拒绝（防重复）

  攻击面分析：
  - 刷高分 → 只影响自己，排行榜一眼可见，手动封禁即可
  - 篡改他人 → token 防护，做不到
  - 批量灌数据 → 速率限制拦住
  - 结论：防不住单用户改自己的分（任何客户端架构都做不到），但防得住影响他人的攻击

  **公开 API 安全性（已确认安全）：**
  - 现有 GET API（/api/players、/api/matches 等）本身是公开数据，无需认证
  - bg_ratings 集合（含 verificationCode）没有 API 暴露，不会泄露
  - 后续如加私密数据（用户登录、个人设置），需加认证中间件

  **CF WAF 防护（零成本，CF 免费套餐支持）：**

  利用浏览器安全模型：JS 无法发送服务端未 CORS 允许的自定义 Header。
  在 CF 后台添加 WAF 规则：
  ```
  URI 路径以 /api/plugin/ 开头
  AND
  请求头 X-HDT-Plugin 不存在
  → 拦截 (Block)
  ```
  插件每次请求加一行：`request.Headers.Add("X-HDT-Plugin", "v1");`
  效果：浏览器请求在 CF 层就被拦截，根本到不了 Flask。

  **两层防御组合效果：**
  | 攻击方式 | CF WAF | JWT | 结果 |
  |---------|--------|-----|------|
  | 浏览器直接调 | ❌ 拦截 | — | 打不通 |
  | curl + 自定义 header | ✅ 放行 | ❌ 无 token | 401 |
  | curl + header + token | ✅ 放行 | ✅ 通过 | 只能改自己的 |

  **关于篡改自己数据：** 客户端架构下无法杜绝（数据在用户机器上算，他就能改）。唯一真正解法是服务端交叉验证（从暴雪服务器拉取实际对局），但暴雪无公开 API。社区联赛的应对策略：排行榜透明 + 社区监督，分数异常手动处理。

  **C# 插件改动（`RatingTracker.cs`）：**
  - 移除：所有 `MongoClient`、`IMongoCollection`、`BsonDocument` 相关代码
  - 移除：`MongoDB.Driver`、`MongoDB.Bson` 引用（减少 DLL 体积）
  - 新增：`HttpClient` 单例，所有写操作改为 `PostAsync` / `PutAsync`
  - 新增：`ApiBaseUrl` 配置项（默认 `https://你的域名`，支持用户修改）
  - 新增：简单的 JSON 序列化（用 `System.Web.Script.Serialization` 或 `Newtonsoft.Json`）
  - 日志、HearthDb 查询、STEP 检测等逻辑保持不变

  **执行顺序：bg_ratings 精简 → 插件架构改造（HTTP API + 认证）→ 内测验证**

  **影响面：**
  - 插件包体积减小（移除 MongoDB.Driver + 依赖 DLL）
  - 安全性提升（不再暴露数据库连接串）
  - 连接池问题自然消失（插件不再占 MongoDB 连接）
  - Flask 成为唯一 MongoDB 客户端，连接池完全可控
  - 需要修改 `sync.ps1` / `sync.sh`（打包文件列表变化）
  - 需要修改 `README.md` 安装说明

### 中优先级
- [ ] 验证 FinalPlacement 在单人/双人/掉线重连等场景的可靠性
- [ ] CheckAndFinalizeMatch 写入竞争优化（8 人并行写 endedAt）
- [ ] 赛季功能（`seasonId` 字段隔离不同届联赛）

### 低优先级
- [ ] 静态资源上 CDN（Tailwind CSS）
- [ ] 如流量到数千人：考虑迁移到 Quart（Flask async 版）
- [ ] 积分趋势图
