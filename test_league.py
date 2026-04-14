#!/usr/bin/env python3
"""
联赛全流程模拟测试 — 模拟真实插件行为

完整流程:
  报名队列 → 等待组(满8人) → STEP 13 check-league → 对局进入「正在进行」
  → 玩家按顺序被淘汰 → 每人独立回菜单 → 独立检查 placement → 上传
  → 8 人都提交 → 对局结束

支持两种模式对比 placement 重试机制:
  --mode=before  模拟修复前: placement 读取失败直接跳过
  --mode=after   模拟修复后: placement 读取失败会重试 (最多 10 次)
  --mode=both    两种模式都跑, 对比结果

用法:
  python3 test_league.py --mode=before
  python3 test_league.py --mode=after
  python3 test_league.py --mode=both
  API_URL=http://localhost:5000 python3 test_league.py --mode=both
"""

import requests
import json
import time
import uuid
import sys
import os
import random
import argparse
from datetime import datetime, timedelta, UTC

BASE_URL = os.environ.get("API_URL", "https://da.iceshoes.dpdns.org/")

# ── 颜色输出 ────────────────────────────────────────
GREEN = "\033[92m"
RED = "\033[91m"
YELLOW = "\033[93m"
CYAN = "\033[96m"
MAGENTA = "\033[95m"
RESET = "\033[0m"
BOLD = "\033[1m"


def ok(msg):
    print(f"  {GREEN}✓{RESET} {msg}")


def fail(msg):
    print(f"  {RED}✗{RESET} {msg}")


def warn(msg):
    print(f"  {YELLOW}⚠{RESET} {msg}")


def info(msg):
    print(f"  {CYAN}→{RESET} {msg}")


def header(msg):
    print(f"\n{BOLD}{CYAN}{'─'*55}{RESET}")
    print(f"  {BOLD}{msg}{RESET}")
    print(f"{CYAN}{'─'*55}{RESET}")


def section(msg):
    print(f"\n  {MAGENTA}{BOLD}{'═'*50}{RESET}")
    print(f"  {MAGENTA}{BOLD}  {msg}{RESET}")
    print(f"  {MAGENTA}{BOLD}{'═'*50}{RESET}\n")


# ── 英雄数据 ────────────────────────────────────────
HEROES_POOL = [
    ("TB_BaconShop_HERO_56", "阿莱克丝塔萨"),
    ("BG20_HERO_202", "阮大师"),
    ("TB_BaconShop_HERO_18", "穆克拉"),
    ("TB_BaconShop_HERO_55", "伊瑟拉"),
    ("BG20_HERO_101", "沃金"),
    ("TB_BaconShop_HERO_52", "苔丝·格雷迈恩"),
    ("TB_BaconShop_HERO_34", "奈法利安"),
    ("TB_BaconShop_HERO_28", "拉卡尼休"),
]


def make_players():
    """创建 8 个模拟玩家"""
    players = []
    for i in range(8):
        hero_id, hero_name = HEROES_POOL[i]
        players.append({
            "battleTag": f"测试选手{i+1:02d}#{2000 + i}",
            "displayName": f"测试选手{i+1:02d}",
            "accountIdLo": str(50000000 + i),
            "rating": 6000 + i * 100,
            "heroCardId": hero_id,
            "heroName": hero_name,
            "mode": "solo",
            "region": "CN",
        })
    return players


# ── 真实插件模拟 ─────────────────────────────────────

# 插件常量 (与 RatingTracker.cs 一致)
PLUGIN_DELAY_SECONDS = 2    # IsInMenu 后等 2 秒读 placement
ON_UPDATE_INTERVAL = 0.1    # OnUpdate 每 ~100ms 调用一次
MAX_RETRIES = 10            # 修复后: placement 失败最多重试次数


