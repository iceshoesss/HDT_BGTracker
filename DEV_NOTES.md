# HDT_BGTracker 开发记录

## 项目概述
炉石传说酒馆战棋分数记录插件，每局结束后自动记录分数并上传到 MongoDB。

## 当前状态
- ✅ 分数获取正常
- ✅ 玩家 ID 获取已修复（`Player.Name`，游戏开始 3 秒后缓存）
- ✅ 对手 ID 获取已实现（遍历实体，过滤本地玩家 + 系统实体 + NPC，找 PLAYER_IDENTITY 标签）
- MongoDB 连接: `mongodb://192.168.31.2:27017`
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

### 对手 ID 获取（已实现）
- 从 `Core.Game.Entities.Values` 遍历所有实体
- 过滤条件：跳过本地玩家（`_cachedPlayerId`）、`GameEntity`、`调酒师鲍勃`
- 匹配有 `PLAYER_IDENTITY (271)` 标签的实体即为对手
- 实测日志确认：`大暴龙 [IDENTITY] id=15` 为对手实体
- 对手信息随分数一起上传到 MongoDB（`opponentId` 字段）

## 修改历史（claw_version 分支）

1. **初始修复** - 尝试从 `AccountInfo` / `Config` 获取 BattleTag
   - 失败：`GameV2` 没有 `AccountInfo`，`Config` 没有 `BattleTag`
2. **解决 Core 歧义** - `using Hearthstone_Deck_Tracker` 导致 `Core` 引用歧义
   - 改用 `typeof(Hearthstone_Deck_Tracker.Config)` 全限定名
3. **添加调试日志** - 列出 Player 所有属性，发现 `Player.Name` 在游戏中有值
4. **5 秒轮询** - 每 5 秒读一次 `Player.Name`，成功获取到 ID
5. **简化为游戏开始读取** - 改为进入游戏时读一次
6. **添加 3 秒延迟** - 进入游戏后 Player.Name 需要初始化时间
7. **实现对手 ID 获取** - 遍历实体，过滤本地玩家/系统/NPC，找 PLAYER_IDENTITY 标签

## 下一步工作
1. 对手获取逻辑已实现，待实际游戏验证
2. 考虑酒馆战棋有多个对手轮换 — 当前记录的是对战时的当前对手
3. 清理调试日志（`DumpAllPlayers`），保留关键信息或改为可选

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
