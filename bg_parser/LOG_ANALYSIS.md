# bg_parser 日志分析 (2026-04-19)

> 给 AI 的提示：所有结论均基于实测日志，直接使用，不要重复实验。

---

## 1. 日志格式概览

Power.log 由 HDT 生成，记录炉石客户端的 power log。
每行格式：`<级别> <时间戳> <模块> - <内容>`

关键模块：
- `GameState.DebugPrintPower()` — 游戏状态变化（最可靠）
- `GameState.DebugPrintGame()` — 游戏元数据（GameType、PlayerName 等）
- `PowerTaskList.DebugPrintPower()` — 任务执行（GameState 的重复/子集，可忽略）

**只看 `GameState.DebugPrintPower()` 的行，忽略 `PowerTaskList`。**

---

## 2. CREATE_GAME 事件

日志中出现 `GameState.DebugPrintPower() - CREATE_GAME` 表示一个游戏状态初始化。

### 2.1 新局 vs 断线重连

| 特征 | 新局 | 断线重连 |
|------|------|----------|
| `tag=TURN` | **不存在** | 存在，值 ≥ 1 |
| `tag=STATE` | 不存在 | `value=RUNNING` |
| `tag=STEP` | 不存在 | `value=MAIN_ACTION` 等 |
| `tag=NUM_TURNS_IN_PLAY` | 不存在 | 存在 |
| `GAME_SEED` | 新值 | 与旧局相同 |
| `GameEntity EntityID` | 不确定 | 与旧局相同 |

**判断规则（最简）：CREATE_GAME 块内有没有 `tag=TURN`**
- 有 → 断线重连（忽略，不触发新游戏）
- 没有 → 真正的新局

实测数据：
```
# 日志1
Line     2  CREATE_GAME, 无 TURN        → 新局 (GAME_SEED=1610508937)
Line  9587  CREATE_GAME, TURN=3          → 重连 (GAME_SEED=1610508937, 同上)
Line 17001  CREATE_GAME, 无 TURN         → 新局 (GAME_SEED=1534385957, 新值)

# 日志2
Line     2  CREATE_GAME, 无 TURN         → 新局
Line  5735  CREATE_GAME, TURN=1          → 重连
```

### 2.2 CREATE_GAME 块内的 Player 实体

新局的 Player 实体：
```
Player EntityID=2 PlayerID=1 GameAccountId=[hi=... lo=1708070391]  ← 本地玩家
Player EntityID=3 PlayerID=9 GameAccountId=[hi=0 lo=0]            ← 酒馆老板/spectator
```

重连的 Player 实体包含更多信息（HERO_ENTITY、PLAYSTATE 等）。

---

## 3. 游戏流程（STEP 流转）

### 3.1 完整回合循环

```
BEGIN_MULLIGAN → MAIN_READY → MAIN_START_TRIGGERS → MAIN_START → MAIN_ACTION → MAIN_END → MAIN_CLEANUP → MAIN_NEXT → MAIN_READY → ...
```

一轮完整循环：
```
MAIN_READY        ← 采购阶段开始（玩家可见的"回合开始"）
MAIN_START_TRIGGERS
MAIN_START
MAIN_ACTION       ← 战斗阶段开始（采购结束）
MAIN_END
MAIN_CLEANUP
MAIN_NEXT
MAIN_READY        ← 下一回合
```

### 3.2 STEP 与用户感知的"回合"

用户感知的"回合" = 采购阶段 = `MAIN_READY` 到 `MAIN_ACTION` 之间的窗口。

实测时间线（日志2，一局只有 2 回合）：
```
12:28:14  CREATE_GAME (新局)
12:28:14  BEGIN_MULLIGAN                    ← 选英雄阶段
12:29:25  MAIN_READY + TURN=1               ← 第1回合采购开始
12:29:40  MAIN_ACTION                       ← 第1回合战斗开始
12:29:58  CREATE_GAME (重连, TURN=1)         ← 断线重连
12:30:12  MAIN_END → MAIN_NEXT → TURN=2      ← 快进
12:30:13  TURN=3 (又快进一轮)
12:30:13  MAIN_READY → MAIN_ACTION          ← 第2回合（用户看到的）
12:30:42  投降 (tag=3479/4356/4302 → PLACE=8)
```

**重要发现**：`GameEntity TURN` 的值**不等于**用户感知的回合数。重连后会快进导致 TURN 偏高。
不要用 TURN 值来数回合，用 `MAIN_READY` 事件来计数。

### 3.3 时间参考

每个 STEP 变化都有精确时间戳（`D 12:29:25.0306260`）。格式：`<级别> <时:分:秒.微秒>`

---

## 4. 玩家信息

### 4.1 本地玩家 BattleTag

来源：`GameState.DebugPrintGame()` 行
```
PlayerID=1, PlayerName=南怀北瑾丨少头脑#5267
PlayerID=9, PlayerName=惊魂之武僧
```

