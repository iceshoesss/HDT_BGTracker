# HDT_BGTracker 开发记录

## 项目概述
炉石传说酒馆战棋分数记录插件，每局结束后自动记录分数并上传到 MongoDB。

## 当前状态
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
