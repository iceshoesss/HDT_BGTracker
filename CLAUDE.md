# CLAUDE.md — AI 开发指南

## 项目概述

炉石传说酒馆战棋联赛插件（HDT 插件 + bg_tool 独立程序）。claw_version 分支。

## 快速导航

| 模块 | 目录 | 说明 |
|------|------|------|
| HDT 插件 | `HDT_BGTracker/` | HDT 内运行的 C# 插件（net472） |
| bg_tool | `bg_tool/` | 独立 WinForms 程序，读 Power.log + HearthMirror |
| bg_parser | `bg_parser/` | Python 参考实现（bg_tool 的原型） |
| 开发文档 | `DEV_NOTES.md` | **必读** — 踩坑记录，所有结论已实测 |
| API 文档 | `API.md` | 插件调用的 Flask API（与 LeagueWeb 共享） |

## 数据流

```
玩家打完一局 → 插件/bg_tool STEP 13 检测
  → POST /api/plugin/check-league → 匹配淘汰赛/积分赛 → 创建 league_matches
  → 游戏结束 → POST /api/plugin/update-placement → 更新排名 + 积分
```

## 版本号

两套独立版本，互不关联：

| 组件 | 位置 | 当前版本 |
|------|------|----------|
| HDT 插件 | `BGTrackerPlugin.cs` + `HDT_BGTracker.csproj` | v0.7.0 |
| bg_tool | `bg_tool/Properties/AssemblyInfo.cs` | v0.4.1 |

修改版本号必须同步两处：csproj 的 `<Version>` 和代码中的 `new Version(x, y, z)`。

## 编译

```powershell
cd HDT_BGTracker
$env:HDT_PATH = "HDT安装路径"
dotnet build -c Release

cd bg_tool
$env:HDT_PATH = "HDT安装路径"
dotnet build -c Release  # 输出 x86 32 位
```

## C# net472 限制（不要踩的坑）

- ❌ 文件作用域 namespace `namespace X;` → 用 `namespace X { }`
- ❌ 目标类型 new `new()` → 用 `new ClassName()`
- ❌ 范围语法 `str[..]` → 用 `str.Substring()`
- ❌ 模式组合 `x is "A" or "B"` → 用 `x == "A" || x == "B"`
- ❌ char 重载 `str.Contains('x')` → 用 `str.Contains("x")`
- ❌ 从末尾索引 `list[^1]` → 用 `list[list.Count - 1]`
- ❌ SDK 项目 `<UseWPF>true</UseWPF>` → 纯 C# 创建 UI
- ❌ HearthMirror.dll 必须 x86 进程加载 → `<PlatformTarget>x86</PlatformTarget>`

## HDT API 关键发现（已实测，不要重复尝试）

- `Core.Game.Player.Name` 游戏结束后变空，必须游戏中缓存
- 进入游戏后**延迟 3 秒**再读 Player.Name
- `Config.Instance.BattleTag` 不存在
- `FinalPlacement` 游戏内始终为 null，只有 `IsInMenu` 后才可读
- `LobbyInfo` 在 STEP 13 时完整可用，game_start 时可能不完整
- Power.log 没有对手 BattleTag，只有 HearthMirror 能获取

## 改代码后必做

1. 更新 `DEV_NOTES.md`
2. 更新 `README.md`
3. 版本号同步递增（csproj + 代码）
4. commit 中文，`fix:/feat:/docs:/refactor:` 前缀

## 配置文件

- `bg_tool/config.json` — API 地址、region、mode（不进 git）
- `bg_tool/config.json.example` — 配置模板
- `shared_config.json` — bg_tool 和 HDT 插件共用配置（向上逐级查找）