- `PlayerID=1` 通常为本地玩家（但不绝对，需结合 GameAccountId 验证）
- `PlayerName` 包含 `#tag` 后缀
- **该行只出现一次**，在 CREATE_GAME 之后

### 4.2 GameAccountId

来源：Player 实体块内
```
Player EntityID=2 PlayerID=1 GameAccountId=[hi=144115211015832391 lo=1708070391]
```

- `lo=1708070391` 是 accountIdLo，跨局唯一
- `lo=0` 是酒馆老板/spectator，不是真实玩家

### 4.3 英雄实体

英雄通过 `FULL_ENTITY` 创建：
```
FULL_ENTITY - Creating [entityName=奥妮克希亚 id=100 zone=HAND zonePos=2 cardId=BG22_HERO_305 player=1]
```

字段：
- `entityName` — 英雄中文名
- `id` — 实体 ID（**重连后可能变化**）
- `cardId` — 英雄卡牌 ID（**重连后不变**）
- `player` — 玩家 slot（**重连后不变**）

**重连安全匹配**：用 `cardId + player` 匹配，不用 `entity_id`。

### 4.4 英雄选择（HERO_ENTITY）

```
TAG_CHANGE Entity=南怀北瑾丨少头脑#5267 tag=HERO_ENTITY value=100
```

- Entity 是玩家 BattleTag → 该玩家选了 entity id=100 的英雄
- 这是唯一标识本地玩家英雄 entity_id 的方式
- **仅对本地玩家可靠**（对手的 HERO_ENTITY 不出现在日志中）

---

## 5. 排名（LEADERBOARD_PLACE）

### 5.1 格式

两种格式：

**格式A**（带 entity 详情，最常见的战斗阶段排名变化）：
```
TAG_CHANGE Entity=[entityName=奥妮克希亚 id=100 zone=PLAY zonePos=0 cardId=BG22_HERO_305 player=1] tag=PLAYER_LEADERBOARD_PLACE value=7
```

**格式B**（简写，仅 BattleTag）：
```
TAG_CHANGE Entity=南怀北瑾丨少头脑#5267 tag=PLAYER_LEADERBOARD_PLACE value=8
```

### 5.2 排名行为

**游戏中**：排名动态变化，每轮战斗结束后根据血量重排。频繁波动，不代表最终排名。
- 实测（日志2）：第一回合内排名变化 6→5→6→5→6→5→6（多次反复）

**投降时**：投降者立即变为 `LEADERBOARD_PLACE=8`。
- 不等于实际排名（投降时可能是任意排名）
- 其他玩家也会出现最终排名

**游戏结束**：`LEADERBOARD_PLACE` 不会稳定。真正的最终排名只在游戏彻底结束后由 HDT 的 `HandleGameEnd()` 写入（Power.log 中不可见）。

### 5.3 关键踩坑

- ❌ 不能用 LEADERBOARD_PLACE 的"最后值"判断最终排名（投降时 PLACE=8 不代表最后一名）
- ❌ 不能用 LEADERPLACE 在游戏内判断淘汰（排名只是当前状态）
- ✅ PLACE=8 + 投降信号 = 确定的投降事件

---

## 6. 投降信号

投降时出现固定模式（4 个 TAG_CHANGE 连续出现）：

```
TAG_CHANGE Entity=<玩家BattleTag> tag=3479 value=1
TAG_CHANGE Entity=<玩家BattleTag> tag=4356 value=1
TAG_CHANGE Entity=GameEntity tag=4302 value=1
TAG_CHANGE Entity=[entityName=... cardId=... player=N] tag=PLAYER_LEADERBOARD_PLACE value=8
```

### 6.1 tag 含义

| tag | Entity | 含义（推测） |
|-----|--------|-------------|
| 3479 | 玩家 | 投降标记？ |
| 4356 | 玩家 | 投降标记？ |
| 4302 | GameEntity | 游戏结束？ |
| PLAYER_LEADERBOARD_PLACE=8 | 投降者英雄 | 立即变为第8 |

### 6.2 实测

**日志1 Game 1 投降（第3回合后）**：
```
12:08:52  tag=3479/4356/4302 + 奥妮克希亚 PLACE=8
```

**日志1 Game 2 投降（第1回合后）**：
```
12:10:26  tag=3479/4356/4302 + 钩牙船长 PLACE=8
```

**日志2 投降（第2回合后）**：
```
12:30:42  tag=3479/4356/4302 → 投降
```

### 6.3 注意事项

- tag 3479/4356 写在**投降者 Player 实体**上（Entity=BattleTag）
- tag 4302 写在 **GameEntity** 上
- PLACE=8 写在**投降者英雄**上
- 投降后日志通常很快结束（或出现下一个 CREATE_GAME）
- **投降者不一定是本地玩家**（但当前只处理本地玩家视角）

