# Power.log 日志分析 — 完整参考手册

> 最后更新：2026-04-19
> 所有结论基于实测日志验证，直接使用，不要重复实验。
> 基于两份日志：日志1（2局，含重连+投降）、日志2（1局，含重连+投降）

---

## 1. Power.log 是什么

HDT（Hearthstone Deck Tracker）生成的游戏日志，记录炉石客户端的 power log。
位于：`<炉石安装目录>/Logs/Hearthstone_<时间戳>/Power.log`

**不是 HDT 插件日志**，是炉石游戏本身的底层日志。

---

## 2. 能拿到什么、拿不到什么

| 数据 | 能拿 | 来源 | 说明 |
|------|------|------|------|
| 本地玩家 BattleTag | ✅ | `DebugPrintGame` 的 `PlayerName` | 含 #tag，如 `南怀北瑾丨少头脑#5267` |
| 本地玩家 accountIdLo | ✅ | `Player GameAccountId=[hi=... lo=N]` | 跨局唯一，如 `1708070391` |
| GAME_SEED | ✅ | `tag=GAME_SEED value=N` | 暴雪内部种子，同一局的重连值相同 |
| 英雄 cardId | ✅ | `FULL_ENTITY cardId=XXX` | 如 `BG22_HERO_305` |
| 英雄中文名 | ✅ | `FULL_ENTITY entityName=XXX` | 如 `奥妮克希亚` |
| 本地英雄 entity_id | ✅ | `HERO_ENTITY value=N` | 可能因重连变化 |
| 所有英雄（cardId+player_slot） | ✅ | `FULL_ENTITY` | 8 个英雄的 cardId 和 player slot |
| 对手 BattleTag | ❌ | 需要 HearthMirror（读进程内存） | Power.log 中不存在 |
| 对手 accountIdLo | ❌ | 需要 HearthMirror | Power.log 中不存在 |
| gameUuid | ❌ | HDT 内部生成，不写入 Power.log | 服务端或 HDT 插件才有 |
| 准确的最终排名 | ⚠️ 部分 | `PLAYER_LEADERBOARD_PLACE` | 见下方详细说明 |

### 排名（LEADERBOARD_PLACE）详解

**投降时**：投降者立即变为 `PLACE=8`，100% 可靠。

**正常淘汰时**：`LEADERBOARD_PLACE` 在游戏内动态变化（战斗阶段不断重排），最后一个观测值 ≈ 排名，但有以下不可靠因素：
- 同轮多人淘汰时排名并列
- 投降排名=8 不代表实际最后一名
- 只有 HDT 的 `HandleGameEnd()` 处理完后才有准确值

**结论**：记录最后一个 `LEADERPLACE` 值，不区分投降/淘汰，不确定的标记为不确定。

---

## 3. 日志行格式

```
<级别> <时间戳> <模块> - <内容>
```

示例：
```
D 12:06:24.7809362 GameState.DebugPrintPower() - CREATE_GAME
D 12:06:24.7809362 GameState.DebugPrintGame() - PlayerID=1, PlayerName=南怀北瑾丨少头脑#5267
D 12:06:24.7809362 PowerTaskList.DebugPrintPower() -     TAG_CHANGE ...
```

级别：D=Debug, I=Info, W=Warning, E=Error

### 两个关键模块

| 模块 | 内容 | 用途 |
|------|------|------|
| `GameState.DebugPrintPower()` | 游戏状态变化 | STEP、LEADERBOARD_PLACE、HERO_ENTITY、投降信号 |
| `GameState.DebugPrintGame()` | 游戏元数据 | GameType、PlayerName（仅出现一次） |
| `PowerTaskList.DebugPrintPower()` | 任务执行细节 | FULL_ENTITY（英雄创建，只在这里出现） |

**重要**：`PowerTaskList` 是 `GameState` 的子集/重复，但 FULL_ENTITY **只出现在 PowerTaskList**，GameState 中没有。

---

## 4. CREATE_GAME 事件

每次 `GameState.DebugPrintPower() - CREATE_GAME` 表示一个游戏状态初始化。

