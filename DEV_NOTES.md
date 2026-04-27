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
| `MatchInfo.LocalPlayer.BattleTag` | `南怀北瑾丨少头脑#5267`（Name#Number 拼接） | ✅ | ✅ HearthMirror 提供 |
| `LobbyPlayer.Name` | `南怀北瑾丨少头脑` | ❌ | 通过 `AccountId.Lo` |
| `AccountId.Lo` | 数字 (ulong) | N/A | ✅ 跨局稳定不变 |

**bg_tool 方案（v0.2.5+）**：STEP 13 时从 `MatchInfo` 获取完整 BattleTag（主力），从 `LobbyInfo` 名字匹配获取 AccountIdLo（主力）。Power.log 的 `PlayerName` 和 `GameAccountId` 保留作为 fallback。

**HDT 插件方案**：`Core.Game.Player.Name`（HDT 内部通过 MatchInfo 设置，含 #tag）+ `LobbyInfo` 名字匹配获取 Lo。

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

### 2.5 英雄名解析

**HDT 插件**（运行在 HDT 内部，HearthDb 已加载）：
- `HearthDb.Cards.All.TryGetValue(heroCardId, out var card)` → `card.GetLocName(Locale.zhCN)`
- csproj 需添加 `HearthDb.dll` 引用

**bg_tool**（独立程序，不依赖 HearthDb）：
- 使用内置 `bg_heroes.json`（嵌入资源，~40KB，763 个英雄含皮肤）
- `HeroNameResolver.Resolve(heroCardId)` 从本地字典查找
- 数据来源：HearthstoneJSON API 一次拉取生成，新英雄发布时需手动更新
- 更新方式：`curl -s "https://api.hearthstonejson.com/v1/latest/zhCN/cards.json" | python3 -c "..." > bg_tool/bg_heroes.json`（见代码注释）

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

## 8. Power.log 直接解析（bg_parser）

> 脱离 HDT 插件，直接从游戏日志提取数据。位于 `bg_parser/bg_parser.py`。

### 8.1 Power.log 格式

关键日志行：

```
GameState.DebugPrintPower() - CREATE_GAME                          → 新游戏开始
GameState.DebugPrintGame() - GameType=GT_BATTLEGROUNDS             → 确认战棋模式
GameState.DebugPrintGame() - PlayerID=7, PlayerName=玩家名#1234    → 本地玩家
GameState.DebugPrintPower() - Player EntityID=20 PlayerID=7 GameAccountId=[hi=.. lo=1708070391]
                                                                   → accountIdLo
PowerTaskList...FULL_ENTITY ... entityName=xxx cardId=BG34_HERO_002 player=7
                                                                   → 英雄实体
TAG_CHANGE Entity=[entityName=xxx ... cardId=xxx player=N] tag=PLAYER_LEADERBOARD_PLACE value=N
                                                                   → 排名更新
```

### 8.2 可提取数据

| 数据 | 来源 | 可靠性 |
|------|------|--------|
| 本地玩家 BattleTag | `DebugPrintGame` 的 `PlayerName` | ✅ 可靠 |
| accountIdLo | `Player EntityID` 的 `lo` 字段 | ⚠️ 可能读到观战者的 Lo，需用 HearthMirror 修正 |
| 英雄名 + cardId | `FULL_ENTITY` + `LEADERBOARD_PLACE` | ✅ 可靠 |
| 最终排名 | `LEADERBOARD_PLACE` 最后出现的值 | ⚠️ 游戏中动态变化 |
| 对手 BattleTag | — | ❌ 不存在于 Power.log |
| 对手 accountIdLo | — | ❌ 不存在于 Power.log |

### 8.3 关键发现

**Power.log 中没有对手身份信息。**

`CREATE_GAME` 块只列出 2 个 Player 实体：
- `PlayerID=7` = 本地玩家（有 GameAccountId）
- `PlayerID=15` = 共享的"酒馆老板/spectator"实体（`lo=0`）

英雄实体中的 `player=N` 与 PlayerID 不同，需注意区分。
HDT 的 `BattlegroundsLobbyInfo`（含对手 BattleTag + accountIdLo）来自 **HearthMirror**——
一个 C# 库，通过读取炉石客户端**进程内存**获取，不是从日志读取。

**LEADERBOARD_PLACE 在游戏中动态变化：**
- 排名会在战斗阶段不断重排（7↔8 互换等）
- 真正的最终排名在游戏结束前最后几秒才确定
- 解析器取最后出现的值，但中间值可能不准确

### 8.4 自动查找日志路径

