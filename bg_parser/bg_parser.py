#!/usr/bin/env python3
"""
Power.log 实时解析器 — 炉石酒馆战棋
实时监控游戏日志，游戏过程中输出玩家信息和排名变化

用法:
    python bg_parser.py                  # 实时监控（自动查找 Power.log）
    python bg_parser.py <Power.log路径>  # 指定路径
    python bg_parser.py --parse <路径>   # 解析已有日志（非实时）
"""

import re
import sys
import os
import glob
import time
import signal
from dataclasses import dataclass, field
from datetime import datetime


@dataclass
class HeroPlacement:
    entity_id: int
    hero_name: str
    card_id: str
    player_slot: int
    placement: int = 0


@dataclass
class GameResult:
    game_seed: int = 0
    local_player_tag: str = ""
    local_player_name: str = ""
    local_account_id_lo: int = 0
    local_hero_name: str = ""
    local_hero_card_id: str = ""
    local_hero_entity_id: int = 0
    local_placement: int = 0
    all_heroes: dict = field(default_factory=dict)  # entity_id -> HeroPlacement
    hero_placements: dict = field(default_factory=dict)  # entity_id -> placement
    is_active: bool = False
    start_time: str = ""


# ─── 英雄卡牌过滤 ─────────────────────────────────────────

_HERO_PREFIXES = (
    'TB_BaconShop_HERO_',
    'BG20_HERO_', 'BG21_HERO_', 'BG22_HERO_', 'BG23_HERO_',
    'BG24_HERO_', 'BG25_HERO_', 'BG26_HERO_', 'BG27_HERO_',
    'BG28_HERO_', 'BG29_HERO_', 'BG30_HERO_', 'BG31_HERO_',
    'BG32_HERO_', 'BG33_HERO_', 'BG34_HERO_',
)


def is_hero_card(card_id: str) -> bool:
    """判断 cardId 是否为 BG 英雄（排除英雄技能）"""
    if card_id == 'TB_BaconShop_HERO_PH':
        return False
    for prefix in _HERO_PREFIXES:
        if card_id.startswith(prefix):
            suffix = card_id[len(prefix):]
            return suffix.isdigit()
    return False


# ─── 日志路径查找 ─────────────────────────────────────────

def find_latest_power_log(custom_path: str = None) -> str:
    """查找最新的 Power.log"""
    if custom_path:
        if os.path.isfile(custom_path):
            return custom_path
        print(f"❌ 文件不存在: {custom_path}")
        sys.exit(1)

    # 方式 1: Windows 注册表查找安装目录
    install_dir = _find_hs_install_dir()
    if install_dir:
        logs_dir = os.path.join(install_dir, "Logs")
        log_path = _find_log_in_dir(logs_dir)
        if log_path:
            return log_path

    # 方式 2: 常见安装路径兜底
    fallback_dirs = [
        r"D:\Battle.net\Hearthstone\Logs",
        r"C:\Program Files (x86)\Hearthstone\Logs",
        r"C:\Program Files\Hearthstone\Logs",
        r"D:\Hearthstone\Logs",
        r"C:\Program Files (x86)\Battle.net\Hearthstone\Logs",
    ]
    for logs_dir in fallback_dirs:
        log_path = _find_log_in_dir(logs_dir)
        if log_path:
            return log_path

    return None


