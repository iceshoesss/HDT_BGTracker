# HDT_BGTracker 开发记录

## 项目概述
炉石传说酒馆战棋分数记录插件，每局结束后自动记录分数并上传到 MongoDB。

## 当前状态

### 联赛网站 (2026-04-08 更新)
- ✅ 英雄头像显示（对局详情/个人历史/正在进行）
  - C# `CreateLeagueMatch` 存储 `heroCardId` 字段
  - 图片来源：`https://art.hearthstonejson.com/v1/256x/{heroCardId}.jpg`（256×256 正方形头像）
  - CSS 裁剪：容器宽>高 + `object-top`，自动聚焦脸部，裁掉底部肩膀
  - `tiles` 格式（256×59 横条）已弃用，效果太差
- ✅ 修复进行中对局不显示
  - 根因：`startedAt` 存为字符串，Python 用 `datetime` 比较，MongoDB BSON 类型排序 String < DateTime，比较永远返回 false
  - 修复：`inject_counts` / `get_active_games` / `cleanup_stale_games` 三处统一用字符串格式比较

### 插件核心
- ✅ 分数获取正常
- ✅ 玩家 ID 获取（`Player.Name`，游戏开始 3 秒后缓存）
- ✅ 玩家 AccountId.Lo 获取（唯一标识，存为 string 避免大数问题）
- ✅ 对手 ID 获取（`BattlegroundsLobbyInfo.Players`，排除自己，含 name + accountIdLo）
- ✅ GameUuid 获取（`BattlegroundsLobbyInfo.GameUuid`）
- ✅ MongoDB 上传（rating + mode + region + placement + accountIdLo + games 数组）
- ✅ 分差记录（聚合管道原子计算，存储在 `ratingChanges` 数组）
- ✅ 排名获取（`CurrentGameStats.BattlegroundsDetails.FinalPlacement`）
- ✅ 浮动面板显示玩家名字+序号（LobbyOverlay）
- ✅ 对局数据记录（games 数组：gameUuid, isLeague, placement, opponents, endTime, ratingChange）
- ✅ STEP tag 检测（替代 63s 固定延迟，STEP 13 时输出带英雄名的 lobby）
- ✅ 日志自动清理（启动时删除超过 3 天的 .log 文件）
- MongoDB 连接: 通过 `skip-worktree` 本地配置，不暴露到 GitHub
- 数据库: `hearthstone`, 集合: `bg_ratings`

## 重要发现

### 玩家 ID 获取问题
原代码从 `Core.Game.Player.AccountId` / `Player.Name` 在**游戏结束后**读取，但此时值为 `-1` / 空字符串。

**关键发现**：
- `Player.Name` 在**游戏进行中**有正确值（如 `南怀北瑾丨少头脑#5267`）
- 游戏结束后 Player 对象被重置，Name 变空
- 进入游戏后需要**延迟约 3 秒**再读取，否则 Player.Name 还没初始化
- `Config.Instance.BattleTag` 在这个版本的 HDT 中**不存在**
- `Core.Game.AccountInfo` 在 `GameV2` 类上**不存在**

**当前方案**：检测到酒馆战棋游戏开始后，等 3 秒读取 `Player.Name` 并缓存，游戏结束后用缓存值上传。

### 对手 ID 获取
- `Core.Game.MetaData.BattlegroundsLobbyInfo` 在游戏加载后可用（不是立即可用，需要等待）
- `BattlegroundsLobbyInfo.Players` 返回 8 个玩家（含自己），每个有 `Name` 和 `AccountId`
- `heroCardId` 通常为空字符串（LobbyInfo 不包含英雄选择信息）
- 自己的 Name 格式带 BattleTag 号（如 `南怀北瑾丨少头脑`），`Player.Name` 也类似但完整格式不同
- 需要在 csproj 中添加 `HearthMirror.dll` 引用，否则编译报错（`BattlegroundsLobbyInfo` 类型在 HearthMirror 程序集中定义）

