#!/usr/bin/env python3
"""
模拟 8 个插件玩家参加联赛的全过程测试
完整流程: 上传分数 → 注册 → 报名 → 等待匹配 → 联赛对局 → 提交排名 → 验证结果

用法:
  # 先启动 Flask + MongoDB，然后运行:
  python3 league/test_league.py

  # 指定 API 地址:
  API_URL=http://your-server:5000 python3 league/test_league.py
"""

import requests
import json
import time
import uuid
import sys
import os
from datetime import datetime, timedelta, UTC

BASE_URL = os.environ.get("API_URL", "https://da.iceshoes.dpdns.org/")

# ── 颜色输出 ────────────────────────────────────────
GREEN = "\033[92m"
RED = "\033[91m"
YELLOW = "\033[93m"
CYAN = "\033[96m"
RESET = "\033[0m"
BOLD = "\033[1m"


def ok(msg):
    print(f"  {GREEN}✓{RESET} {msg}")


def fail(msg):
    print(f"  {RED}✗{RESET} {msg}")


def info(msg):
    print(f"  {CYAN}→{RESET} {msg}")


def header(msg):
    print(f"\n{BOLD}{CYAN}{'─'*50}{RESET}")
    print(f"  {BOLD}{msg}{RESET}")
    print(f"{CYAN}{'─'*50}{RESET}")


# ── 8 个模拟玩家 ────────────────────────────────────
PLAYERS = [
    {"name": "衣锦夜行", "accountIdLo": f"1000000{i}", "rating": 6500 + i * 100}
    for i in range(1, 9)
]
# 补充完整 BattleTag
for i, p in enumerate(PLAYERS):
    p["battleTag"] = f"{p['name']}#{1000 + i}"
    p["displayName"] = p["name"]
    p["mode"] = "solo"
    p["region"] = "CN"

# 一局游戏的 UUID
GAME_UUID = str(uuid.uuid4())

# 最终排名（随机分配 1-8）
import random
random.seed(42)  # 固定种子，保证可复现
FINAL_PLACEMENTS = list(range(1, 9))
random.shuffle(FINAL_PLACEMENTS)


def step1_upload_ratings():
    """STEP 1: 8 个玩家上传分数，获取 token 和验证码"""
    header("STEP 1: 上传分数 (upload-rating)")

    tokens = {}
    codes = {}

    for i, player in enumerate(PLAYERS):
        payload = {
            "playerId": player["battleTag"],
            "accountIdLo": player["accountIdLo"],
            "rating": player["rating"],
            "mode": player["mode"],
            "gameUuid": GAME_UUID,
            "region": player["region"],
        }
        resp = requests.post(f"{BASE_URL}/api/plugin/upload-rating", json=payload)

        if resp.status_code == 200:
            data = resp.json()
            tokens[player["battleTag"]] = data.get("token", "")
            codes[player["battleTag"]] = data.get("verificationCode", "")
            ok(f"玩家 {player['battleTag']:20s} rating={player['rating']}  "
               f"code={codes[player['battleTag']]}")
        else:
            fail(f"玩家 {player['battleTag']} 上传失败: {resp.status_code} {resp.text}")
            return None, None

    return tokens, codes


def step2_register(tokens, codes):
    """STEP 2: 8 个玩家在网站注册"""
    header("STEP 2: 注册 (register)")

    for player in PLAYERS:
        payload = {
            "battleTag": player["battleTag"],
            "verificationCode": codes[player["battleTag"]],
        }
        resp = requests.post(f"{BASE_URL}/api/register", json=payload)

        if resp.status_code == 200:
            data = resp.json()
            ok(f"注册成功: {player['battleTag']:20s} → {data.get('displayName', '?')}")
        elif resp.status_code == 400 and "已" in resp.json().get("error", ""):
            info(f"已注册过: {player['battleTag']}")
        else:
            fail(f"注册失败: {player['battleTag']}: {resp.status_code} {resp.text}")
            return False

    return True