def simulate_single_plugin(player, game_uuid, placement, placement_delay, mode):
    """
    模拟一个玩家的插件行为。

    真实流程:
    1. 玩家被淘汰 → 回到菜单 → IsInMenu = true
    2. 插件记录 _gameEndTime, 等 2 秒
    3. 读 FinalPlacement
       - placement_delay: FinalPlacement 从淘汰时刻起多少秒后才可读
       - 取决于 HDT 何时写入该值 (自然淘汰 vs 投降等不同情况)
    4. 修复前: placement=null → 跳过, 放弃
       修复后: placement=null → 等下一个 OnUpdate 周期(~100ms)重试, 最多 10 次

    参数:
        player: 玩家信息 dict
        game_uuid: 对局 UUID
        placement: 排名 1-8
        placement_delay: FinalPlacement 可用延迟 (秒), 从淘汰时算起
        mode: "before" 或 "after"

    返回: (success: bool, attempts: int, reason: str)
    """
    name = player["displayName"]

    # ── 阶段 1: 淘汰 → 回到菜单 → IsInMenu = true ──
    # 插件记录 _gameEndTime, 下一个 OnUpdate 返回 (等 2 秒)
    # 这里不需要模拟, 直接跳到 2 秒后

    # ── 阶段 2: 2 秒后开始尝试读 placement ──
    # 修复前: 只读一次
    # 修复后: 读失败就等 ~100ms 重试, 最多 10 次

    total_wait = PLUGIN_DELAY_SECONDS  # 已经等了 2 秒

    for attempt in range(1 if mode == "before" else MAX_RETRIES + 1):
        if mode == "after" and attempt > 0:
            total_wait += ON_UPDATE_INTERVAL

        # 检查 FinalPlacement 是否已可用
        placement_ready = total_wait >= placement_delay

        if placement_ready:
            # 上传
            points = 9 if placement == 1 else max(1, 9 - placement)
            payload = {
                "playerId": player["battleTag"],
                "gameUuid": game_uuid,
                "accountIdLo": player["accountIdLo"],
                "placement": placement,
            }
            try:
                resp = requests.post(
                    f"{BASE_URL}/api/plugin/update-placement",
                    json=payload, timeout=10,
                )
                if resp.status_code in (200, 409):
                    return True, attempt + 1, f"第{placement}名 +{points}分"
                else:
                    return False, attempt + 1, f"HTTP {resp.status_code}"
            except Exception as e:
                return False, attempt + 1, f"异常: {e}"

        # placement 还没好
        if mode == "before":
            # 修复前: 只读一次, 放弃
            return False, 1, f"placement=null, 放弃 (delay={placement_delay:.1f}s > 等待{PLUGIN_DELAY_SECONDS}s)"
        # else: 修复后, 继续下一轮循环

    # 修复后: 重试耗尽
    return False, MAX_RETRIES, f"重试耗尽 (delay={placement_delay:.1f}s, 总等待{total_wait:.1f}s)"


# ── 流程函数 ────────────────────────────────────────

def step_register_and_get_codes(players):
    """上传分数获取验证码 + 注册到网站"""
    header("STEP 1: 上传分数 + 注册")

    codes = {}
    for p in players:
        payload = {
            "playerId": p["battleTag"],
            "accountIdLo": p["accountIdLo"],
            "rating": p["rating"],
            "mode": "solo",
            "region": "CN",
        }
        try:
            resp = requests.post(f"{BASE_URL}/api/plugin/upload-rating", json=payload, timeout=10)
            if resp.status_code == 200:
                data = resp.json()
                codes[p["battleTag"]] = data.get("verificationCode", "")
                ok(f"{p['displayName']:12s} rating={p['rating']}  code={codes[p['battleTag']]}")
            else:
                fail(f"{p['displayName']:12s} upload-rating → {resp.status_code}")
        except Exception as e:
            fail(f"{p['displayName']:12s} → {e}")

    header("STEP 1b: 注册到网站")
    for p in players:
        try:
            resp = requests.post(f"{BASE_URL}/api/register", json={
                "battleTag": p["battleTag"],
                "verificationCode": codes.get(p["battleTag"], ""),
            }, timeout=10)
            if resp.status_code == 200:
                ok(f"{p['displayName']:12s} 注册成功")
            elif resp.status_code == 400 and "已" in resp.json().get("error", ""):
                info(f"{p['displayName']:12s} 已注册过")
        except Exception as e:
            fail(f"{p['displayName']:12s} → {e}")

    return codes