1. Windows 注册表：`HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Blizzard Entertainment\Hearthstone\InstallPath`
2. 常见安装路径兜底（D:\Battle.net\、C:\Program Files\ 等）
3. 在 `Logs\` 下找最新修改的 `Hearthstone_*` 文件夹

### 8.5 实时监控机制

- 100ms 轮询读取文件新内容
- 每 ~10 秒检查 Logs 目录下是否有更新的 `Hearthstone_xx` 文件夹
- 玩家重启游戏 → 自动切换到新日志 → 扫描新文件已有内容重建对局状态

### 8.6 中途接入与断线重连

**启动时找最后一个 CREATE_GAME 位置开始扫描。** 原因：
- 从头扫描会输出所有历史对局，没有意义
- 只关心最后一局：如果已结束则等待下一局，如果未结束则是断线重连/中途启动
- `_find_last_create_game_pos()` 跳到该位置，静默处理已有数据（不打印历史排名变化）
- 扫描完后如果对局仍在进行，输出当前状态概要（玩家、英雄、当前排名），然后继续实时监控

**游戏结束检测（扫描阶段）：**
- `RE_LB_TAG` 匹配到本地玩家 BattleTag → 游戏已结束（HDT 只在游戏结束时用 BattleTag 格式写排名行）
- 8 个英雄全部有 placement → 游戏已结束
- 以上两个信号任一命中即标记 `is_active = False`，不再追踪该局

**断线重连英雄匹配：**
- LEADERBOARD_PLACE 通过 `card_id + player_slot` 匹配已有英雄（而非 entity_id）
- 原因：断线重连后 entity_id 可能变化，但 card_id + player_slot 保持一致
- 没有匹配到已有英雄时才创建新的 HeroPlacement（正常游戏 FULL_ENTITY 已创建）

**LEADERBOARD_PLACE 追踪所有玩家（不仅是本地玩家）：**
- 通过 `RE_LB_ENTITY` 正则匹配带 cardId 的 LEADERBOARD_PLACE 行
- 每次匹配时更新 `all_heroes` 和 `hero_placements`（如果 entity 不存在则自动创建）
- 本地玩家判定：entity_id 匹配 `local_hero_entity_id`，或 `player_slot == 7`
- 断线重连时本地玩家的 HERO_ENTITY 可能不出现，通过 player_slot=7 回退识别

### 8.7 HearthMirror 集成（获取对手 Lo）

bg_parser 通过 pythonnet 加载 HearthMirror.dll，在 STEP 13（MAIN_CLEANUP）时读取大厅 8 个玩家的 `AccountId.Lo` + `HeroCardId`。

**前置条件**：
- **必须 32 位 Python**（HearthMirror.dll 是 x86 编译，64 位 Python 无法加载）
- pythonnet 包
- HearthMirror.dll 路径通过 `HDT_PATH` 环境变量指定，或放在 `bg_parser/` 同级目录

```powershell
$env:HDT_PATH = "C:\...\HDT"
python bg_parser/bg_parser.py
```

**加载机制**：
- `pythonnet.set_runtime('netfx')` 只能调用一次，重复调用会报 "already been loaded"
- 解决方案：`set_runtime` 失败时 catch 异常继续执行（运行时可能已加载）
- 用 `_mirror_init_attempted` 标志确保只尝试初始化一次，失败不再重试

**读取方式**：
- `HearthMirror.Reflection()` 实例化 → `GetBattlegroundsLobbyInfo()` → `Players`
- 每个 Player 有 `AccountId.Lo`、`AccountId.Hi`、`HeroCardId`
- **只有本地玩家有 `Name`**，对手 Name 为空（这是 HearthMirror 的限制）

**时机验证**：
- ❌ `game_start`（CREATE_GAME）时内存未就绪：只拿到 7 人，第 8 个 Lo=0
- ✅ `MAIN_CLEANUP`（STEP 13）时完整获取 8 人 + 英雄信息

**可选依赖设计**：
- 导入失败不影响 bg_parser 正常运行
- 没有 32 位 Python 或找不到 DLL 时静默降级

---

## 9. 待办与已知问题

### 已知问题

- [x] **bg_tool 获取到观战者的 ID**（2026-04-23）：bg_tool 从 Power.log 的 CREATE_GAME 块读取第一个非零 `GameAccountId.Lo` 作为本地玩家 ID，但观战好友也会有非零 Lo，如果排在前面就被错误采用。HDT 插件用 HearthMirror LobbyInfo 按名字匹配不存在此问题。修复方案：STEP 13 调用 `FetchLobbyPlayers(playerTag)` 时传入本地玩家 BattleTag，HearthMirror 通过 `p.Name == displayName` 匹配本地玩家并提取真实 Lo，覆盖 Power.log 的值。已修复。
- [x] **bg_tool 闪退无报错**（2026-04-20）：最初误判为 x86 注册表重定向问题，实际根因是未打开游戏时 `LogPathFinder.Find()` 返回 null，程序直接 return 退出。修复方案：未找到 Power.log 时循环等待游戏启动（3 秒重试），而非直接退出。见 commit `b793bec`。
- [x] **bg_tool 英雄解析失败**（2026-04-20）：`HERO_ENTITY` 在 Power.log 中有时出现在 `FULL_ENTITY` 之前，导致 `FindHeroByEntity` 返回 null → 英雄名显示"(等待数据)"，后续 LEADERBOARD_PLACE 也因 `Game.HeroCardId` 为空无法匹配排名。修复方案：HERO_ENTITY 找不到英雄时不播报事件，等 FULL_ENTITY 补上后播报；STEP MAIN_START/MAIN_CLEANUP 时主动重试 `FindHeroByEntity`；LEADERBOARD_PLACE 增加 EntityId 匹配 fallback。已修复。**注意**：playerSlot 不能作为本地玩家标识（实际日志中本地英雄 player=1，Python parser 也不使用 playerSlot）。
- [x] **bg_tool 启动读取旧数据**（2026-04-20）：上局游戏未正常结束（无 STATE=COMPLETE），工具启动时 `FindLastCreateGamePos` 定位到最后一个 CREATE_GAME 并扫描旧数据，误认为"进行中"。修复方案：ScanExisting 后检测 active 但无任何游戏数据（无英雄/PlayerTag/AccountIdLo）时，判定为旧数据，跳到文件尾等待新游戏。已修复。**已知边界场景**：玩家断线重连回同一局时，bg_tool 从重连的 CREATE_GAME 开始扫描，`_pendingNewGame` 为 null（全新 Parser），重连数据无法恢复旧局状态，Game 为空壳。该场景不常见，暂不处理。
- [x] **bg_tool 缺少 HearthDb 引用**（2026-04-20）：最初移除了 `ResolveHeroName()` 对 HearthDb 的依赖，改由服务端解析英雄名。2026-04-22 重新加回 csproj 引用。2026-04-23 改用内置 JSON 替代：删除 HearthDb.dll 引用，`HeroNameResolver.cs` 从嵌入资源 `bg_heroes.json`（~40KB）加载静态字典，手写极简 JSON 解析器，零外部依赖。同时修复 `ApiClient.CheckLeagueAsync` 中 players dict 匿名对象序列化 bug（`ToString()` 输出 C# 对象格式而非 JSON），改为 `Dictionary<string, object>`。
- [x] **check-league 400 空参数**（2026-04-22）：HearthMirror 的 LobbyInfo 在 STEP 13 偶尔延迟加载，gameUuid 为空时插件仍发送请求导致服务端 400。修复方案：HDT 插件 CheckLeagueQueue 中 gameUuid 为空时等待 3 秒重试，仍为空则跳过；bg_tool ApiClient.CheckLeagueAsync 中 gameUuid 为空直接跳过。已修复。
- [x] **联赛对局玩家名字为空**（2026-04-22）：HearthMirror 只有本地玩家有 Name（显示名，无 #tag），插件发送的 players dict 中其他 7 人 battleTag 为空串。服务端 check-league 构建 players 时 `detail.get("battleTag", fallback)` 因 detail dict 存在（有 heroCardId）导致空串覆盖了 fallback。修复方案：服务端改用 `or` 三级 fallback（请求数据 → 等待组 name → player_records.playerId 查库）。注意必须查 player_records 而非 league_players，因为 player_records.playerId 是插件上传的完整 battleTag（带 #tag），league_players.battleTag 可能缺 #tag。已修复。
- [x] **bg_tool STEP 13 MatchInfo 不可用导致 check-league 被跳过**（2026-04-24）：bg_tool 在 STEP 13 时调用 `FetchMatchInfo()` 获取 BattleTag，但 BG 模式下 MatchInfo 在此时机不可用，导致 `matchOk=false` 直接短路，8 个玩家全部无法上报联赛对局。实际启动时已通过 `GetBattleTag()` 和 `GetAccountId()` 获取了 BattleTag 和 Lo，`FetchMatchInfo()` 完全多余。修复方案：去掉 STEP 13 的 `FetchMatchInfo()` 调用，直接用启动时缓存的 `LocalPlayerBattleTag`；`FetchLobbyPlayers` 匹配 Lo 失败时 fallback 到启动时的 `Game.AccountIdLo`。已修复。
- [ ] **bg_parser 游戏结束检测不完全可靠**（2026-04-16）：Python 参考实现，仅用于测试验证，不做修改。
- [x] **bg_tool 日志栏无输出**（2026-04-26 发现，2026-04-27 修复）：选手端常见问题，bg_tool 完全运行正常（服务正常、验证码、英雄检测都OK），但日志栏 RichTextBox 无任何输出。bg_tool.log 文件可能有内容（待确认）。根因：`UiTextWriter.AppendToUi` 中 `catch { }` 静默吞掉所有异常，`BeginInvoke` 失败时日志无声丢失。修复方案：`catch (Exception ex) { _inner.WriteLine($"[UI日志异常] {ex.Message}"); }`。已修复。
- [x] **bg_tool `_parser` 引用线程安全**（2026-04-26 发现，2026-04-27 修复）：`LogMonitorLoop` 后台线程会重新赋值 `_parser`（新日志文件扫描时 `ScanExisting(currentPath, out _parser, out pos)`），UI 线程通过 `UpdateUI()` 读 `_parser?.Game`，无锁保护。极端情况下 UI 线程读到半初始化的 Parser。修复方案：加 `_parserLock` 对象，所有 `_parser` 读写均在 `lock (_parserLock)` 内完成；`LogMonitorLoop` 中用本地 `parser` 变量避免频繁加锁。已修复。
- [x] **bg_tool `_state`/`_leagueChecked`/`_scanning` 无线程同步**（2026-04-26 发现，2026-04-27 修复）：三个字段被后台线程写、UI 线程读，无 `volatile` 或锁。x86 上不太容易触发，但存在理论上的可见性问题。修复方案：三个字段均加 `volatile` 修饰。已修复。
- [x] **bg_tool 断线重连后中途启动对局丢失**（2026-04-27 发现并修复）：bg_tool 在断线重连后才启动时，`FindLastCreateGamePos` 定位到重连的 CREATE_GAME（含 TURN tag），`_pendingNewGame` 为 null 导致回滚不执行，`ResetGame()` 清空所有数据。更关键的是 `"reconnect"` 事件只打日志不改 `_state`，导致 `_state` 停留在 `Waiting`，游戏结束时排名上报逻辑被跳过。修复方案：`"reconnect"` 事件中若 `_state == Waiting` 则设为 `InGame`。不影响 bg_tool 已运行时的正常重连流程（`_state` 已是 `InGame`）。已修复。
- [x] **bg_tool Power.log 删除后卡死**（2026-04-27 发现并修复）：炉石退出时 Power.log 被销毁，bg_tool 主循环 `FileInfo.Length` 抛 `FileNotFoundException` → `catch { Sleep(2000); continue; }` → 无限重试旧路径。虽然 `fileCheckCounter` 确实递增（100 轮后触发 `CheckNewLogFile`），但每轮 2 秒延迟意味着需要 200+ 秒才能恢复。修复方案：`FileInfo` 的 `FileNotFoundException` 单独处理，立即调用 `LogPathFinder.Find()` 搜索新日志；找到后通过 `TryScanAndSwitch` 切换；未找到则 2 秒后重试。同时提取 `TryScanAndSwitch` 和 `HandleScannedGameState` 消除三处重复代码。已修复。
- [ ] **bg_tool `LogPathFinder.Find()` 扫描所有盘符**（2026-04-26 发现）：`DriveInfo.GetDrives()` + `Directory.GetDirectories(root, pattern)` 遍历所有固定盘符根目录。在有网络映射盘、慢速硬盘、无权限目录的机器上可能阻塞很久。建议改为只扫描注册表和进程路径找到的目录，兜底路径列表保留但不全盘扫描。
- [ ] **bg_tool `HearthMirrorClient` 缓存值不更新**（2026-04-26 发现）：`LocalPlayerBattleTag` 和 `LocalPlayerLo` 启动时获取一次再也不更新。用户切换 Battle.net 账号、炉石进程重启（bg_tool 未关）、HearthMirror 初始化时炉石还在登录界面等场景会使用过期数据。建议定期刷新或在每局开始时重新获取。
- [ ] **bg_tool `UpdateUI()` 每次调用都读文件**（2026-04-26 发现）：`GameStore.GetRecent` 和 `GetToday` 每次 `File.ReadAllText` + 解析整个 JSON。`UpdateUI` 在每次游戏事件时调用，`games.json` 积累大量记录后会造成 UI 卡顿。建议加缓存或增量更新。
- [ ] **bg_tool bg_tool.log 无运行时轮转**（2026-04-26 发现）：只在启动时检查 1MB 上限并覆盖重写。工具运行多天后日志文件无限增长。建议运行时检查文件大小并轮转。
- [ ] **bg_tool 观战时 `league_matches` 写入观战者名字**（2026-04-26 发现）：虽然联赛数据按 Lo 匹配完全正确（积分/排名无影响），但 `league_matches.players` 中的 `battleTag`/`displayName` 会写成观战者的名字。排行榜已通过按 `accountIdLo` 分组修复显示问题，但底层数据仍需修正。根因：bg_tool 发送的 `playerId` 和 `players[lo].battleTag` 在观战时是观战者身份。修复方向：服务端 `check-league` 不信任插件传的 `battleTag`，改为从 `player_records` 按 `accountIdLo` 查库获取正确身份。

### 待办

- [x] **好友房 GameType 识别**（2026-04-24 修复）：bg_tool 和 bg_parser 精确匹配 `GT_BATTLEGROUNDS` 导致好友房（`GT_BATTLEGROUNDS_FRIENDLY`）被丢弃。已改为 `StartsWith("GT_BATTLEGROUNDS")` 前缀匹配。
- [ ] **好友房对手数据**（2026-04-24 分析）：好友房 Power.log 结构不同——只有 2 个 Player 实体（本地玩家 + 旅店老板），所有对手英雄分配到 player=16（旅店老板），无法从日志获取对手 Lo。HearthMirror 的 LobbyInfo 在好友房下可能也拿不到对手数据（需验证）。待实现。
- [x] bg_tool 对接 Flask API（check-league / update-placement）— v0.2.0
- [x] bg_tool WinForms 独立软件改造 — v0.2.0 框架完成，UI 细节优化中
- [x] UUID 问题 — 已解决，用 Lo 集合生成确定性 UUID（SHA256），同一局所有人 gameUuid 一致
- [ ] ELO 评分系统上线（feature/elo 分支有代码，已 revert）
- [ ] QQ 机器人更多命令（报名/退出队列、管理员命令）
- [ ] 比赛定制规则：断线重连检测标记（players[].reconnected 字段，将来按需实现）
- [ ] bg_tool WinForms UI 细节优化

### 已知限制

- bg_tool 必须 x86 运行（HearthMirror.dll 是 32 位程序集，无法编译为 x64）
- bg_tool 无法从游戏获取 Region/Mode，需手动配置
- bg_tool 只能检测自己的断线重连，其他玩家的断线需服务端交叉验证

---

## 10. 独立可执行程序（bg_tool）

### 状态

Python 原型（bg_parser）功能已完善，C# 重写版（bg_tool）已完成基础功能。

**技术选型：C# net472**（而非 Go/.NET 8）
- 原因：需要直接引用 HearthMirror.dll（.NET Framework 程序集），Go 和 .NET 8 无法加载
- bg_tool 与 HDT 插件共享同一框架（net472），用户只需一个可执行文件

### 已完成

- [x] Power.log 实时监控 + 自动查找日志路径
- [x] CREATE_GAME / STEP / LEADERBOARD_PLACE 状态机解析
- [x] 玩家信息提取（BattleTag、accountIdLo、英雄名+cardId）
- [x] 排名追踪
- [x] 投降检测
- [x] 断线重连检测与恢复
- [x] 中途接入预加载玩家信息
- [x] 自动切换日志文件（游戏重启）
- [x] HearthMirror 直接引用：STEP 13 获取对手 Lo + HeroCardId
- [x] 批量解析 `--parse` 模式
- [x] Ctrl+C 优雅退出

### 待办

- [ ] 游戏结束检测可靠性修复（针对非正常退出场景）
- [ ] 多局连续追踪稳定性测试
- [ ] LEADERBOARD_PLACE 动态排名追踪（仅本地玩家，当前已实现）
- [ ] WinForms 独立软件改造（见下方 UI 设计）

### Python → C# 迁移对照

| Python bg_parser | C# bg_tool | 说明 |
|------------------|-----------|------|
| `re.compile()` | `static readonly Regex(..., Compiled)` | 预编译正则 |
| `dataclass Game` | `class Game` | 同字段 |
| `dataclass Hero` | `class Hero` | 同字段 |
| `class Parser` | `class Parser` | 同状态机逻辑 |
| `fetch_lobby_players()` | `HearthMirrorClient.FetchLobbyPlayers()` | 直接引用 DLL |
| `find_latest_power_log()` | `LogPathFinder.Find()` | 含注册表查找 |
| `signal.SIGINT` | `Console.CancelKeyPress` | 优雅退出 |
| `time.sleep(0.1)` | `Thread.Sleep(100)` | 轮询间隔 |

### C# net472 兼容性踩坑

- ❌ **HearthMirror.dll 必须 x86 进程加载**：csproj 需 `<PlatformTarget>x86</PlatformTarget>`，否则 AnyCPU 在 64 位系统以 64 位运行，报"试图加载格式不正确的程序"
- ❌ `namespace BgTool;`（文件作用域）→ 需要 `namespace BgTool { }`（C# 10 语法）
- ❌ `new(...)`（目标类型 new）→ 需要 `new Regex(...)` 等显式类型（C# 9）
- ❌ `cardId[prefix.Length..]`（范围语法）→ 需要 `cardId.Substring(prefix.Length)`（需 System.Range）
- ❌ `step is "A" or "B"`（模式组合）→ 需要 `step == "A" || step == "B"`（C# 9）
- ❌ `name.Contains('#')`（char 重载）→ 需要 `name.Contains("#")`（.NET Core 才有）
- ❌ `[^1]`（从末尾索引）→ 需要 `list[list.Count - 1]`（需 System.Index）
- GameResult 数据结构
- process_line 状态机逻辑
- tail_log 文件监控逻辑

---

## 11. WinForms 独立软件改造（开发中）

bg_tool 最终目标：脱离 HDT 插件，独立存在一个上报分数给联赛网站的软件。

### UI 设计（2026-04-20 确认）

```
┌──────────────────────────────────────────┐
│ 🍺 酒馆战棋联赛工具           🔗 已连接  │
│ 南怀北瑾丨少头脑#5267                     │
├──────────────────────────────────────────┤
│                                          │
│         ⏳ 等待对局中...                   │
│                                          │
├───────────────────────┬──────────────────┤
│ 最近战绩              │ 今日统计         │
│ ① 风暴之王托里姆 +9   │ 总局数      5    │
│ ④ 阿莱克丝塔萨   +5   │ 前四     60%    │
│ ⑦ 巫妖王         −2   │ 场均排名  3.2   │
│ ② 米尔豪斯       +7   │ 积分变动  +23   │
│ ⑥ 拉卡尼休       −3   │                  │
├───────────────────────┴──────────────────┤
│ 验证码  A1B2C3             [📋 复制]     │
├──────────────────────────────────────────┤
│ ⚠️ 上次上传失败，将在下局重试   [详情]   │
└──────────────────────────────────────────┘
```

- 玩家名可点击，打开浏览器跳转 player 页面
- 验证码显示 + 一键复制（来自 upload-rating API）
- 对局中状态：主区域显示英雄名 + "8 人联赛 · 等待结算"
- 错误横幅：底部，有错误才出现，点详情展开
- 三种状态：等待中 / 对局中 / 已上传，自动切换
- 最近战绩 5 局，今日统计 4 项（总局数/前四/场均排名/积分变动）

### 技术方案

- OutputType 改为 `WinExe`（无控制台窗口）
- 后台线程跑现有 Parser 状态机，通过事件回调更新 UI
- `Parser`、`HearthMirrorClient`、`LogPathFinder` 代码不动，只加 WinForms 前端
- UI 用 WinForms 原生控件，深色主题
- 玩家名点击用 `Process.Start()` 打开浏览器

### 数据来源

| 数据 | 来源 | Demo 状态 |
|------|------|----------|
| 玩家名 | Flask API / 本地配置 | 先硬编码 |
| 连接状态 | API 健康检查 | 先硬编码 |
| 对局状态 | Parser 事件 | ✅ 本地可实现 |
| 最近战绩 | Parser 本地记录 | ✅ 本地可实现 |
| 今日统计 | Parser 本地计算 | ✅ 本地可实现 |
| 验证码 | Flask upload-rating API | 需 API 接入 |
| 错误状态 | HTTP 请求失败记录 | ✅ 本地可实现 |

---

## 12. UUID 问题（已解决）

> 2026-04-24 最终解决：服务端生成 UUID

### 问题历史

**第一版（已废弃）**：客户端各自生成随机 UUID → 同一局不同玩家 UUID 不同 → 服务端创建多条对局记录。

**第二版（已废弃）**：客户端用 Lo 集合生成确定性 UUID（SHA256）→ 理论上同一局所有人 UUID 一致。**实际踩坑**：不同玩家的 bg_tool 通过 HearthMirror 读取各自客户端内存，`LobbyPlayers` 列表可能有差异（某人的 `AccountId.Lo=0` 导致过滤后非零 Lo 列表不同），算出不同 UUID。淘汰赛 `existing_match` 检查依赖 `gameUuid` 匹配，UUID 不同时后续玩家无法找到已有 match，`gamesPlayed` 被多次递增，BO N 只有前 N 个玩家能成功匹配。

**最终方案（v0.10.0 / v0.4.0）**：服务端生成 UUID，客户端不再自行计算。

### 解决方案

**服务端（LeagueWeb routes_plugin.py）**：
1. 第一个玩家 check-league 匹配到淘汰赛组后，服务端用 `uuid4()` 生成 UUID
2. 通过 `update_one({tournamentGroupId, endedAt: None}, {$setOnInsert: {...}}, upsert=True)` 原子创建 match
3. 后续玩家 check-league 时 upsert 发现已有 match，`$setOnInsert` 不执行
4. 所有玩家从 `find_one({tournamentGroupId, endedAt: None})` 拿到同一个 UUID
5. 响应中返回 `gameUuid` 字段

**客户端（bg_tool / HDT 插件）**：
1. check-league 不再发送 `gameUuid` 参数
2. 从响应中提取 `gameUuid`，存入 `_currentGameUuid`（bg_tool）/ `_cachedGameUuid`（插件）
3. update-placement 使用服务端返回的 UUID

**关键设计**：
- upsert 原子性保证并发安全：即使多个请求同时到达，只有一个能 insert，其余 find 到同一个文档
- 每局新 UUID：BO1 结束后 `endedAt` 被写入，下一局 upsert 不会匹配到旧 match
- 积分赛不受影响：仍使用客户端 gameUuid

### Lo=0 问题（已解决）

HearthMirror 返回 8 个玩家中总有一个 `AccountId.Lo=0`，原因是最后一个玩家是机器人。非并发问题，无需进一步处理。

---

## 13. QQ 机器人集成（BG_QQBot 仓库）

> 独立仓库：[BG_QQBot](https://github.com/iceshoesss/BG_QQBot)，v0.1.1（2026-04-17）

### 已实现

- 排行榜 TOP 10、选手查询、队列状态、最近对局
- QQ 绑定码验证 + 解绑
- Webhook 接收（LeagueWeb 超时/掉线对局 → bot 转发群通知 @ 玩家）
- 帮助命令
1. **查询排名** — 群内发送指令查询排行榜/选手详情
2. **管理员补录** — 管理员通过机器人补录问题对局排名
3. **问题对局通知** — 对局超时/掉线时自动通知相关玩家

### 架构

```
QQ群 ↔ QQ机器人 ↔ HTTP API ↔ Flask ↔ MongoDB
```

机器人作为独立服务运行，通过 HTTP API 与 Flask 通信。不需要 WebSocket，现有 SSE 也不需要改。

### 需要的改动

#### 1. Webhook 通知（Flask 侧）

- 新增环境变量 `WEBHOOK_URL`（QQ 机器人的接收地址）
- 在问题对局发生时（超时、部分掉线），POST 通知到 webhook URL
- payload 包含对局信息 + 玩家列表（battleTag）

#### 2. QQ 号绑定机制（Flask 侧）

采用 **方案 A**：在 `league_players` 上加字段

```json
{
  "battleTag": "衣锦夜行#1000",
  "bindCode": "A3F8",
  "bindCodeExpire": "2026-04-17T08:30:00Z"
}
```

- `bindCode`：一次性绑定码，有效期 5 分钟
- `bindCodeExpire`：过期时间
- 绑定成功后清除这两个字段

流程：
1. 玩家在网站点击「绑定 QQ」→ 生成临时绑定码
2. 玩家在 QQ 机器人输入 `/绑定 A3F8`
3. 机器人调 API 验证 → 匹配到 battleTag → 机器人写入本地映射表
4. 绑定码用完即废

**机器人侧**维护 QQ 号 ↔ battleTag 映射表，不存入 Flask 数据库。

#### 3. 新增 API 端点

| 端点 | 方法 | 说明 |
|------|------|------|
| `/api/bind-code` | POST | 登录用户生成绑定码（返回 code） |
| `/api/bind-code/verify` | POST | 机器人验证绑定码（返回 battleTag） |

机器人调用第二个端点时，需要带机器人自己的认证 token（环境变量 `BOT_API_KEY`）。

### 涉及的数据集合

| 集合 | 改动 |
|------|------|
| `league_players` | 新增 `bindCode`、`bindCodeExpire` 字段（临时） |

不需要新建集合，不需要改动现有字段。

## 14. 淘汰赛设计（LeagueWeb feat/knockout 分支）

> 2026-04-22 讨论并确认

### 核心设计

- 淘汰赛预分组，管理员创建赛事时分配 8 人一组（`tournament_groups` 集合）
- 每组打 N 局（BO3/BO5/BO7），每轮可配置不同（如海选 BO3、决赛 BO5）
- 按 N 局累计积分排名，前 4 晋级
- **插件不需要改动**——插件只上报 8 个 Lo，判断逻辑全在 Flask 侧

### 匹配机制

淘汰赛 **不走 `league_waiting_queue`**，直接在 `tournament_groups` 内部匹配：

```
check-league → 先查 tournament_groups（status=waiting + gamesPlayed < boN + Lo 集合匹配）
  → 匹配到 → isLeague=true，创建 league_matches（带 tournamentGroupId）
  → 没匹配到 → 查 league_waiting_queue（积分赛）→ 都没匹配到 → isLeague=false