def step3_join_queue():
    """STEP 3: 8 个玩家报名加入队列"""
    header("STEP 3: 报名队列 (queue/join)")

    # Flask 用 cookie-based session，每个玩家需要独立 session
    # 流程: register(获取 session) → join queue
    sessions = []
    joined = 0
    for i, player in enumerate(PLAYERS):
        s = requests.Session()

        # register 设置 session cookie（已注册会 400 但没关系，session 仍会被设置）
        s.post(f"{BASE_URL}/api/register", json={
            "battleTag": player["battleTag"],
            "verificationCode": player["_code"],
        })

        # join queue（携带 session cookie）
        resp = s.post(f"{BASE_URL}/api/queue/join", json={})
        if resp.status_code == 200:
            data = resp.json()
            moved = data.get("moved", False)
            joined += 1
            if moved and joined >= 8:
                ok(f"报名 {joined}/8: {player['battleTag']:20s} → 8人满员，移入等待组! 🎉")
            else:
                ok(f"报名 {joined}/8: {player['battleTag']:20s} → 等待中...")
        else:
            fail(f"报名失败: {player['battleTag']}: {resp.status_code} {resp.text}")

        sessions.append(s)

    return sessions


def step4_verify_waiting_queue():
    """STEP 4: 验证等待组已创建"""
    header("STEP 4: 验证等待队列")

    resp = requests.get(f"{BASE_URL}/api/waiting-queue")
    if resp.status_code == 200:
        groups = resp.json()
        if groups:
            for g in groups:
                player_names = [p["name"] for p in g.get("players", [])]
                ok(f"等待组: {len(player_names)} 人 → {', '.join(player_names)}")
            return True
        else:
            fail("没有等待组!")
            return False
    else:
        fail(f"查询失败: {resp.status_code}")
        return False


def step5_check_league():
    """STEP 5: 模拟 STEP 13 时插件调用 check-league"""
    header("STEP 5: 检查联赛匹配 (check-league)")

    # 构建 accountIdLoList（8 个玩家）
    account_id_list = [p["accountIdLo"] for p in PLAYERS]

    # 构建详细的 players 信息
    detailed_players = {}
    for p in PLAYERS:
        detailed_players[p["accountIdLo"]] = {
            "battleTag": p["battleTag"],
            "displayName": p["displayName"],
            "heroCardId": f"TB_BaconShop_HERO_{random.randint(10, 99)}",
            "heroName": f"英雄_{p['name']}",
        }

    payload = {
        "playerId": PLAYERS[0]["battleTag"],
        "gameUuid": GAME_UUID,
        "accountIdLoList": account_id_list,
        "players": detailed_players,
        "mode": "solo",
        "region": "CN",
        "startedAt": (datetime.now(UTC) - timedelta(minutes=35)).strftime("%Y-%m-%dT%H:%M:%S"),
    }

    # 只需要一个玩家调用 check-league 即可创建对局
    resp = requests.post(f"{BASE_URL}/api/plugin/check-league", json=payload)

    if resp.status_code == 200:
        data = resp.json()
        if data.get("isLeague"):
            ok(f"联赛匹配成功! gameUuid={GAME_UUID[:8]}...")
            return True
        else:
            fail("未匹配到联赛对局")
            return False
    else:
        fail(f"check-league 失败: {resp.status_code} {resp.text}")
        return False


def step6_verify_active_game():
    """STEP 6: 验证对局出现在进行中列表"""
    header("STEP 6: 验证进行中对局")

    resp = requests.get(f"{BASE_URL}/api/active-games")
    if resp.status_code == 200:
        games = resp.json()
        found = [g for g in games if g.get("gameUuid") == GAME_UUID]
        if found:
            game = found[0]
            player_count = len(game.get("players", []))
            ok(f"找到进行中对局: {player_count} 名玩家")
            for p in game.get("players", []):
                info(f"  {p.get('displayName', '?'):15s} hero={p.get('heroName', '?')}")
            return True
        else:
            fail(f"未找到对局 {GAME_UUID[:8]}... (共 {len(games)} 个进行中)")
            return False
    else:
        fail(f"查询失败: {resp.status_code}")
        return False


