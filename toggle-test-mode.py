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

工作原理：基于代码中的 BEGIN/END TEST_MODE 标记进行整块替换。
标记格式：
  # >>> BEGIN TEST_MODE   /  # <<< END TEST_MODE        (Python)
  // >>> BEGIN TEST_MODE  /  // <<< END TEST_MODE       (C#)
"""

import sys
import os

os.chdir(os.path.dirname(os.path.abspath(__file__)) or ".")

BEGIN = "BEGIN TEST_MODE"
END = "END TEST_MODE"

# =====================================================================
# C# 插件 — RatingTracker.cs 中 else 分支的两种形态
# =====================================================================

CS_NORMAL = '''\
                            // >>> BEGIN TEST_MODE
                            else
                            {
                                _isLeagueGame = false;
                                Log("CheckLeagueQueue: 未匹配到等待组，普通天梯局");
                            }
                            // <<< END TEST_MODE'''

CS_TEST = '''\
                            // >>> BEGIN TEST_MODE
                            else
                            {
                                // [TESTING] 暂时跳过联赛判断，所有对局都当联赛处理
                                _isLeagueGame = true;
                                Log("CheckLeagueQueue: [TESTING] 跳过等待组匹配，强制标记为联赛对局");
                            }
                            // <<< END TEST_MODE'''

# =====================================================================
# Flask — app.py 中 matched_group is None 后的两种形态
# =====================================================================

FLASK_NORMAL = '''\
    # >>> BEGIN TEST_MODE
    if matched_group is None:
        # 非联赛局，但仍处理验证码
        resp = {"isLeague": False}
        player_id = data.get("playerId", "").strip()
        if player_id and player_id != "unknown":
            account_id_lo_for_code = data.get("accountIdLo", "").strip()
            existing_rating = db.bg_ratings.find_one({"playerId": player_id})
            if existing_rating:
                vc = existing_rating.get("verificationCode")
                if vc:
                    resp["verificationCode"] = vc
                if account_id_lo_for_code and not existing_rating.get("accountIdLo"):
                    db.bg_ratings.update_one(
                        {"_id": existing_rating["_id"]},
                        {"$set": {"accountIdLo": account_id_lo_for_code}}
                    )
            else:
                mode = data.get("mode", "solo")
                region = data.get("region", "CN")
                now_str = datetime.now(UTC).strftime("%Y-%m-%dT%H:%M:%S")
                doc = {
                    "playerId": player_id,
                    "accountIdLo": account_id_lo_for_code,
                    "rating": 0,
                    "lastRating": 0,
                    "ratingChange": 0,
                    "mode": mode,
                    "region": region,
                    "timestamp": now_str,
                    "gameCount": 0,
                }
                result = db.bg_ratings.insert_one(doc)
                vc = _generate_verification_code(result.inserted_id)
                db.bg_ratings.update_one(
                    {"_id": result.inserted_id},
                    {"$set": {"verificationCode": vc}}
                )
                resp["verificationCode"] = vc
        return jsonify(resp)
    # <<< END TEST_MODE'''

FLASK_TEST = '''\
    # >>> BEGIN TEST_MODE
    if matched_group is None:
        # [TESTING] 暂时跳过等待组匹配，直接用插件上报的玩家数据创建联赛对局
        detailed_players = data.get("players", {})
        account_ids_raw = data.get("accountIdLoList", [])
        account_ids = sorted(account_ids_raw) if isinstance(account_ids_raw, list) else []
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

        # 验证码处理
        resp = {"isLeague": True}
        player_id = data.get("playerId", "").strip()
        if player_id and player_id != "unknown":
            account_id_lo_for_code = data.get("accountIdLo", "").strip()
            existing_rating = db.bg_ratings.find_one({"playerId": player_id})
            if existing_rating:
                vc = existing_rating.get("verificationCode")
                if vc:
                    resp["verificationCode"] = vc
            else:
                now_str = datetime.now(UTC).strftime("%Y-%m-%dT%H:%M:%S")
                doc = {
                    "playerId": player_id,
                    "accountIdLo": account_id_lo_for_code,
                    "rating": 0, "lastRating": 0, "ratingChange": 0,
                    "mode": mode, "region": region, "timestamp": now_str, "gameCount": 0,
                }
                result = db.bg_ratings.insert_one(doc)
                vc = _generate_verification_code(result.inserted_id)
                db.bg_ratings.update_one({"_id": result.inserted_id}, {"$set": {"verificationCode": vc}})
                resp["verificationCode"] = vc
        return jsonify(resp)
    # <<< END TEST_MODE'''


def find_marker_block(content: str) -> str | None:
    """返回两个标记之间的完整文本（含标记行），找不到返回 None"""
    begin_idx = content.find(BEGIN)
    end_idx = content.find(END)
    if begin_idx < 0 or end_idx < 0:
        return None
    # 往前找行首，往后找行尾
    block_start = content.rfind("\n", 0, begin_idx) + 1
    end_line_end = content.find("\n", end_idx)
    if end_line_end < 0:
        end_line_end = len(content)
    return content[block_start:end_line_end]


def detect_mode(content: str, normal: str, test: str) -> str | None:
    block = find_marker_block(content)
    if block is None:
        return None
    # 去掉标记行本身，只比较内容
    def strip_markers(b):
        return "\n".join(
            line for line in b.split("\n")
            if BEGIN not in line and END not in line
        )
    block_core = strip_markers(block)
    normal_core = strip_markers(normal)
    test_core = strip_markers(test)
    if block_core == normal_core:
        return "normal"
    if block_core == test_core:
        return "test"
    return None


def replace_block(content: str, new_block: str) -> str:
    old_block = find_marker_block(content)
    if old_block is None:
        raise ValueError(f"找不到 {BEGIN}/{END} 标记")
    return content.replace(old_block, new_block, 1)


def main():
    args = sys.argv[1:]

    cs_path = "HDT_BGTracker/RatingTracker.cs"
    flask_path = "league/app.py"

    with open(cs_path, "r", encoding="utf-8") as f:
        cs_content = f.read()
    with open(flask_path, "r", encoding="utf-8") as f:
        flask_content = f.read()

    # 检测当前模式
    cs_mode = detect_mode(cs_content, CS_NORMAL, CS_TEST)
    flask_mode = detect_mode(flask_content, FLASK_NORMAL, FLASK_TEST)

    if cs_mode is None:
        print(f"⚠ C# 文件 ({cs_path}) 无法识别模式，TEST_MODE 标记可能被修改")
        sys.exit(1)
    if flask_mode is None:
        print(f"⚠ Flask 文件 ({flask_path}) 无法识别模式，TEST_MODE 标记可能被修改")
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

    if target == "test":
        cs_content = replace_block(cs_content, CS_TEST)
        flask_content = replace_block(flask_content, FLASK_TEST)
    else:
        cs_content = replace_block(cs_content, CS_NORMAL)
        flask_content = replace_block(flask_content, FLASK_NORMAL)

    with open(cs_path, "w", encoding="utf-8") as f:
        f.write(cs_content)
    with open(flask_path, "w", encoding="utf-8") as f:
        f.write(flask_content)

    print(f"✅ 已切换到 {target} 模式")
    os.system(f"git diff --stat {cs_path} {flask_path}")


if __name__ == "__main__":
    main()