def step_join_queue(players, codes):
    """8 个玩家依次报名入队"""
    header("STEP 2: 报名入队")

    for p in players:
        s = requests.Session()
        s.post(f"{BASE_URL}/api/login", json={
            "battleTag": p["battleTag"],
            "verificationCode": codes.get(p["battleTag"], ""),
        })
        resp = s.post(f"{BASE_URL}/api/queue/join", json={}, timeout=10)
        if resp.status_code == 200:
            data = resp.json()
            if data.get("moved"):
                ok(f"{p['displayName']:12s} → 等待组满员! 🎉")
            else:
                q_resp = requests.get(f"{BASE_URL}/api/queue", timeout=5)
                q_count = len(q_resp.json()) if q_resp.status_code == 200 else "?"
                ok(f"{p['displayName']:12s} → 报名队列 ({q_count}人)")
        else:
            fail(f"{p['displayName']:12s} → {resp.status_code}")


def step_verify_waiting_queue():
    """验证等待组"""
    header("STEP 3: 验证等待队列")
    resp = requests.get(f"{BASE_URL}/api/waiting-queue", timeout=10)
    if resp.status_code == 200:
        groups = resp.json()
        if groups:
            for g in groups:
                names = [p["name"] for p in g.get("players", [])]
                ok(f"等待组: {len(names)} 人 → {', '.join(names)}")
            return True
    fail("没有等待组!")
    return False


def step_check_league(players):
    """STEP 13: 插件调用 check-league"""
    header("STEP 4: STEP 13 — 插件调用 check-league")

    game_uuid = str(uuid.uuid4())
    started_at = (datetime.now(UTC) - timedelta(minutes=5)).strftime("%Y-%m-%dT%H:%M:%SZ")

    detailed_players = {}
    for p in players:
        detailed_players[p["accountIdLo"]] = {
            "battleTag": p["battleTag"],
            "displayName": p["displayName"],
            "heroCardId": p["heroCardId"],
            "heroName": p["heroName"],
        }

    account_id_list = [p["accountIdLo"] for p in players]
    is_league = False

    for p in players:
        payload = {
            "playerId": p["battleTag"],
            "accountIdLo": p["accountIdLo"],
            "gameUuid": game_uuid,
            "accountIdLoList": account_id_list,
            "players": detailed_players,
            "mode": "solo",
            "region": "CN",
            "startedAt": started_at,
        }
        try:
            resp = requests.post(f"{BASE_URL}/api/plugin/check-league", json=payload, timeout=10)
            if resp.status_code == 200 and resp.json().get("isLeague"):
                ok(f"{p['displayName']:12s} → isLeague=true ★")
                is_league = True
        except Exception as e:
            fail(f"{p['displayName']:12s} → {e}")

    return game_uuid, is_league


def step_verify_active_game(game_uuid, players):
    """验证对局进入「正在进行」"""
    header("STEP 5: 验证对局进入「正在进行」")

    resp = requests.get(f"{BASE_URL}/api/active-games", timeout=10)
    if resp.status_code != 200:
        fail(f"查询失败: {resp.status_code}")
        return False

    found = [g for g in resp.json() if g.get("gameUuid") == game_uuid]
    if not found:
        fail(f"未找到对局 {game_uuid[:8]}...")
        return False

    game_players = found[0].get("players", [])
    ok(f"找到进行中对局! {len(game_players)} 名玩家")
    for gp in game_players:
        info(f"  {gp.get('displayName','?'):12s}  英雄={gp.get('heroName','?'):10s}  进行中")
    return True