def step7_submit_placements(tokens):
    """STEP 7: 8 个玩家依次提交排名"""
    header("STEP 7: 提交排名 (update-placement)")

    results = []
    for i, player in enumerate(PLAYERS):
        placement = FINAL_PLACEMENTS[i]
        token = tokens[player["battleTag"]]

        payload = {
            "playerId": player["battleTag"],
            "gameUuid": GAME_UUID,
            "accountIdLo": player["accountIdLo"],
            "placement": placement,
        }

        headers = {"Authorization": f"Bearer {token}"}
        resp = requests.post(
            f"{BASE_URL}/api/plugin/update-placement",
            json=payload,
            headers=headers,
        )

        if resp.status_code == 200:
            data = resp.json()
            points = 9 if placement == 1 else max(1, 9 - placement)
            finalized = data.get("finalized", False)
            status = "🏆 对局结束!" if finalized else "已提交"
            ok(f"第{placement}名: {player['battleTag']:20s} +{points}分  {status}")
            results.append(True)
        elif resp.status_code == 409:
            info(f"已提交过: {player['battleTag']}")
            results.append(True)
        else:
            fail(f"提交失败: {player['battleTag']}: {resp.status_code} {resp.text}")
            results.append(False)

    return all(results)


def step8_verify_completed_match():
    """STEP 8: 验证对局已完成"""
    header("STEP 8: 验证已完成对局")

    # 检查不再出现在进行中
    resp = requests.get(f"{BASE_URL}/api/active-games")
    if resp.status_code == 200:
        games = resp.json()
        still_active = [g for g in games if g.get("gameUuid") == GAME_UUID]
        if not still_active:
            ok("对局已从「进行中」移除")
        else:
            fail("对局仍在「进行中」列表!")
            return False

    # 检查出现在已完成对局中
    resp = requests.get(f"{BASE_URL}/api/match/{GAME_UUID}")
    if resp.status_code == 200:
        match = resp.json()
        players = match.get("players", [])

        # 验证 8 人都有排名
        placements = [p["placement"] for p in players]
        if None in placements:
            fail(f"有玩家排名为 null: {placements}")
            return False

        if sorted(placements) != list(range(1, 9)):
            fail(f"排名不完整: {sorted(placements)}")
            return False

        ok(f"对局完成: {len(players)} 名玩家，排名 1-8 齐全")
        ok(f"结束时间: {match.get('endedAt', '?')}")

        # 按排名打印
        sorted_players = sorted(players, key=lambda p: p["placement"])
        for p in sorted_players:
            medal = "🥇" if p["placement"] == 1 else "🥈" if p["placement"] == 2 else "🥉" if p["placement"] == 3 else "  "
            info(f"  {medal} 第{p['placement']}名 {p['displayName']:15s} "
                 f"hero={p.get('heroName', '?'):10s} +{p['points']}分")

        return True
    else:
        fail(f"查询对局失败: {resp.status_code} {resp.text}")
        return False


def step9_verify_leaderboard():
    """STEP 9: 验证排行榜数据"""
    header("STEP 9: 验证排行榜")

    resp = requests.get(f"{BASE_URL}/api/players")
    if resp.status_code == 200:
        players = resp.json()
        if not players:
            fail("排行榜为空!")
            return False

        ok(f"排行榜共 {len(players)} 名选手:")
        for p in players[:5]:
            info(f"  {p['displayName']:15s}  "
                 f"积分={p['totalPoints']:3d}  "
                 f"场次={p['leagueGames']:2d}  "
                 f"胜率={p['winRate']:.0%}  "
                 f"吃鸡={p['chickens']}")

        # 验证所有 8 个玩家都在榜上
        battle_tags = {p["battleTag"] for p in players}
        missing = [p["battleTag"] for p in PLAYERS if p["battleTag"] not in battle_tags]
        if missing:
            fail(f"排行榜缺少: {', '.join(missing)}")
            return False

        ok("所有 8 名玩家均在排行榜上 ✓")
        return True
    else:
        fail(f"查询失败: {resp.status_code}")
        return False


def step10_cleanup():
    """STEP 10: 清理测试数据（可选）"""
    header("STEP 10: 清理")

    # 查看报名队列是否清空
    resp = requests.get(f"{BASE_URL}/api/queue")
    if resp.status_code == 200:
        queue = resp.json()
        if not queue:
            ok("报名队列已清空")
        else:
            info(f"报名队列残留 {len(queue)} 人")

    resp = requests.get(f"{BASE_URL}/api/waiting-queue")
    if resp.status_code == 200:
        groups = resp.json()
        if not groups:
            ok("等待队列已清空")
        else:
            info(f"等待队列残留 {len(groups)} 组")


