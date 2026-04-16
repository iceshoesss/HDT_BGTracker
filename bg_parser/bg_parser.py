#!/usr/bin/env python3
"""
Power.log 解析器 — 炉石酒馆战棋
从游戏日志中提取：本地玩家信息、英雄、排名

用法:
    python bg_parser.py <Power.log路径>
    python bg_parser.py                    # 自动查找游戏安装目录的 Power.log
"""

import re
import sys
import os
import glob
from dataclasses import dataclass, field


@dataclass
class HeroPlacement:
    """一个英雄的排名信息"""
    entity_id: int
    hero_name: str
    card_id: str
    player_slot: int
    placement: int = 0


@dataclass
class GameResult:
    """一局游戏的完整结果"""
    game_seed: int = 0
    game_type: str = ""
    local_player_tag: str = ""
    local_player_name: str = ""
    local_account_id_lo: int = 0
    local_hero_name: str = ""
    local_hero_card_id: str = ""
    local_hero_entity_id: int = 0
    local_placement: int = 0
    all_heroes: list = field(default_factory=list)
    is_battlegrounds: bool = False


# BG 英雄 cardId 前缀
_HERO_PREFIXES = (
    'TB_BaconShop_HERO_',
    'BG20_HERO_', 'BG21_HERO_', 'BG22_HERO_', 'BG23_HERO_',
    'BG24_HERO_', 'BG25_HERO_', 'BG26_HERO_', 'BG27_HERO_',
    'BG28_HERO_', 'BG29_HERO_', 'BG30_HERO_', 'BG31_HERO_',
    'BG32_HERO_', 'BG33_HERO_', 'BG34_HERO_',
)


def is_hero_card(card_id: str) -> bool:
    """
    判断 cardId 是否为 BG 英雄（非英雄技能）

    英雄:     BG34_HERO_002, TB_BaconShop_HERO_37, BG32_HERO_002
    英雄技能: BG34_HERO_002p, BG32_HERO_002p, BG34_HERO_000p
    """
    if card_id == 'TB_BaconShop_HERO_PH':  # 占位符
        return False
    if not any(card_id.startswith(p) for p in _HERO_PREFIXES):
        return False
    # 英雄技能: 后缀是纯字母（如 p, pp, p1），英雄的后缀是纯数字
    # 提取前缀后面的尾部: BG34_HERO_002 → "002", BG34_HERO_002p → "002p"
    for prefix in _HERO_PREFIXES:
        if card_id.startswith(prefix):
            suffix = card_id[len(prefix):]
            # 如果后缀含字母 → 英雄技能
            return suffix.isdigit()
    return False


def find_power_log(custom_path: str = None) -> str:
    if custom_path and os.path.isfile(custom_path):
        return custom_path

    search_dirs = [
        r"D:\Battle.net\Hearthstone\Logs",
        r"C:\Program Files (x86)\Hearthstone\Logs",
        r"C:\Program Files\Hearthstone\Logs",
    ]

    for base_dir in search_dirs:
        if not os.path.isdir(base_dir):
            continue
        folders = sorted(glob.glob(os.path.join(base_dir, "Hearthstone_*")), reverse=True)
        for folder in folders:
            log_path = os.path.join(folder, "Power.log")
            if os.path.isfile(log_path):
                return log_path

    for base_dir in search_dirs:
        log_path = os.path.join(base_dir, "Power.log")
        if os.path.isfile(log_path):
            return log_path

    return None


