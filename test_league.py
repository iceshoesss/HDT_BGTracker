#!/usr/bin/env python3
"""
联赛全流程模拟测试 — 模拟真实插件行为, 对比 placement 重试机制

用法:
  python3 test_league.py --mode=before
  python3 test_league.py --mode=after
  python3 test_league.py --mode=both
  API_URL=http://localhost:5000 python3 test_league.py --mode=both
"""

import requests
import uuid
import sys
import os
import random
import argparse
from datetime import datetime, timedelta, UTC

BASE_URL = os.environ.get("API_URL", "https://da.iceshoes.dpdns.org/")

# ── 插件常量 (与 RatingTracker.cs 一致) ──────────────
PLUGIN_DELAY = 2.0       # IsInMenu 后等 2 秒读 placement
ON_UPDATE_INTERVAL = 0.1 # OnUpdate ~100ms
MAX_RETRIES = 10         # 最多重试次数

# ── 输出 ────────────────────────────────────────────
G, R, Y, C, M, B, N = "\033[92m", "\033[91m", "\033[93m", "\033[96m", "\033[95m", "\033[1m", "\033[0m"


def ok(msg):    print(f"  {G}✓{N} {msg}")
def fail(msg):  print(f"  {R}✗{N} {msg}")
def warn(msg):  print(f"  {Y}⚠{N} {msg}")
def info(msg):  print(f"  {C}→{N} {msg}")
def header(msg):
    print(f"\n{B}{C}{'─'*55}{N}")
    print(f"  {B}{msg}{N}")
    print(f"{C}{'─'*55}{N}")
def section(msg):
    print(f"\n  {M}{B}{'═'*50}{N}")
    print(f"  {M}{B}  {msg}{N}")
    print(f"  {M}{B}{'═'*50}{N}\n")


# ── 玩家数据 ────────────────────────────────────────
HEROES = [
    ("TB_BaconShop_HERO_56", "阿莱克丝塔萨"), ("BG20_HERO_202", "阮大师"),
    ("TB_BaconShop_HERO_18", "穆克拉"), ("TB_BaconShop_HERO_55", "伊瑟拉"),
    ("BG20_HERO_101", "沃金"), ("TB_BaconShop_HERO_52", "苔丝·格雷迈恩"),
    ("TB_BaconShop_HERO_34", "奈法利安"), ("TB_BaconShop_HERO_28", "拉卡尼休"),
]


def make_players():
    return [{
        "battleTag": f"测试选手{i+1:02d}#{2000+i}",
        "displayName": f"测试选手{i+1:02d}",
        "accountIdLo": str(50000000 + i),
        "rating": 6000 + i * 100,
        "heroCardId": HEROES[i][0],
        "heroName": HEROES[i][1],
    } for i in range(8)]


# ── 纯逻辑: 判断 placement 能否上传成功 ─────────────

def can_upload_success(placement_delay, mode):
    """
    模拟插件读 placement 的结果。

    真实流程:
      淘汰 → IsInMenu → 等 2s → 读 FinalPlacement
      修复前: 读一次, null 就放弃
      修复后: null 就等 ~100ms 重试, 最多 10 次

    参数:
      placement_delay: FinalPlacement 从淘汰起多少秒后可读
      mode: "before" / "after"

    返回: (success, attempts)
    """
    wait = PLUGIN_DELAY  # 已经等了 2 秒

    if mode == "before":
        return (wait >= placement_delay, 1)

    # after: 重试最多 10 次
    for attempt in range(1, MAX_RETRIES + 1):
        if wait >= placement_delay:
            return (True, attempt)
        wait += ON_UPDATE_INTERVAL
    return (False, MAX_RETRIES)


# ── API 调用 ────────────────────────────────────────

def api(method, path, json=None, session=None):
    s = session or requests
    return s.request(method, f"{BASE_URL}{path}", json=json, timeout=10)