### 4.1 新局 vs 断线重连

CREATE_GAME 块内的标签（从 CREATE_GAME 行到 `PowerTaskList.DebugDump()` 行之间）：

| 标签 | 新局 | 断线重连 |
|------|------|----------|
| `tag=TURN` | **不存在** | 存在，值 ≥ 1 |
| `tag=STATE` | 不存在 | `value=RUNNING` |
| `tag=STEP` | 不存在 | 存在（如 `MAIN_ACTION`） |
| `tag=NUM_TURNS_IN_PLAY` | 不存在 | 存在 |
| `GAME_SEED` | 新值 | 与旧局相同 |
| `GameEntity EntityID` | 不确定 | 与旧局相同 |

**判断规则：CREATE_GAME 块内有没有 `tag=TURN`**
- 有 → 断线重连（忽略，不触发新游戏）
- 没有 → 真正的新局

实测：
```
# 新局
CREATE_GAME
    GameEntity EntityID=1
    tag=CARDTYPE value=GAME
    tag=ZONE value=PLAY
    ...（无 TURN/STATE/STEP）

# 重连
CREATE_GAME
    GameEntity EntityID=1
    tag=STATE value=RUNNING
    tag=STEP value=MAIN_ACTION
    tag=TURN value=3
    tag=NUM_TURNS_IN_PLAY value=4
    ...
```

### 4.2 CREATE_GAME 块结构

```
CREATE_GAME                          ← 块开始
    GameEntity EntityID=1
        tag=...
        tag=GAME_SEED value=N        ← 对局种子
    Player EntityID=2 PlayerID=1 GameAccountId=[hi=... lo=1708070391]  ← 本地玩家
    Player EntityID=3 PlayerID=9 GameAccountId=[hi=0 lo=0]            ← 酒馆老板
    ...（更多 tag）
PowerTaskList.DebugDump()            ← 块结束标志
```

**块结束标志**：`PowerTaskList.DebugDump()` 出现。之后是 `DebugPrintGame()`（含 PlayerName、GameType）。

---

## 5. 游戏流程

### 5.1 STEP 流转

```
BEGIN_MULLIGAN → MAIN_READY → MAIN_START_TRIGGERS → MAIN_START → MAIN_ACTION → MAIN_END → MAIN_CLEANUP → MAIN_NEXT → MAIN_READY → ...
```

一轮循环：
```
MAIN_READY            ← 采购阶段开始
MAIN_START_TRIGGERS
MAIN_START
MAIN_ACTION           ← 战斗阶段开始
MAIN_END
MAIN_CLEANUP
MAIN_NEXT
→ 回到 MAIN_READY     ← 下一回合
```

### 5.2 STEP 与回合计数

`GameEntity tag=TURN` 每次 `MAIN_READY` 时递增，但**不等于用户感知的回合数**。
重连后游戏会快进多个 STEP 循环，导致 TURN 值偏高。

**可靠做法**：用 `MAIN_READY` 事件计数回合，但接受重连后快进导致的误差。
**更可靠做法**：不计回合数，只记录游戏事件（开始、重连、结束）。

### 5.3 时间线示例（日志2，1局2回合）

```
12:28:14  CREATE_GAME (新局)
12:28:14  BEGIN_MULLIGAN
12:29:25  MAIN_READY + TURN=1 (第1回合采购开始)
12:29:40  MAIN_ACTION (第1回合战斗)
12:29:58  RECONNECT (CREATE_GAME, TURN=1, STATE=RUNNING)
12:30:12  MAIN_END → MAIN_NEXT → TURN=2 (快进)
12:30:13  TURN=3 (快进) → MAIN_READY → MAIN_ACTION (第2回合)
12:30:42  投降 (tag=3479/4356/4302 → PLACE=8)
```

---

## 6. 玩家信息

### 6.1 本地玩家 BattleTag

