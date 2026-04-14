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
                                   ├ player_records
                                   ├ league_matches
                                   ├ league_queue
                                   ├ league_waiting_queue
                                   └ league_players
```

### 数据流

```
玩家打完一局 → 插件 OnUpdate 检测游戏结束
  → 读取 rating + placement + opponents
  → HTTP POST /api/plugin/upload-rating → Flask 写入 player_records
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

### 1.x 版本管理

项目有**两套独立版本号**，互不关联：

| 组件 | 版本位置 | 何时递增 |
|------|----------|----------|
| C# 插件 | `BGTrackerPlugin.cs` + `HDT_BGTracker.csproj` | 插件功能/bugfix |
| 联赛网站 | `league/app.py` → `WEB_VERSION` | 网站功能/bugfix |

版本号规则：`主版本.次版本.修订号`
- **修订号 +1** — 修 bug
- **次版本 +1** — 加新功能
- **主版本 +1** — 大改/重构/正式发布

`WEB_VERSION` 通过 `inject_counts` context processor 注入所有模板，显示在 `base.html` 底部。
修改时只需改 `app.py` 中的 `WEB_VERSION = "x.y.z"` 一处。

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
- 读取：`gameEntity.GetTag(GameTag.STEP)` 返回 int

**BG 模式 step 流转（实测）：**

| STEP | 名称 | 说明 |
|------|------|------|
| 4 | BEGIN_MULLIGAN | 筹选开始 |
| 9 | MAIN_START | 主回合开始 |
| 10 | MAIN_ACTION | 战斗阶段 |
| 13 | MAIN_CLEANUP | 第一轮战斗结束，此时英雄已选定 |

**STEP 完整字典（0-17）：**
```
0=INVALID, 1=BEGIN_FIRST, 2=BEGIN_SHUFFLE, 3=BEGIN_DRAW, 4=BEGIN_MULLIGAN,
5=MAIN_BEGIN, 6=MAIN_READY, 7=MAIN_RESOURCE, 8=MAIN_DRAW, 9=MAIN_START,
10=MAIN_ACTION, 11=MAIN_COMBAT, 12=MAIN_NEXT, 13=MAIN_CLEANUP,
14=MAIN_START_TRIGGERS, 15=MAIN_GAMEOVER, 16=?, 17=?
```

**游戏结束检测（2026-04-12 实测）：**
- BG 中游戏每轮循环：`9(MAIN_START) → 10(MAIN_ACTION) → 13(MAIN_CLEANUP) → 9 → ...`
- STEP 10 不是游戏结束，只是**战斗阶段**，每轮都会出现
- 投降或自然淘汰：**无新 STEP 变化**，游戏直接切回菜单
- **结论：STEP 检测无法判断 BG 游戏结束**

**游戏结束信号来源（HDT 源码分析）：**
- `IsInMenu = true` 由 HDT 日志解析器驱动，非游戏 API
- 触发路径：HDT 监控 `LoadingScreen.log` → 检测 mode 从 `GAMEPLAY` 变为其他 → `HandleInMenu()` → `IsInMenu = true`
- 自然淘汰时：`State = COMPLETE` tag 先触发 `HandleGameEnd(true)`，`FinalPlacement` 此时已可读
- 投降时：mode 切回菜单 → `HandleInMenu()` → `IsInMenu = true`
- **当前方案：保持 `IsInMenu` 检测，这是最简单可靠的方式**
- 备选方案：轮询 `FinalPlacement`，非 null 即游戏结束（自然淘汰时 `HandleGameEnd(true)` 已填好值）

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
    # +8 hours，处理 BSON datetime 和带 Z 的字符串
    return cst.strftime("%Y-%m-%d %H:%M")

app.jinja_env.filters['cst'] = to_cst_str
```

**重要**：`to_iso_str()` 自 v0.5.3 起统一返回带 `Z` 的 UTC 时间。前端 `new Date(str)` 会正确解析为 UTC，再手动 +8h 转北京时间。所有 MongoDB 字符串比较（如 `startedAt` cutoff）也必须同步加 `Z`。

### 4.4 active games 时间比较

`startedAt` 存为字符串格式（带 `Z` 后缀），Python 查询必须用字符串比较（不是 datetime 对象）：

```python
# ✅ 正确 — cutoff 字符串必须带 Z 后缀匹配 startedAt 格式
cutoff_str = (datetime.now(UTC) - timedelta(minutes=80)).strftime("%Y-%m-%dT%H:%M:%SZ")
query = {"startedAt": {"$gte": cutoff_str}}

# ❌ 错误 — cutoff 不带 Z，而 startedAt 带 Z，字符串比较会出错
cutoff_str = (datetime.now(UTC) - timedelta(minutes=80)).strftime("%Y-%m-%dT%H:%M:%S")

# ❌ 错误 — MongoDB BSON datetime 和 String 排序不同
cutoff_dt = datetime.now(UTC) - timedelta(minutes=80)
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

