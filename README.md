# HDT_BGTracker

炉石传说酒馆战棋分数记录插件，在每局结束后自动记录分数并上传到 MongoDB。

## 数据结构

MongoDB 数据库: `hearthstone`，集合: `bg_ratings`

```json
{
  "playerId": "玩家名#1234",
  "accountIdLo": "1708070391",
  "rating": 6500,
  "lastRating": 6477,
  "ratingChange": 23,
  "ratingChanges": [23, -15, 40, -30],
  "placements": [4, 1, 3, 6],
  "gameCount": 42,
  "mode": "solo",
  "timestamp": "2026-04-07T09:15:00.0000000Z",
  "region": "CN",
  "games": [
    {
      "gameUuid": "888fc109-8a0c-42d8-8b21-fcee26708e8f",
      "isLeague": false,
      "placement": 3,
      "opponents": [
        { "name": "对手名", "accountIdLo": "12345678" }
      ],
      "endTime": "2026-04-07T15:05:00.0000000Z",
      "ratingChange": 23
    }
  ]
}
```

- `playerId` — 玩家 BattleTag（如 `南怀北瑾丨少头脑#5267`）
- `accountIdLo` — 玩家唯一标识（暴雪 AccountId.Lo，字符串存储避免大数问题）
- `rating` — 当前酒馆战棋分数
- `lastRating` — 上一局的分数
- `ratingChange` — 本局分差（`当前分 - 上局分`）
- `ratingChanges` — 每局分差的历史数组
- `placements` — 每局排名的历史数组（1-8，null 表示未获取到）
- `gameCount` — 累计上传局数
- `mode` — `solo`（单人）或 `duo`（双人）
- `timestamp` — UTC 时间
- `region` — 服务器区域
- `games` — 对局明细数组
  - `gameUuid` — 游戏唯一标识
  - `isLeague` — 是否联赛（当前固定 false）
  - `placement` — 本局排名（1-8 或 null）
  - `opponents` — 对手列表（name + accountIdLo）
  - `endTime` — 对局结束时间 UTC
  - `ratingChange` — 本局分数变化

## 编译

### 前置条件

- .NET SDK（dotnet 命令可用）
- 安装好的 HDT（Hearthstone Deck Tracker）

### 步骤

cmd
```cmd
cd HDT_BGTracker
set HDT_PATH=YOUR_HDT_ADDRESS
dotnet build -c Release
```

powershell
```powershell
cd HDT_BGTracker
$env:HDT_PATH = "YOUR_HDT_ADDRESS"
dotnet build -c Release
```

linux

```bash
cd HDT_BGTracker
export HDT_PATH=YOUR_HDT_ADDRESS
dotnet build -c Release
```


编译成功后，`bin\Release\net472\` 下会生成所有需要的 DLL。

## 打包安装

将需要的 DLL 打包成 zip：

```cmd
cd bin\Release\net472
tar -a -cf HDT_BGTracker.zip HDT_BGTracker.dll MongoDB.Bson.dll MongoDB.Driver.dll MongoDB.Driver.Core.dll DnsClient.dll MongoDB.Libmongocrypt.dll SharpCompress.dll
```



## 使用

- 插件启用后自动运行，无需手动操作
- 每局酒馆战棋结束后（返回主菜单时），自动读取分数并上传
- 点击插件设置中的「测试连接」按钮可验证 MongoDB 连接
- 日志在 `%AppData%\HearthstoneDeckTracker\BGTracker\tracker.log`

