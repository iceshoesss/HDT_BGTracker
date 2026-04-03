# HDT_BGTracker

炉石传说酒馆战棋分数记录插件，在每局结束后自动记录分数并上传到 MongoDB。

## 数据结构

MongoDB 数据库: `hearthstone`，集合: `bg_ratings`

```json
{
  "rating": 6500,
  "mode": "solo",
  "timestamp": "2026-04-03T09:15:00.0000000Z",
  "region": "CN"
}
```

- `rating` — 酒馆战棋分数（整数）
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

默认连接地址: `mongodb://192.168.31.2:27017`

如需修改，编辑 `RatingTracker.cs` 中的 `MongoUrl` 常量。