来源：`GameState.DebugPrintGame()` 行，CREATE_GAME 之后出现。
```
PlayerID=1, PlayerName=南怀北瑾丨少头脑#5267
PlayerID=9, PlayerName=惊魂之武僧          ← 酒馆老板，不是真实玩家
```
- 只出现一次，必须缓存
- `PlayerID=1` 通常为本地玩家
- `古怪之德鲁伊` 和 `惊魂之武僧` 是酒馆老板，跳过

### 6.2 GameAccountId

来源：CREATE_GAME 块内的 Player 实体
```
Player EntityID=2 PlayerID=1 GameAccountId=[hi=144115211015832391 lo=1708070391]
```
- `lo=1708070391` 是 accountIdLo
- `lo=0` 是酒馆老板，忽略
- 跨局稳定不变

### 6.3 英雄实体

来源：`FULL_ENTITY`（**只在 PowerTaskList 中出现**）
```
FULL_ENTITY - Creating [entityName=奥妮克希亚 id=100 zone=HAND zonePos=2 cardId=BG22_HERO_305 player=1]
```

字段：
- `entityName` — 英雄中文名
- `id` — 实体 ID（重连后可能变化）
- `cardId` — 卡牌 ID（重连后不变）
- `player` — 玩家 slot（重连后不变）

### 6.4 英雄选择（HERO_ENTITY）

```
TAG_CHANGE Entity=南怀北瑾丨少头脑#5267 tag=HERO_ENTITY value=100
```
- Entity 是玩家 BattleTag → 该玩家选了 entity id=100 的英雄
- **只有本地玩家的 HERO_ENTITY 出现在日志中**
- 对手的 HERO_ENTITY 不可见

### 6.5 英雄匹配（重连安全）

重连后 entity_id 可能变化，用 `cardId + player` 匹配最安全。

### 6.6 英雄与 FULL_ENTITY 的时序

FULL_ENTITY（创建英雄）和 HERO_ENTITY（选定英雄）的出现顺序不确定：
- 可能 FULL_ENTITY 先 → HERO_ENTITY 后匹配
- 可能 HERO_ENTITY 先 → FULL_ENTITY 后匹配

需要双向匹配：
- FULL_ENTITY 时检查是否匹配已知的 hero_entity_id
- HERO_ENTITY 时检查是否匹配已有的 all_heroes

---

## 7. 排名（LEADERBOARD_PLACE）

### 7.1 格式

**格式A**（带 entity 详情）：
```
TAG_CHANGE Entity=[entityName=奥妮克希亚 id=100 zone=PLAY zonePos=0 cardId=BG22_HERO_305 player=1] tag=PLAYER_LEADERBOARD_PLACE value=7
```

**格式B**（简写，仅 BattleTag）：
```
TAG_CHANGE Entity=南怀北瑾丨少头脑#5267 tag=PLAYER_LEADERBOARD_PLACE value=8
```

### 7.2 行为

- 游戏中：排名动态变化，每轮战斗后重排，不代表最终排名
- 投降时：投降者立即变为 PLACE=8
- 正常淘汰时：PLACE 设为淘汰时的值，但可能不准

### 7.3 记录策略

- 追踪本地英雄的 PLACE 变化（通过 cardId 匹配）
- 游戏结束时记录最后一个 PLACE 值
- 投降：PLACE=8，确定
- 正常淘汰：最后一个 PLACE，不确定

---

## 8. 投降信号

投降时出现固定 4 行模式：

```
TAG_CHANGE Entity=<玩家BattleTag> tag=3479 value=1    ← 玩家实体
TAG_CHANGE Entity=<玩家BattleTag> tag=4356 value=1    ← 玩家实体
TAG_CHANGE Entity=GameEntity tag=4302 value=1         ← 游戏实体
TAG_CHANGE Entity=[entityName=... cardId=... player=N] tag=PLAYER_LEADERBOARD_PLACE value=8  ← 英雄排名变8
```

### tag 含义（推测）

| tag | Entity | 含义 |
|-----|--------|------|
| 3479 | 玩家 | 投降标记 |
| 4356 | 玩家 | 投降标记 |
| 4302 | GameEntity | 游戏结束标记 |

### 注意