### HDT Overlay API
- 添加覆盖层元素：`Core.OverlayCanvas.Children.Add(element)`（`OverlayCanvas` 是 `Canvas` 类型）
- 移除：`Core.OverlayCanvas.Children.Remove(element)`
- 鼠标事件穿透控制：`OverlayExtensions.SetIsOverlayHitTestVisible(element, true)`（需要 `using Hearthstone_Deck_Tracker.Utility.Extensions`）
- `Core.OverlayWindow` 是 `Window` 类型，没有 `Children` 属性，不能直接添加子元素
- 参考项目：[HDT_BGrank](https://github.com/IBM5100o/HDT_BGrank)

### 排名获取（FinalPlacement）
- 来源：`Core.Game.CurrentGameStats.BattlegroundsDetails.FinalPlacement`
- 返回 `int?`，值为 1-8（第几名），null 表示未获取到
- `BattlegroundsDetails` 类型是 `BattlegroundsLobbyDetails`，包含：
  - `FinalPlacement` — 最终排名
  - `LobbyRawHeroDbfIds` — 大厅可用英雄 DBF ID 列表
  - `FriendlyPlayerEntityId` — 自己的实体 ID
  - `FriendlyRawHeroDbfId` — 自己选的英雄 DBF ID
  - `AnomalyDbfId` — 异常模式 DBF ID
- 在游戏结束后、返回主菜单时读取（和 rating 读取时机一致）
- 时机很重要：太早可能为 null，当前方案等 2 秒再读

### MongoDB 聚合管道更新
- `Update.Pipeline(BsonDocument[])` — 传入聚合管道 stage 数组
- 管道内 `$rating` 引用文档当前字段值（不是 C# 变量）
- **`$push` 不能作为管道 stage**，用 `$set` + `$concatArrays` 替代：
  ```javascript
  { $set: { arrayField: { $concatArrays: [{ $ifNull: ["$arrayField", []] }, [newItem]] } } }
  ```
- `$ifNull` 兜底：字段不存在时用默认值
- `$toString` 可以在管道中将 Int64 转 String，兼容旧数据类型不一致
- 管道内引用计算值：`"$ratingChange"` 引用 Stage 1 的 `$set` 结果
- MongoDB 驱动版本 2.19.2

### MongoDB.Driver 2.19.2 API 兼容性 ⚠️

**重要：项目锁定 MongoDB.Driver 2.19.2，以下 API 在此版本不可用，必须用替代写法。**

| 新版 API (2.21+) | 2.19.2 替代写法 | 说明 |
|---|---|---|
| `Update.Set("field", val)` 链式调用 | `new BsonDocument("$set", new BsonDocument { {"field", val} })` | `UpdateDefinitionBuilder` 不支持链式 `.Set()` |
| `Update.SetOnInsert("field", val)` | `new BsonDocument("$setOnInsert", new BsonDocument { ... })` | 2.19.2 没有 `SetOnInsert` |
| `.Find(filter).FirstOrDefault()` | 需要 `using MongoDB.Driver;` | `Find` 是 `IMongoCollectionExtensions` 扩展方法 |
| `Builders<>.Filter.Eq()` + `Builders<>.Update.Set()` 链式 | 直接用 `BsonDocument` 构建 filter 和 update | 避免混用两种风格 |

**原则：新建代码统一用 `BsonDocument` 风格，不要用 `Builders<>` 的链式 API。**

示例——正确的 upsert 写法：
```csharp
var filter = new BsonDocument("gameUuid", gameUuid);
var update = new BsonDocument("$setOnInsert", new BsonDocument
{
    { "players", playersArray },
    { "region", region },
    { "mode", mode },
    { "startedAt", startedAt },
    { "endedAt", BsonNull.Value }
});
_collection.UpdateOne(filter, update, new UpdateOptions { IsUpsert = true });
```

示例——正确的数组内定位更新：
```csharp
var filter = new BsonDocument
{
    { "gameUuid", gameUuid },
    { "players.accountIdLo", accountIdLo }
};
var update = new BsonDocument("$set", new BsonDocument
{
    { "players.$.placement", 3 },
    { "players.$.points", 6 }
});
_collection.UpdateOne(filter, update);
```

**必须加 `using MongoDB.Driver;`**，否则 `Find()` 扩展方法不可见（`InsertOne`/`UpdateOne` 是接口实例方法，不受影响）。

### STEP Tag 与游戏阶段检测
- `GameEntity` 上的 `STEP` tag（tag ID 198）标记当前游戏阶段
- BG 模式下 step 流转（实测数据）：
  ```
  0 (INVALID) → 4 (BEGIN_MULLIGAN) → 13 (MAIN_CLEANUP) → 9 (MAIN_START) → 10 (MAIN_ACTION) → ...
  ```
- BG 不走标准天梯的 MAIN_READY(6) 等中间 step
- **STEP 13 (MAIN_CLEANUP)** 是第一个可靠的变化点，约 40s，替代了原来的 63s 固定延迟
- STEP 4 (BEGIN_MULLIGAN) = 英雄选择阶段开始
- STEP 13 (MAIN_CLEANUP) = 第一轮战斗结束，英雄选择早已完成
- 枚举值：`HearthDb.Enums.GameStep`（需 `using HearthDb.Enums`）
- Tag 读取：`entity.GetTag(GameTag.STEP)` 返回 int

## 🔍 如何查找 HDT API（速查）

当需要某个功能但不知道 HDT 提供了什么 API 时：

1. **HDT 源码在 GitHub**：`https://github.com/HearthSim/Hearthstone-Deck-Tracker`
2. **关键文件**：
   - `Hearthstone Deck Tracker/Hearthstone/GameV2.cs` — 主游戏类，包含大部分 BG 相关属性
   - `Hearthstone Deck Tracker/Stats/GameStats.cs` — 游戏统计，包含 `BattlegroundsLobbyDetails`（含 `FinalPlacement`）
   - `Hearthstone Deck Tracker/Hearthstone/Player.cs` — 玩家类
   - `Hearthstone Deck Tracker/API/` — 插件 API 接口
3. **查看源码方式**：
   - 直接访问 raw 文件：`https://raw.githubusercontent.com/HearthSim/Hearthstone-Deck-Tracker/refs/heads/master/Hearthstone%20Deck%20Tracker/Hearthstone/GameV2.cs`
   - 注意 URL 中空格编码为 `%20`
4. **查找技巧**：
   - 在 HDT GitHub 上搜关键字（如 `Placement`、`Rating`、`Leaderboard`）
   - 先看 `GameV2.cs` 中的 public 属性，大部分 BG 数据都在这里
   - `HearthMirror.Reflection.Client` 提供了从游戏内存读取数据的方法
5. **参考项目**：
   - [HDT_BGrank](https://github.com/IBM5100o/HDT_BGrank) — 显示对手 MMR
   - [Battlegrounds-Match-Data](https://github.com/jawslouis/Battlegrounds-Match-Data) — 记录 BG 比赛数据到 CSV

### net472 SDK 项目 WPF 限制
- `net472` SDK-style 项目**不支持** `<UseWPF>true</UseWPF>`（仅 .NET Core 3.0+ 支持）
- XAML 编译（`<Page>` + `InitializeComponent`）在 net472 SDK 项目中**无法正常工作**
- **解决方案**：纯 C# 代码创建 WPF UI，不用 XAML（`new TextBlock()`, `new Grid()` 等）
- 需要手动添加 `<Reference Include="System.Xaml">` 引用（`UserControl` 依赖此程序集）

## 修改历史（claw_version 分支）

1. **初始修复** - 尝试从 `AccountInfo` / `Config` 获取 BattleTag
   - 失败：`GameV2` 没有 `AccountInfo`，`Config` 没有 `BattleTag`
2. **解决 Core 歧义** - `using Hearthstone_Deck_Tracker` 导致 `Core` 引用歧义
3. **添加调试日志** - 发现 `Player.Name` 在游戏中有值
4. **5 秒轮询 → 简化为游戏开始读取** - 成功获取 PlayerId
5. **添加 3 秒延迟** - Player.Name 初始化时间
6. **对手 ID 调试** - 发现 `BattlegroundsLobbyInfo.Players`
7. **LogLobbyPlayers** - 输出 lobby 玩家名单日志
8. **添加 HearthMirror 引用** - 解决 `BattlegroundsLobbyInfo` 类型编译错误
9. **LobbyOverlay 浮动面板** - 纯 C# WPF UI（XAML 在 net472 SDK 项目中不可用）
10. **分差记录** - MongoDB 聚合管道原子计算 `lastRating` + `ratingChanges`
11. **修复 `$push` 管道错误** - `$push` 不能作为管道 stage，改用 `$set` + `$concatArrays`
12. **MongoDB 地址外置** - 经历环境变量 / local.config 方案后撤回，最终用 `git update-index --skip-worktree` 方案
13. **排名获取** - 通过查 HDT 源码发现 `CurrentGameStats.BattlegroundsDetails.FinalPlacement`
14. **BattleTag 调试**（2026-04-07 session）— 见下方详细记录
15. **数据库结构扩展 accountIdLo + games 数组** — 新增玩家唯一标识和对局明细记录
16. **AccountId.Lo 类型修复** — ulong → string（避免 BsonInt64 隐式转换歧义）
17. **日志自动清理** — 插件启动时删除超过 3 天的 .log 文件
18. **STEP tag 诊断** — 通过 dump STEP 变化确定 BG 模式下 step 流转
19. **STEP 13 替代固定延迟** — 用 MAIN_CLEANUP 检测替代 63s HeroSelectionDelay

## 📋 2026-04-07 开发日志：BattleTag / AccountId / 英雄映射

### 目标
确认能否获取其他玩家的 BattleTag（名字#号），为联赛匹配做准备。

### 实验过程

#### 实验 1：全量 Dump Player/LobbyPlayer/MetaData 属性
- 用反射 Dump 所有公共属性，嵌套对象递归展开（最多 2 层）
- **结果**：
  - `Player.Name` = `南怀北瑾丨少头脑#5267` ✅ 自己有完整 BattleTag
  - `LobbyPlayer.Name` = `南怀北瑾丨少头脑` ❌ 不带 #tag
  - `LobbyPlayer.AccountId` = `HearthMirror.Objects.AccountId` { Hi, Lo }
  - `MetaData.AccountId` = 同上（自己的 AccountId）

#### 实验 2：确认 AccountId.Lo 是否跨局稳定
- 测试了 3 次（2 局游戏 + 1 次插件重启）
- 自己的 `AccountId.Lo = 1708070391` 三次完全一致 ✅
- **结论**：`AccountId.Lo` 是暴雪内部的账号唯一标识，不会变

#### 实验 3：Entity 系统中的 PLAYER_IDENTITY
- 遍历 `Core.Game.Entities.Values`，检查 `Tags` 字典中 `PLAYER_IDENTITY (271)` 标签
- **结果**：只有 `GameEntity`、`调酒师鲍勃`、自己（带 `#5267`）有 PLAYER_IDENTITY
- 对手的 entity **没有** PLAYER_IDENTITY 标签
- 对手的 entity.Name 也是不带 #tag 的纯名字
- **结论**：Entity 系统也无法获取其他玩家的 BattleTag

#### 实验 4：能否从 AccountId.Lo 推算 #tag
- 自己：`#5267` vs `Lo=1708070391`
- `1708070391 % 10000 = 391 ≠ 5267`
- **结论**：两者无数学关系，是暴雪内部并行的两套独立 ID 体系

#### 实验 5：能否通过 Blizzard API 反查
- 查了 Battle.net API 文档
- `/account/user` 只能查自己的 BattleTag（需要 OAuth）
- **没有**通过 AccountId 查他人 BattleTag 的公开接口
- **结论**：暴雪不暴露这个映射关系

### 最终结论：玩家 ID 获取
| 数据源 | Name 格式 | 有 #tag？ | 唯一标识 |
|--------|----------|----------|---------|
| `Player.Name` | `南怀北瑾丨少头脑#5267` | ✅ | — |
| `LobbyPlayer.Name` | `南怀北瑾丨少头脑` | ❌ | `AccountId.Lo` |
| Entity `e.Name` | 同上 | ❌ | `EntityId` |
| `AccountId.Lo` | 数字 | N/A | ✅ 跨局稳定不变 |

**联赛匹配方案**：用 **LobbyPlayer.Name** 匹配已注册玩家，`AccountId.Lo` 作备用唯一标识（防同名）。

### 英雄 ID → 英雄名映射
- HeroCardId 格式：`TB_BaconShop_HERO_56`、`BG20_HERO_202`、`BG31_HERO_802` 等
- **最佳方案**：使用 `HearthDb.Cards.All[cardId].Name`
  - HDT 自带的卡牌数据库，不需要自己维护映射表
  - 不需要网络请求，HDT 更新时自动同步
  - 用法：`using HearthDb.CardDefs; Cards.All.TryGetValue(heroCardId, out var card)` → `card.Name`
- 需要在 csproj 中添加 `HearthDb.dll` 引用（位于 HDT 目录）
- 已添加引用，**待编译验证**（本次会话时间不够）
- 备选数据源：`https://api.hearthstonejson.com/v1/latest/zhCN/cards.json`（如果 HearthDb 不可用）

### 实验中的英雄名对应关系（部分，来自 HearthstoneJSON）
| CardId | 英雄名 |
|--------|--------|
| TB_BaconShop_HERO_56 | 阿莱克丝塔萨 |
| TB_BaconShop_HERO_50 | 苔丝·格雷迈恩 |
| TB_BaconShop_HERO_36 | 舞者达瑞尔 |
| TB_BaconShop_HERO_57 | 诺兹多姆 |
| TB_BaconShop_HERO_76 | 奥拉基尔 |
| TB_BaconShop_HERO_74 | 林地守护者欧穆 |
| TB_BaconShop_HERO_92 | 亚煞极 |
| TB_BaconShop_HERO_23 | 沙德沃克 |
| TB_BaconShop_HERO_64 | 尤朵拉船长 |
| TB_BaconShop_HERO_16 | 挂机的阿凯 |
| BG20_HERO_202 | 阮大师 |
| BG20_HERO_102 | 萨鲁法尔大王 |
| BG23_HERO_306 | 希尔瓦娜斯·风行者 |
| BG31_HERO_802 | 阿塔尼斯 |

### 下一步
1. **编译验证 HearthDb** — 确认 `HearthDb.dll` 在 HDT 目录下，`Cards.All` 可用
2. **集成英雄名到 overlay** — 用 `Cards.All` 查英雄名替换 cardId 显示
3. **将 lobby 玩家信息上传到 MongoDB** — opponents 数组（name + AccountId.Lo + hero + placement）
4. **开始联赛功能开发** — 后端 API + 插件匹配逻辑

## 下一步工作
1. **测试未验证的改动** — accountIdLo + games 数组上传、STEP 13 英雄名获取（本次开发未测试，需实际开局验证）
2. 编译验证 HearthDb 引用是否可用
3. 联赛功能：后端 API + 插件 lobby 匹配逻辑
4. 验证 FinalPlacement 在不同场景下是否可靠（单人/双人/掉线重连等）
5. games 数组中增加英雄名字段（需先验证 HearthDb）

## 📋 2026-04-08 开发日志：Bug 修复 + 联赛网站 Demo

### 参与者
- 用户 + Claw (AI pair programmer)

### 插件 Bug 修复

#### Bug 1：Lobby 玩家名单重复输出
- **现象**：每 ~100ms 输出一次 "=== Lobby 游戏开始 ==="，同一局游戏重复输出数十次
- **原因**：`LogLobbyPlayers(includeHeroes: false)` 的触发条件 `!_heroLogged` 不合理 — `_heroLogged` 要等 STEP 13 才变 true，而 PlayerId 就绪后这个条件在每个 OnUpdate 周期都满足
- **修复**：新增 `_lobbyLogged` 标志，PlayerId 就绪后只输出一次 lobby 名单
- **提交**：`8167dda`

#### Bug 2：STEP 变化诊断日志清理
- **说明**：STEP 变化日志是调试阶段添加的，确认 STEP 13 检测可行后已无用
- **操作**：移除 39 行诊断代码（STEP 变化日志 + 启动时 STEP 初始值日志 + StepNames 字典），保留 STEP 13 英雄选择检测逻辑
- **提交**：`ab58079`

#### Bug 3：上传成功后日志报错 "BsonString→BsonInt64"
- **现象**：日志显示"上传 MongoDB 失败: 无法将类型为 BsonString 的对象强制转换为 BsonInt64"
- **原因**：上传实际成功，但成功日志中 `opponents.Select(o => o["accountIdLo"].AsInt64)` 用了 `.AsInt64`，而 accountIdLo 在 `GetOpponentInfo` 中存的是 `BsonString`（`p.AccountId.Lo.ToString()`）
- **修复**：`.AsInt64` → `.AsString`
- **提交**：`782379f`

#### 浮动窗口代码注释
- **说明**：LobbyOverlay（游戏内浮动面板）暂时不用，相关代码全部注释保留
- **涉及文件**：`BGTrackerPlugin.cs`（_overlay 字段 + OnLoad/OnUnload 中的创建和清理）、`RatingTracker.cs`（_overlay 字段 + SetOverlay 方法 + Hide 调用 + DisplayResult 调用）
- **提交**：`1b7fa84`

### 联赛网站 Demo

#### 技术选型
- 放弃 Next.js，改用 **Flask + Jinja2 + Tailwind CSS CDN**
- 理由：Python 更简单，不需要 Node.js 构建工具，一个文件就能跑
- 暂不接入 MongoDB，使用内嵌 mock 数据

#### 已实现功能
- **排行榜页** (`/`)：12 名选手，积分/场次/胜率/场均排名/吃鸡，点击列头前端排序，右侧实时对局侧边栏（秒级计时器），最近 5 场对局摘要
- **选手详情页** (`/player/<battleTag>`)：总积分/场次/吃鸡率/场均排名 + 历史对局列表
- **对局详情页** (`/match/<gameUuid>`)：8 人排名、英雄、积分
- **API**：`/api/players`、`/api/matches`、`/api/active-games`

#### Bug：实时计时器不更新
- **现象**：侧边栏计时器显示 `⏱️ --:--` 不动
- **原因**：`startedAt` 使用 ISO 字符串（如 `2026-04-07T22:15:00Z`），JavaScript `new Date()` 在某些环境下解析不可靠；且脚本在 `<head>` 中被 Tailwind CDN 阻塞
- **修复**：改用 epoch 时间戳（`time.time()`），JS 移至 `</body>` 前
- **提交**：`44a0042`、`eb47fc8`

#### 主播名替换
- Mock 数据人名从虚构名称改为酒馆战棋邀请赛真实主播：衣锦夜行、瓦莉拉、墨衣、安德罗妮、驴鸽、异灵术、岛猫、赤小兔、甜水七、慕容清清、小呆萝拉、王师傅
- **提交**：`0724eec`、`c9c76d1`

### 数据库结构评审建议

当前 LEAGUE_PLAN.md 中定义的三张表（`league_players`、`league_matches`、`league_active_games`）基本够用，但有以下潜在问题：

1. **选手对局查询性能** — `league_matches` 中 `players` 是嵌套数组，查「某人历史对局」需全集合扫描
   - 建议：`league_matches` 上加 `players.battleTag` 多键索引
2. **积分一致性** — `totalPoints`、`wins`、`avgPlacement` 预计算在 player 表，插件每次上传自行更新；若多端同时写入可能不一致
   - 建议：Phase 2 接入真实数据时，改为后端用聚合管道统一计算
3. **活跃对局残留** — 插件崩溃时 `league_active_games` 记录不会被清理
   - 建议：加 TTL 索引（如 2 小时自动删除）
4. **赛季隔离** — 当前无赛季字段，若举办第二届联赛，历史积分和当前积分会混在一起
   - 建议：`league_matches` 和 `league_players` 增加 `seasonId` 字段（可后期迭代）
5. **对局表 vs 嵌入式方案** — 当前 `bg_ratings` 集合中 `games` 数组内嵌在 player 文档里，`league_matches` 又单独存了一份。两套数据有冗余
   - 建议：明确以 `league_matches` 为对局数据唯一来源，`bg_ratings` 中的 `games` 仅作备份

### 下一步（联赛网站）
1. Phase 1（当前）：用 mock 数据打磨 UI 和交互 ✅ 基本完成
2. Phase 2：安装 MongoDB，导入 mock 数据，API Routes 改为读 MongoDB
3. Phase 3：插件对接 — 上传到 `league_matches` + 写入/删除 `league_active_games`
4. Phase 4：选手注册验证码流程、选手个人页、对局详情页

## 📋 2026-04-08 (下) 开发日志：MongoDB 接入 + Bug 修复 + 报名队列

### 参与者
- 用户 + Claw (AI pair programmer)

### Phase 2 完成：Flask 后端接入 MongoDB
- `app.py` 连接 MongoDB `[YOUR_HOST]:27017`，数据库 `hearthstone`
- 所有 API 和页面改为从 MongoDB 实时读取数据

### Bug 修复

#### Bug 1：排行榜无数据
- **现象**：排行榜为空
- **原因**：`get_players()` 查询 `league_players` 集合，但 C# 插件只写 `bg_ratings` 和 `league_matches`，从不写 `league_players`
- **修复**：改用 MongoDB 聚合管道从 `league_matches` 计算排行榜，不依赖 `league_players`
  - `$unwind` players 数组 → `$group` 按 battleTag → `$sum` 积分/场次/胜场
  - 通过 `$lookup` 从 `bg_ratings` 获取 `gameCount`（BG 总场次）
- **提交**：`0c13f82`

#### Bug 2：对局数据与 MongoDB 有出入 + ⏱️ 00:00 不动
- **现象**：时间显示异常，计时器不动
- **原因**：C# 驱动存 `DateTime.UtcNow.ToString("o")` → MongoDB 存为 **BSON datetime** → PyMongo 读出来是 Python `datetime` 对象，不是字符串
  - 模板 `{{ match.endedAt[:16] }}` 对 datetime 对象做字符串切片 → 结果不对
  - `get_active_games()` 中 `g["startedAt"].replace("Z", "+00:00")` → datetime 对象没有 `.replace()` 方法 → 异常 → fallback 到 `time.time()` → 计时器永远 ≈ 0
- **修复**：
  - 新增 `to_epoch()` — 安全处理 datetime / BSON datetime / 字符串 → epoch 秒数
  - 新增 `to_iso_str()` — 统一转 ISO 字符串
  - 所有查询函数中 `endedAt` / `startedAt` 统一转字符串后再传给模板
  - active games 查询兼容 `endedAt` 字段不存在的情况（`$or: [{endedAt: null}, {endedAt: {$exists: false}}]`）
- **提交**：`0c13f82`（同上）

#### Bug 3：胜率定义修正
- **原定义**：`wins = placement == 1`（只有吃鸡算赢）
- **修正**：`wins = placement <= 4`（前四都算胜利），新增 `chickens = placement == 1`（吃鸡）
- 排行榜新增「吃鸡率」列，选手页同步更新
- **提交**：`e3723ca`

#### Bug 4：totalGames 数据源
- **原做法**：从 `league_matches` 聚合计数
- **修正**：通过 `$lookup` 从 `bg_ratings.gameCount` 获取（BG 总局数），同时保留 `leagueGames` 字段（联赛局数，用于胜率/场均排名计算）
- **提交**：`4d69a8b`

### 新功能：报名队列
- 新增 `league_queue` 集合，存储报名名单
- API：`GET /api/queue`、`POST /api/queue/join`、`POST /api/queue/leave`
- 首页右侧「正在进行」下方新增报名队列面板
- 显示已报名名单 + 报名/取消按钮（硬编码当前用户为「衣锦夜行」，待登录系统实现后替换）
- **提交**：`04116ba`

### 数据流总结（当前）
```
C# 插件 → MongoDB:
  bg_ratings (玩家分数记录，含 gameCount)
  league_matches (联赛对局，含 players 数组)

Flask 后端 ← MongoDB:
  排行榜 = league_matches 聚合 + bg_ratings $lookup (gameCount)
  最近对局 = league_matches (endedAt != null)
  正在进行 = league_matches (endedAt == null)
  选手页 = league_matches 聚合 (单个 battleTag)
  报名队列 = league_queue
```

## 编译方法
```cmd
cd HDT_BGTracker\HDT_BGTracker\HDT_BGTracker
set HDT_PATH=C:\Users\cc\Downloads\HDT_BGTracker\HDT_BGTracker\HDT_BGTracker
dotnet build -c Release
```

## 日志位置
`%AppData%\HearthstoneDeckTracker\BGTracker\tracker.log`

## Git 操作
- 仓库: `https://github.com/iceshoesss/HDT_BGTracker`
- 分支: `claw_version`
- 需要 Fine-grained PAT（Contents Read and write）才能 push
- **Push 需要 LD_PRELOAD**：服务器 git-remote-http 链接了 GnuTLS 版 libcurl，连 GitHub 不稳定，需用 OpenSSL 版覆盖：
  ```bash
  LD_PRELOAD=/usr/lib/x86_64-linux-gnu/libcurl.so.4 git push origin claw_version
  ```

## Clone 备选方案（GnuTLS 报错时）

部分服务器/容器环境 git 使用 GnuTLS，clone GitHub 仓库可能报错：
```
fatal: GnuTLS recv error (-110): The TLS connection was non-properly terminated.
```

### 替代流程

用 `curl` 下载 zipball → 解压 → 补齐 git 仓库：

```bash
# 1. 下载分支 zipball（替换 <TOKEN> 为你的 PAT）
curl -L -o repo.zip \
  -H "Authorization: token <TOKEN>" \
  -H "Accept: application/vnd.github.v3+json" \
  "https://api.github.com/repos/iceshoesss/HDT_BGTracker/zipball/claw_version"

# 2. 解压并重命名
unzip -q repo.zip
mv iceshoesss-HDT_BGTracker-* HDT_BGTracker
rm repo.zip

# 3. 补回 git 仓库（用于后续 commit & push）
cd HDT_BGTracker
git init
git remote add origin https://iceshoesss:<TOKEN>@github.com/iceshoesss/HDT_BGTracker.git
git fetch origin claw_version
git checkout -b claw_version origin/claw_version
```

> 注意：zipball 下载的目录名带 commit hash 后缀（`iceshoesss-HDT_BGTracker-<hash>`），所以要 `mv` 重命名。`git fetch` 之前先清掉解压的非 git 文件，否则 checkout 会因未跟踪文件冲突而中止（`rm -rf * .gitignore && git checkout ...`）。

## 📋 2026-04-08 (下) 开发日志：注册验证 + 登录系统 + UI 修复

### 参与者
- 用户 + OpenClaw (AI pair programmer)

### 联赛注册验证系统

#### 设计讨论
- **核心问题**：用户注册时如何证明自己拥有某个 BattleTag？
- **方案演进**：
  1. 初版：基于 `accountIdLo` 生成验证码 → 被否决，因为 LobbyInfo 中 8 人都能看到彼此的 AccountId.Lo，存在盗用风险
  2. 改为基于 `bg_ratings` 文档的 MongoDB `ObjectId` 生成 → 安全性最好，ObjectId 仅存在于服务端，游戏内不可见
  3. 时序问题：`ObjectId` 是 MongoDB 插入时才生成的，插件上传前不知道 → 用户提出：上传完成后读回 `_id`，基于它生成验证码再存储

#### 最终方案
- **验证码生成**：`SHA256("bgtracker:" + ObjectId.ToString())` 前 8 位大写
- **插件流程**：上传成功 → 检查 `bg_ratings` 是否有 `verificationCode` 字段 → 没有则读 `_id` 生成并写入 → 日志打印
- **后端流程**：用户输入 BattleTag + 验证码 → 从 `bg_ratings` 读存储的 `verificationCode` → 比对一致 → 注册成功
- **安全保证**：验证码基于服务端生成的 ObjectId，游戏内任何 API 均不可见

#### 插件改动 (RatingTracker.cs)
- 新增 `GenerateVerificationCode(ObjectId objectId)` — SHA256 哈希取前 8 位
- 上传成功后新增验证码逻辑：读文档 → 检查 `verificationCode` → 无则基于 `_id` 生成 → `$set` 存储 → 日志打印

#### 后端改动 (app.py)
- `POST /api/register` — 输入 battleTag + verificationCode，从 `bg_ratings` 读存储值比对，一致则写入 `league_players`（verified=true）

### Session 登录系统

#### 设计讨论
- **问题**：注册后用户如何登录？没有密码系统
- **方案**：登录也用 BattleTag + 验证码（验证码是确定性的，永远不会变，日志里永远有）
- **安全**：验证码作为"密码"使用，防止他人冒用已注册 BattleTag

#### 实现
- `POST /api/login` — BattleTag + 验证码 → 查 `bg_ratings` 比对 → 写 Flask session
- `POST /api/logout` — 清除 session
- `context_processor` 注入 `current_user` 到所有模板
- 导航栏：未登录显示「注册/登录」链接，已登录显示 `BattleTag` + 退出按钮
- 注册页改为注册/登录双模式切换
- 注册成功自动登录
- 报名队列从 session 读取用户名，未登录提示先登录并跳转

### UI 修复

#### Bug：报名队列不自动刷新
- **现象**：第二个人报名后必须手动刷新页面才能看到
- **修复**：添加 `setInterval(fetchQueue, 1000)` 每秒刷新

#### Bug：正在进行列表自动浮动遮挡
- **现象**：滚动页面时正在进行列表浮动遮挡报名列表
- **修复**：移除 `sticky top-6` 定位

### 提交历史
| Commit | 说明 |
|--------|------|
| `cba2bd6` | feat: 联赛注册验证系统（插件验证码 + 后端注册 API） |
| `3fd83e2` | feat: 注册页面模板 + /register 路由 |
| `29c8978` | feat: Session 登录系统 + 导航栏用户状态 |
| `438e5be` | fix: 报名队列每5秒自动刷新 |
| `d400423` | fix: 报名队列刷新间隔改为1秒 |
| `cce8aeb` | fix: 正在进行列表去掉 sticky 定位 |

## 📋 2026-04-08 (晚) 开发日志：等待队列 + UI 布局重构 + 排行榜增强

### 参与者
- 用户 + OpenClaw (AI pair programmer)

### 开发环境搭建
- Clone 仓库到 OpenClaw workspace，配置 GitHub PAT 用于 push
- Git 用户：`OpenClaw <openclaw@claw.ai>`

### 等待队列功能

#### 设计演进
1. **初版**：`league_waiting_queue` 存储平铺的 name 列表，满 8 人从报名队列整体移入
2. **需求变更**：用户澄清 — 不是满 8 人合并到一个队列，而是**每满 N 人创建一个独立等待组**
3. **最终方案**：`league_waiting_queue` 每条文档是一个独立组，包含 `players` 数组

#### 数据结构

`league_waiting_queue` 集合：
```json
{
  "_id": ObjectId,
  "players": [
    { "name": "衣锦夜行" },
    { "name": "瓦莉拉" }
  ],
  "createdAt": "2026-04-08T15:45:00Z"
}
```

- 每满 N 人创建一条新文档（测试阶段 N=2，正式改为 8）
- 多组可并存（如 4 人报名 → 2 个独立等待组）
- 玩家退出时从组内移除；组内无人则删除整组

#### 后端改动 (`app.py`)

**新增 API：**
- `GET /api/waiting-queue` — 返回所有等待组，按 `createdAt` 排序

**修改 API：**
- `POST /api/queue/join` — 报名时检查是否已在等待组（`players.name` 查询），满 N 人后创建新等待组文档并清空报名队列中的对应条目
- `POST /api/queue/leave` — 同时从报名队列和等待组中移除。等待组内还有人则 `$set` 更新 players 数组，没人了则删除文档

#### 前端改动 (`index.html`)

**等待队列 UI：**
- 新增「⏳ 等待队列」面板，显示「X 组」计数
- 每组显示为独立卡片，标题「第 1 组」「第 2 组」，列出组员
- 当前用户在等待组中时，显示「❌ 退出等待」按钮
- 报名队列按钮在用户已在等待组时变灰显示「已在等待队列」
- 报名队列和等待队列均每秒轮询自动刷新

### 网页布局重构

#### 原布局
```
┌─────────────────────────────┬──────────────┐
│  排行榜 + 最近对局            │ ⚔️ 正在进行   │
│                             │ 📋 报名队列   │
│                             │ ⏳ 等待队列   │
└─────────────────────────────┴──────────────┘
```
右侧三个面板纵向堆叠，导致「正在进行」下方有大量空白。

#### 新布局
```
┌──────────────────────────┬────────┬────────┐
│  排行榜 + 最近对局         │⚔️ 正在 │📋 报名 │
│                          │  进行  │⏳ 等待 │
└──────────────────────────┴────────┴────────┘
```
- 右侧改为 `flex` 横排两列：左边「正在进行」，右边「报名+等待」纵向堆叠
- 三列宽度均为 `w-72`（288px）
- `items-start` 顶部对齐，消除拉伸空白

#### 间距收紧
- 容器：`max-w-7xl`（1280px）→ `max-w-[1440px]`（1440px），排行榜多 ~220px
- 左右外间距：`gap-6` → `gap-3`
- 右侧两列间距：`gap-6` → `gap-3`
- 队列列内间距：`space-y-6` → `space-y-3`
- 排行榜与最近对局间距：`mb-6` → `mb-3`

### 排行榜增强

#### 搜索框
- 排行榜标题右侧新增搜索输入框
- 实时过滤，匹配 `displayName` 和 `battleTag`
- 右侧显示筛选后人数

#### 分页
- 每页 20 条（测试时用 10 条），超过一页时底部显示翻页按钮
- 搜索、排序操作自动回到第一页
- `PAGE_SIZE` 常量控制，改数字即可

### 提交历史
| Commit | 说明 |
|--------|------|
| `da5dc90` | feat: 等待队列 UI + 满N人自动从报名队列移入等待队列 |
| `d64367b` | fix: 右侧栏顺序调整为 报名队列→等待队列 |
| `739f90a` | fix: 报名/等待队列与正在进行并排显示 |
| `8bc2d5e` | fix: 放宽容器+收紧间距，排行榜获得更多空间 |
| `e95f7d6` | fix: 右侧面板顶部对齐，消除正在进行下方空白 |
| `1f07771` | feat: 排行榜新增玩家搜索框 |
| `e6db2b6` | fix: 搜索框和计数右对齐 |
| `4e86406` | feat: 排行榜分页，每页10条 |
| `84bc9c7` | chore: PAGE_SIZE 改为 20（用户提交） |
| `4142514` | fix: 测试阶段等待队列阈值改为2人 |
| `51e7e75` | feat: 等待队列改为每满N人创建独立组 |

### 待办
- [ ] 测试等待队列完整流程（8人→自动创建组→组显示→退出）
- [ ] 等待组与插件对局匹配逻辑（Phase 3）

## 📋 2026-04-09 备忘：轮询改造评估

### 现状
前端有 3 处轮询（`index.html`）：
- 正在进行的对局 — 每 5 秒 fetch
- 报名队列 — 每 1 秒 fetch
- 等待队列 — 每 1 秒 fetch

### 评估结论
当前 20 桌规模，轮询完全够用，无性能瓶颈。1 秒轮询偏频繁，可优化为 3-5 秒。

### 将来改造方案：SSE（Server-Sent Events）
如果用户量上升需要替换轮询，推荐 SSE 而非 WebSocket：
- **理由**：场景是"后端有数据变更时推给前端"，单向通信，不需要 WebSocket 的双向能力
- **优势**：Flask 原生支持（`Response` + generator），无需额外依赖（不用 flask-socketio / eventlet）
- **改造量**：前端用 `EventSource` 替换 `setInterval` + `fetch`，后端新增 SSE 端点监听 MongoDB change stream 或定时检查变更
- **适用端点**：`/api/events/active-games`、`/api/events/queue`、`/api/events/waiting-queue`

### 对比

| 方案 | 复杂度 | 方向 | Flask 支持 | 额外依赖 |
|------|--------|------|-----------|---------|
| 轮询 | 最低 | 前端→后端 | 原生 | 无 |
| SSE | 低 | 后端→前端（单向） | 原生 | 无 |
| WebSocket | 高 | 双向 | 需 socketio | flask-socketio + eventlet |

### 结论
**现阶段不改造**。用户量上去后再换 SSE，改造量不大。

## 📋 2026-04-09 开发日志：联赛对局验证流程

### 参与者
- 用户 + OpenClaw (AI pair programmer)

### 设计目标
在 STEP 13（英雄选择完成）时，将 LobbyInfo 8 个玩家的 accountIdLo 与 MongoDB `league_waiting_queue` 比对，确认是否为联赛对局。

### 流程
```
STEP 13 触发
  → LogLobbyPlayers (heroName)
  → CheckLeagueQueue() [异步]
      读取 LobbyInfo 8 人 accountIdLo
      → 遍历 league_waiting_queue 中的等待组
      → accountIdLo 完全匹配？
        → 是 → _isLeagueGame = true → 删除等待组 → CreateLeagueMatchDirect() → 创建 league_matches
        → 否 → _isLeagueGame = false → 走原有 bg_ratings 逻辑
  → 游戏结束
      → _isLeagueGame ? UpdateLeaguePlacement() : 跳过
```

### 无缝衔接
- 等待组删除 → 网站「等待队列」面板消失
- league_matches 文档创建（endedAt=null）→ 网站「正在进行」面板出现

### 插件改动 (RatingTracker.cs)
- 新增 `_isLeagueGame` 状态标志
- 新增 `_waitingQueueCollection`（连接 `league_waiting_queue` 集合）
- 新增 `CheckLeagueQueue()` — STEP 13 触发，比对 8 人 accountIdLo 与等待组
- 新增 `CreateLeagueMatchDirect()` — 匹配成功后直接创建 league_matches（复用已有 lobbyInfo）
- `CreateLeagueMatch()` 保留作为 fallback
- `UpdateLeaguePlacement()` 加 `_isLeagueGame` 守卫
- 游戏结束 cleanup 时重置 `_isLeagueGame = false`

### 待测试
- [ ] 编译验证（需要 HDT_PATH 环境变量）
- [ ] 实际游戏验证等待队列匹配逻辑
- [ ] 验证 8 个玩家的插件都能正确触发（upsert 防重复）

### 优化建议（待实施）

#### 1. 去掉排行榜的 bg_ratings $lookup
**问题**：当前 `totalGames = bg_ratings.leagueCount`，需要额外一次 $lookup 查 bg_ratings 集合。但 `leagueCount` 依赖插件写入，如果插件崩溃就少 +1。

**建议**：直接用 `league_matches` 聚合的 `leagueGames`（玩家实际有 placement 的对局数）作为 `totalGames`，去掉 `bg_ratings` 的 $lookup。更可靠，且减少一次 MongoDB 查询。

影响范围：`app.py` 中 `get_players()` 和 `get_player()` 两个聚合管道。

#### 2. CheckAndFinalizeMatch 写入竞争（低优先级）
**现状**：8 个玩家的插件游戏结束后各自执行 `CheckAndFinalizeMatch`，如果发现所有人都填了 placement 就写入 `endedAt`。8 个插件并行时可能 4-5 个同时发现 allDone=true，重复写入同一个 endedAt。

**影响**：结果一致（$set 原子操作），只是多几次无意义的查询和写入。20 桌规模无感。

**未来优化**（规模到几百桌时）：只让一个人负责写 endedAt，比如仅当自己的 accountIdLo 是 players 数组第一个时才执行 CheckAndFinalizeMatch。

## 📋 2026-04-09 开发日志：超时/掉线对局处理 + 问题对局管理页 + UI 精修

### 参与者
- 用户 + OpenClaw (AI pair programmer)

### 问题背景
- 游戏超时（80 分钟）或玩家掉线时，`league_matches` 中 `players.placement` 和 `players.points` 为 null
- `cleanup_stale_games()` 原来只写 `endedAt`，不区分正常结束和超时
- 对局详情页 `match.html` 渲染 null placement 会出错
- 「最近对局」列表会显示不完整的对局（旧数据中 `endedAt` 有值但 placement 全 null）

### 插件/后端改动

#### 超时对局标记 (`app.py`)
- `cleanup_stale_games()`：超时对局新增 `status: "timeout"` 字段
- **新增** `cleanup_partial_matches()`：处理部分掉线对局——有人上报了 placement 但其他人没填，导致永远不会 allDone。超时后标记 `status: "abandoned"` 并写入 `endedAt`
- `get_active_games()` 每次查询时同时调用两个 cleanup 函数

#### 最近对局过滤 (`app.py`)
- `get_completed_matches()` 改为聚合管道，用 `$not: {$elemMatch: {placement: null}}` 精确过滤
- 排除三类对局：有 `status` 字段的（新 cleanup 标记的）、旧数据中 placement 有 null 的、`endedAt` 为 null 的
- **踩坑**：`{"players.placement": {"$ne": null}}` 对数组的语义是"至少一个 ≠ null"，不是"全部 ≠ null"。超时局里只要有人填了 placement 就会被漏进来。必须用 `$not + $elemMatch`

#### 选手页/对战统计兼容
- `get_player_matches()` 返回 `status` 字段
- `get_rival_stats()` 跳过 `status` 为 timeout/abandoned 的对局

### 前端改动

#### 对局详情页 (`match.html`)
- null placement 显示 `-`，null points 显示「无数据」
- 超时/中断对局顶部显示黄色警告条：「此对局因超时自动结束，排名数据未被记录」

#### 选手历史对局 (`player.html`)
- null placement 显示 ⚠️ 图标
- 超时/中断标签：`· 超时` / `· 中断`

#### 问题对局管理页 (`/problems`) — **新增**
- 聚合查询所有 timeout/abandoned/旧数据缺失的对局
- 每局以 2×4 网格显示 8 个玩家，含英雄头像
- 已填 placement 的玩家：正常白底卡片，显示排名+积分
- 未填的玩家：红色边框卡片，显示「未记录」
- 整张卡片可点击跳转对局详情
- 暂无入口链接（需后续添加）

#### 最近对局样式统一 (`index.html`)
- 从文字列表改为 2×4 英雄头像网格，与问题对局风格一致
- 去掉日期行，更简洁
- 防御性 null 检查：跳过 placement 为 null 的玩家

### UI 精修
- 导航栏「注册/登录」→「登录」
- 「X 名选手」→「X 名注册选手」（改为查 `league_players` 集合 verified=True 计数）
- 登录页：去掉注册/登录切换，统一为「🔑 选手登录」，首次登录自动注册
- placeholder 从「南怀北瑾丨少头脑#5267」改为「能干的猛兽#1234」
- 所有页面去掉 solo/duo mode 标志（联赛无意义）

### 提交历史
| Commit | 说明 |
|--------|------|
| `9361ac7` | feat: 超时/掉线对局处理 — status 标记 + 前端容错 |
| `2a6ee50` | fix: 最近对局用聚合管道精确排除不完整对局 |
| `bd1f05d` | feat: 问题对局管理页面 /problems |
| `74e6481` | style: 最近对局改用英雄头像网格布局 + 问题对局整卡可点击 |
| `745bb70` | fix: 去掉 solo 标志、登录页简化、替换 placeholder |
| `09ba088` | fix: 导航栏改登录、选手数改注册数、最近对局去日期 |

### 待办
- [ ] 问题对局页面添加入口链接
- [ ] 手动补录功能：当事人填自己的排名 / 管理员统一填
