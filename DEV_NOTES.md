# HDT_BGTracker 开发文档

> 给 AI 的提示：本文档是开发参考，不是用户手册。所有"踩坑记录"和"已验证结论"都是实际测试得出的，直接使用即可，不要重复实验。

---

## 1. 项目架构

```
C# 插件 (HDT_BGTracker/)          Flask 网站 (LeagueWeb/)
┌─────────────────────┐           ┌─────────────────────┐
│ HDT 插件生命周期     │           │ app.py              │
│  ├ OnUpdate (~100ms) │  HTTP     │  ├ 页面路由          │
│  ├ STEP 13 检测      │  POST     │  ├ REST API          │
│  ├ 联赛匹配          │ ───────→ │  ├ 插件 API          │
│  └ HttpClient 上传   │  JSON     │  └ SSE 推送          │
└─────────────────────┘           │     ↓ 写入           │
                                  │  MongoDB 聚合管道     │
                                  └─────────────────────┘
                                          ↓
                                   MongoDB (hearthstone)
                                   ├ player_records
                                   ├ league_matches
                                   ├ league_queue
                                   ├ league_waiting_queue
                                   └ league_players
```

### 数据流

```
玩家打完一局 → 插件 OnUpdate 检测游戏结束
  → STEP 13: POST /api/plugin/check-league → 匹配等待组 → 创建 league_matches
  → 游戏结束: POST /api/plugin/update-placement → 更新排名 + 积分
  → 验证码：服务端基于 ObjectId 生成，check-league 时返回

网站请求 → Flask → MongoDB 聚合管道 → 返回 JSON/HTML
  → 排行榜 = league_matches $unwind + $group（30s 缓存）
  → 选手页 = league_matches 聚合（单个 battleTag）
  → SSE 端点 = 内部轮询 MongoDB，有变化才推送
```

### 插件 API 端点

| 端点 | 认证 | 时机 | 说明 |
|------|------|------|------|
| `POST /api/plugin/check-league` | Bearer | STEP 13 | 检查联赛匹配，返回验证码 |
| `POST /api/plugin/update-placement` | Bearer | 游戏结束 | 更新排名 + 积分 |

所有请求带 `Authorization: Bearer <key>` + `X-HDT-Plugin: <版本号>` header，服务端双重校验。

### 版本管理

项目有**两套独立版本号**，互不关联：

| 组件 | 版本位置 | 何时递增 |
|------|----------|----------|
| C# 插件 | `BGTrackerPlugin.cs` + `HDT_BGTracker.csproj` | 插件功能/bugfix |
| 联赛网站 | `app.py` → `WEB_VERSION` | 网站功能/bugfix |

版本号规则：`主版本.次版本.修订号`
- **修订号 +1** — 修 bug
- **次版本 +1** — 加新功能
- **主版本 +1** — 大改/重构/正式发布

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
- `Config.Instance.BattleTag` **不存在**
- `Core.Game.AccountInfo` **不存在**
- `AccountId.Lo` 无法反推 BattleTag（暴雪两套独立 ID 体系）
- `lobbyInfo.heroCardId` 通常为空（STEP 13 后才有值）

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

### 2.4 STEP Tag 检测

**BG 模式 step 流转（实测）：**

| STEP | 名称 | 说明 |
|------|------|------|
| 4 | BEGIN_MULLIGAN | 筹选开始 |
| 9 | MAIN_START | 主回合开始 |
| 10 | MAIN_ACTION | 战斗阶段 |
| 13 | MAIN_CLEANUP | 第一轮战斗结束，英雄已选定 |

每轮循环：`9 → 10 → 13 → 9 → ...`

**游戏结束检测**：
- 投降或自然淘汰：**无新 STEP 变化**，游戏直接切回菜单
- **STEP 检测无法判断 BG 游戏结束**
- 游戏结束依赖 HDT 的 `IsInMenu = true`（由日志 mode 变化驱动）
- **当前方案：保持 `IsInMenu` 检测**

**关于 FinalPlacement 的踩坑记录（2026-04-15 实测）**：
- ❌ `FinalPlacement` **在游戏内始终为 null**，不可用于提前检测淘汰
- `FinalPlacement` 由 HDT 的 `HandleGameEnd()` 设置，读取的是 `PLAYER_LEADERBOARD_PLACE` tag
- 该 tag 只有在 HDT 处理完整 power log（`STATE=COMPLETE`）后才可读
- 游戏内轮询 ~500 次全部 null，IsInMenu 触发后才变为实际值
- ❌ 英雄血量 ≤ 0 可在游戏内读取，但**无法解决并列排名和投降排名问题**
  - 自然淘汰时有并列可能（同轮多人淘汰，排名相同）
  - 投降时排名不确定（可能在任意轮次，排名不等于"活着人数+1"）
  - 只有服务端的 `PLAYER_LEADERBOARD_PLACE` 才是准确的