```

BO 系列赛流程：
1. 管理员创建赛事 → tournament_groups 初始化（status=waiting, boN=N, gamesPlayed=0）
2. 8 人进游戏 → check-league 匹配到 → 创建 match → gamesPlayed=1
3. 打完 → update-placement 写排名到 league_matches → gamesPlayed < boN → status 回到 waiting
4. 8 人重开游戏 → check-league 再次匹配（同一个 tournament_group）→ 创建新 match
5. 全部打完 → status=done → 从 league_matches 聚合排名 → 晋级判定 → 自动创建下一轮分组

### tournament_groups 数据结构

排名数据（totalPoints/games/placement/qualified/eliminated）**不存储在 tournament_groups 中**，而是从 league_matches 按 tournamentGroupId 聚合计算。tournament_groups 只存身份信息和元数据。

```json
{
  "tournamentName": "2026 春季赛",
  "round": 1,
  "groupIndex": 1,
  "status": "waiting",        // waiting / active / done
  "boN": 3,                   // 本组打几局
  "gamesPlayed": 1,           // 已完成局数
  "players": [
    {
      "battleTag": "xxx#1234",
      "accountIdLo": "1708070391",
      "displayName": "xxx",
      "heroCardId": "TB_BaconShop_HERO_56",
      "heroName": "阿莱克丝塔萨",
      "empty": false
    }
  ],
  "nextRoundGroupId": 1,      // 晋级目标组号
  "startedAt": null,
  "endedAt": null
}
```

### 已知问题：移动端玩家

手机玩家无法使用插件，获取不到 accountIdLo。插件上报的是对手的 Lo（来自 HearthMirror 读内存），但对手的 battleTag 无法获取（HearthMirror 限制：只有本地玩家有 tag，对手只有 Lo）。

讨论的解决方案（待定）：
1. 匹配阈值降为 5/8 人匹配即可（容错手机玩家 Lo 缺失）
2. 手机玩家提前开一局，由同局有插件的玩家获取他们的 Lo，管理员手动注册

### 积分规则

| 排名 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 |
|------|---|---|---|---|---|---|---|---|
| 积分 | 9 | 7 | 6 | 5 | 4 | 3 | 2 | 1 |

BO N 下每局积分不变，N 局累加。公式：`points = placement == 1 ? 9 : max(1, 9 - placement)`

## 16. HDT 日志读取方案对比分析

> 2026-04-23 阅读 HDT 源码整理，**重要参考资料**。

### HDT 架构概览

HDT 采用**双管齐下**策略：
- **Power.log 文件监控**：游戏生命周期、STEP 变化、对局事件（通过 `HearthWatcher.LogFileWatcher`）
- **HearthMirror 内存读取**：实时获取玩家信息、排名、对局 UUID 等（通过 C# 直接引用 DLL）

两者互补，缺一不可。bg_tool 的架构与 HDT 类似，但 bg_tool 是独立程序，不依赖 HDT 框架。

### HDT 日志路径查找（LogWatcherManager.Start）

**关键源码**：`Hearthstone Deck Tracker/LogReader/LogWatcherManager.cs`

```csharp
public async Task Start(GameV2 game)
{
    if(!Helper.HearthstoneDirExists)
        await FindHearthstone();  // 找不到就等进程
    var logDirectory = Path.Combine(
        Config.Instance.HearthstoneDirectory,
        Config.Instance.HearthstoneLogsDirectoryName
    );
    _logWatcher.Start(logDirectory);
}