def register_players(players):
    header("STEP 1: 上传分数获取验证码 + 注册")
    codes = {}
    for p in players:
        resp = api("POST", "/api/plugin/upload-rating", {
            "playerId": p["battleTag"], "accountIdLo": p["accountIdLo"],
            "rating": p["rating"], "mode": "solo", "region": "CN",
        })
        if resp.status_code == 200:
            codes[p["battleTag"]] = resp.json().get("verificationCode", "")
            ok(f"{p['displayName']:12s} code={codes[p['battleTag']]}")
        else:
            fail(f"{p['displayName']:12s} {resp.status_code}")

    for p in players:
        resp = api("POST", "/api/register", {
            "battleTag": p["battleTag"],
            "verificationCode": codes.get(p["battleTag"], ""),
        })
        status = "注册成功" if resp.status_code == 200 else "已注册过" if resp.status_code == 400 else f"失败{resp.status_code}"
        (ok if resp.status_code in (200, 400) else fail)(f"{p['displayName']:12s} {status}")
    return codes


def join_queue(players, codes):
    header("STEP 2: 报名入队")
    for p in players:
        s = requests.Session()
        api("POST", "/api/login", {"battleTag": p["battleTag"], "verificationCode": codes[p["battleTag"]]}, s)
        resp = api("POST", "/api/queue/join", {}, s)
        if resp.status_code == 200:
            moved = resp.json().get("moved")
            ok(f"{p['displayName']:12s} {'→ 等待组满!' if moved else '→ 排队中'}")
        else:
            fail(f"{p['displayName']:12s} {resp.status_code}")


def verify_waiting_queue():
    header("STEP 3: 验证等待组")
    resp = api("GET", "/api/waiting-queue")
    if resp.status_code == 200:
        for g in resp.json():
            names = [p["name"] for p in g.get("players", [])]
            ok(f"等待组 {len(names)} 人: {', '.join(names)}")
        return True
    fail("无等待组")
    return False


def check_league(players):
    header("STEP 4: STEP 13 — check-league")
    game_uuid = str(uuid.uuid4())
    started_at = (datetime.now(UTC) - timedelta(minutes=5)).strftime("%Y-%m-%dT%H:%M:%SZ")
    detailed = {p["accountIdLo"]: {
        "battleTag": p["battleTag"], "displayName": p["displayName"],
        "heroCardId": p["heroCardId"], "heroName": p["heroName"],
    } for p in players}
    id_list = [p["accountIdLo"] for p in players]
    is_league = False

    for p in players:
        resp = api("POST", "/api/plugin/check-league", {
            "playerId": p["battleTag"], "accountIdLo": p["accountIdLo"],
            "gameUuid": game_uuid, "accountIdLoList": id_list,
            "players": detailed, "mode": "solo", "region": "CN", "startedAt": started_at,
        })
        if resp.status_code == 200 and resp.json().get("isLeague"):
            ok(f"{p['displayName']:12s} isLeague=true ★")
            is_league = True
    return game_uuid, is_league


def verify_active_game(game_uuid):
    header("STEP 5: 验证进入「正在进行」")
    resp = api("GET", "/api/active-games")
    if resp.status_code == 200:
        found = [g for g in resp.json() if g.get("gameUuid") == game_uuid]
        if found:
            ps = found[0].get("players", [])
            ok(f"进行中对局, {len(ps)} 名玩家")
            for gp in ps:
                info(f"  {gp.get('displayName','?'):12s} {gp.get('heroName','?')} 进行中")
            return True
    fail("未找到进行中对局")
    return False


def simulate_eliminations(game_uuid, players, mode):
    """
    模拟淘汰: 每个玩家独立判断能否上传成功, 成功则调 API。
    placement_delay 用纯随机模拟 HDT 写入时机的不确定性。
    """
    header(f"STEP 6: 模拟淘汰 (mode={mode})")

    rng = random.Random(123)

    # 淘汰顺序随机, 最先淘汰 = 第8名
    order = list(range(8))
    rng.shuffle(order)

    # placement_delay: 最先淘汰的延迟最长, 最后存活的最短
    delays = []
    for i in range(8):
        if i == 7:    delays.append(0.3)           # 第1名: 游戏结束, 立即可用
        elif i == 6:  delays.append(0.8)           # 第2名
        elif i == 0:  delays.append(rng.uniform(3.0, 5.0))  # 第8名: 最先淘汰, 最慢
        else:         delays.append(rng.uniform(0.5, 3.5))

    uploaded = failed = 0

    for order_idx, player_idx in enumerate(order):
        placement = 8 - order_idx
        p = players[player_idx]
        delay = delays[order_idx]

        success, attempts = can_upload_success(delay, mode)

        medal = "🥇" if placement == 1 else "🥈" if placement == 2 else "🥉" if placement == 3 else "  "

        if success:
            points = 9 if placement == 1 else max(1, 9 - placement)
            resp = api("POST", "/api/plugin/update-placement", {
                "playerId": p["battleTag"], "gameUuid": game_uuid,
                "accountIdLo": p["accountIdLo"], "placement": placement,
            })
            if resp.status_code in (200, 409):
                uploaded += 1
                ok(f"{medal} 第{placement}名 {p['displayName']:12s} +{points}分 (第{attempts}次)")
            else:
                failed += 1
                fail(f"{medal} 第{placement}名 {p['displayName']:12s} API错误 {resp.status_code}")
        else:
            failed += 1
            fail(f"{medal} 第{placement}名 {p['displayName']:12s} placement丢失 "
                 f"(delay={delay:.1f}s, 尝试{attempts}次)")

    return uploaded, failed


