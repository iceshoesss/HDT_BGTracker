# HDT_BGTracker

炉石传说酒馆战棋分数记录插件，在每局结束后自动记录分数并上传到 MongoDB。

## 数据结构

MongoDB 数据库: `hearthstone`，集合: `bg_ratings`

```json
{
  "playerId": "玩家名#1234",
  "rating": 6500,
  "lastRating": 6477,
  "ratingChange": 23,
  "ratingChanges": [23, -15, 40, -30],
  "placements": [4, 1, 3, 6],
  "gameCount": 42,
  "mode": "solo",
  "timestamp": "2026-04-07T09:15:00.0000000Z",
  "region": "CN"
}
```

- `rating` — 当前酒馆战棋分数
- `lastRating` — 上一局的分数
- `ratingChange` — 本局分差（`当前分 - 上局分`）
- `ratingChanges` — 每局分差的历史数组
- `placements` — 每局排名的历史数组（1-8，null 表示未获取到）
- `gameCount` — 累计上传局数
- `mode` — `solo`（单人）或 `duo`（双人）
- `timestamp` — UTC 时间
- `region` — 服务器区域

## 编译

### 前置条件

- .NET SDK（dotnet 命令可用）
- 安装好的 HDT（Hearthstone Deck Tracker）

### 步骤

```cmd
cd HDT_BGTracker
set HDT_PATH=C:\Users\你的用户名\Downloads\HDT-V2.4.2\HDT
dotnet build -c Release
```

编译成功后，`bin\Release\net472\` 下会生成所有需要的 DLL。

## 打包安装

将需要的 DLL 打包成 zip：

```cmd
cd bin\Release\net472
tar -a -cf HDT_BGTracker.zip HDT_BGTracker.dll MongoDB.Bson.dll MongoDB.Driver.dll MongoDB.Driver.Core.dll DnsClient.dll MongoDB.Libmongocrypt.dll SharpCompress.dll
```

把这个 zip 直接丢进 HDT 的 Plugins 目录（Options → Tracker → Plugins → Plugins Folder），重启 HDT 即可。

## 使用

- 插件启用后自动运行，无需手动操作
- 每局酒馆战棋结束后（返回主菜单时），自动读取分数并上传
- 点击插件设置中的「测试连接」按钮可验证 MongoDB 连接
- 日志在 `%AppData%\HearthstoneDeckTracker\BGTracker\tracker.log`

## MongoDB 配置

MongoDB 连接地址配置在 `RatingTracker.cs` 中的 `MongoUrl` 常量。该文件通过 `git update-index --skip-worktree` 忽略本地修改，不会被意外提交到 GitHub。

如需修改连接地址，直接编辑本地 `RatingTracker.cs` 即可。

如需临时恢复 Git 跟踪（比如要更新文件中的其他逻辑）：
```bash
git update-index --no-skip-worktree HDT_BGTracker/RatingTracker.cs
# 改完 commit 后重新锁上
git update-index --skip-worktree HDT_BGTracker/RatingTracker.cs
```