private async Task FindHearthstone()
{
    Log.Warn("Hearthstone not found, waiting for process...");
    Process? proc;
    while((proc = User32.GetHearthstoneProc()) == null)
        await Task.Delay(500);  // 每 500ms 检查一次
    var dir = new FileInfo(proc.MainModule.FileName).Directory?.FullName;
    Config.Instance.HearthstoneDirectory = dir;
    Config.Save();  // 存下来下次直接用
}
```

**查找顺序**：
1. `Config.Instance.HearthstoneDirectory`（用户在 HDT 设置中手动指定，持久化保存）
2. `Helper.FindHearthstoneDir()`（注册表查找，3 个位置）
3. **等炉石进程启动**，从 `Process.MainModule.FileName` 推导安装目录
4. 找到后写入 Config，下次直接用

**bg_tool 已采纳**：v0.2.6 加入了相同的进程检测方案（`FindHsDirFromProcess()`）。

### HDT 日志文件监控（LogFileWatcher）

**关键源码**：`HearthWatcher/LogReader/LogFileWatcher.cs`

HDT 的日志监控与 bg_tool 的对比：

| 特性 | HDT LogFileWatcher | bg_tool MainForm |
|------|-------------------|-----------------|
| 文件句柄 | 保持打开，不重复开关 | 每 100ms 重新打开 FileStream |
| 行前缀过滤 | `StartsWithFilters` 过滤，只处理 `GameState.` 和 `PowerTaskList.DebugPrintPower()` | v0.2.6 加入相同过滤 |
| 文件大小预检 | 有（检查 offset vs 文件大小） | v0.2.6 加入（FileInfo.Length 预检） |
| 线程模型 | ConcurrentQueue，读取和处理分离 | 单线程，读一行处理一行 |
| 日志目录切换 | 检测 Hearthstone_* 子目录创建时间，MoveTo 文件判断活跃目录 | LogPathFinder.CheckNewLogFile 检测新目录 |
| 内存保护 | MAX_LOG_LINE_BUFFER=100000，防止 BG 后期爆内存 | 无（但实际影响不大，BG 对局不会太长） |

### HDT 日志过滤机制（重点）

HDT 的 `LogWatcherInfo` 定义了过滤规则：

```csharp
public static LogWatcherInfo PowerLogWatcherInfo => new LogWatcherInfo
{
    Name = "Power",
    StartsWithFilters = new[] {
        "PowerTaskList.DebugPrintPower",  // FULL_ENTITY、TAG_CHANGE 等
        "GameState.",                      // DebugPrintGame、DebugPrintPower 等
        "PowerProcessor.EndCurrentTaskList"
    },
    ContainsFilters = new[] {
        "Begin Spectating", "Start Spectator", "End Spectator"
    }
};
```

**原理**：Power.log 中 90%+ 的行是 `PowerTaskList.DebugDump()` 输出（冗余的游戏状态 dump），只有 `GameState.` 和 `PowerTaskList.DebugPrintPower()` 开头的行包含有用信息。HDT 在读取层就过滤掉无用行，避免进入解析器。

**bg_tool v0.2.6 已采纳**：
```csharp
if (!line.Contains("GameState.") && !line.Contains("PowerTaskList.DebugPrintPower()")
    && !line.Contains("PowerTaskList.DebugDump()"))
    continue;