def parse_power_log(log_path: str) -> list[GameResult]:
    """
    解析 Power.log，提取每局游戏的结果

    日志关键行:
    ──────────────
    GameState.DebugPrintPower() - CREATE_GAME                          → 新游戏
    GameState.DebugPrintGame() - GameType=GT_BATTLEGROUNDS             → 确认战棋
    GameState.DebugPrintGame() - PlayerID=7, PlayerName=xxx#1234       → 本地玩家
    GameState.DebugPrintPower() - Player EntityID=20 PlayerID=7 GameAccountId=[hi=.. lo=xxx]
    PowerTaskList...FULL_ENTITY ... entityName=xxx ... cardId=xxx      → 英雄实体
    GameState...TAG_CHANGE Entity=xxx tag=HERO_ENTITY value=X          → 选英雄
    ...TAG_CHANGE Entity=[entityName=xxx ... cardId=xxx player=N] tag=PLAYER_LEADERBOARD_PLACE value=N
                                                                      → 排名（最终值=排名）
    """

    games: list[GameResult] = []
    current_game = None

    # 正则
    re_full_entity = re.compile(
        r'FULL_ENTITY - (?:Creating|Updating)\s+'
        r'\[?entityName=(.+?)\s+id=(\d+)\s+zone=\w+.*?'
        r'cardId=(\w+)\s+player=(\d+)\]'
    )

    re_lb_entity = re.compile(
        r'TAG_CHANGE Entity=\[entityName=(.+?) id=(\d+) zone=\w+.*?'
        r'cardId=(\w+).*?player=(\d+)\]\s+tag=PLAYER_LEADERBOARD_PLACE value=(\d+)'
    )

    re_lb_tag = re.compile(
        r'TAG_CHANGE Entity=(.+?) tag=PLAYER_LEADERBOARD_PLACE value=(\d+)\s*$'
    )

    hero_by_entity: dict[int, HeroPlacement] = {}
    hero_placements: dict[int, int] = {}

    with open(log_path, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            # === 新游戏开始（只在 GameState 的 CREATE_GAME，跳过 PowerTaskList 重复） ===
            if ('CREATE_GAME' in line
                    and 'DebugPrintPower()' in line
                    and 'PowerTaskList' not in line):
                if current_game and current_game.is_battlegrounds:
                    _finalize_game(current_game, hero_by_entity, hero_placements)
                    games.append(current_game)

                current_game = GameResult()
                hero_by_entity = {}
                hero_placements = {}
                continue

            if current_game is None:
                continue

            # 跳过 PowerTaskList 的 CREATE_GAME 重复
            if 'PowerTaskList' in line and 'CREATE_GAME' in line:
                continue

            # === DebugPrintGame ===
            if 'DebugPrintGame()' in line:
                m = re.search(r'GameType=(\w+)', line)
                if m:
                    current_game.game_type = m.group(1)
                    if current_game.game_type == 'GT_BATTLEGROUNDS':
                        current_game.is_battlegrounds = True

                m = re.search(r'PlayerID=(\d+),\s*PlayerName=(.+?)$', line)
                if m:
                    player_name = m.group(2).strip()
                    if player_name != '古怪之德鲁伊' and not current_game.local_player_tag:
                        current_game.local_player_tag = player_name
                        if '#' in player_name:
                            current_game.local_player_name = player_name.rsplit('#', 1)[0]
                        else:
                            current_game.local_player_name = player_name
                continue

            # === accountIdLo ===
            m = re.search(
                r'Player EntityID=(\d+) PlayerID=(\d+) GameAccountId=\[hi=\d+ lo=(\d+)\]',
                line
            )
            if m:
                account_lo = int(m.group(3))
                if account_lo != 0:
                    current_game.local_account_id_lo = account_lo
                continue

            # === GameSeed ===
            m = re.search(r'tag=GAME_SEED value=(\d+)', line)
            if m:
                current_game.game_seed = int(m.group(1))
                continue

            # === HERO_ENTITY ===
            m = re.search(r'TAG_CHANGE Entity=(.+?) tag=HERO_ENTITY value=(\d+)', line)
            if m:
                entity_name = m.group(1).strip()
                hero_entity_id = int(m.group(2))
                if entity_name == current_game.local_player_tag:
                    current_game.local_hero_entity_id = hero_entity_id
                continue

            # === FULL_ENTITY: 英雄实体 ===
            m = re_full_entity.search(line)
            if m:
                hero_name = m.group(1)
                entity_id = int(m.group(2))
                card_id = m.group(3)
                player_slot = int(m.group(4))

                if is_hero_card(card_id) and entity_id not in hero_by_entity:
                    hero_by_entity[entity_id] = HeroPlacement(
                        entity_id=entity_id,
                        hero_name=hero_name,
                        card_id=card_id,
                        player_slot=player_slot,
                    )
                continue

            # === LEADERBOARD_PLACE: entityName 格式 ===
            m = re_lb_entity.search(line)
            if m:
                hero_name = m.group(1)
                entity_id = int(m.group(2))
                card_id = m.group(3)
                player_slot = int(m.group(4))
                placement = int(m.group(5))

                # 只处理英雄（跳过英雄技能）
                if not is_hero_card(card_id):
                    continue

                # 始终更新排名（最后出现的值 = 最终排名）
                hero_placements[entity_id] = placement

                if entity_id not in hero_by_entity:
                    hero_by_entity[entity_id] = HeroPlacement(
                        entity_id=entity_id,
                        hero_name=hero_name,
                        card_id=card_id,
                        player_slot=player_slot,
                    )
                continue

            # === LEADERBOARD_PLACE: BattleTag 格式（本地玩家） ===
            m = re_lb_tag.search(line)
            if m:
                tag = m.group(1).strip()
                placement = int(m.group(2))
                if tag == current_game.local_player_tag:
                    if current_game.local_hero_entity_id:
                        hero_placements[current_game.local_hero_entity_id] = placement
                    current_game.local_placement = placement
                continue

    # 最后一局
    if current_game and current_game.is_battlegrounds:
        _finalize_game(current_game, hero_by_entity, hero_placements)
        games.append(current_game)

    return games


def _finalize_game(
    game: GameResult,
    hero_by_entity: dict[int, HeroPlacement],
    hero_placements: dict[int, int]
):
    """合并实体信息和排名，确定本地玩家数据"""
    for entity_id, hero in hero_by_entity.items():
        if entity_id in hero_placements:
            hero.placement = hero_placements[entity_id]

    game.all_heroes = list(hero_by_entity.values())

    # 本地玩家英雄
    if game.local_hero_entity_id and game.local_hero_entity_id in hero_by_entity:
        hero = hero_by_entity[game.local_hero_entity_id]
        game.local_hero_name = hero.hero_name
        game.local_hero_card_id = hero.card_id
        if game.local_hero_entity_id in hero_placements:
            game.local_placement = hero_placements[game.local_hero_entity_id]


def print_game_result(game: GameResult):
    print(f"{'='*50}")
    print(f"🎮 游戏类型: {game.game_type}")
    print(f"🎲 游戏种子: {game.game_seed}")
    print(f"👤 玩家: {game.local_player_tag}")
    if game.local_player_name:
        print(f"   显示名: {game.local_player_name}")
    if game.local_account_id_lo:
        print(f"   账号ID: {game.local_account_id_lo}")
    print(f"🦸 英雄: {game.local_hero_name} ({game.local_hero_card_id})")
    print(f"🏆 排名: {game.local_placement}")

    if game.all_heroes:
        # 只显示有排名的英雄
        ranked = [h for h in game.all_heroes if h.placement > 0]
        unranked = [h for h in game.all_heroes if h.placement == 0]

        if ranked:
            print(f"\n📊 全部排名:")
            sorted_heroes = sorted(ranked, key=lambda h: h.placement)
            seen_placements = set()
            for h in sorted_heroes:
                marker = " ← 你" if h.entity_id == game.local_hero_entity_id else ""
                print(f"   第{h.placement}名: {h.hero_name} ({h.card_id}){marker}")
                seen_placements.add(h.placement)
            if unranked:
                print(f"\n   ⚠️ 未参与排名的英雄（可能是未选中的候选项）:")
                for h in unranked:
                    print(f"   - {h.hero_name} ({h.card_id})")


def main():
    log_path = sys.argv[1] if len(sys.argv) > 1 else None

    found_path = find_power_log(log_path)
    if not found_path:
        print("❌ 未找到 Power.log")
        print("用法: python bg_parser.py <Power.log路径>")
        sys.exit(1)

    print(f"📖 读取: {found_path}")
    print(f"📏 大小: {os.path.getsize(found_path) / 1024:.1f} KB")
    print()

    games = parse_power_log(found_path)

    if not games:
        print("⚠️ 未发现酒馆战棋对局")
        sys.exit(0)

    print(f"🎯 共发现 {len(games)} 局战棋对局\n")

    for i, game in enumerate(games, 1):
        print(f"\n{'#'*50}")
        print(f"# 第 {i} 局")
        print_game_result(game)

    placements = [g.local_placement for g in games if g.local_placement > 0]
    if placements:
        print(f"\n{'='*50}")
        print(f"📈 汇总:")
        print(f"   总场次: {len(games)}")
        print(f"   已结算: {len(placements)}")
        print(f"   平均排名: {sum(placements)/len(placements):.2f}")
        print(f"   前四: {sum(1 for p in placements if p <= 4)}/{len(placements)}")
        print(f"   吃鸡: {sum(1 for p in placements if p == 1)}/{len(placements)}")


if __name__ == '__main__':
    main()