## 5. 更新记录

### v0.3.0 (2026-04-14) — Web
- **测试模式改为重叠人数匹配**：
  - 原逻辑：test 模式无脑判联赛（跳过一切匹配）
  - 新逻辑：遍历等待组，计算本局玩家 `accountIdLo` 与等待组的交集，重叠 ≥ `MIN_MATCH_PLAYERS` 才判联赛
  - `MIN_MATCH_PLAYERS` 常量控制阈值（test=3, normal=8）
  - 报名队列→等待组的移入阈值同步跟随 `MIN_MATCH_PLAYERS`（不再硬编码 8）
  - `toggle-test-mode.py` 切换模式时自动改 `MIN_MATCH_PLAYERS` 值
  - 日志带 `[TEST]` 标签，方便排查

### v0.2.13 (2026-04-14)
- **登录状态持久化修复**：
  - 显式配置 `SESSION_COOKIE_SAMESITE = "Lax"` + `SESSION_COOKIE_HTTPONLY = True`
  - 修复登录后点击其他页面（选手页、问题对局页等）丢失登录状态的问题
  - `SESSION_COOKIE_SECURE` 暂为 `False`（HTTP 可用），上线 HTTPS 后改为 `True`
- **时间显示修正**：
  - `to_iso_str()` 统一返回带 `Z` 后缀的 UTC 时间字符串
  - 所有 MongoDB 字符串比较用的 cutoff 时间同步加 `Z`（保证 `startedAt` 查询正确）
  - player.html `toCst()` 重写：改用 `getUTC*` 方法 +8h 转北京时间，修复双重时区偏移
  - 兼容新旧格式（带/不带 Z 后缀）的时间字符串
- **版本号管理**：
  - `app.py` 新增 `WEB_VERSION` 常量，通过 context processor 注入所有模板
  - `base.html` 底部显示版本号

### v0.5.3 (2026-04-14)
- **placement 重试机制：修复淘汰玩家排名丢失**
  - 问题：玩家被淘汰后回到菜单（IsInMenu=true），插件等 2 秒读 FinalPlacement，可能为 null（HDT 尚未写入）
  - 原逻辑：placement=null → 跳过 → 重置状态 → 排名永远丢失
  - 新逻辑：placement=null → 不重置状态 → 下个 OnUpdate 周期重试，最多 10 次
  - `UpdateLeaguePlacement` 改为返回 `bool`（true=已上传，false=需重试）
  - `IsInMenu` 分支增加 `_placementRetryCount` 计数器
- **测试脚本 `test_league.py`**
  - 模拟真实插件行为：每个玩家独立淘汰 → 独立检测 IsInMenu → 独立读 placement
  - `--mode=before/after/both` 对比重试机制效果

### v0.5.2 (2026-04-13)
- **队列超时机制**：
  - `@app.before_request` 刷新 `league_players.lastSeen` + `league_queue.lastSeen`
  - 报名队列 10 分钟无活动自动踢出（`QUEUE_TIMEOUT_MINUTES`）
  - 等待队列 20 分钟自动解散，不再回到报名队列（`WAITING_QUEUE_TIMEOUT_MINUTES`）
  - 登出时自动退出所有队列
  - 清理时机：周期性（60s）+ 队列操作时 + check-league 时
- **代码质量**：
  - 验证码逻辑抽成 `_ensure_verification_code()`，消除 3 处重复代码
  - `print()` 全部替换为 `logging` 模块（info/warning/error）
  - 移除 `league_players` 中未使用的统计字段（totalGames/totalPoints/wins/chickens/avgPlacement）
  - `toggle-test-mode.py` 同步更新

### v0.5.1 (2026-04-13)
- 版本号 0.5.1，编译输出 DLL 改用下划线分隔（`HDT_BGTracker_0.5.1.dll`）

### v0.5.0 (2026-04-13)
- 编译输出自动带版本号（`HDT_BGTracker-0.5.0.dll`）

### check-league 并发修复 (2026-04-13)
- `check-league` 补充 `accountIdLo` 字段
- fallback 查 `league_matches` 防并发丢失联赛标记

### 网站 UI 迭代 (2026-04-12 ~ 04-13)
- 首页新增问题对局提醒（红色提示框 + SSE 实时更新）
- 问题对局提示移至正在进行下方，进行中对局每页改 3 个
- 选手名位置调整，移除选手页 🎮 头像
- 注册流程 bug 修复

### 折线图修复 (2026-04-12)
- 折线图时间方向修正：左旧右新
- Revert 管理员修改锁定排名（误合）

### 数据统计修复 (2026-04-12)
- 所有统计数据排除 timeout/abandoned 对局

### 插件优化 (2026-04-12)
- 精简插件日志，移除冗余缓存和玩家列表输出
- 测试模式 accountIdLo → accountIdLoList，修复玩家列表为空

---

## 6. 数据库结构

### 集合一览