def _find_hs_install_dir() -> str:
    """从 Windows 注册表获取炉石安装路径"""
    try:
        import winreg
    except ImportError:
        return None

    keys = [
        (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WOW6432Node\Blizzard Entertainment\Hearthstone"),
        (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\Blizzard Entertainment\Hearthstone"),
        (winreg.HKEY_CURRENT_USER, r"SOFTWARE\Blizzard Entertainment\Hearthstone"),
    ]
    for hive, path in keys:
        try:
            key = winreg.OpenKey(hive, path)
            install_path, _ = winreg.QueryValueEx(key, "InstallPath")
            winreg.CloseKey(key)
            if install_path and os.path.isdir(install_path):
                return install_path
        except (FileNotFoundError, OSError):
            continue
    return None


def _find_log_in_dir(logs_dir: str) -> str:
    """在 Logs 目录下查找最新的 Power.log"""
    if not os.path.isdir(logs_dir):
        return None

    # 查找 Hearthstone_时间戳 归档文件夹
    candidates = []

    # 归档文件夹
    for folder in glob.glob(os.path.join(logs_dir, "Hearthstone_*")):
        log_path = os.path.join(folder, "Power.log")
        if os.path.isfile(log_path):
            candidates.append((os.path.getmtime(log_path), log_path))

    # 直接在 Logs 根目录的 Power.log
    root_log = os.path.join(logs_dir, "Power.log")
    if os.path.isfile(root_log):
        candidates.append((os.path.getmtime(root_log), root_log))

    if not candidates:
        return None

    # 取最新修改的
    candidates.sort(reverse=True)
    return candidates[0][1]


# ─── 核心解析引擎 ─────────────────────────────────────────

# 预编译正则
RE_CREATE_GAME = re.compile(r'DebugPrintPower\(\).*CREATE_GAME')
RE_GAME_TYPE = re.compile(r'GameType=(\w+)')
RE_PLAYER_INFO = re.compile(r'PlayerID=(\d+),\s*PlayerName=(.+?)$')
RE_ACCOUNT_ID = re.compile(
    r'Player EntityID=(\d+) PlayerID=(\d+) GameAccountId=\[hi=\d+ lo=(\d+)\]'
)
RE_GAME_SEED = re.compile(r'tag=GAME_SEED value=(\d+)')
RE_HERO_ENTITY = re.compile(r'TAG_CHANGE Entity=(.+?) tag=HERO_ENTITY value=(\d+)')
RE_FULL_ENTITY = re.compile(
    r'FULL_ENTITY - (?:Creating|Updating)\s+'
    r'\[?entityName=(.+?)\s+id=(\d+)\s+zone=\w+.*?'
    r'cardId=(\w+)\s+player=(\d+)\]'
)
RE_LB_ENTITY = re.compile(
    r'TAG_CHANGE Entity=\[entityName=(.+?) id=(\d+) zone=\w+.*?'
    r'cardId=(\w+).*?player=(\d+)\]\s+tag=PLAYER_LEADERBOARD_PLACE value=(\d+)'
)
RE_LB_TAG = re.compile(
    r'TAG_CHANGE Entity=(.+?) tag=PLAYER_LEADERBOARD_PLACE value=(\d+)\s*$'
)


def process_line(line: str, game: GameResult):
    """
    处理一行日志，返回 True 表示有新数据需要展示

    状态机:
    ────
    空闲 → CREATE_GAME → 对局中（收集玩家信息、英雄、排名）
    对局中 → 下一个 CREATE_GAME → 保存结果、开始新局
    """
    # ── 新游戏开始 ──
    if RE_CREATE_GAME.search(line) and 'PowerTaskList' not in line:
        if game.is_active:
            # 结束上一局
            return 'game_end'

        # 开始新局
        game.is_active = True
        game.game_seed = 0
        game.local_player_tag = ""
        game.local_player_name = ""
        game.local_account_id_lo = 0
        game.local_hero_name = ""
        game.local_hero_card_id = ""
        game.local_hero_entity_id = 0
        game.local_placement = 0
        game.all_heroes = {}
        game.hero_placements = {}
        game.start_time = datetime.now().strftime("%H:%M:%S")
        return 'game_start'

    if not game.is_active:
        return None

    # 跳过 PowerTaskList 的 CREATE_GAME 重复
    if 'PowerTaskList' in line and 'CREATE_GAME' in line:
        return None

    # ── GameType ──
    m = RE_GAME_TYPE.search(line)
    if m and 'DebugPrintGame()' in line:
        game_type = m.group(1)
        if game_type != 'GT_BATTLEGROUNDS':
            game.is_active = False
            return 'not_bg'
        return None

    # ── PlayerName ──
    m = RE_PLAYER_INFO.search(line)
    if m and 'DebugPrintGame()' in line and not game.local_player_tag:
        player_name = m.group(2).strip()
        if player_name != '古怪之德鲁伊':
            game.local_player_tag = player_name
            if '#' in player_name:
                game.local_player_name = player_name.rsplit('#', 1)[0]
            else:
                game.local_player_name = player_name
            return 'player_info'

    # ── accountIdLo ──
    m = RE_ACCOUNT_ID.search(line)
    if m:
        account_lo = int(m.group(3))
        if account_lo != 0 and not game.local_account_id_lo:
            game.local_account_id_lo = account_lo
            return 'account_info'

    # ── GameSeed ──
    m = RE_GAME_SEED.search(line)
    if m:
        game.game_seed = int(m.group(1))
        return None

    # ── HERO_ENTITY（本地玩家选英雄）──
    m = RE_HERO_ENTITY.search(line)
    if m:
        entity_name = m.group(1).strip()
        hero_entity_id = int(m.group(2))
        if entity_name == game.local_player_tag:
            game.local_hero_entity_id = hero_entity_id
            return 'hero_selected'

    # ── FULL_ENTITY（英雄实体创建）──
    m = RE_FULL_ENTITY.search(line)
    if m:
        hero_name = m.group(1)
        entity_id = int(m.group(2))
        card_id = m.group(3)
        player_slot = int(m.group(4))
        if is_hero_card(card_id) and entity_id not in game.all_heroes:
            game.all_heroes[entity_id] = HeroPlacement(
                entity_id=entity_id, hero_name=hero_name,
                card_id=card_id, player_slot=player_slot,
            )
        return None

    # ── LEADERBOARD_PLACE（排名更新）──
    m = RE_LB_ENTITY.search(line)
    if m:
        hero_name = m.group(1)
        entity_id = int(m.group(2))
        card_id = m.group(3)
        player_slot = int(m.group(4))
        placement = int(m.group(5))

        if not is_hero_card(card_id):
            return None

        old_placement = game.hero_placements.get(entity_id, 0)
        game.hero_placements[entity_id] = placement

        if entity_id not in game.all_heroes:
            game.all_heroes[entity_id] = HeroPlacement(
                entity_id=entity_id, hero_name=hero_name,
                card_id=card_id, player_slot=player_slot,
            )
        game.all_heroes[entity_id].placement = placement

        # 本地玩家的英雄排名变化
        if entity_id == game.local_hero_entity_id:
            game.local_placement = placement
            if placement != old_placement:
                return 'placement_update'

        return None

    # ── LEADERBOARD_PLACE（BattleTag 格式，本地玩家）──
    m = RE_LB_TAG.search(line)
    if m:
        tag = m.group(1).strip()
        placement = int(m.group(2))
        if tag == game.local_player_tag:
            old = game.local_placement
            game.local_placement = placement
            if game.local_hero_entity_id:
                game.hero_placements[game.local_hero_entity_id] = placement
                if game.local_hero_entity_id in game.all_heroes:
                    game.all_heroes[game.local_hero_entity_id].placement = placement
            if placement != old:
                return 'placement_update'

    return None


def print_status(game: GameResult, event: str):
    """根据事件类型打印状态"""
    if event == 'game_start':
        print(f"\n{'─'*50}")
        print(f"🎮 新对局开始 | {game.start_time}")

    elif event == 'player_info':
        print(f"👤 {game.local_player_tag}")

    elif event == 'account_info':
        print(f"   账号ID: {game.local_account_id_lo}")

    elif event == 'hero_selected':
        hero = game.all_heroes.get(game.local_hero_entity_id)
        if hero:
            game.local_hero_name = hero.hero_name
            game.local_hero_card_id = hero.card_id
            print(f"🦸 英雄: {hero.hero_name} ({hero.card_id})")

    elif event == 'placement_update':
        rank_emoji = {1: '🥇', 2: '🥈', 3: '🥉'}.get(game.local_placement, '  ')
        print(f"📊 {rank_emoji} 排名更新: 第 {game.local_placement} 名")

    elif event == 'game_end':
        print(f"\n{'─'*50}")
        print(f"🏁 对局结束")
        print(f"👤 {game.local_player_tag}")
        hero = game.all_heroes.get(game.local_hero_entity_id)
        if hero:
            print(f"🦸 {hero.hero_name} ({hero.card_id})")
        print(f"🏆 最终排名: 第 {game.local_placement} 名")

        ranked = sorted(
            [h for h in game.all_heroes.values() if h.placement > 0],
            key=lambda h: h.placement
        )
        if ranked:
            print(f"\n📊 全部排名:")
            for h in ranked:
                marker = " ← 你" if h.entity_id == game.local_hero_entity_id else ""
                print(f"   第{h.placement}名: {h.hero_name} ({h.card_id}){marker}")
        print()


# ─── 实时监控模式 ─────────────────────────────────────────

def tail_log(log_path: str):
    """实时监控 Power.log 变化，自动切换到新日志文件"""
    game = GameResult()
    running = True
    current_path = log_path
    check_interval = 0.1      # 轮询间隔
    file_check_counter = 0
    file_check_every = 100    # 每 100 次轮询检查一次新文件（约 10 秒）

    def signal_handler(sig, frame):
        nonlocal running
        running = False
        print("\n⏹ 停止监控")

    signal.signal(signal.SIGINT, signal_handler)

    print(f"👁 监控: {current_path}")
    print(f"   等待游戏开始...")
    print(f"   (Ctrl+C 停止)\n")

    # 跳过已有内容
    pos = _get_file_end(current_path)

    while running:
        try:
            # 定期检查是否有新的日志文件
            file_check_counter += 1
            if file_check_counter >= file_check_every:
                file_check_counter = 0
                new_path = _check_new_log_file(current_path)
                if new_path:
                    # 结束当前对局（如果有）
                    if game.is_active:
                        print(f"\n🔄 检测到游戏重启，当前对局中断")
                        game.is_active = False
                        game = GameResult()

                    current_path = new_path
                    pos = _get_file_end(current_path)
                    print(f"\n🔄 切换到新日志: {current_path}")
                    print(f"   等待游戏开始...\n")

            # 读取新内容
            with open(current_path, 'r', encoding='utf-8', errors='replace') as f:
                f.seek(pos)
                lines = f.readlines()
                pos = f.tell()

            for line in lines:
                event = process_line(line, game)
                if event:
                    print_status(game, event)
                    if event == 'game_end':
                        game = GameResult()

            time.sleep(check_interval)

        except FileNotFoundError:
            # 文件可能被删除/移动，尝试找新文件
            new_path = _check_new_log_file(current_path)
            if new_path:
                current_path = new_path
                pos = _get_file_end(current_path)
                game = GameResult()
                print(f"\n🔄 日志文件已切换: {current_path}")
                print(f"   等待游戏开始...\n")
            else:
                print("❌ 日志文件消失，等待恢复...")
                time.sleep(3)
        except Exception as e:
            print(f"⚠️ 错误: {e}")
            time.sleep(1)


def _get_file_end(path: str) -> int:
    """获取文件末尾位置"""
    try:
        with open(path, 'r', encoding='utf-8', errors='replace') as f:
            f.seek(0, 2)
            return f.tell()
    except (FileNotFoundError, OSError):
        return 0


def _check_new_log_file(current_path: str) -> str:
    """
    检查是否有比当前更新的日志文件
    返回新路径或 None
    """
    # 从当前路径推断 Logs 目录
    current_dir = os.path.dirname(current_path)
    parent = os.path.dirname(current_dir)

    # 判断当前路径是 归档文件夹/Power.log 还是 Logs/Power.log
    basename = os.path.basename(current_dir)
    if basename.startswith("Hearthstone_"):
        logs_dir = parent
    else:
        logs_dir = current_dir

    if not os.path.isdir(logs_dir):
        return None

    # 找所有日志文件，按修改时间排序
    candidates = []
    for folder in glob.glob(os.path.join(logs_dir, "Hearthstone_*")):
        p = os.path.join(folder, "Power.log")
        if os.path.isfile(p) and os.path.abspath(p) != os.path.abspath(current_path):
            candidates.append((os.path.getmtime(p), p))

    root_log = os.path.join(logs_dir, "Power.log")
    if os.path.isfile(root_log) and os.path.abspath(root_log) != os.path.abspath(current_path):
        candidates.append((os.path.getmtime(root_log), root_log))

    if not candidates:
        return None

    # 取最新且比当前文件更新的
    current_mtime = 0
    try:
        current_mtime = os.path.getmtime(current_path)
    except OSError:
        pass

    candidates.sort(reverse=True)
    newest_mtime, newest_path = candidates[0]

    if newest_mtime > current_mtime:
        return newest_path

    return None


# ─── 批量解析模式 ─────────────────────────────────────────

def parse_file(log_path: str):
    """解析完整日志文件"""
    game = GameResult()
    games = []

    print(f"📖 读取: {log_path}")
    print(f"📏 大小: {os.path.getsize(log_path) / 1024:.1f} KB\n")

    with open(log_path, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            event = process_line(line, game)
            if event == 'game_end':
                games.append(game)
                game = GameResult()

    if game.is_active:
        games.append(game)

    if not games:
        print("⚠️ 未发现战棋对局")
        return

    print(f"🎯 共 {len(games)} 局\n")
    for i, g in enumerate(games, 1):
        print(f"\n{'#'*50}")
        print(f"# 第 {i} 局")
        print_status(g, 'game_end')

    # 汇总
    placements = [g.local_placement for g in games if g.local_placement > 0]
    if placements:
        print(f"{'='*50}")
        print(f"📈 汇总: {len(games)} 局")
        print(f"   平均排名: {sum(placements)/len(placements):.2f}")
        print(f"   前四: {sum(1 for p in placements if p <= 4)}/{len(placements)}")
        print(f"   吃鸡: {sum(1 for p in placements if p == 1)}/{len(placements)}")


# ─── 入口 ─────────────────────────────────────────────────

def main():
    # 解析参数
    if '--parse' in sys.argv:
        idx = sys.argv.index('--parse')
        path = sys.argv[idx + 1] if idx + 1 < len(sys.argv) else None
        log_path = find_latest_power_log(path)
        if not log_path:
            print("❌ 未找到 Power.log")
            sys.exit(1)
        parse_file(log_path)
        return

    # 实时模式（默认）
    path = sys.argv[1] if len(sys.argv) > 1 and not sys.argv[1].startswith('--') else None
    log_path = find_latest_power_log(path)
    if not log_path:
        print("❌ 未找到 Power.log")
        print("用法: python bg_parser.py [Power.log路径]")
        sys.exit(1)

    tail_log(log_path)


if __name__ == '__main__':
    main()
