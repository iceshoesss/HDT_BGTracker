# HDT_BGTracker 开发记录

## 项目概述
炉石传说酒馆战棋分数记录插件，每局结束后自动记录分数并上传到 MongoDB。

## 当前状态
- ✅ 分数获取正常
- ✅ 玩家 ID 获取（`Player.Name`，游戏开始 3 秒后缓存）
- ✅ 对手 ID 获取（`BattlegroundsLobbyInfo.Players`，排除自己）
- ✅ MongoDB 上传（rating + mode + region + placement）
- ✅ 分差记录（聚合管道原子计算，存储在 `ratingChanges` 数组）
- ✅ 排名获取（`CurrentGameStats.BattlegroundsDetails.FinalPlacement`）
- ✅ 浮动面板显示玩家名字+序号（LobbyOverlay）
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
- MongoDB 驱动版本 2.19.2

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

## 下一步工作
1. 将 lobby 玩家名单上传到 MongoDB（opponents 数组）
2. 后续获取真实分数后替换序号显示
3. 验证 FinalPlacement 在不同场景下是否可靠（单人/双人/掉线重连等）

## 🏆 联赛功能规划（待开发）

### 概述
利用插件实现酒馆战棋联赛，根据排名计分（第1名9分，第2名7分，第3名6分，以此类推）。

### 流程
1. 玩家通过游戏 ID 截图注册，获得唯一 ID
2. 联赛网站点击加入队列，满 8 人后比赛开始
3. 插件在游戏开始时获取大厅 8 人名字，调后端 API 检查是否匹配队列
4. 匹配则标记为联赛局，游戏结束时上传 placement 到联赛数据库

### 数据库设计
- `players` — 注册选手（playerId, battleTag, registeredAt）
- `queue` — 当前队列（满 8 人触发比赛）
- `league_games` — 联赛局记录（gameId, seasonId, players[], placement, points）
  - points 直接算好存入（9/7/6/5/4/3/2/1）

### 需要的后端 API
- `POST /api/register` — 注册选手
- `POST /api/queue/join` — 加入队列
- `POST /api/match/check` — 插件传入大厅 8 人名字，返回是否匹配队列 + gameId
- `POST /api/match/result` — 插件传入 gameId + placement
- `GET /api/standings` — 积分榜

### 待确认
- `BattlegroundsLobbyInfo.Players` 的名字是否带 BattleTag 号（如 `名#1234`），决定匹配策略
- 将来可能拆分插件：分数追踪 vs 联赛功能，作为独立插件维护

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