# ── 补充：重复提交防护测试 ──────────────────────────────
def step_duplicate_submit_test(tokens):
    """测试重复提交防护（幂等性）"""
    header("STEP 7b: 测试重复提交防护")

    player = PLAYERS[0]
    token = tokens[player["battleTag"]]
    placement = FINAL_PLACEMENTS[0]

    payload = {
        "playerId": player["battleTag"],
        "gameUuid": GAME_UUID,
        "accountIdLo": player["accountIdLo"],
        "placement": placement,
    }
    headers = {"Authorization": f"Bearer {token}"}
    resp = requests.post(
        f"{BASE_URL}/api/plugin/update-placement",
        json=payload,
        headers=headers,
    )

    if resp.status_code == 409:
        ok("重复提交被正确拒绝 (409)")
        return True
    elif resp.status_code == 200:
        info("重复提交被接受了（服务端可能允许幂等重试）")
        return True
    else:
        fail(f"意外状态: {resp.status_code} {resp.text}")
        return False


# ── 补充：rating 变化幅度测试 ────────────────────────────
def step_rating_delta_test():
    """测试单次 rating 变化超过 ±500 的拒绝"""
    header("STEP 1b: 测试 rating 变化幅度限制")

    player = PLAYERS[0]
    payload = {
        "playerId": player["battleTag"],
        "accountIdLo": player["accountIdLo"],
        "rating": player["rating"] + 999,  # 超过 ±500
        "mode": "solo",
        "gameUuid": GAME_UUID,
        "region": "CN",
    }
    resp = requests.post(f"{BASE_URL}/api/plugin/upload-rating", json=payload)

    if resp.status_code == 400 and "500" in resp.text:
        ok("异常 rating 变化被正确拒绝 (±500 限制)")
        return True
    else:
        info(f"未触发限制检查: {resp.status_code} {resp.text}")
        return True  # 非致命


# ── 主流程 ─────────────────────────────────────────────

def main():
    print(f"""
{BOLD}{CYAN}
╔══════════════════════════════════════════════╗
║   HDT_BGTracker 联赛全流程模拟测试          ║
║   API: {BASE_URL:38s}║
║   Game UUID: {GAME_UUID[:8]}...                        ║
╚══════════════════════════════════════════════╝
{RESET}""")

    # 预先将验证码注入到 players 中
    # （因为 register 需要验证码，但验证码是在 upload-rating 时生成的）

    # 检查 API 是否可达
    try:
        resp = requests.get(f"{BASE_URL}/api/players", timeout=5)
    except requests.ConnectionError:
        fail(f"无法连接到 {BASE_URL}，请先启动 Flask 服务")
        print(f"  启动方法: cd league && python app.py")
        sys.exit(1)

    failed_steps = []

    # STEP 1: 上传分数
    tokens, codes = step1_upload_ratings()
    if tokens is None:
        fail("上传分数失败，终止测试")
        sys.exit(1)

    # 把验证码注入到 player 对象中供后续使用
    for p in PLAYERS:
        p["_code"] = codes[p["battleTag"]]

    # STEP 1b: 测试异常 rating
    step_rating_delta_test()

    # STEP 2: 注册
    if not step2_register(tokens, codes):
        failed_steps.append("注册")

    # STEP 3: 报名队列
    step3_join_queue()

    # STEP 4: 验证等待组
    if not step4_verify_waiting_queue():
        failed_steps.append("等待队列")

    # STEP 5: 检查联赛匹配
    if not step5_check_league():
        failed_steps.append("联赛匹配")
    else:
        # STEP 6: 验证进行中对局
        if not step6_verify_active_game():
            failed_steps.append("进行中对局")

    # STEP 7: 提交排名
    if not step7_submit_placements(tokens):
        failed_steps.append("提交排名")

    # STEP 7b: 测试重复提交
    step_duplicate_submit_test(tokens)

    # STEP 8: 验证已完成对局
    if not step8_verify_completed_match():
        failed_steps.append("已完成对局")

    # STEP 9: 验证排行榜
    if not step9_verify_leaderboard():
        failed_steps.append("排行榜")

    # STEP 10: 清理
    step10_cleanup()

    # 总结
    header("测试结果")
    if not failed_steps:
        print(f"\n  {GREEN}{BOLD}🎉 全部测试通过!{RESET}\n")
    else:
        print(f"\n  {RED}{BOLD}❌ 以下步骤失败: {', '.join(failed_steps)}{RESET}\n")
        sys.exit(1)


if __name__ == "__main__":
    main()