- ⚠️ 玩家淘汰后 AFK 挂机不回菜单时，`IsInMenu` 延迟触发，上传会有延迟
  - 这是 HDT 层面的限制，插件无法绕过
  - 服务端有 80 分钟超时兜底

### 2.5 HeroDb → 英雄名

- `HearthDb.Cards.All.TryGetValue(heroCardId, out var card)` → `card.GetLocName(Locale.zhCN)`
- HDT 自带卡牌数据库，无需外部请求
- csproj 需添加 `HearthDb.dll` 引用

### 2.6 Overlay API

- 添加：`Core.OverlayCanvas.Children.Add(element)`（`Canvas` 类型）
- 移除：`Core.OverlayCanvas.Children.Remove(element)`
- 鼠标穿透：`OverlayExtensions.SetIsOverlayHitTestVisible(element, true)`
- `Core.OverlayWindow` 是 `Window` 类型，**没有** `Children` 属性

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

### 3.3 HTTP API 客户端

插件通过 `HttpClient` + `JavaScriptSerializer`（`System.Web.Extensions`）与 Flask 通信。

**csproj 必须添加的引用：**
```xml
<Reference Include="System.Net.Http"><Private>False</Private></Reference>
<Reference Include="System.Web.Extensions"><Private>False</Private></Reference>
```

**注意事项：**
- `HttpClient` 应为单例复用（不要每次请求 new）
- 所有 HTTP 调用在 `Task.Run` 中执行（不阻塞 HDT 主线程）
- 插件请求必须带 `X-HDT-Plugin` header（CF WAF 要求）

<details>
<summary>归档：曾用 MongoDB.Driver 直连（已废弃）</summary>

项目曾锁定 MongoDB.Driver 2.19.2，使用 `BsonDocument` 风格操作。
后改为 HTTP API，C# 插件不再直接操作 MongoDB。

</details>

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

### 4.3 时间处理

C# 插件存 `DateTime.UtcNow.ToString("o")`，MongoDB 可能存为 BSON datetime 或字符串。必须统一处理：

```python
# 安全转 ISO 字符串（统一带 Z 后缀，方便前端 new Date() 正确解析为 UTC）
def to_iso_str(dt_val):
    if isinstance(dt_val, (datetime, bson_datetime.datetime)):
        return dt_val.strftime("%Y-%m-%dT%H:%M:%SZ")
    s = str(dt_val)
    if s and not s.endswith("Z") and "+" not in s and s.count("-") <= 2:
        s += "Z"
    return s

# 转北京时间（Jinja filter，后端直接输出 CST）
def to_cst_str(dt_val):
    return cst.strftime("%Y-%m-%d %H:%M")

app.jinja_env.filters['cst'] = to_cst_str
```

**重要**：`to_iso_str()` 统一返回带 `Z` 的 UTC 时间。所有 MongoDB 字符串比较（如 `startedAt` cutoff）也必须同步加 `Z`。

### 4.4 active games 时间比较

`startedAt` 存为字符串格式（带 `Z` 后缀），Python 查询必须用字符串比较：

```python
# ✅ 正确 — cutoff 字符符串必须带 Z 后缀匹配 startedAt 格式
cutoff_str = (datetime.now(UTC) - timedelta(minutes=80)).strftime("%Y-%m-%dT%H:%M:%SZ")
query = {"startedAt": {"$gte": cutoff_str}}

# ❌ 错误 — MongoDB BSON datetime 和 String 排序不同
cutoff_dt = datetime.now(UTC) - timedelta(minutes=80)
query = {"startedAt": {"$gte": cutoff_dt}}  # 永远 false
```

### 4.5 排行榜聚合管道

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

- 折线图：y 轴反转（1 在上，8 在下），按排名 8 色梯度着色
- 饼图：环形饼图，同色系
- 数据来源：前端从 Jinja2 注入的 `matches_json` 提取，纯客户端计算

---

## 5. 数据库结构

### 集合一览

| 集合 | 写入方 | 说明 |
|------|--------|------|
| `player_records` | `/api/plugin/check-league` | 玩家记录（含验证码） |
| `league_matches` | 插件 API | 联赛对局（8 人完整数据） |
| `league_queue` | Flask 网站 | 报名队列（含 `lastSeen`） |
| `league_waiting_queue` | 网站 + 插件 | 等待组（满 N 人创建） |
| `league_players` | Flask 网站 | 已注册选手 |

