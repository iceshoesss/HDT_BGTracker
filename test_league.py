#!/usr/bin/env python3
"""
联赛全流程模拟测试 — 模拟真实插件行为

完整流程:
  报名队列 → 等待组(满8人) → STEP 13 check-league → 对局进入「正在进行」
  → 玩家按顺序被淘汰 → 每人回菜单读 FinalPlacement → 上传 update-placement
  → 8 人都提交 → 对局结束 → 出现在「最近对局」

支持两种模式对比 placement 重试机制的效果：
  --mode=before  模拟修复前：placement 读取失败直接跳过
  --mode=after   模拟修复后：placement 读取失败会重试
  --mode=both    两种模式都跑，对比结果

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


# ── 完整流程函数 ─────────────────────────────────────

def step_register_and_get_codes(players):
    """上传分数获取验证码 + 注册到网站"""
    header("STEP 1: 上传分数 + 注册")

    codes = {}
    for p in players:
        # upload-rating 获取验证码
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

    # 注册到网站
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
    """8 个玩家依次报名入队 → 满 8 人自动移入等待组"""
    header("STEP 2: 报名入队 (报名队列 → 等待组)")

    sessions = []
    for p in players:
        s = requests.Session()
        # login 获取 session
        s.post(f"{BASE_URL}/api/login", json={
            "battleTag": p["battleTag"],
            "verificationCode": codes.get(p["battleTag"], ""),
        })
        # 加入队列
        resp = s.post(f"{BASE_URL}/api/queue/join", json={}, timeout=10)
        if resp.status_code == 200:
            data = resp.json()
            moved = data.get("moved", False)
            if moved:
                ok(f"{p['displayName']:12s} → 等待组满员，移入! 🎉")
            else:
                # 查看当前队列人数
                q_resp = requests.get(f"{BASE_URL}/api/queue", timeout=5)
                q_count = len(q_resp.json()) if q_resp.status_code == 200 else "?"
                ok(f"{p['displayName']:12s} → 报名队列 ({q_count}人)")
        else:
            fail(f"{p['displayName']:12s} → {resp.status_code} {resp.text[:80]}")
        sessions.append(s)

    return sessions


def step_verify_waiting_queue():
    """验证等待组已创建"""
    header("STEP 3: 验证等待队列")

    resp = requests.get(f"{BASE_URL}/api/waiting-queue", timeout=10)
    if resp.status_code == 200:
        groups = resp.json()
        if groups:
            for g in groups:
                names = [p["name"] for p in g.get("players", [])]
                ok(f"等待组: {len(names)} 人 → {', '.join(names)}")
            return True
        else:
            fail("没有等待组!")
            return False
    else:
        fail(f"查询失败: {resp.status_code}")
        return False


def step_check_league(players):
    """
    STEP 13: 所有插件调用 check-league
    服务端匹配等待组 → 创建 league_matches → 等待组被删除
    """
    header("STEP 4: STEP 13 — 所有插件调用 check-league")

    game_uuid = str(uuid.uuid4())
    started_at = (datetime.now(UTC) - timedelta(minutes=5)).strftime("%Y-%m-%dT%H:%M:%SZ")

    # 模拟所有 8 个插件几乎同时调用 check-league
    # （真实游戏中每个玩家的插件独立触发）
    detailed_players = {}
    for p in players:
        detailed_players[p["accountIdLo"]] = {
            "battleTag": p["battleTag"],
            "displayName": p["displayName"],
            "heroCardId": p["heroCardId"],
            "heroName": p["heroName"],
        }

    account_id_list = [p["accountIdLo"] for p in players]

    is_league_confirmed = False
    for i, p in enumerate(players):
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
            if resp.status_code == 200:
                data = resp.json()
                if data.get("isLeague"):
                    ok(f"{p['displayName']:12s} → isLeague=true ★")
                    is_league_confirmed = True
                else:
                    info(f"{p['displayName']:12s} → isLeague=false")
            else:
                fail(f"{p['displayName']:12s} → {resp.status_code}")
        except Exception as e:
            fail(f"{p['displayName']:12s} → {e}")

    return game_uuid, is_league_confirmed


def step_verify_active_game(game_uuid, players):
    """验证对局进入「正在进行」列表"""
    header("STEP 5: 验证对局进入「正在进行」")

    resp = requests.get(f"{BASE_URL}/api/active-games", timeout=10)
    if resp.status_code != 200:
        fail(f"查询失败: {resp.status_code}")
        return False

    games = resp.json()
    found = [g for g in games if g.get("gameUuid") == game_uuid]

    if not found:
        fail(f"未找到对局 {game_uuid[:8]}... (进行中共 {len(games)} 局)")
        return False

    game = found[0]
    game_players = game.get("players", [])
    ok(f"找到进行中对局! {len(game_players)} 名玩家")

    for gp in game_players:
        name = gp.get("displayName", "?")
        hero = gp.get("heroName", "?")
        placement = gp.get("placement")
        # 正在进行中所有人的 placement 应该是 null
        status = "进行中" if placement is None else f"已死亡(第{placement}名)"
        info(f"  {name:12s}  英雄={hero:10s}  {status}")

    return True


def step_simulate_eliminations(game_uuid, players, mode):
    """
    模拟游戏过程：玩家按随机顺序被淘汰
    
    真实场景：
    1. 玩家在游戏中被淘汰 → 回到菜单 → IsInMenu=true
    2. 插件等 2 秒后读取 FinalPlacement
    3. 如果 placement 有值 → POST /api/plugin/update-placement
    4. 修复前: placement=null → 跳过，排名丢失
    5. 修复后: placement=null → 重试，最终上传成功
    
    模拟 placement 延迟:
    - 第8名(最先淘汰): FinalPlacement 延迟最长(~6s)，游戏还在进行
    - 第1名(最后存活): FinalPlacement 几乎立即可用(~0.5s)，游戏已结束
    """
    header(f"STEP 6: 模拟游戏过程 — 玩家淘汰 (mode={mode})")

    # 固定种子保证两种模式淘汰顺序一致
    rng = random.Random(123)

    # 淘汰顺序: indices[0] = 第8名(最先淘汰), indices[7] = 第1名(最后存活)
    elimination_order = list(range(8))
    rng.shuffle(elimination_order)

    # placement 延迟: 最先淘汰的延迟最长
    placement_delays = []
    for i in range(8):
        if i == 7:      # 第1名: 游戏结束，立即可用
            placement_delays.append(0.5)
        elif i == 6:    # 第2名: 几乎同时
            placement_delays.append(1.0)
        elif i == 0:    # 第8名: 最先淘汰，延迟最长
            placement_delays.append(6.0)
        else:
            placement_delays.append(rng.uniform(2.0, 5.0))

    # 打印淘汰计划
    info("淘汰计划:")
    for order_idx, player_idx in enumerate(elimination_order):
        placement = 8 - order_idx
        p = players[player_idx]
        delay = placement_delays[order_idx]
        info(f"  第{placement}名 {p['displayName']:12s} (placement_delay={delay:.1f}s)")

    # ── 开始模拟 ──
    print()
    info("游戏进行中，等待玩家被淘汰...")

    # 每个玩家的状态
    player_states = {}
    for i in range(8):
        player_states[i] = {
            "eliminated": False,
            "eliminated_at": None,
            "uploaded": False,
            "attempts": 0,
        }

    total_uploaded = 0
    total_failed = 0
    game_start = time.time()
    max_game_time = 15  # 最长模拟 15 秒
    check_interval = 0.25  # 每 0.25 秒检查

    while True:
        elapsed = time.time() - game_start

        if elapsed > max_game_time:
            warn("模拟超时，结束")
            break

        # 检查哪些玩家该被淘汰了
        # 淘汰节奏: 每隔 ~1.2 秒淘汰一个玩家
        for order_idx, player_idx in enumerate(elimination_order):
            elimination_time = order_idx * 1.2 + 0.5  # 秒

            if elapsed >= elimination_time and not player_states[player_idx]["eliminated"]:
                player_states[player_idx]["eliminated"] = True
                player_states[player_idx]["eliminated_at"] = time.time()
                p = players[player_idx]
                placement = 8 - order_idx
                info(f"  ⚰️  {p['displayName']:12s} 被淘汰 (第{placement}名) "
                     f"[placement 将在 {placement_delays[order_idx]:.1f}s 后可用]")

        # 已淘汰的玩家尝试上传 placement
        for i in range(8):
            state = player_states[i]
            if state["eliminated"] and not state["uploaded"]:
                p = players[i]
                time_since_elim = time.time() - state["eliminated_at"]
                state["attempts"] += 1

                # 找到这个玩家的 placement 和 delay
                order_idx = elimination_order.index(i)
                placement = 8 - order_idx
                delay = placement_delays[order_idx]

                # 模拟 FinalPlacement 是否可用
                placement_ready = time_since_elim >= delay

                if not placement_ready:
                    # FinalPlacement 还没写入
                    if mode == "before":
                        # 修复前: placement=null → 跳过，不再重试
                        state["uploaded"] = True  # 标记为"已处理"
                        total_failed += 1
                        fail(f"  {p['displayName']:12s} ✗ placement=null, "
                             f"跳过不再重试 (delay={delay:.1f}s, 已等{time_since_elim:.1f}s)")
                    else:
                        # 修复后: placement=null → 跳过本轮，下轮重试
                        pass  # 不做任何事，等下一轮循环
                else:
                    # FinalPlacement 可用，上传
                    points = 9 if placement == 1 else max(1, 9 - placement)
                    payload = {
                        "playerId": p["battleTag"],
                        "gameUuid": game_uuid,
                        "accountIdLo": p["accountIdLo"],
                        "placement": placement,
                    }
                    try:
                        resp = requests.post(
                            f"{BASE_URL}/api/plugin/update-placement",
                            json=payload, timeout=10,
                        )
                        if resp.status_code == 200:
                            data = resp.json()
                            finalized = data.get("finalized", False)
                            state["uploaded"] = True
                            total_uploaded += 1
                            ok(f"  {p['displayName']:12s} ✓ 第{placement}名 +{points}分"
                               f"{' 🏁对局结束' if finalized else ''}"
                               f" (第{state['attempts']}次尝试)")
                        elif resp.status_code == 409:
                            state["uploaded"] = True
                            total_uploaded += 1
                            info(f"  {p['displayName']:12s} 已提交过(幂等)")
                        else:
                            state["uploaded"] = True
                            total_failed += 1
                            fail(f"  {p['displayName']:12s} ✗ HTTP {resp.status_code}: {resp.text[:60]}")
                    except Exception as e:
                        state["uploaded"] = True
                        total_failed += 1
                        fail(f"  {p['displayName']:12s} ✗ 异常: {e}")

        # 检查是否所有人都已处理
        all_eliminated = all(player_states[i]["eliminated"] for i in range(8))
        all_processed = all(player_states[i]["uploaded"] for i in range(8) if player_states[i]["eliminated"])
        if all_eliminated and all_processed:
            break

        time.sleep(check_interval)

    # 处理超时未处理的
    for i in range(8):
        if player_states[i]["eliminated"] and not player_states[i]["uploaded"]:
            p = players[i]
            total_failed += 1
            fail(f"  {p['displayName']:12s} ✗ 超时 (尝试{player_states[i]['attempts']}次)")

    # 打印汇总
    print()
    info("淘汰结果汇总:")
    for order_idx, player_idx in enumerate(elimination_order):
        p = players[player_idx]
        placement = 8 - order_idx
        state = player_states[player_idx]
        delay = placement_delays[order_idx]

        if state["uploaded"] and total_uploaded > 0 and total_failed == 0:
            status = f"{GREEN}✓ 已上传{RESET}"
        elif state["uploaded"] and not any(
            not player_states[pi]["uploaded"]
            for pi in range(8) if player_states[pi]["eliminated"]
        ):
            status = f"{GREEN}✓ 已上传{RESET}"
        else:
            status = f"{RED}✗ 丢失{RESET}"

        medal = "🥇" if placement == 1 else "🥈" if placement == 2 else "🥉" if placement == 3 else "  "
        print(f"    {medal} 第{placement}名 {p['displayName']:12s} "
              f"delay={delay:.1f}s 尝试{state['attempts']}次 {status}")

    return total_uploaded, total_failed, game_uuid


def step_verify_completed(game_uuid, players):
    """验证对局从「正在进行」移除，出现在「最近对局」中"""
    header("STEP 7: 验证对局完成")

    # 检查是否从进行中移除
    resp = requests.get(f"{BASE_URL}/api/active-games", timeout=10)
    if resp.status_code == 200:
        still_active = [g for g in resp.json() if g.get("gameUuid") == game_uuid]
        if not still_active:
            ok("对局已从「正在进行」移除")
        else:
            warn("对局仍在「正在进行」列表（可能有玩家未提交）")

    # 检查对局详情
    time.sleep(0.5)
    resp = requests.get(f"{BASE_URL}/api/match/{game_uuid}", timeout=10)
    if resp.status_code != 200:
        fail(f"查询对局失败: {resp.status_code}")
        return False, {}

    match = resp.json()
    match_players = match.get("players", [])

    results = {}
    all_ok = True

    for mp in match_players:
        name = mp.get("displayName", "?")
        placement = mp.get("placement")
        points = mp.get("points")
        aid = mp.get("accountIdLo")

        if placement is not None:
            medal = "🥇" if placement == 1 else "🥈" if placement == 2 else "🥉" if placement == 3 else "  "
            ok(f"{medal} {name:12s} → 第{placement}名 {points}分")
            results[aid] = (True, placement)
        else:
            fail(f"    {name:12s} → placement=null (排名丢失!)")
            results[aid] = (False, None)
            all_ok = False

    ended_at = match.get("endedAt")
    if ended_at:
        ok(f"对局已结束: endedAt={ended_at}")
    else:
        warn("endedAt 为 null（部分玩家未提交）")

    return all_ok, results


def run_single_mode(players, codes, mode):
    """运行单个模式的完整测试"""
    section(f"模式: {mode.upper()} "
            f"{'(修复前: placement 失败不重试)' if mode == 'before' else '(修复后: placement 失败重试)'}")

    # STEP 2: 报名入队
    step_join_queue(players, codes)

    # STEP 3: 验证等待组
    if not step_verify_waiting_queue():
        fail("等待组未创建，终止测试")
        return False

    # STEP 4: STEP 13 check-league
    game_uuid, is_league = step_check_league(players)
    if not is_league:
        fail("联赛匹配失败，终止测试")
        return False

    # STEP 5: 验证进入正在进行
    if not step_verify_active_game(game_uuid, players):
        fail("对局未进入「正在进行」，终止测试")
        return False

    # STEP 6: 模拟淘汰
    uploaded, failed, game_uuid = step_simulate_eliminations(game_uuid, players, mode)

    # STEP 7: 验证完成
    time.sleep(1)
    all_ok, verify_map = step_verify_completed(game_uuid, players)

    # 总结
    header(f"{mode.upper()} 模式总结")
    print(f"  插件上传: {GREEN}{uploaded} 成功{RESET}  {RED}{failed} 失败{RESET}")
    print(f"  服务端验证: {'✅ 全部正常' if all_ok else '❌ 有玩家排名丢失'}")
    print()

    return all_ok


def cleanup_before_mode():
    """清理 before 模式可能残留的等待组"""
    try:
        resp = requests.get(f"{BASE_URL}/api/waiting-queue", timeout=5)
        if resp.status_code == 200 and resp.json():
            info("清理 before 模式残留等待组...")
            # 等待组无法通过 API 清除，但 check-league 会删除它
    except:
        pass


def main():
    parser = argparse.ArgumentParser(description="联赛全流程模拟测试 — placement 重试对比")
    parser.add_argument("--mode", choices=["before", "after", "both"], default="both",
                        help="测试模式")
    parser.add_argument("--api", default=None, help="API 地址")
    args = parser.parse_args()

    if args.api:
        global BASE_URL
        BASE_URL = args.api

    random.seed(42)
    players = make_players()

    print(f"""
{BOLD}{CYAN}
╔══════════════════════════════════════════════════════╗
║  HDT_BGTracker 联赛模拟测试  (placement 重试对比)   ║
║  API: {BASE_URL:44s}║
║  模式: {args.mode:44s}║
╚══════════════════════════════════════════════════════╝
{RESET}""")

    # 检查 API 可达
    try:
        requests.get(f"{BASE_URL}/api/players", timeout=5)
    except requests.ConnectionError:
        fail(f"无法连接到 {BASE_URL}")
        sys.exit(1)

    # STEP 1: 注册（所有模式共享）
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
            print(f"  {GREEN}{BOLD}🎉 修复生效! 修复后所有玩家排名正常上传{RESET}")
        elif before_ok and after_ok:
            print(f"  {YELLOW}两种模式都成功 (可能本次 placement delay 较短){RESET}")
        elif not before_ok and not after_ok:
            print(f"  {RED}两种模式都有问题，需要进一步排查{RESET}")
        print()

    else:
        ok_result = run_single_mode(players, codes, args.mode)
        section("测试结果")
        if ok_result:
            print(f"  {GREEN}{BOLD}✅ 测试通过{RESET}")
        else:
            print(f"  {RED}{BOLD}❌ 测试失败{RESET}")
        print()


if __name__ == "__main__":
    main()
