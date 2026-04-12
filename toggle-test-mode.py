#!/usr/bin/env python3
"""
toggle-test-mode.py — 切换联赛测试/正常模式

测试模式：所有对局都强制标记为联赛对局（跳过等待组匹配）
正常模式：未匹配到等待组的对局视为普通天梯局

用法：
  python toggle-test-mode.py          # 显示当前状态
  python toggle-test-mode.py test     # 切换到测试模式
  python toggle-test-mode.py normal   # 切换到正常模式
  python toggle-test-mode.py flip     # 翻转
"""

import sys
import os

os.chdir(os.path.dirname(os.path.abspath(__file__)) or ".")

# =====================================================================
# C# 插件 — RatingTracker.cs 中 else 分支的两种形态
# =====================================================================

CS_NORMAL = '''\
                            else
                            {
                                _isLeagueGame = false;
                                Log("CheckLeagueQueue: 未匹配到等待组，普通天梯局");
                            }'''

CS_TEST = '''\
                            else
                            {
                                // [TESTING] 暂时跳过联赛判断，所有对局都当联赛处理
                                // _isLeagueGame = false;
                                // Log("CheckLeagueQueue: 未匹配到等待组，普通天梯局");
                                _isLeagueGame = true;
                                Log("CheckLeagueQueue: [TESTING] 跳过等待组匹配，强制标记为联赛对局");
                            }'''

# =====================================================================
# Flask — app.py 中 matched_group is None 后的两种形态
# =====================================================================

FLASK_NORMAL = '''\
    if matched_group is None:
        return jsonify({"isLeague": False})'''

FLASK_TEST = '''\
    if matched_group is None:
        # [TESTING] 暂时跳过等待组匹配，直接用插件上报的玩家数据创建联赛对局
        detailed_players = data.get("players", {})
        players = []
        for lo in account_ids:
            detail = detailed_players.get(lo, {})
            players.append({
                "accountIdLo": lo,
                "battleTag": detail.get("battleTag", ""),
                "displayName": detail.get("displayName", ""),
                "heroCardId": detail.get("heroCardId", ""),
                "heroName": detail.get("heroName", ""),
                "placement": None,
                "points": None,
            })

        mode = data.get("mode", "solo")
        region = data.get("region", "CN")
        started_at = data.get("startedAt", datetime.now(UTC).strftime("%Y-%m-%dT%H:%M:%S"))

        db.league_matches.update_one(
            {"gameUuid": game_uuid},
            {"$setOnInsert": {
                "players": players,
                "region": region,
                "mode": mode,
                "startedAt": started_at,
                "endedAt": None,
            }},
            upsert=True,
        )

        return jsonify({"isLeague": True})'''


def detect_mode(content: str, normal: str, test: str) -> str | None:
    if normal in content:
        return "normal"
    if test in content:
        return "test"
    return None


def replace(content: str, old: str, new: str) -> str:
    if old not in content:
        raise ValueError(f"找不到预期文本，文件可能已被手动修改:\n{old[:80]}...")
    return content.replace(old, new, 1)


def main():
    args = sys.argv[1:]

    # 读取文件
    cs_path = "HDT_BGTracker/RatingTracker.cs"
    flask_path = "league/app.py"

    with open(cs_path, "r", encoding="utf-8") as f:
        cs_content = f.read()
    with open(flask_path, "r", encoding="utf-8") as f:
        flask_content = f.read()

    # 检测当前模式
    cs_mode = detect_mode(cs_content, CS_NORMAL, CS_TEST)
    flask_mode = detect_mode(flask_content, FLASK_NORMAL, FLASK_TEST)

    if cs_mode is None or flask_mode is None:
        print("⚠ 无法识别当前模式，文件可能被手动修改过")
        sys.exit(1)

    if cs_mode != flask_mode:
        print(f"⚠ 两个文件模式不一致: C#={cs_mode}, Flask={flask_mode}")
        sys.exit(1)

    current = cs_mode
    print(f"当前模式: {current}")

    if not args:
        sys.exit(0)

    target = args[0]
    if target == "flip":
        target = "test" if current == "normal" else "normal"
    if target not in ("test", "normal"):
        print(f"用法: {sys.argv[0]} [test|normal|flip]")
        sys.exit(1)

    if target == current:
        print(f"已经是 {target} 模式，无需切换")
        sys.exit(0)

    print(f"切换到: {target} 模式")

    # 执行替换
    if target == "test":
        cs_content = replace(cs_content, CS_NORMAL, CS_TEST)
        flask_content = replace(flask_content, FLASK_NORMAL, FLASK_TEST)
    else:
        cs_content = replace(cs_content, CS_TEST, CS_NORMAL)
        flask_content = replace(flask_content, FLASK_TEST, FLASK_NORMAL)

    # 写回
    with open(cs_path, "w", encoding="utf-8") as f:
        f.write(cs_content)
    with open(flask_path, "w", encoding="utf-8") as f:
        f.write(flask_content)

    print(f"✅ 已切换到 {target} 模式")
    os.system("git diff --stat HDT_BGTracker/RatingTracker.cs league/app.py")


if __name__ == "__main__":
    main()