def verify_completed(game_uuid):
    header("STEP 7: 验证对局完成")

    resp = api("GET", "/api/active-games")
    if resp.status_code == 200:
        still = [g for g in resp.json() if g.get("gameUuid") == game_uuid]
        (ok if not still else warn)("已从「正在进行」移除" if not still else "仍在进行中")

    resp = api("GET", f"/api/match/{game_uuid}")
    if resp.status_code != 200:
        fail(f"查询失败 {resp.status_code}")
        return False

    match = resp.json()
    all_ok = True
    for mp in match.get("players", []):
        name, pl, pts = mp.get("displayName","?"), mp.get("placement"), mp.get("points")
        if pl is not None:
            medal = "🥇" if pl == 1 else "🥈" if pl == 2 else "🥉" if pl == 3 else "  "
            ok(f"{medal} {name:12s} 第{pl}名 {pts}分")
        else:
            fail(f"    {name:12s} placement=null")
            all_ok = False

    ended = match.get("endedAt")
    (ok if ended else warn)(f"endedAt={ended}" if ended else "endedAt=null")
    return all_ok


def run_mode(players, codes, mode):
    label = "修复前: 只读一次" if mode == "before" else "修复后: 最多重试10次"
    section(f"{mode.upper()} ({label})")

    join_queue(players, codes)
    if not verify_waiting_queue(): return False
    game_uuid, is_league = check_league(players)
    if not is_league: return False
    if not verify_active_game(game_uuid): return False

    up, fail_count = simulate_eliminations(game_uuid, players, mode)

    import time; time.sleep(1)
    ok_result = verify_completed(game_uuid)

    header(f"{mode.upper()} 总结")
    print(f"  上传: {G}{up} 成功{N}  {R}{fail_count} 失败{N}")
    print(f"  验证: {'✅ 正常' if ok_result else '❌ 异常'}\n")
    return ok_result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--mode", choices=["before","after","both"], default="both")
    parser.add_argument("--api", default=None)
    args = parser.parse_args()
    if args.api: global BASE_URL; BASE_URL = args.api

    players = make_players()

    print(f"""
{B}{C}
╔══════════════════════════════════════════════════════╗
║  HDT_BGTracker 联赛模拟测试  (placement 重试对比)   ║
║  API: {BASE_URL:44s}║
╚══════════════════════════════════════════════════════╝
{N}""")

    try: requests.get(f"{BASE_URL}/api/players", timeout=5)
    except: fail(f"无法连接 {BASE_URL}"); sys.exit(1)

    codes = register_players(players)
    if len(codes) < 8: fail("注册不完整"); sys.exit(1)

    if args.mode == "both":
        before_ok = run_mode(players, codes, "before")
        import time; time.sleep(2)
        after_ok = run_mode(players, codes, "after")
        section("对比总结")
        print(f"  {'修复前':10s} → {'✅' if before_ok else '❌ 排名丢失'}")
        print(f"  {'修复后':10s} → {'✅' if after_ok else '❌'}")
        if not before_ok and after_ok:
            print(f"\n  {G}{B}🎉 修复生效!{N}")
    else:
        ok_result = run_mode(players, codes, args.mode)
        section("结果")
        print(f"  {'✅ 通过' if ok_result else '❌ 失败'}")


if __name__ == "__main__":
    main()
