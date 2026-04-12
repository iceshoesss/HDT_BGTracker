# 酒馆战棋联赛网站 — 开发计划

## 项目目标

从 HDT_BGTracker 插件的数据出发，构建一个酒馆战棋联赛网站，实现：
- 选手注册与验证
- 自动对局记录（插件上报）
- 实时排行榜（可排序）
- 对局详情与选手个人页
- 实时进行中的对局展示

---

## 数据库结构（MongoDB）

数据库名：`hearthstone`

### 1. `league_players` — 选手表

| 字段 | 类型 | 说明 |
|------|------|------|
| `_id` | ObjectId | 自动生成 |
| `battleTag` | string | 完整 BattleTag，如 `南怀北瑾丨少头脑#5267`，唯一索引 |
| `accountIdLo` | long | 暴雪账号唯一标识（跨局稳定），唯一索引 |
| `displayName` | string | 显示名称（不含 #tag） |
| `verified` | bool | 是否通过验证码验证 |
| `verificationCode` | string | 6位验证码，注册时生成 |
| `totalPoints` | int | 累计积分 |
| `totalGames` | int | 总场次 |
| `wins` | int | 吃鸡次数（第1名） |
| `avgPlacement` | double | 平均排名 |
| `lastGameAt` | datetime | 最后一局时间 |
| `createdAt` | datetime | 注册时间 |

索引：
- `battleTag` 唯一索引
- `accountIdLo` 唯一索引
- `totalPoints` 降序（排行榜查询）

### 2. `league_matches` — 对局表

| 字段 | 类型 | 说明 |
|------|------|------|
| `_id` | ObjectId | 自动生成 |
| `gameUuid` | string | 对局唯一标识（来自 HDT LobbyInfo），唯一索引 |
| `players` | array | 本局所有玩家（见下方） |
| `region` | string | 服务器区域（CN/US/EU 等） |
| `mode` | string | `solo` 或 `duo` |
| `startedAt` | datetime | 对局开始时间 |
| `endedAt` | datetime | 对局结束时间 |

`players` 数组每项：

| 字段 | 类型 | 说明 |
|------|------|------|
| `battleTag` | string | 玩家 BattleTag |
| `accountIdLo` | long | 账号唯一标识 |
| `displayName` | string | 显示名称 |
| `heroName` | string | 英雄中文名 |
| `placement` | int | 排名 1-8 |
| `points` | int | 本局得分 |

**积分规则：** 第1名=9分，第2名起 = 9 - 排名

| 排名 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 |
|------|---|---|---|---|---|---|---|---|
| 积分 | 9 | 7 | 6 | 5 | 4 | 3 | 2 | 1 |

索引：
- `gameUuid` 唯一索引
- `endedAt` 降序（最近对局查询）

### 3. `league_active_games` — 实时活跃对局

| 字段 | 类型 | 说明 |
|------|------|------|
| `_id` | ObjectId | 自动生成 |
| `gameUuid` | string | 对局唯一标识 |
| `players` | array | 简化的玩家信息 `{ displayName }` |
| `startedAt` | datetime | 游戏开始时间 |

生命周期：插件检测到游戏开始 → 写入；游戏结束 → 删除。网站侧边栏实时读取此集合。

索引：
- `gameUuid` 唯一索引

---

---

## 网站功能

### 主页面 — 排行榜 + 实时对局

布局：
```
┌──────────────────────────────────────────────────────┬──────────────────┐
│  🏆 酒馆战棋联赛                                     │  ⚔️ 正在进行     │
├──────────────────────────────────────────────────────┤                  │
│  #  玩家          积分  场次  胜率  场均排名  吃鸡    │  ┌──────────────┐ │
│  ────────────────────────────────────────────────    │  │ 南怀北瑾...  │ │
│  1  南怀北瑾少头脑  142   20   45%    2.8      9     │  │ 月光骑士     │ │
│  2  疾风剑豪      128   18   38%    3.1      7     │  │ 虚空行者     │ │
│  3  暗夜精灵      115   22   32%    3.5      7     │  │ ...          │ │
│  ...                                                 │  │ 开始: 20:23  │ │
│  (点击列头可排序)                                     │  │ ⏱️ 08:42     │ │
│                                                      │  ├──────────────┤ │
│  最近对局                                            │  │ 疾风剑豪     │ │
│  #3  10:00  暗夜精灵(1st) 星辰大海(2nd) ...           │  │ ...          │ │
│  #2  11:30  疾风剑豪(1st) 南怀北瑾(2nd) ...           │  │ ⏱️ 02:13     │ │
│  #1  12:15  南怀北瑾(1st) 冰霜法师(2nd) ...           │  └──────────────┘ │
└──────────────────────────────────────────────────────┴──────────────────┘
```

排序逻辑：
- 默认按 `totalPoints` 降序
- 点击「场次」按 `totalGames` 排序
- 点击「胜率」按 `wins/totalGames` 排序
- 点击「场均排名」按 `avgPlacement` 升序（越低越好）