| 集合 | 写入方 | 说明 |
|------|--------|------|
| `player_records` | Flask API（`/api/plugin/upload-rating`） | 玩家分数记录（含验证码） |
| `league_matches` | Flask API（`/api/plugin/check-league` + `/api/plugin/update-placement`） | 联赛对局（8人完整数据） |
| `league_queue` | Flask 网站 | 报名队列（含 `lastSeen` 超时踢出） |
| `league_waiting_queue` | Flask 网站 + `/api/plugin/check-league` | 等待组（满 N 人自动创建，20 分钟超时解散） |
| `league_players` | Flask 网站 | 已注册选手（含 `lastSeen` 活跃追踪） |

> C# 插件不再直接操作 MongoDB，所有写入通过 Flask API 中转。

### 队列超时机制

| 队列 | 超时时间 | 触发条件 | 行为 |
|------|----------|----------|------|
| `league_queue` | 10 分钟 | `lastSeen` 超时 | 自动踢出 |
| `league_waiting_queue` | 20 分钟 | `createdAt` 超时 | 解散组，活跃玩家回报名队列 |

`lastSeen` 由 `@app.before_request` 在每次页面请求时刷新，报名队列条目同步更新。
登出时自动退出所有队列。
清理时机：周期性（60s）+ 队列 API 调用时 + 插件 check-league 调用时。

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

## 7. 部署

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

## 8. 待办

### 高优先级
- [x] **插件流程精简：去除上传分数，纯联赛插件** ✅ 已完成
  - 背景：插件最初目的是上传分数，现核心用途是联赛记录，分数上传已无必要
  - 新流程：
    ```
    游戏开始 → 等 3 秒缓存 playerId/accountIdLo/gameUuid
      → CheckLeagueQueue() → POST /api/plugin/check-league（同时可返回验证码）
        → 服务端返回 isLeague: true/false
      → 游戏结束（回到菜单 IsInMenu）
        → 联赛局: UpdateLeaguePlacement() → POST /api/plugin/update-placement
        → 非联赛局: 什么都不做
      → 重置状态
    ```
  - 移除：`TryUploadRating()`、`UploadRating()`、`IncrementLeagueCount()`
  - 2026-04-12 实测：BG 游戏无结束 step，STEP 10 只是战斗阶段；游戏结束依赖 HDT 的 `IsInMenu`（由日志 mode 变化驱动）
- [x] 编译验证 HearthDb 引用是否可用（`Cards.All` 查英雄名）
- [x] SSE 连接 120 秒自动断开 + 客户端重连，防僵尸连接堆积
- [x] **player_records 精简：移除 `games`、`ratingChanges`、`placements` 数组** ✅ 已完成
  - 联赛所有数据（排名、英雄、时间戳）已完整存储在 `league_matches` 中，`player_records` 的历史数组纯属冗余
  - 移除后单条从 ~14.5KB 降到 ~1.5KB，节省 90%
  - 保留字段：`playerId`、`accountIdLo`、`rating`、`lastRating`、`ratingChange`、`gameCount`、`mode`、`region`、`timestamp`、`verificationCode`
  - 已同步更新 `README.md`、`API.md` 数据结构文档
- [x] **插件架构改造：直连 MongoDB → HTTP API（通过 CF Tunnel）** ✅ 已完成
  - 背景：CF Tunnel 只能穿透 HTTP，无法暴露 MongoDB 端口
  - C# 插件：`MongoDB.Driver` → `HttpClient` + `JavaScriptSerializer`
  - Flask 端：3 个 `/api/plugin/*` 端点，由插件 HTTP 调用
  - 插件体积：仅 1 个 DLL（移除 MongoDB 全套依赖）
  - `ApiBaseUrl` 配置项支持本地/生产切换

### 中优先级
- [ ] CheckAndFinalizeMatch 写入竞争优化（8 人并行写 endedAt）
- [ ] 赛季功能（`seasonId` 字段隔离不同届联赛）

### 低优先级
- [x] **玩家页对局分析模块：折线图 + 饼图** ✅ 已完成
  - 使用 ECharts 5.x（CDN 加载，无后端依赖）
  - 左侧折线图：近 20 局排名走势，按排名着色（8 色梯度）
  - 右侧环形饼图：排名分布占比
  - 数据来自前端 `matches_json`，纯客户端计算，零额外请求
- [x] **正在进行对局分页** ✅ 已完成
  - 每页最多显示 4 个对局，带分页按钮
  - SSE 推送时保留当前页码
- [ ] **公网生产部署：Nginx + HTTPS**（待有公网 IP + 域名时启用）
  - 已准备 `docker-compose.prod.yml`（Flask + Nginx + Certbot 三件套）
  - 已准备 `nginx.conf`（反向代理 + SSL 终止 + SSE 长连接支持）
  - CF Tunnel 延迟较高，公网部署后切直连
  - 上线步骤：域名解析 → nginx.conf 替换 YOUR_DOMAIN → certbot 签证 → docker compose up