```
注：bg_tool 额外保留 `PowerTaskList.DebugDump()` 是因为 Parser 用它标记 CREATE_GAME 块结束。

### HDT 游戏状态检测

HDT 不依赖单一信号判断游戏状态：
- **游戏开始**：`LoadingScreen.OnSceneLoaded` + `MulliganManager.HandleGameStart`
- **游戏结束**：`STATE=COMPLETE` + `IsInMenu`（由日志 mode 变化驱动）
- **排名获取**：`PLAYER_LEADERBOARD_PLACE` tag（来自 Power.log）+ HearthMirror 内存读取

bg_tool 的游戏结束检测依赖 `IsInMenu`（与 HDT 一致），排名只从 Power.log 读取。

### bg_tool 与 HDT 的功能对比

| 功能 | HDT | bg_tool | 说明 |
|------|-----|---------|------|
| 日志路径查找 | 注册表 + Config + 进程检测 | 注册表 + 进程检测 + 硬coded + 盘符扫描 | bg_tool 兜底更多 |
| 玩家 BattleTag | HearthMirror MatchInfo | HearthMirror MatchInfo | ✅ 相同方案 |
| 对手 AccountId.Lo | HearthMirror LobbyInfo | HearthMirror LobbyInfo | ✅ 相同方案 |
| 英雄信息 | HearthMirror + Power.log | HearthMirror + Power.log | ✅ 相同方案 |
| 排名获取 | HearthMirror + Power.log | Power.log only | bg_tool 不走 HDT，无内存读取 |
| 游戏结束检测 | IsInMenu + STATE=COMPLETE | IsInMenu + STATE=COMPLETE | ✅ 相同方案 |
| 联赛匹配 | 无（HDT 不做联赛） | check-league API | bg_tool 独有 |
| 断线重连 | 日志 timestamp 跳跃检测 | CREATE_GAME 块 TURN tag 检测 | 不同实现 |

### 关键结论

1. **HDT 也读 Power.log**，不是纯内存方案。HearthMirror 补充了日志无法获取的实时数据。
2. **日志路径查找是通用问题**，HDT 用进程检测解决，bg_tool 已采纳。
3. **行前缀过滤是必要的优化**，Power.log 冗余数据量大，不过滤会影响性能。
4. **bg_tool 的排名获取是瓶颈**——HDT 有 HearthMirror 双重保障，bg_tool 只有 Power.log，`PLAYER_LEADERBOARD_PLACE` 延迟问题无法绕过（服务端 80 分钟超时兜底）。

---

## 15. 更新记录

<details>
<summary>展开完整版本历史</summary>

### C# 插件

### bg_tool

#### v0.2.8 (2026-04-24) — HearthMirror 为主力 + 玩家名立即显示
- 移除 Diagnose() 诊断代码
- 启动流程改为先检测炉石进程（1 秒轮询），再调 HearthMirror 获取 ID
- HearthMirror 拿到 Tag 后立即更新 UI（不等验证码上传完成）
- HearthMirror 失败时 5 秒重试（原 30 秒）

#### v0.2.6 (2026-04-23) — HDT 方案借鉴：进程检测 + 日志轮询优化 + 中途启动修复
- LogPathFinder 新增 `FindHsDirFromProcess()`：从运行中的炉石进程获取安装目录（HDT 同款方案）
  - `Process.GetProcessesByName("Hearthstone")` → `MainModule.FileName` → 推导安装目录 → 拼 `\Logs`
  - 结果缓存（`_processDirCached`），不重复扫描
  - 注册表找不到时自动触发，作为第三级兜底（在硬coded路径之前）
- 国服常见中文路径兜底：`D:\暴雪战网\炉石传说\Hearthstone\Logs` 等 6 个组合
- 盘符扫描：自动扫描所有固定盘符根目录下 `*Hearthstone*` 和 `*炉石*` 子目录
- 日志轮询优化（参考 HDT LogFileWatcher）：
  - 文件大小预检：`FileInfo.Length` 先检查，无新数据不打开 FileStream（减少 90% IO）
  - 行前缀过滤：跳过 `PowerTaskList.DebugDump()` 冗余行（占日志 90%+），只保留 `GameState.` 和 `PowerTaskList.DebugPrintPower()` 再进 Parser
- **修复中途启动丢失联赛对局**：扫描完成后检测到进行中对局时，补发 `check-league`（`TriggerCheckLeagueIfNeeded()`）
  - 根因：STEP 13 在扫描阶段被 `_scanning` 守卫跳过，实时监控中不会再次触发，导致 `_state` 停留在 `InGame`，游戏结束时不上报 `placement`
- 新增 DEV_NOTES §16：HDT 日志读取方案对比分析（重要参考资料）

#### 2026-04-21 bg_tool 对接 Flask API
- 新增 `ApiClient.cs`：极简 HTTP 客户端，无第三方 JSON 库依赖，手写序列化
- 新增 `Config.cs` + `config.json`：API 地址、Key、Region/Mode 配置
- 流程：英雄选定（STEP=MAIN_CLEANUP）→ check-league → 游戏结束 → update-placement
- 服务端版本兼容：`X-HDT-Plugin` header 硬编 `0.5.7`（bg_tool 内部版本保持独立）
- check-league 获取 GameUuid：来自 HearthMirror 的 `BattlegroundsLobbyInfo.GameUuid`
- update-placement 失败重试 3 次（2 秒间隔）
- config.json 编译时自动复制到输出目录

#### 2026-04-21 BattlegroundsLobbyInfo 结构（已验证）

HearthMirror 的 `Reflection.GetBattlegroundsLobbyInfo()` 返回 `HearthMirror.Objects.BattlegroundsLobbyInfo`：

**BattlegroundsLobbyInfo 属性：**
| 属性 | 类型 | 说明 |
|------|------|------|
| GameUuid | String | 对局 UUID，如 `281ac196-681f-4668-930f-e85e4076b010` |
| Players | List\<BattlegroundsLobbyPlayer\> | 8 个玩家 |

**没有** Region 或 GameMode 属性。

**BattlegroundsLobbyPlayer 属性：**
| 属性 | 类型 | 说明 |
|------|------|------|
| AccountId | AccountId | 含 Hi (long) 和 Lo (ulong) |
| HeroCardId | String | 如 `BG26_HERO_101` |
| Name | String | 玩家名（本地玩家有值，其他 7 人通常为空） |

**HDT 插件获取 Region/Mode 的方式：** 不是来自 HearthMirror，而是 HDT 自身 API：
- Region：`Core.Game.CurrentRegion.ToString()` → "CN"/"US"/"EU"
- Mode：`Core.Game.IsBattlegroundsDuosMatch ? "duo" : "solo"`
- 这些是 HDT 框架（Hearthstone Deck Tracker）的内置能力，通过拦截 Battle.net 客户端启动参数和游戏元数据获取
- bg_tool 作为独立程序没有 `Core.Game`，无法获取
- **临时方案**：硬编码 region="CN", mode="solo"

**详细发现过程（2026-04-21）：**
1. 起初怀疑 Region/Mode 来自 HearthMirror 的 LobbyInfo → 用反射 dump 验证 → LobbyInfo 只有 GameUuid + Players，**无 Region/GameMode**
2. 查看 HDT 插件 `RatingTracker.cs` → `GetRegion()` 调用 `Core.Game.CurrentRegion`，`GetMode()` 用 `Core.Game.IsBattlegroundsDuosMatch`
3. `Core.Game` 是 HDT 的 `GameV2` 类，Region 来自 Battle.net 客户端的区域配置（不是游戏内存），Mode 来自 HDT 对游戏实体标签的解析
4. 结论：bg_tool 无法绕过 HDT 框架获取这两项，必须硬编码或从外部配置

#### v0.2.5 (2026-04-23)
- 玩家身份（BattleTag + AccountIdLo）改用 HearthMirror 为主力，Power.log 降为 fallback
- 新增 `HearthMirrorClient.FetchMatchInfo()`：从 `MatchInfo.LocalPlayer.BattleTag` 获取完整 BattleTag（Name#Number）
- STEP 13 流程：FetchMatchInfo → FetchLobbyPlayers → 两者都成功才触发 check-league
- **关键**：BattleTag 或 Lo 任一未被 HearthMirror 确认 → 跳过 check-league，不发请求，避免用错误 ID 创建对局记录
- Power.log 的 `PlayerName` 和 `GameAccountId` 保留作为初始值（HearthMirror 不可用时兜底，但不会触发 check-league）

#### v0.2.4 (2026-04-23)
- 修复本地玩家 ID 获取到观战好友 ID 的 bug：Power.log CREATE_GAME 块中观战者也可能有非零 GameAccountId.Lo，Parser 取第一个非零值会命中观战者。改为 STEP 13 时通过 HearthMirror LobbyInfo 按名字匹配本地玩家，用匹配到的 Lo 覆盖 Power.log 的值
- `HearthMirrorClient.FetchLobbyPlayers()` 新增 `localPlayerName` 参数，内部提取 displayName 匹配 `p.Name`
- 新增 `HearthMirrorClient.LocalPlayerLo` 属性，Parser 在 STEP 13 时检查并修正 `Game.AccountIdLo`
- 修正时输出诊断日志 `[Parser] 🔧 修正本地玩家 Lo: xxx → yyy`

#### v0.2.3 (2026-04-23)
- check-league 接入 HearthDb 解析英雄名，POST 携带 heroName（与插件格式一致）
- 修复 players dict 匿式对象 JSON 序列化 bug（改为 Dictionary<string, object>）
- 不再过滤 Lo=0 玩家，8 人全量发送
- 本地玩家附带 battleTag + displayName + startedAt
- gameUuid 为空重试 3 次（共 ~9 秒），Lo 全为 0 时跳过

#### v0.2.2 (2026-04-21)
- 修复非联赛对局验证码不显示：check-league 回调中验证码更新与 isLeague 判断解耦，无论是否联赛都同步验证码到 UI

#### v0.2.1 (2026-04-21)
- 标题栏显示版本号（从程序集版本读取）
- 点击玩家名跳转 URL 改为 apiBaseUrl 拼接
- 断线重连时日志显示具体时间 `[游戏] 🔄 断线重连 HH:mm:ss`
- apiKey 改为编译时常量（`ApiClient.cs` 的 `const string ApiKey`），不暴露在 config.json
- 启动扫描旧日志不触发 check_league（`_scanning` 标志），避免读到 HearthMirror 残留内存数据
- 日志优化：bg_tool.log 超 1MB 覆盖重写，移除 Lo=0 诊断日志，增加 API 连接日志

#### v0.2.0 (2026-04-21)
- WinForms 桌面应用：深色主题 UI，对局状态/最近战绩/今日统计/验证码复制
- 对接 Flask API：check-league（STEP 13）+ update-placement（游戏结束），失败重试 3 次
- testMode：仍调用 check-league 创建对局记录，强制标记为联赛
- 联赛对局持久化到 games.json，最近战绩和今日统计从文件读取，重启不丢失
- 启动时 Ping API 服务器，状态栏显示服务正常/异常
- 日志面板：Console 输出同步显示到 UI + bg_tool.log 文件
- 统一配置：bg_tool 和 HDT 插件都从 shared_config.json 读取（向上逐级查找）
- config.json 不再进 git，改用 config.json.example + shared_config.example.json 模板
- mock_server.py 支持状态追踪：随机验证码、对局记录、update-placement 校验

#### v0.1.1 (2026-04-19)
- 修复 HearthMirror Lo 获取：Reflection 实例改为单例缓存，对齐 Python 全局变量模式
- 修复中途启动无法使用：PreloadPlayerInfo 扩展为预加载 PlayerName + AccountIdLo + HeroEntityId
- STEP 13 无数据时不再静默，输出警告日志
- csproj 添加 PlatformTarget x86（HearthMirror.dll 是 32 位程序集，AnyCPU 64 位进程无法加载）
- 添加 AssemblyResolve 事件，从 HDT_PATH 自动加载 HearthMirror 的依赖 DLL（如 untapped-scry-dotnet.dll）

#### v0.1.0 (2026-04-19)
- C# 重写 Python bg_parser，net472 + HearthMirror 直接引用
- Power.log 实时监控 + 自动查找日志路径（注册表 + 常见路径兜底）
- CREATE_GAME / STEP / LEADERBOARD_PLACE 状态机解析
- 玩家信息提取（BattleTag、accountIdLo、英雄名+cardId）
- 投降检测、断线重连检测与恢复
- 中途接入：PreloadPlayerInfo 预加载 PlayerName
- 自动切换日志文件（游戏重启检测）
- HearthMirror 集成：直接引用 DLL，STEP 13 获取对手 Lo + HeroCardId
- 批量解析 --parse 模式
- Ctrl+C 优雅退出
- 静默处理文件短暂不可访问（IOException）

### C# 插件

#### v0.5.8 (2026-04-23)
- check-league LobbyInfo 延迟加载保护加强：gameUuid 为空重试 3 次（共 ~9 秒），accountIdLo 全为 0 时等 3 秒后重新读取 LobbyInfo
- 服务端同步：拒绝 accountIdLo 全为 0 的请求，日志补充 playerId 便于定位

#### v0.5.7 (2026-04-18)
- 修复日志刷屏：GetPlayerId/GetAccountIdLo 未找到时加 1 秒日志节流，避免 OnUpdate 每 100ms 写一条重复日志

### v0.5.6 (2026-04-14)
- 修复 GetPlayerId 失败导致 update-placement 静默丢失：三个缓存独立重试
- 修复 409 误判为失败：已提交的 placement 返回 409 时不再重试

#### v0.5.5 (2026-04-14)
- update-placement 网络失败时重试 3 次
- 插件认证：Bearer token + 版本号 header，服务端双重校验

#### v0.5.4 (2026-04-14)
- check-league 网络失败时重试 3 次

#### v0.5.3 (2026-04-14)
- placement 为 null 时重试上传，解决淘汰玩家排名丢失

#### v0.5.2 (2026-04-13)
- 队列超时机制：报名 10 分钟踢出，等待 20 分钟解散
- 验证码逻辑去重，print → logging

#### v0.5.1 (2026-04-13)
- 编译输出改用下划线分隔

#### 插件架构改造 (2026-04-12)
- 直连 MongoDB → HTTP API（CF Tunnel 限制）

### 联赛网站

#### v0.4.0 (2026-04-17)
- QQ 绑定码 API：`/api/bind-code` 生成绑定码（5 分钟有效），`/api/bind-code/verify` 机器人验证
- Webhook 通知：超时/掉线对局自动 POST 到 QQ 机器人
- 队列退出自动补人：等待组有人退出时，自动从报名队列拉人补满
- player 页面绑定按钮：已登录用户在自己主页可见

#### v0.3.4 (2026-04-15)
- 7人提交后自动推算第8人排名：当 7 位玩家提交 placement 后，自动计算剩余玩家的排名（唯一剩余数字），立即写入 endedAt 结束对局
- 适用于插件 API 和管理员补录 API 两个端点
- 解决第一名 AFK 不上传导致对局无法结束的问题

#### v0.3.3 (2026-04-17)
- 修复 player 页面 battleTag 不带 #tag：不再依赖 league_matches 中插件上报的不完整数据，改为从 league_players 读取真实 battleTag；匹配逻辑也从 battleTag 改为 accountIdLo，兼容带/不带 #tag 的访问

#### v0.3.1 (2026-04-14)
- 插件认证 + 版本强制更新：所有 /api/plugin/* 端点双重校验
  - API Key：配置 PLUGIN_API_KEY 后，插件请求必须带 Authorization: Bearer <key>，否则 403
  - 版本检查：X-HDT-Plugin header 版本号低于 MIN_PLUGIN_VERSION 则 403
  - 两个 env var 配合使用，发新插件时同步更换即可让旧插件失效

#### v0.3.0 (2026-04-14)
- 测试模式改为重叠人数匹配，MIN_MATCH_PLAYERS 可配置
- 报名队列阈值联动：满 N 人移入等待组，N 跟随 MIN_MATCH_PLAYERS（test=3, normal=8）
- toggle-test-mode.py 拆分为独立脚本，只管本仓库的 app.py

#### v0.2.13 (2026-04-14)
- 修复登录后导航到其他页面丢失登录状态的问题（Session cookie SameSite 配置）
- 修复 player 页面历史对局时间显示错误（双重时区偏移）
- 时间格式统一：所有 ISO 时间字符串带 Z 后缀，前端正确解析为 UTC
- 新增 WEB_VERSION 常量，页面底部显示当前版本号

#### 网站 UI 迭代 (2026-04-12 ~ 04-13)
- 问题对局提醒、选手页图表、SSE 实时推送

#### 数据统计修复 (2026-04-12)
- 排除 timeout/abandoned 对局

</details>