def step_simulate_game(game_uuid, players, mode):
    """
    模拟游戏过程 — 每个玩家独立模拟

    真实场景:
    1. 游戏进行中, 玩家按随机顺序被淘汰
    2. 每个玩家被淘汰后独立回到菜单
    3. 每个玩家的插件独立检测到 IsInMenu, 独立读 placement, 独立上传
    4. 各玩家之间完全独立, 互不影响
    """
    header(f"STEP 6: 模拟游戏淘汰 (mode={mode})")

    # 固定种子保证两种模式一致
    rng = random.Random(123)

    # 淘汰顺序: [7] = 第8名(最先淘汰), [0] = 第1名(最后存活)
    elimination_order = list(range(8))
    rng.shuffle(elimination_order)

    # 排名分配: 最先淘汰 = 第8名
    player_placements = {}
    for order_idx, player_idx in enumerate(elimination_order):
        player_placements[player_idx] = 8 - order_idx

    # 淘汰间隔: 每隔 ~1.5 秒淘汰一个
    elimination_times = [i * 1.5 + 0.5 for i in range(8)]

    # placement 延迟: 每个玩家不同
    # - 第8名(最先淘汰): 游戏刚开始, FinalPlacement 可能延迟较长
    # - 第1名(最后存活): 游戏完全结束, FinalPlacement 立即可用
    placement_delays = {}
    for order_idx, player_idx in enumerate(elimination_order):
        placement = 8 - order_idx
        if placement == 1:
            placement_delays[player_idx] = 0.3   # 最后存活, 游戏结束
        elif placement == 2:
            placement_delays[player_idx] = 0.8   # 倒数第二个
        elif placement == 8:
            placement_delays[player_idx] = 4.0   # 最先淘汰, 延迟最长
        else:
            placement_delays[player_idx] = rng.uniform(1.0, 3.5)

    # 打印淘汰计划
    info("淘汰计划:")
    for order_idx, player_idx in enumerate(elimination_order):
        placement = 8 - order_idx
        p = players[player_idx]
        delay = placement_delays[player_idx]
        elim_t = elimination_times[order_idx]
        info(f"  第{placement}名 {p['displayName']:12s} "
             f"淘汰t={elim_t:.1f}s  placement_delay={delay:.1f}s")

    print()
    info("游戏开始, 玩家独立淘汰...")

    # ── 模拟: 每个玩家独立处理 ──
    # 按淘汰顺序, 等待对应时间后, 模拟该玩家的插件行为
    results = {}  # player_idx -> (success, attempts, reason)
    total_uploaded = 0
    total_failed = 0

    game_start = time.time()

    for order_idx, player_idx in enumerate(elimination_order):
        placement = player_placements[player_idx]
        elim_time = elimination_times[order_idx]
        delay = placement_delays[player_idx]
        p = players[player_idx]

        # 等到这个玩家该被淘汰的时间
        target_time = game_start + elim_time
        now = time.time()
        if target_time > now:
            time.sleep(target_time - now)

        info(f"  ⚰️  {p['displayName']:12s} 被淘汰 (第{placement}名)")

        # 这个玩家回到菜单, IsInMenu = true
        # 插件: 等 2 秒 → 尝试读 placement → 上传
        # 模拟这个过程 (不实际等待, 直接调用模拟函数)
        success, attempts, reason = simulate_single_plugin(
            player=p,
            game_uuid=game_uuid,
            placement=placement,
            placement_delay=delay,
            mode=mode,
        )

        results[player_idx] = (success, attempts, reason)

        if success:
            total_uploaded += 1
            ok(f"  {p['displayName']:12s} ✓ {reason} (第{attempts}次尝试)")
        else:
            total_failed += 1
            fail(f"  {p['displayName']:12s} ✗ {reason}")

    # 打印汇总
    print()
    info("淘汰结果汇总:")
    for order_idx, player_idx in enumerate(elimination_order):
        p = players[player_idx]
        placement = player_placements[player_idx]
        delay = placement_delays[player_idx]
        success, attempts, reason = results[player_idx]

        medal = "🥇" if placement == 1 else "🥈" if placement == 2 else "🥉" if placement == 3 else "  "
        status = f"{GREEN}✓{RESET}" if success else f"{RED}✗{RESET}"
        print(f"    {medal} 第{placement}名 {p['displayName']:12s} "
              f"delay={delay:.1f}s 尝试{attempts}次 {status} {reason}")

    return total_uploaded, total_failed