---

## 7. 游戏结束检测

### 7.1 可靠信号

| 信号 | 可靠性 | 说明 |
|------|--------|------|
| 下一个非重连 CREATE_GAME | ✅ 确定 | 旧局结束，新局开始 |
| 投降信号（tag=3479/4356/4302） | ✅ 确定 | 当前玩家投降 |
| 日志结束 | ⚠️ 可能 | 最后一局可能未结束 |

### 7.2 不可靠的信号

| 信号 | 问题 |
|------|------|
| GameEntity TURN 值 | 不等于用户感知的回合数 |
| LEADERBOARD_PLACE=8 | 仅在投降时有意义，游戏中排名波动无意义 |
| STEP=MAIN_CLEANUP | 每轮都有，不是游戏结束 |

### 7.3 天梯投降 vs 联赛投降

在 Power.log 中，天梯投降和联赛投降的日志完全一样（同样的 tag=3479/4356/4302 模式）。
无法通过日志区分游戏模式（天梯/联赛）——需要依赖服务端的等待组匹配。

---

## 8. 断线重连后的状态恢复

### 8.1 重连 CREATE_GAME 包含的信息

重连时的 CREATE_GAME 块会包含完整的当前游戏状态：
- 所有 Player 实体（含 HERO_ENTITY、PLAYSTATE 等）
- GameEntity（含 TURN、STEP、NUM_TURNS_IN_PLAY 等）
- GAME_SEED（与旧局相同）

### 8.2 重连后的实体 ID 变化

**实测（日志1）**：重连后本地英雄 entity_id 保持不变（100→100）。
**注意**：不能保证所有情况下 entity_id 不变。安全做法是用 cardId+player 匹配。

### 8.3 重连后的快进

重连后游戏会快速过一遍所有 STEP，产生大量中间状态的 LEADERBOARD_PLACE 变化。
这些变化是无意义的快进数据，不应触发排名更新事件。

---

## 9. 日志时间线汇总

### 日志1（2局完整对局）

```
12:06:24  Game 1 CREATE_GAME (新局)
12:06:35  英雄选定 (奥妮克希亚)
12:07:04  TURN=1 (第1回合)
12:07:51  TURN=2 (第2回合，拔线)
12:08:12  RECONNECT (CREATE_GAME, TURN=3, STATE=RUNNING)
12:08:36  TURN=4 (快进)
12:08:37  TURN=5 (快进)
12:08:52  投降 (tag=3479/4356/4302 + PLACE=8)
12:09:42  Game 2 CREATE_GAME (新局)
12:09:54  英雄选定 (钩牙船长)
12:10:18  TURN=1 (第1回合)
12:10:26  投降 (tag=3479/4356/4302 + PLACE=8)
```

### 日志2（1局，2回合）

```
12:28:14  CREATE_GAME (新局)
12:28:14  BEGIN_MULLIGAN
12:29:25  TURN=1 + MAIN_READY (第1回合采购开始)
12:29:40  MAIN_ACTION (第1回合战斗)
12:29:58  RECONNECT (CREATE_GAME, TURN=1, STATE=RUNNING)
12:30:12  MAIN_END → TURN=2 (快进)
12:30:13  TURN=3 (快进) → MAIN_READY → MAIN_ACTION (第2回合)
12:30:42  投降 (tag=3479/4356/4302 + PLACE=8)
```

---

## 10. bg_parser 重写需求

### 核心状态机

```
空闲 → [CREATE_GAME, 无TURN] → 游戏进行中
游戏进行中 → [CREATE_GAME, 无TURN] → 旧局结束 + 新局开始
游戏进行中 → [投降信号] → 当前玩家投降，游戏结束
游戏进行中 → [CREATE_GAME, 有TURN] → 忽略（重连）
游戏进行中 → [日志结束] → 标记为未完成
```

### 需要追踪的数据

- 本地玩家: BattleTag, accountIdLo
- 本地英雄: entity_id, cardId, player_slot, heroName
- 所有英雄: entity_id → {cardId, player_slot, heroName, placement}
- 回合数: 通过 MAIN_READY 计数
- 当前阶段: MAIN_READY(采购) / MAIN_ACTION(战斗)
- 游戏状态: 活跃 / 已结束(投降) / 已结束(新局开始)

### 不需要追踪的数据（曾有 bug）

- ~~GameEntity TURN 值~~ — 不等于回合数，不可靠
- ~~LEADERBOARD_PLACE 的最后值~~ — 游戏中波动无意义
- ~~通过 IsInMenu 判断游戏结束~~ — Power.log 中无此信号

### 输出

- 一局游戏一个 GameResult
- 包含: 本地玩家信息、英雄、投降排名(8)、回合数
- 不包含: 其他玩家的最终排名（Power.log 中不可获取）
