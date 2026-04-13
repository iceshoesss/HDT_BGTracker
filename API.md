# API 文档

完整 API 文档已迁移至 **[LeagueWeb](https://github.com/iceshoesss/LeagueWeb)** 仓库。

→ [LeagueWeb/API.md](https://github.com/iceshoesss/LeagueWeb/blob/main/API.md)

---

## 插件相关端点速查

以下端点供 C# HDT 插件调用：

| 端点 | 方法 | 认证 | 时机 | 说明 |
|------|------|------|------|------|
| `/api/plugin/upload-rating` | POST | 无 | 游戏结束 | 上报分数 + 获取验证码 |
| `/api/plugin/check-league` | POST | 无 | STEP 13 | 检查联赛匹配 |
| `/api/plugin/update-placement` | POST | 无 | 游戏结束 | 更新排名 |

详细请求/响应格式见 [LeagueWeb/API.md](https://github.com/iceshoesss/LeagueWeb/blob/main/API.md#插件专用-api)。

## 数据库

- 数据库名：`hearthstone`
- 关键集合：`player_records`（玩家记录+验证码）、`league_matches`（联赛对局）
- 完整集合说明见 LeagueWeb API 文档