def step_verify_completed(game_uuid, players):
    """验证对局完成"""
    header("STEP 7: 验证对局完成")

    # 检查从进行中移除
    resp = requests.get(f"{BASE_URL}/api/active-games", timeout=10)
    if resp.status_code == 200:
        still_active = [g for g in resp.json() if g.get("gameUuid") == game_uuid]
        if not still_active:
            ok("对局已从「正在进行」移除")
        else:
            warn("对局仍在「正在进行」(可能有玩家未提交)")

    # 检查对局详情
    time.sleep(0.5)
    resp = requests.get(f"{BASE_URL}/api/match/{game_uuid}", timeout=10)
    if resp.status_code != 200:
        fail(f"查询对局失败: {resp.status_code}")
        return False

    match = resp.json()
    match_players = match.get("players", [])

    all_ok = True
    for mp in match_players:
        name = mp.get("displayName", "?")
        placement = mp.get("placement")
        points = mp.get("points")

        if placement is not None:
            medal = "🥇" if placement == 1 else "🥈" if placement == 2 else "🥉" if placement == 3 else "  "
            ok(f"{medal} {name:12s} → 第{placement}名 {points}分")
        else:
            fail(f"    {name:12s} → placement=null (丢失!)")
            all_ok = False

    ended_at = match.get("endedAt")
    if ended_at:
        ok(f"对局已结束: endedAt={ended_at}")
    else:
        warn("endedAt 为 null")

    return all_ok


def run_single_mode(players, codes, mode):
    """运行单个模式"""
    section(f"模式: {mode.upper()} "
            f"{'(修复前: 只读一次 placement)' if mode == 'before' else '(修复后: 最多重试 10 次)'}")

    step_join_queue(players, codes)

    if not step_verify_waiting_queue():
        fail("等待组未创建")
        return False

    game_uuid, is_league = step_check_league(players)
    if not is_league:
        fail("联赛匹配失败")
        return False

    if not step_verify_active_game(game_uuid, players):
        fail("未进入进行中")
        return False

    uploaded, failed = step_simulate_game(game_uuid, players, mode)

    time.sleep(1)
    all_ok = step_verify_completed(game_uuid, players)

    header(f"{mode.upper()} 模式总结")
    print(f"  插件上传: {GREEN}{uploaded} 成功{RESET}  {RED}{failed} 失败{RESET}")
    print(f"  服务端验证: {'✅ 全部正常' if all_ok else '❌ 有排名丢失'}")
    print()

    return all_ok


def main():
    parser = argparse.ArgumentParser(description="联赛模拟测试 — placement 重试对比")
    parser.add_argument("--mode", choices=["before", "after", "both"], default="both")
    parser.add_argument("--api", default=None)
    args = parser.parse_args()

    if args.api:
        global BASE_URL
        BASE_URL = args.api

    players = make_players()

    print(f"""
{BOLD}{CYAN}
╔══════════════════════════════════════════════════════╗
║  HDT_BGTracker 联赛模拟测试  (placement 重试对比)   ║
║  API: {BASE_URL:44s}║
║  模式: {args.mode:44s}║
╚══════════════════════════════════════════════════════╝
{RESET}""")

    try:
        requests.get(f"{BASE_URL}/api/players", timeout=5)
    except requests.ConnectionError:
        fail(f"无法连接到 {BASE_URL}")
        sys.exit(1)

    codes = step_register_and_get_codes(players)
    if len(codes) < 8:
        fail("部分玩家注册失败")
        sys.exit(1)

    if args.mode == "both":
        before_ok = run_single_mode(players, codes, "before")
        time.sleep(2)
        after_ok = run_single_mode(players, codes, "after")

        section("对比总结")
        print(f"  {'修复前 (before)':20s} → {'✅ 全部上传' if before_ok else '❌ 部分排名丢失'}")
        print(f"  {'修复后 (after)':20s} → {'✅ 全部上传' if after_ok else '❌ 部分排名丢失'}")
        print()
        if not before_ok and after_ok:
            print(f"  {GREEN}{BOLD}🎉 修复生效!{RESET}")
        elif before_ok and after_ok:
            print(f"  {YELLOW}两种模式都成功{RESET}")
        elif not before_ok and not after_ok:
            print(f"  {RED}两种模式都有问题{RESET}")
        print()
    else:
        ok_result = run_single_mode(players, codes, args.mode)
        section("测试结果")
        print(f"  {GREEN}{BOLD}✅ 通过{RESET}" if ok_result else f"  {RED}{BOLD}❌ 失败{RESET}")


if __name__ == "__main__":
    main()