### 队列超时

| 队列 | 超时 | 行为 |
|------|------|------|
| `league_queue` | 10 分钟 | 自动踢出 |
| `league_waiting_queue` | 20 分钟 | 解散组 |

### 积分规则

| 排名 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 |
|------|---|---|---|---|---|---|---|---|
| 积分 | 9 | 7 | 6 | 5 | 4 | 3 | 2 | 1 |

公式：`points = placement == 1 ? 9 : max(1, 9 - placement)`

### 对局 status 字段

| 值 | 含义 |
|----|------|
| （不存在） | 正常完成 |
| `"timeout"` | 超时（80 分钟） |
| `"abandoned"` | 部分玩家掉线 |

---

## 6. ELO 评分方案（待上线）

> 设计于 2026-04-14，暂未启用，等上线后根据实际情况决定是否切换。

### 设计目标

- 解决固定积分制"打得越多分越高"的通胀问题
- 防止找演员刷分（低分对手赢了几乎不涨分）
- 赛季内规则固定，K 值不可中途更改

### 参数

| 参数 | 值 | 说明 |
|------|-----|------|
| 初始分 | 50 | 新玩家从 50 开始 |
| K 值 | 2 | 赛季内固定 |
| 比例因子 | 400 | 标准值 |
| 结算方式 | 8 人拆 28 对逐对 ELO | 两两比较 |

### 计算公式

```
对于每个玩家对 (A, B)：
  E_A = 1 / (1 + 10^((R_B - R_A) / 400))    ← A 对 B 的预期胜率
  S_A = 1（A 排名高于 B）| 0.5（平）| 0（A 排名低于 B）
  Δ_A += K × (S_A - E_A)                      ← 累加到 A 的总变动
```

### 预期分数分布（100 局后）

| 水平 | 分数 |
|------|------|
| 顶尖 | 200 ~ 250 |
| 较强 | 100 ~ 150 |
| 平均 | 30 ~ 70 |
| 较弱 | -50 ~ 0 |
| 垫底 | -150 ~ -100 |

### 存储

- `league_players.elo` — 玩家当前 ELO 分数
- `league_matches.players[].eloDelta` — 该局的 ELO 变动（待实现）
- 历史对局 ELO 通过 `calc_elo.py` 重放计算

### 代码位置

- `LeagueWeb/calc_elo.py` — 历史数据 ELO 计算脚本
- `LeagueWeb/feature/elo` 分支 — 排行榜 ELO 列完整代码（未合入 main）

---

## 7. 部署

### Docker

```bash
docker build -t league-web:latest ./league
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
- MongoDB 地址通过环境变量 `MONGO_URL` 覆盖

---

## 8. 待办

- [ ] **插件架构改造：直连 MongoDB → HTTP API（通过 CF Tunnel）**
  - 背景：CF Tunnel 只能穿透 HTTP，无法暴露 MongoDB 端口
  - C# 插件：`MongoDB.Driver` → `HttpClient` + `JavaScriptSerializer`
  - Flask 端：`/api/plugin/*` 端点，由插件 HTTP 调用
  - 插件体积：仅 1 个 DLL（移除 MongoDB 全套依赖）

---

## 9. 更新记录

<details>
<summary>展开完整版本历史</summary>

### v0.5.6 (2026-04-14)
- 修复 GetPlayerId 失败导致 update-placement 静默丢失：三个缓存独立重试
- 修复 409 误判为失败：已提交的 placement 返回 409 时不再重试

### v0.5.5 (2026-04-14)
- update-placement 网络失败时重试 3 次
- 插件认证：Bearer token + 版本号 header，服务端双重校验

### v0.5.4 (2026-04-14)
- check-league 网络失败时重试 3 次

### v0.5.3 (2026-04-14)
- placement 为 null 时重试上传，解决淘汰玩家排名丢失

### v0.5.2 (2026-04-13)
- 队列超时机制：报名 10 分钟踢出，等待 20 分钟解散
- 验证码逻辑去重，print → logging

### v0.5.1 (2026-04-13)
- 编译输出改用下划线分隔

### v0.3.0 Web (2026-04-14)
- 测试模式改为重叠人数匹配，MIN_MATCH_PLAYERS 可配置

### v0.2.13 Web (2026-04-14)
- 登录状态持久化修复，时间显示修正

### 插件架构改造 (2026-04-12)
- 直连 MongoDB → HTTP API（CF Tunnel 限制）

### 网站 UI 迭代 (2026-04-12 ~ 04-13)
- 问题对局提醒、选手页图表、SSE 实时推送

### 数据统计修复 (2026-04-12)
- 排除 timeout/abandoned 对局

</details>
