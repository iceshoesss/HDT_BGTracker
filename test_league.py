#!/usr/bin/env python3
"""
联赛全流程模拟测试 — 模拟真实插件行为

支持两种模式对比 placement 重试机制的效果：
  --mode=before  模拟修复前：placement 读取失败直接跳过
  --mode=after   模拟修复后：placement 读取失败会重试

用法:
  python3 test_league.py --mode=before   # 模拟修复前（预期：部分玩家排名丢失）
  python3 test_league.py --mode=after    # 模拟修复后（预期：所有玩家排名正常）
  python3 test_league.py --mode=both     # 两种模式都跑，对比结果
  API_URL=http://localhost:5000 python3 test_league.py --mode=both

场景说明：
  - 8 个玩家报名 → 等待组满 → STEP 13 联赛匹配
  - 模拟游戏过程：玩家按随机顺序被淘汰
  - 每个玩家被淘汰后回到菜单，插件尝试读取 FinalPlacement 并上传
  - 修复前：被淘汰的玩家 FinalPlacement 可能为 null → 跳过 → 排名丢失
  - 修复后：FinalPlacement 为 null 时重试 → 最终成功上传
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


# ── 8 个模拟玩家 ────────────────────────────────────
# 使用真实英雄数据
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

random.seed(42)


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


# ── HDT 插件模拟器 ──────────────────────────────────

class HDTPluginSimulator:
    """
    模拟 HDT 插件的行为：
    - 游戏进行中：缓存 playerId, accountIdLo, gameUuid
    - STEP 13：调用 check-league
    - 游戏结束（玩家被淘汰/游戏结束）：
      读取 FinalPlacement，上传到 /api/plugin/update-placement
    
    placement_available_delay: 模拟 FinalPlacement 可用的延迟（秒）
      - 游戏最后结束的玩家（第1名）：延迟短（~1秒），因为游戏已经结束
      - 中途被淘汰的玩家：延迟长（~5秒+），因为游戏还在进行中
    """

    def __init__(self, player, game_uuid, started_at, mode="after", total_players=8):
        self.player = player
        self.game_uuid = game_uuid
        self.started_at = started_at
        self.mode = mode  # "before" 或 "after"
        self.total_players = total_players

        # 插件缓存
        self.player_id = player["battleTag"]
        self.account_id_lo = player["accountIdLo"]
        self.is_league_game = False
        self.verification_code = None

        # placement 模拟
        self.placement = None
        self._placement_available = False
        self._placement_delay = 0  # placement 可用前的延迟（秒）
        self._eliminated_at = None  # 被淘汰的时间戳

    def set_elimination_order(self, placement, eliminated_at, placement_delay):
        """
        设置该玩家的淘汰顺序
        placement: 最终排名 1-8
        eliminated_at: 被淘汰的时间戳
        placement_delay: FinalPlacement 可用的延迟（模拟 HDT 写入 timing）
        """
        self.placement = placement
        self._eliminated_at = eliminated_at
        self._placement_delay = placement_delay

    def check_league(self):
        """模拟 STEP 13 时调用 check-league"""
        payload = {
            "playerId": self.player_id,
            "gameUuid": self.game_uuid,
            "accountIdLo": self.account_id_lo,
            "accountIdLoList": [],  # 服务端不校验这个
            "mode": self.player["mode"],
            "region": self.player["region"],
            "startedAt": self.started_at,
        }

        try:
            resp = requests.post(f"{BASE_URL}/api/plugin/check-league", json=payload, timeout=10)
            if resp.status_code == 200:
                data = resp.json()
                self.is_league_game = data.get("isLeague", False)
                self.verification_code = data.get("verificationCode")
                return True
        except Exception as e:
            print(f"    check-league 异常: {e}")
        return False

    def try_upload_placement(self, elapsed_since_elimination):
        """
        模拟插件尝试上传 placement
        
        elapsed_since_elimination: 从被淘汰到现在过了多少秒
        
        返回: (uploaded: bool, reason: str)
        """
        # 模拟 FinalPlacement 是否已可用
        placement_ready = elapsed_since_elimination >= self._placement_delay

        if not placement_ready:
            # FinalPlacement 还没写入 — 模拟 HDT timing
            if self.mode == "before":
                # 修复前：placement=null → 跳过，不再重试
                return False, f"placement=null,跳过(delay={self._placement_delay}s,已过{elapsed_since_elimination:.1f}s)"
            else:
                # 修复后：placement=null → 返回 false，调用方会重试
                return False, f"placement=null,重试(delay={self._placement_delay}s,已过{elapsed_since_elimination:.1f}s)"
        
        # placement 已可用，上传
        points = 9 if self.placement == 1 else max(1, 9 - self.placement)
        payload = {
            "playerId": self.player_id,
            "gameUuid": self.game_uuid,
            "accountIdLo": self.account_id_lo,
            "placement": self.placement,
        }

        try:
            resp = requests.post(f"{BASE_URL}/api/plugin/update-placement", json=payload, timeout=10)
            if resp.status_code == 200:
                data = resp.json()
                finalized = data.get("finalized", False)
                return True, f"第{self.placement}名 +{points}分{' 🏁对局结束' if finalized else ''}"
            elif resp.status_code == 409:
                return True, "已提交过(幂等)"
            else:
                return False, f"HTTP {resp.status_code}: {resp.text[:80]}"
        except Exception as e:
            return False, f"异常: {e}"


# ── 模拟真实游戏流程 ─────────────────────────────────

def simulate_game(players, mode):
    """
    模拟一局完整的联赛游戏过程
    
    真实场景：
    1. 8 个玩家进入游戏
    2. STEP 13: 每个玩家的插件调用 check-league
    3. 游戏进行，玩家按随机顺序被淘汰
    4. 每个玩家被淘汰后回到菜单，插件读取 FinalPlacement 并上传
    5. 最后一个存活的玩家（第1名）在游戏完全结束时上传
    """
    game_uuid = str(uuid.uuid4())
    started_at = (datetime.now(UTC) - timedelta(minutes=35)).strftime("%Y-%m-%dT%H:%M:%SZ")

    # 随机分配排名（淘汰顺序）
    elimination_order = list(range(8))  # 0-7 对应 8 个玩家
    random.shuffle(elimination_order)
    # elimination_order[0] = 第8名（最先被淘汰），elimination_order[7] = 第1名（最后存活）

    # 每个玩家的 placement 可用延迟
    # - 第8名（最先淘汰）：游戏刚开始，FinalPlacement 可能要等 3-8 秒才可用
    # - 第1名（最后存活）：游戏结束时 FinalPlacement 立即可用（~1 秒）
    placement_delays = []
    for i in range(8):
        if i == 7:
            # 第1名：游戏完全结束，FinalPlacement 立即可用
            placement_delays.append(0.5)
        elif i == 6:
            # 第2名：几乎同时结束
            placement_delays.append(1.0)
        elif i == 0:
            # 第8名（最先淘汰）：游戏还在早期，FinalPlacement 写入最慢
            placement_delays.append(6.0)
        else:
            # 中间名次：随机延迟 2-5 秒
            placement_delays.append(random.uniform(2.0, 5.0))

    # 创建插件模拟器
    plugins = []
    for i, player in enumerate(players):
        plugin = HDTPluginSimulator(player, game_uuid, started_at, mode=mode)
        plugins.append(plugin)

    # 分配淘汰信息
    for order_idx, player_idx in enumerate(elimination_order):
        placement = 8 - order_idx  # 最先淘汰 = 第8名
        plugins[player_idx].set_elimination_order(
            placement=placement,
            eliminated_at=time.time(),
            placement_delay=placement_delays[order_idx],
        )

    header(f"模拟游戏开始 (mode={mode}, gameUuid={game_uuid[:8]}...)")

    # STEP 1: 所有玩家的插件在 STEP 13 时调用 check-league
    info("STEP 13: 所有插件调用 check-league")
    for p in plugins:
        if p.check_league():
            ok(f"{p.player['displayName']:12s} → isLeague={p.is_league_game}")
        else:
            fail(f"{p.player['displayName']:12s} → check-league 失败")

    # STEP 2: 模拟游戏过程 — 玩家按淘汰顺序被淘汰
    info("游戏进行中，玩家按顺序被淘汰...")

    total_uploaded = 0
    total_failed = 0
    upload_results = {}  # player_idx -> (success, reason)

    # 模拟游戏时间流逝，每秒检查一次
    game_start = time.time()
    max_wait_time = 12  # 最长等待 12 秒（覆盖所有 placement delay）
    check_interval = 0.3  # 每 0.3 秒检查一次

    # 跟踪每个玩家的状态
    player_states = {}  # player_idx -> {eliminated: bool, eliminated_at: float, uploaded: bool}
    for i in range(8):
        player_states[i] = {
            "eliminated": False,
            "eliminated_at": None,
            "uploaded": False,
            "attempts": 0,
        }

    elapsed = 0
    while elapsed < max_wait_time:
        elapsed = time.time() - game_start

        # 检查哪些玩家应该被淘汰了
        for order_idx, player_idx in enumerate(elimination_order):
            # 淘汰时间 = 每个玩家按顺序淘汰，间隔约 0.5-1.5 秒
            elimination_time = order_idx * 1.0 + 0.5  # 秒

            if elapsed >= elimination_time and not player_states[player_idx]["eliminated"]:
                player_states[player_idx]["eliminated"] = True
                player_states[player_idx]["eliminated_at"] = time.time()
                p = plugins[player_idx]
                info(f"  ⚰️  {p.player['displayName']:12s} 被淘汰 (第{p.placement}名) "
                     f"[placement_delay={p._placement_delay}s]")

        # 已被淘汰但还没上传的玩家尝试上传
        for i in range(8):
            state = player_states[i]
            if state["eliminated"] and not state["uploaded"]:
                p = plugins[i]
                time_since_elimination = time.time() - state["eliminated_at"]
                state["attempts"] += 1

                success, reason = p.try_upload_placement(time_since_elimination)

                if success:
                    state["uploaded"] = True
                    total_uploaded += 1
                    ok(f"  {p.player['displayName']:12s} ✓ {reason} "
                       f"(第{state['attempts']}次尝试)")
                    upload_results[i] = (True, reason)
                elif mode == "before":
                    # 修复前：第一次失败就放弃
                    state["uploaded"] = True  # 标记为"已处理"（虽然没成功）
                    total_failed += 1
                    fail(f"  {p.player['displayName']:12s} ✗ {reason} "
                         f"(修复前：不再重试)")
                    upload_results[i] = (False, reason)
                # else: 修复后，下次循环继续尝试

        # 检查是否所有玩家都已处理完毕
        all_done = all(
            player_states[i]["uploaded"]
            for i in range(8)
            if player_states[i]["eliminated"]
        )
        if all_done and all(player_states[i]["eliminated"] for i in range(8)):
            break

        time.sleep(check_interval)

    # 处理超时的玩家
    for i in range(8):
        if not player_states[i]["uploaded"] and player_states[i]["eliminated"]:
            p = plugins[i]
            total_failed += 1
            fail(f"  {p.player['displayName']:12s} ✗ 超时未上传 "
                 f"(尝试{player_states[i]['attempts']}次)")
            upload_results[i] = (False, "超时")

    # 打印淘汰顺序表
    print()
    info("淘汰顺序表:")
    for order_idx, player_idx in enumerate(elimination_order):
        p = plugins[player_idx]
        placement = 8 - order_idx
        state = player_states[player_idx]
        delay = p._placement_delay
        attempts = state["attempts"]

        if state["uploaded"] and (player_idx in upload_results and upload_results[player_idx][0]):
            status = f"{GREEN}✓ 已上传{RESET}"
        else:
            status = f"{RED}✗ 丢失{RESET}"

        medal = "🥇" if placement == 1 else "🥈" if placement == 2 else "🥉" if placement == 3 else "  "
        print(f"    {medal} 第{placement}名 {p.player['displayName']:12s} "
              f"delay={delay:.1f}s 尝试{attempts}次 {status}")

    return game_uuid, total_uploaded, total_failed, upload_results


def verify_results(game_uuid, players):
    """验证服务端数据"""
    header("验证服务端数据")

    resp = requests.get(f"{BASE_URL}/api/match/{game_uuid}")
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

        if placement is not None:
            ok(f"{name:12s} → 第{placement}名 {points}分")
            results[mp.get("accountIdLo")] = (True, placement)
        else:
            fail(f"{name:12s} → placement=null (排名丢失!)")
            results[mp.get("accountIdLo")] = (False, None)
            all_ok = False

    ended_at = match.get("endedAt")
    if ended_at:
        ok(f"对局已结束: endedAt={ended_at}")
    else:
        warn("endedAt 为 null（部分玩家可能未提交）")

    return all_ok, results


def run_single_mode(mode):
    """运行单个模式的完整测试"""
    section(f"模式: {mode.upper()} {'(修复前 - placement 失败不重试)' if mode == 'before' else '(修复后 - placement 失败重试)'}")

    random.seed(42)  # 固定种子，保证两种模式的淘汰顺序一致
    players = make_players()

    # 检查 API 可达
    try:
        requests.get(f"{BASE_URL}/api/players", timeout=5)
    except requests.ConnectionError:
        fail(f"无法连接到 {BASE_URL}")
        return False

    # 注册玩家（获取验证码）
    header("注册玩家")
    codes = {}
    for p in players:
        # 先上传一次 rating 获取验证码
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
                ok(f"{p['displayName']:12s} → code={codes[p['battleTag']]}")
            else:
                fail(f"{p['displayName']:12s} → {resp.status_code}")
        except Exception as e:
            fail(f"{p['displayName']:12s} → {e}")

    # 注册到网站
    header("注册到网站")
    for p in players:
        try:
            resp = requests.post(f"{BASE_URL}/api/register", json={
                "battleTag": p["battleTag"],
                "verificationCode": codes.get(p["battleTag"], ""),
            }, timeout=10)
            if resp.status_code == 200:
                ok(f"{p['displayName']:12s} 注册成功")
            elif resp.status_code == 400:
                info(f"{p['displayName']:12s} 已注册过")
        except Exception as e:
            fail(f"{p['displayName']:12s} → {e}")

    # 报名入队
    header("报名入队")
    sessions = []
    for p in players:
        s = requests.Session()
        # 先 login 获取 session
        s.post(f"{BASE_URL}/api/login", json={
            "battleTag": p["battleTag"],
            "verificationCode": codes.get(p["battleTag"], ""),
        })
        # 加入队列
        resp = s.post(f"{BASE_URL}/api/queue/join", json={}, timeout=10)
        if resp.status_code == 200:
            data = resp.json()
            moved = data.get("moved", False)
            status = "→ 等待组满员!" if moved else "→ 等待中"
            ok(f"{p['displayName']:12s} {status}")
        sessions.append(s)

    time.sleep(0.5)

    # 验证等待组
    resp = requests.get(f"{BASE_URL}/api/waiting-queue", timeout=10)
    if resp.status_code == 200:
        groups = resp.json()
        for g in groups:
            names = [p["name"] for p in g.get("players", [])]
            ok(f"等待组: {len(names)} 人 → {', '.join(names)}")

    # 模拟游戏
    game_uuid, uploaded, failed, upload_results = simulate_game(players, mode)

    # 验证结果
    time.sleep(1)  # 等服务端处理完毕
    all_ok, verify_results_map = verify_results(game_uuid, players)

    # 打印总结
    header(f"{mode.upper()} 模式总结")
    print(f"  上传成功: {GREEN}{uploaded}/8{RESET}")
    print(f"  上传失败: {RED}{failed}/8{RESET}")

    # 与服务端数据交叉验证
    for p in players:
        uploaded_ok = upload_results.get(players.index(p), (False, ""))[0]
        server_ok = verify_results_map.get(p["accountIdLo"], (False, None))[0]

        if uploaded_ok and server_ok:
            ok(f"{p['displayName']:12s} ✓ 插件上传 ✓ 服务端记录")
        elif uploaded_ok and not server_ok:
            warn(f"{p['displayName']:12s} ✓ 插件上传 ✗ 服务端未记录")
        elif not uploaded_ok and server_ok:
            warn(f"{p['displayName']:12s} ✗ 插件未上传 ✓ 服务端有记录(其他玩家触发?)")
        else:
            fail(f"{p['displayName']:12s} ✗ 插件未上传 ✗ 服务端无记录")

    return all_ok


def main():
    parser = argparse.ArgumentParser(description="联赛全流程模拟测试")
    parser.add_argument("--mode", choices=["before", "after", "both"], default="both",
                        help="测试模式: before=修复前, after=修复后, both=两者对比")
    parser.add_argument("--api", default=None, help="API 地址 (默认用环境变量 API_URL)")
    args = parser.parse_args()

    if args.api:
        global BASE_URL
        BASE_URL = args.api

    random.seed(42)

    print(f"""
{BOLD}{CYAN}
╔══════════════════════════════════════════════════════╗
║  HDT_BGTracker 联赛模拟测试  (placement 重试对比)   ║
║  API: {BASE_URL:44s}║
║  模式: {args.mode:44s}║
╚══════════════════════════════════════════════════════╝
{RESET}""")

    if args.mode == "both":
        # 先跑 before
        before_ok = run_single_mode("before")

        # 清理等待组（before 可能留下未完成的对局）
        info("清理 before 模式残留数据...")
        try:
            requests.get(f"{BASE_URL}/api/waiting-queue", timeout=5)
        except:
            pass
        time.sleep(2)

        # 再跑 after
        after_ok = run_single_mode("after")

        # 对比总结
        section("对比总结")
        print(f"  {'修复前 (before)':20s} → {'✅ 全部上传' if before_ok else '❌ 部分排名丢失'}")
        print(f"  {'修复后 (after)':20s} → {'✅ 全部上传' if after_ok else '❌ 部分排名丢失'}")
        print()
        if not before_ok and after_ok:
            print(f"  {GREEN}{BOLD}🎉 修复生效！修复后所有玩家排名正常上传{RESET}")
        elif before_ok and after_ok:
            print(f"  {YELLOW}两种模式都成功（可能本次测试 placement delay 较短）{RESET}")
        elif not before_ok and not after_ok:
            print(f"  {RED}两种模式都有问题，需要进一步排查{RESET}")
        print()

    else:
        ok_result = run_single_mode(args.mode)
        section("测试结果")
        if ok_result:
            print(f"  {GREEN}{BOLD}✅ 测试通过{RESET}")
        else:
            print(f"  {RED}{BOLD}❌ 测试失败{RESET}")
        print()


if __name__ == "__main__":
    main()