实时对局区域：
- 读取 `league_active_games`
- 每个对局卡片显示玩家名 + 开始时间 + 已过时长（JavaScript 实时计时）
- 对局结束（集合中删除）后自动消失

### 选手个人页 — `/player/<battleTag>`

- 头部：玩家名 + 总积分 + 总场次 + 胜率 + 平均排名
- 历史对局列表：时间、英雄、排名、得分
- 积分变化趋势图（可选，后续迭代）

### 对局详情 — `/match/<gameUuid>`

- 8 个玩家的排名、英雄、得分
- 对局时间、区域

### 注册页 — `/register`

- 输入 BattleTag → 生成 6 位验证码 → 用户在 HDT 插件输入 → 自动绑定
- 验证成功后显示为已认证选手

---

## 技术选型

- **前端**：Flask + Jinja2 模板 + 纯 CSS（已有 `league/` 目录骨架）
- **后端**：Flask，直连 MongoDB
- **数据库**：MongoDB（已有实例）
- **部署**：gunicorn + systemd，Nginx 反代
- **实时更新**：轮询（20桌规模不需要 WebSocket，每 5-10 秒 fetch active_games 即可）

---

## 队列机制

选手在网站点击"参赛"进入队列，凑齐 8 人后开赛，插件上报对局数据与队列匹配时记为联赛对局。

### 数据流

```
网站队列 (league_queue)          插件上报 (raw_games)
8人入队 → 标记 ready             游戏结束 → 上报完整对局
[p1,p2...p8]                     [p1,p2...p8]
         \                       /
          └───── 后端匹配检查 ─────┘
                 8人 accountIdLo 完全匹配？
                ╱                  ╲
              是                    否
              ▼                     ▼
        写入 league_matches     忽略（普通天梯局）
        更新 league_players
        清空队列
```

### league_queue 集合

| 字段 | 类型 | 说明 |
|------|------|------|
| `_id` | ObjectId | 自动生成 |
| `players` | array | 队列中的玩家列表 `{ accountIdLo, displayName }` |
| `status` | string | `waiting`（凑人中）/ `ready`（8人已满，等待开赛） |
| `createdAt` | datetime | 入队时间 |
| `readyAt` | datetime | 标记 ready 的时间（用于超时判断） |

### 匹配逻辑

- 插件上报 `raw_games` 时，取该对局 8 个玩家的 `accountIdLo`
- 查询 `league_queue` 中 status=ready 的队列，比对 accountIdLo 集合
- **完全匹配** → 写入 `league_matches`，更新 `league_players` 统计，删除队列
- **不匹配** → 忽略（普通天梯局）

### 队列超时

- ready 后 **10 分钟** 无匹配对局 → 自动清空队列
- 超时后玩家需要重新入队

---

## API 设计

| 路由 | 方法 | 说明 |
|------|------|------|
| `/api/players` | GET | 排行榜（支持 ?sort=points&order=desc） |
| `/api/players/<battleTag>` | GET | 选手详情 |
| `/api/matches` | GET | 最近对局列表 |
| `/api/matches/<gameUuid>` | GET | 对局详情 |
| `/api/active-games` | GET | 当前进行中的对局 |
| `/api/queue` | GET | 当前队列状态 |
| `/api/queue/join` | POST | 入队（accountIdLo） |
| `/api/queue/leave` | POST | 离队（accountIdLo） |
| `/api/register` | POST | 注册（输入 battleTag，返回 verificationCode） |
| `/api/verify` | POST | 验证（输入 battleTag + code） |
| `/api/raw-game` | POST | 插件上报对局数据（触发联赛匹配检查） |

---

## 开发顺序

### Phase 1 — 插件完善对局数据上报（当前）
1. 上传数据补充 `heroName`、`startedAt`、完整 8 人 accountIdLo
2. 新增 `raw_games` 集合写入（完整对局数据，供联赛匹配用）
3. 保持原有 `bg_ratings` 写入不变

### Phase 2 — Flask 网站接入真实数据
1. 排行榜从 `league_players` 查询
2. 对局列表从 `league_matches` 查询
3. 实时对局轮询 `league_active_games`

### Phase 3 — 注册与验证
1. 注册流程（battleTag → 生成验证码）
2. 插件端验证码输入与自动绑定
3. `league_players` 自动创建与标记 verified

### Phase 4 — 队列系统
1. 队列入队/离队 API
2. 队列页面（显示当前排队状态）
3. 8 人就绪 → ready 状态
4. 后端匹配逻辑（raw_games 与队列比对）
5. 超时自动清空

### Phase 5 — 功能完善
1. 选手个人页
2. 对局详情页
3. 积分趋势图
4. 赛季功能（可选）

---

## 待确认

- [ ] MongoDB 连接地址（网站服务器能否直连现有 MongoDB）
- [ ] 是否需要域名，还是先用 IP + 端口访问
- [ ] 积分规则是否需要调整（当前：1st=9, 2nd=7, 3rd=6, ..., 8th=1）
- [ ] 队列超时时间（当前默认 10 分钟）
- [ ] 是否需要管理员手动确认开赛，还是 8 人满了自动 ready