- 投降和天梯投降的日志完全一样，无法区分
- tag 3479/4356 写在玩家实体上，tag 4302 写在 GameEntity 上
- PLACE=8 写在投降者英雄上

---

## 9. 游戏结束检测

### 9.1 可靠信号

| 信号 | 可靠性 | 说明 |
|------|--------|------|
| 非重连 CREATE_GAME | ✅ 确定 | 旧局结束，新局开始 |
| 投降信号（3479/4356/4302→PLACE=8） | ✅ 确定 | 当前玩家投降 |
| 日志结束 | ⚠️ 可能 | 最后一局可能未结束 |

### 9.2 不可靠的信号

| 信号 | 问题 |
|------|------|
| GameEntity TURN 值 | 不等于回合数，重连后快进 |
| LEADERBOARD_PLACE=8 | 只在投降时有意义 |
| STEP=MAIN_CLEANUP | 每轮都有，不是游戏结束 |

---

## 10. 断线重连

### 10.1 识别

CREATE_GAME 块内有 `tag=TURN` → 断线重连。

### 10.2 重连后的变化

- 实体 ID：实测保持不变（100→100），但不能保证
- GAME_SEED：不变（验证是同一局）
- 会快速过一遍所有 STEP，产生大量无意义的状态变化
- 英雄信息在重连的 CREATE_GAME 块中重新发送

### 10.3 快进数据

重连后游戏会快速跳过所有中间 STEP，产生：
- 多个 MAIN_READY/MAIN_ACTION 循环
- 多个 LEADERBOARD_PLACE 变化
- 这些都是快进数据，不代表真实游戏进程

---

## 11. bg_parser 状态机

```
空闲
  ↓ [CREATE_GAME, 无TURN] → 新局开始，is_active=True
游戏进行中
  ↓ [CREATE_GAME, 有TURN] → 忽略（重连），继续当前局
  ↓ [投降信号] → 标记投降，游戏结束
  ↓ [CREATE_GAME, 无TURN] → 旧局结束，新局开始
  ↓ [日志结束] → 标记为未完成
```

### CREATE_GAME 块处理

```
CREATE_GAME 行 → 进入块模式
  ↓
块内扫描：
  - tag=TURN → 标记为重连
  - GameAccountId → 提取 accountIdLo
  - GAME_SEED → 提取对局种子
  ↓
PowerTaskList.DebugDump() → 块结束
  ↓
  有TURN？ → 回滚新局，恢复旧局，标记重连
  无TURN？ → 确认新局，旧局存档
```

### 需要追踪的数据

- `player_tag` — 本地玩家 BattleTag
- `player_display_name` — 不含 #tag 的显示名
- `account_id_lo` — 暴雪账号唯一 ID
- `game_seed` — 对局种子
- `hero_entity_id` — HERO_ENTITY 选定的 entity id
- `hero_name` / `hero_card_id` — 本地英雄信息
- `hero_placement` — 最后一个 LEADERBOARD_PLACE 值
- `all_heroes` — 所有英雄，用 `(cardId, playerSlot)` 去重
- `is_active` / `reconnected` / `conceded` / `placement_confirmed`

---

## 12. 与 HDT 插件的对比

| 能力 | bg_parser (Power.log) | HDT 插件 (HearthMirror) |
|------|----------------------|-------------------------|
| 本地玩家信息 | ✅ | ✅ |
| 对手信息 | ❌ | ✅ |
| gameUuid | ❌ | ✅ |
| 准确排名 | ⚠️ 观测值 | ✅ FinalPlacement |
| 联赛判定 | ❌ | ✅ (调 API) |
| 实时监控 | ✅ | ✅ |
| 跨平台 | ✅ (Python) | ❌ (Windows + HDT) |

bg_parser 的定位：**独立于 HDT 的备选方案**，不需要安装 HDT，但数据不如插件完整。

---

## 13. 待验证

- [ ] 正常淘汰（非投降）时 LEADERPLACE 的行为
- [ ] 多人同轮淘汰时的排名
- [ ] 断线重连后 entity_id 是否永远不变（目前只测了 2 个样本）
- [ ] 不同炉石版本的日志格式变化
