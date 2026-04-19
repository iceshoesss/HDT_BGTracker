#!/usr/bin/env python3
"""
bg_parser.py — 炉石酒馆战棋 Power.log 解析器（v2 重写）

功能：
  - 解析已有日志文件（--parse）
  - 实时监控日志变化（默认）

设计文档：bg_parser/LOG_ANALYSIS.md
"""

import re
import sys
import os
import glob
import time
import signal
from dataclasses import dataclass, field
from datetime import datetime
from typing import Optional


# ═══════════════════════════════════════════════════════════
#  数据模型
# ═══════════════════════════════════════════════════════════

@dataclass
class Hero:
    """一个英雄实体"""
    entity_id: int
    hero_name: str
    card_id: str
    player_slot: int  # player=N 中的 N
    placement: int = 0


@dataclass
class Game:
    """一局游戏的状态"""
    # 玩家信息
    player_tag: str = ""            # 南怀北瑾丨少头脑#5267
    player_display_name: str = ""   # 南怀北瑾丨少头脑
    account_id_lo: int = 0          # 1708070391

    # 对局标识
    game_seed: int = 0              # GAME_SEED（暴雪内部种子，非 UUID）

    # 英雄信息
    hero_entity_id: int = 0         # HERO_ENTITY 选定的 entity id
    hero_name: str = ""
    hero_card_id: str = ""
    hero_placement: int = 0         # 最终 LEADERBOARD_PLACE（投降=8，正常淘汰=观测值）

    # 所有英雄（用 (card_id, player_slot) 去重）
    all_heroes: dict = field(default_factory=dict)  # (card_id, slot) → Hero

    # 游戏状态
    is_active: bool = False
    reconnected: bool = False
    conceded: bool = False
    placement_confirmed: bool = False  # 排名是否确定（投降=true，正常淘汰=false）
    start_time: str = ""            # 日志中的第一个时间戳
    end_time: str = ""

    # HearthMirror 对手信息
    lobby_players: list = field(default_factory=list)  # [{"lo": int, "heroCardId": str}, ...]


# ═══════════════════════════════════════════════════════════
#  HearthMirror 集成（可选）
# ═══════════════════════════════════════════════════════════

_mirror_reflection = None

def _try_init_mirror():
    """尝试初始化 HearthMirror，成功返回 True"""
    global _mirror_reflection
    if _mirror_reflection is not None:
        return True
    try:
        import pythonnet
        pythonnet.set_runtime('netfx')
        import clr
        # 查找同级目录或 HDT 目录下的 HearthMirror.dll
        hm_paths = [
            os.path.join(os.path.dirname(__file__), '..', '..', 'HearthMirror.dll'),
            os.path.join(os.path.dirname(__file__), 'HearthMirror.dll'),
        ]
        hm_path = None
        for p in hm_paths:
            if os.path.isfile(p):
                hm_path = os.path.abspath(p)
                break
        if not hm_path:
            return False
        clr.AddReference(hm_path)
        import HearthMirror
        _mirror_reflection = HearthMirror.Reflection()
        print("[HearthMirror] ✅ 已加载，可获取对手 Lo")
        return True
    except Exception as e:
        print(f"[HearthMirror] ⚠️ 不可用: {e}")
        return False


def fetch_lobby_players() -> list:
    """通过 HearthMirror 获取大厅 8 个玩家的 Lo + HeroCardId"""
    global _mirror_reflection
    if _mirror_reflection is None:
        if not _try_init_mirror():
            return []
    try:
        lobby = _mirror_reflection.GetBattlegroundsLobbyInfo()
        if lobby is None or lobby.Players is None or lobby.Players.Count == 0:
            return []
        result = []
        for p in lobby.Players:
            lo = p.AccountId.Lo if p.AccountId else 0
            hero = p.HeroCardId if p.HeroCardId else ""
            result.append({"lo": lo, "heroCardId": hero})
        return result
    except Exception as e:
        print(f"[HearthMirror] 读取失败: {e}")
        return []


# ═══════════════════════════════════════════════════════════
#  英雄卡牌过滤
# ═══════════════════════════════════════════════════════════

_HERO_PREFIXES = (
    'TB_BaconShop_HERO_',
    'BG20_HERO_', 'BG21_HERO_', 'BG22_HERO_', 'BG23_HERO_',
    'BG24_HERO_', 'BG25_HERO_', 'BG26_HERO_', 'BG27_HERO_',
    'BG28_HERO_', 'BG29_HERO_', 'BG30_HERO_', 'BG31_HERO_',
    'BG32_HERO_', 'BG33_HERO_', 'BG34_HERO_', 'BG35_HERO_',
)

_HERO_EXCLUDE = ('TB_BaconShop_HERO_PH',)


def is_hero_card(card_id: str) -> bool:
    if card_id in _HERO_EXCLUDE:
        return False
    for prefix in _HERO_PREFIXES:
        if card_id.startswith(prefix):
            suffix = card_id[len(prefix):]
            return suffix.isdigit()
    return False


# ═══════════════════════════════════════════════════════════
#  正则表达式
# ═══════════════════════════════════════════════════════════

_RE_CREATE_GAME = re.compile(r'GameState\.DebugPrintPower\(\) - CREATE_GAME$')
_RE_GAME_TYPE = re.compile(r'GameType=(\w+)')
_RE_GAME_SEED = re.compile(r'tag=GAME_SEED value=(\d+)')
_RE_PLAYER_NAME = re.compile(r'PlayerID=(\d+),\s*PlayerName=(.+?)$')
_RE_ACCOUNT_ID = re.compile(r'GameAccountId=\[hi=\d+ lo=(\d+)\]')
_RE_HERO_ENTITY = re.compile(r'TAG_CHANGE Entity=(.+?) tag=HERO_ENTITY value=(\d+)')

_RE_FULL_ENTITY = re.compile(
    r'FULL_ENTITY - (?:Creating|Updating)\s+'
    r'\[?entityName=(.+?)\s+id=(\d+)\s+zone=\w+.*?'
    r'cardId=(\w+)\s+player=(\d+)\]'
)

_RE_LB_ENTITY = re.compile(
    r'TAG_CHANGE Entity=\[entityName=(.+?) id=(\d+) zone=\w+.*?'
    r'cardId=(\w+).*?player=(\d+)\]\s+tag=PLAYER_LEADERBOARD_PLACE value=(\d+)'
)

_RE_LB_TAG = re.compile(r'TAG_CHANGE Entity=(.+?) tag=PLAYER_LEADERBOARD_PLACE value=(\d+)\s*$')
_RE_STEP = re.compile(r'TAG_CHANGE Entity=GameEntity tag=STEP value=(\w+)')
_RE_CONCEDE_PLAYER_TAG = re.compile(r'TAG_CHANGE Entity=.+? tag=(3479|4356) value=1')
_RE_CONCEDE_GAME_TAG = re.compile(r'TAG_CHANGE Entity=GameEntity tag=4302 value=1')
_RE_GAME_STATE_COMPLETE = re.compile(r'TAG_CHANGE Entity=GameEntity tag=STATE value=COMPLETE')

# 提取日志时间戳
_RE_TIMESTAMP = re.compile(r'^[DIWE] (\d{2}:\d{2}:\d{2})\.(\d+)')


# ═══════════════════════════════════════════════════════════
#  核心解析引擎
# ═══════════════════════════════════════════════════════════

class Parser:
    """状态机解析器"""

    def __init__(self):
        self.game = Game()
        self.games: list[Game] = []
        self._in_create_block = False
        self._create_has_turn = False
        self._pending_new_game: Optional[Game] = None  # 新局暂存，重连时回滚
        self._concede_pending = False
        self._concede_tag = ""
        self._last_reconnect_time = ""

    def process_line(self, line: str) -> Optional[str]:
        """
        处理一行日志，返回事件类型或 None。

        事件:
          game_start / reconnect / player_info / account_info /
          hero_entity / hero_found / phase_change / concede / game_end / not_bg
        """
        # ── CREATE_GAME ──
        if _RE_CREATE_GAME.search(line):
            self._in_create_block = True
            self._create_has_turn = False
            # 保存旧局，立即创建新局（让 DebugPrintGame 等行能处理）
            if self.game.is_active:
                self._pending_new_game = self.game  # 暂存旧局
            else:
                self._pending_new_game = None
            self._reset_game()  # 立即激活新局
            return None

        # ── CREATE_GAME 块内 ──
        if self._in_create_block:
            if 'tag=TURN value=' in line:
                # 断线重连！回滚到旧局
                self._create_has_turn = True
                if self._pending_new_game:
                    self.games.pop() if self.games and self.games[-1] is self.game else None
                    self.game = self._pending_new_game
                    self.game.reconnected = True
                    self._pending_new_game = None
                return None
            if 'GameAccountId=' in line:
                m = _RE_ACCOUNT_ID.search(line)
                if m:
                    lo = int(m.group(1))
                    if lo != 0 and not self.game.account_id_lo:
                        self.game.account_id_lo = lo
            # GAME_SEED
            if 'GAME_SEED' in line:
                m = _RE_GAME_SEED.search(line)
                if m:
                    self.game.game_seed = int(m.group(1))
            if 'PowerTaskList.DebugDump()' in line:
                self._in_create_block = False
                # 如果是新局（非重连），旧局已经在 _reset_game 时被暂存
                # 此时确认新局生效，把旧局存入 games
                if not self._create_has_turn and self._pending_new_game:
                    old = self._pending_new_game
                    old.is_active = False
                    old.end_time = datetime.now().strftime("%H:%M:%S")
                    self.games.append(old)
                    self._pending_new_game = None
                return 'reconnect' if self._create_has_turn else 'game_start'
            # DebugPrintGame / 其他行 → 不在块内处理，交给下面的逻辑
            if 'PowerTaskList.DebugPrintPower()' not in line:
                # 跳过块内的 PowerTaskList（还没到块结束）
                pass

        # ── 只处理 GameState + PowerTaskList ──
        if 'GameState.' not in line and 'PowerTaskList.DebugPrintPower()' not in line:
            return None

        if not self.game.is_active:
            return None

        # ── PowerTaskList：只处理 FULL_ENTITY ──
        if 'PowerTaskList.' in line:
            return self._handle_powertasklist(line)

        return self._handle_gamestate(line)

    def _reset_game(self):
        self.game = Game(is_active=True, start_time=datetime.now().strftime("%H:%M:%S"))
        self._concede_pending = False
        self._concede_tag = ""

    def _end_game(self):
        if not self.game.is_active:
            return
        self.game.is_active = False
        self.game.end_time = datetime.now().strftime("%H:%M:%S")
        self.games.append(self.game)

    def _handle_gamestate(self, line: str) -> Optional[str]:
        """处理 GameState.DebugPrintPower 和 DebugPrintGame 行"""

        # GameType
        m = _RE_GAME_TYPE.search(line)
        if m and 'DebugPrintGame()' in line:
            if m.group(1) != 'GT_BATTLEGROUNDS':
                self._end_game()
                return 'not_bg'
            return None

        # PlayerName
        m = _RE_PLAYER_NAME.search(line)
        if m and 'DebugPrintGame()' in line and not self.game.player_tag:
            name = m.group(2).strip()
            if name in ('古怪之德鲁伊', '惊魂之武僧'):
                return None
            self.game.player_tag = name
            if '#' in name:
                self.game.player_display_name = name.rsplit('#', 1)[0]
            else:
                self.game.player_display_name = name
            return 'player_info'

        # HERO_ENTITY（本地玩家选英雄）
        m = _RE_HERO_ENTITY.search(line)
        if m:
            entity_name = m.group(1).strip()
            hero_entity_id = int(m.group(2))
            if entity_name == self.game.player_tag:
                self.game.hero_entity_id = hero_entity_id
                # 回填英雄名（FULL_ENTITY 可能已经创建了该英雄）
                hero = self._find_hero_by_entity(hero_entity_id)
                if hero:
                    self.game.hero_name = hero.hero_name
                    self.game.hero_card_id = hero.card_id
                return 'hero_entity'

        # FULL_ENTITY（仅 GameState 中的，PowerTaskList 在另一路径处理）
        m = _RE_FULL_ENTITY.search(line)
        if m:
            hero_name = m.group(1)
            entity_id = int(m.group(2))
            card_id = m.group(3)
            player_slot = int(m.group(4))
            if is_hero_card(card_id):
                key = (card_id, player_slot)
                if key not in self.game.all_heroes:
                    hero = Hero(entity_id=entity_id, hero_name=hero_name,
                                card_id=card_id, player_slot=player_slot)
                    self.game.all_heroes[key] = hero
                    if entity_id == self.game.hero_entity_id:
                        self.game.hero_name = hero_name
                        self.game.hero_card_id = card_id
                # 如果 HERO_ENTITY 已经设了但英雄名还没填（FULL_ENTITY 在 HERO_ENTITY 之后出现）
                elif entity_id == self.game.hero_entity_id and not self.game.hero_name:
                    existing = self.game.all_heroes[key]
                    self.game.hero_name = existing.hero_name
                    self.game.hero_card_id = existing.card_id
            return None

        # STEP
        m = _RE_STEP.search(line)
        if m:
            step = m.group(1)
            if step in ('MAIN_READY', 'MAIN_ACTION'):
                return 'phase_change'
            if step == 'MAIN_CLEANUP':
                # 英雄选定完毕，尝试获取对手 Lo
                self.game.lobby_players = fetch_lobby_players()
                if self.game.lobby_players:
                    print("[HearthMirror] 📋 获取到 {} 个玩家".format(len(self.game.lobby_players)))
                    for lp in self.game.lobby_players:
                        print("   Lo={}, Hero={}".format(lp['lo'], lp['heroCardId']))
                return 'phase_change'
            return None

        # LEADERBOARD_PLACE（追踪本地英雄排名变化）
        m = _RE_LB_ENTITY.search(line)
        if m:
            entity_id = int(m.group(2))
            card_id = m.group(3)
            player_slot = int(m.group(4))
            placement = int(m.group(5))
            if is_hero_card(card_id):
                # 用 cardId+playerSlot 匹配本地英雄
                if self.game.hero_card_id and card_id == self.game.hero_card_id:
                    self.game.hero_placement = placement
            # 投降检测：PLACE=8 且投降信号已触发
            if self._concede_pending and placement == 8:
                self.game.conceded = True
                self.game.placement_confirmed = True
                self.game.hero_placement = 8
                self._end_game()
                return 'concede'
            return None

        # LEADERBOARD_PLACE 简写格式（仅 BattleTag）
        m = _RE_LB_TAG.search(line)
        if m:
            tag = m.group(1).strip()
            placement = int(m.group(2))
            if tag == self.game.player_tag:
                self.game.hero_placement = placement
            # 投降检测
            if self._concede_pending and placement == 8:
                self.game.conceded = True
                self.game.placement_confirmed = True
                self.game.hero_placement = 8
                self._end_game()
                return 'concede'
            return None

        # 投降信号前兆：tag=3479/4356
        if _RE_CONCEDE_PLAYER_TAG.search(line):
            tag_match = re.search(r'Entity=(.+?) tag=(?:3479|4356)', line)
            if tag_match:
                self._concede_tag = tag_match.group(1).strip()
            self._concede_pending = True
            return None

        # 投降信号：tag=4302
        if self._concede_pending and _RE_CONCEDE_GAME_TAG.search(line):
            return None

        # 游戏结束：STATE=COMPLETE（自然淘汰 / 胜利 / 投降兜底）
        if _RE_GAME_STATE_COMPLETE.search(line):
            # 投降已有单独处理，这里兜底
            if not self.game.conceded:
                if self.game.hero_placement > 0:
                    self.game.placement_confirmed = True
            self._end_game()
            return 'game_end'

        return None

    def _handle_powertasklist(self, line: str) -> Optional[str]:
        """PowerTaskList 行：只取 FULL_ENTITY"""
        m = _RE_FULL_ENTITY.search(line)
        if not m:
            return None

        hero_name = m.group(1)
        entity_id = int(m.group(2))
        card_id = m.group(3)
        player_slot = int(m.group(4))

        if not is_hero_card(card_id):
            return None

        # 用 (card_id, player_slot) 去重
        key = (card_id, player_slot)
        if key not in self.game.all_heroes:
            hero = Hero(
                entity_id=entity_id,
                hero_name=hero_name,
                card_id=card_id,
                player_slot=player_slot,
            )
            self.game.all_heroes[key] = hero
            # 如果这正是本地玩家选的英雄
            if entity_id == self.game.hero_entity_id:
                self.game.hero_name = hero_name
                self.game.hero_card_id = card_id
            return 'hero_found'
        return None

    def _find_hero_by_entity(self, entity_id: int) -> Hero | None:
        """通过 entity_id 查找英雄（仅限当前活跃的 entity_id）"""
        for hero in self.game.all_heroes.values():
            if hero.entity_id == entity_id:
                return hero
        return None


# ═══════════════════════════════════════════════════════════
#  日志路径查找
# ═══════════════════════════════════════════════════════════

def find_latest_power_log(custom_path: str = None) -> str:
    if custom_path:
        if os.path.isfile(custom_path):
            return custom_path
        print(f"❌ 文件不存在: {custom_path}")
        sys.exit(1)

    install_dir = _find_hs_install_dir()
    if install_dir:
        log_path = _find_log_in_dir(os.path.join(install_dir, "Logs"))
        if log_path:
            return log_path

    for logs_dir in [
        r"D:\Battle.net\Hearthstone\Logs",
        r"C:\Program Files (x86)\Hearthstone\Logs",
        r"C:\Program Files\Hearthstone\Logs",
        r"D:\Hearthstone\Logs",
    ]:
        log_path = _find_log_in_dir(logs_dir)
        if log_path:
            return log_path
    return None


def _find_hs_install_dir() -> str:
    try:
        import winreg
    except ImportError:
        return None
    for hive, path in [
        (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WOW6432Node\Blizzard Entertainment\Hearthstone"),
        (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\Blizzard Entertainment\Hearthstone"),
        (winreg.HKEY_CURRENT_USER, r"SOFTWARE\Blizzard Entertainment\Hearthstone"),
    ]:
        try:
            key = winreg.OpenKey(hive, path)
            p, _ = winreg.QueryValueEx(key, "InstallPath")
            winreg.CloseKey(key)
            if p and os.path.isdir(p):
                return p
        except (FileNotFoundError, OSError):
            continue
    return None


def _find_log_in_dir(logs_dir: str) -> str:
    if not os.path.isdir(logs_dir):
        return None
    candidates = []
    for folder in glob.glob(os.path.join(logs_dir, "Hearthstone_*")):
        p = os.path.join(folder, "Power.log")
        if os.path.isfile(p):
            candidates.append((os.path.getmtime(p), p))
    root_log = os.path.join(logs_dir, "Power.log")
    if os.path.isfile(root_log):
        candidates.append((os.path.getmtime(root_log), root_log))
    if not candidates:
        return None
    candidates.sort(reverse=True)
    return candidates[0][1]


def _find_last_create_game_pos(path: str) -> int:
    pos = 0
    last_pos = 0
    with open(path, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            if _RE_CREATE_GAME.search(line):
                last_pos = pos
            pos += len(line.encode('utf-8', errors='replace'))
    return last_pos


# ═══════════════════════════════════════════════════════════
#  输出
# ═══════════════════════════════════════════════════════════

def print_game_result(game: Game, index: int = 0):
    print(f"\n{'─' * 50}")
    prefix = f"第 {index} 局" if index > 0 else "对局"
    print(f"🎮 {prefix}")

    if game.player_tag:
        print(f"👤 {game.player_tag}")
    if game.account_id_lo:
        print(f"   账号ID: {game.account_id_lo}")
    if game.game_seed:
        print(f"   GAME_SEED: {game.game_seed}")
    if game.hero_name:
        print(f"🦸 英雄: {game.hero_name} ({game.hero_card_id})")

    # 排名
    if game.placement_confirmed:
        print(f"🏆 排名: 第 {game.hero_placement} 名（确定）")
    elif game.hero_placement > 0:
        print(f"🏆 排名: 第 {game.hero_placement} 名（不确定，游戏内最终观测值）")
    else:
        print(f"🏆 排名: 未知（未观测到最终排名）")

    # 状态
    events = []
    if game.reconnected:
        events.append("断线重连")
    if game.conceded:
        events.append("投降")
    elif not game.end_time:
        events.append("未正常结束")
    if events:
        print(f"📊 {' + '.join(events)}")

    # 显示其他英雄
    my_key = (game.hero_card_id, None) if game.hero_card_id else None
    others = [
        h for h in game.all_heroes.values()
        if h.hero_name and h.hero_name != game.hero_name
    ]
    # 去重（按 hero_name）
    seen = set()
    unique_others = []
    for h in others:
        if h.hero_name not in seen:
            seen.add(h.hero_name)
            unique_others.append(h)

    if unique_others:
        print(f"\n📊 其他英雄:")
        for h in unique_others:
            print(f"   {h.hero_name} ({h.card_id})")

    # HearthMirror 对手信息
    if game.lobby_players:
        print(f"\n🔍 HearthMirror 对手信息:")
        for lp in game.lobby_players:
            print("   Lo={}, Hero={}".format(lp['lo'], lp['heroCardId']))

    print()


def print_summary(games: list[Game]):
    if not games:
        print("\n⚠️ 没有对局")
        return
    conceded = sum(1 for g in games if g.conceded)
    reconnected = sum(1 for g in games if g.reconnected)
    print(f"\n{'=' * 50}")
    print(f"📈 汇总: {len(games)} 局")
    print(f"   投降: {conceded} | 断线重连: {reconnected}")


# ═══════════════════════════════════════════════════════════
#  批量解析
# ═══════════════════════════════════════════════════════════

def parse_file(log_path: str):
    parser = Parser()
    print(f"📖 读取: {log_path}")
    print(f"📏 大小: {os.path.getsize(log_path) / 1024:.1f} KB\n")

    with open(log_path, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            event = parser.process_line(line)
            if event:
                _log_event(event, parser.game)

    if parser.game.is_active:
        parser._end_game()

    if not parser.games:
        print("⚠️ 未发现战棋对局")
        return

    print(f"\n🎯 共 {len(parser.games)} 局\n")
    for i, g in enumerate(parser.games, 1):
        print_game_result(g, i)
    print_summary(parser.games)


def _log_event(event: str, game: Game):
    ts = datetime.now().strftime("%H:%M:%S")
    if event == 'game_start':
        print(f"  [{ts}] 🎮 新局开始")
    elif event == 'reconnect':
        print(f"  [{ts}] 🔄 断线重连（忽略）")
    elif event == 'player_info':
        print(f"  [{ts}] 👤 {game.player_tag}")
    elif event == 'hero_entity':
        print(f"  [{ts}] 🦸 选定英雄: {game.hero_name or '(等待数据)'}")
    elif event == 'concede':
        print(f"  [{ts}] 🏳️ 投降")
    elif event == 'game_end':
        print(f"  [{ts}] 🏁 对局结束")
    elif event == 'not_bg':
        print(f"  [{ts}] ⚠️ 非战棋模式，跳过")


# ═══════════════════════════════════════════════════════════
#  实时监控
# ═══════════════════════════════════════════════════════════

def tail_log(log_path: str):
    running = True
    current_path = log_path
    check_interval = 0.1
    file_check_counter = 0
    file_check_every = 100

    def signal_handler(sig, frame):
        nonlocal running
        running = False
        print("\n⏹ 停止监控")

    signal.signal(signal.SIGINT, signal_handler)

    def scan_existing(file_path: str) -> tuple[Parser, int]:
        p = Parser()
        cg_pos = _find_last_create_game_pos(file_path)
        with open(file_path, 'r', encoding='utf-8', errors='replace') as f:
            f.seek(cg_pos)
            for line in f:
                p.process_line(line)
            end_pos = f.tell()
        return p, end_pos

    print(f"👁 监控: {current_path}")
    print(f"   (Ctrl+C 停止)\n")

    try:
        parser, pos = scan_existing(current_path)
        if parser.game.is_active:
            _print_mid_game(parser.game)
        else:
            pos = _get_file_end(current_path)
            print(f"   等待游戏开始...")
    except Exception as e:
        print(f"⚠️ 首次扫描失败: {e}")
        parser = Parser()
        pos = _get_file_end(current_path)

    while running:
        try:
            file_check_counter += 1
            if file_check_counter >= file_check_every:
                file_check_counter = 0
                new_path = _check_new_log_file(current_path)
                if new_path:
                    if parser.game.is_active:
                        print(f"\n🔄 游戏重启，当前对局中断")
                    current_path = new_path
                    print(f"🔄 切换: {current_path}")
                    try:
                        parser, pos = scan_existing(current_path)
                        if parser.game.is_active:
                            _print_mid_game(parser.game)
                        else:
                            pos = _get_file_end(current_path)
                            print(f"   等待游戏开始...")
                    except Exception as e:
                        print(f"⚠️ 扫描失败: {e}")
                        parser = Parser()
                        pos = _get_file_end(current_path)

            with open(current_path, 'r', encoding='utf-8', errors='replace') as f:
                f.seek(pos)
                lines = f.readlines()
                pos = f.tell()

            for line in lines:
                event = parser.process_line(line)
                if event:
                    if event in ('game_end', 'concede') and not parser.game.is_active:
                        print_game_result(parser.games[-1])
                    else:
                        _log_event(event, parser.game)

            time.sleep(check_interval)

        except FileNotFoundError:
            new_path = _check_new_log_file(current_path)
            if new_path:
                current_path = new_path
                try:
                    parser, pos = scan_existing(current_path)
                    print(f"🔄 日志切换: {current_path}")
                    if parser.game.is_active:
                        _print_mid_game(parser.game)
                    else:
                        pos = _get_file_end(current_path)
                except Exception:
                    parser = Parser()
                    pos = _get_file_end(current_path)
            else:
                print("❌ 日志消失，等待...")
                time.sleep(3)
        except Exception as e:
            print(f"⚠️ 错误: {e}")
            time.sleep(1)


def _print_mid_game(game: Game):
    print(f"{'─' * 50}")
    print(f"🎮 进行中的对局")
    if game.player_tag:
        print(f"👤 {game.player_tag}")
    if game.hero_name:
        print(f"🦸 {game.hero_name} ({game.hero_card_id})")
    if game.reconnected:
        print(f"🔄 已重连")
    print()


def _get_file_end(path: str) -> int:
    try:
        with open(path, 'r', encoding='utf-8', errors='replace') as f:
            f.seek(0, 2)
            return f.tell()
    except (FileNotFoundError, OSError):
        return 0


def _check_new_log_file(current_path: str) -> str:
    current_dir = os.path.dirname(current_path)
    parent = os.path.dirname(current_dir)
    basename = os.path.basename(current_dir)
    logs_dir = parent if basename.startswith("Hearthstone_") else current_dir
    if not os.path.isdir(logs_dir):
        return None
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
    try:
        current_mtime = os.path.getmtime(current_path)
    except OSError:
        current_mtime = 0
    candidates.sort(reverse=True)
    newest_mtime, newest_path = candidates[0]
    return newest_path if newest_mtime > current_mtime else None


# ═══════════════════════════════════════════════════════════
#  入口
# ═══════════════════════════════════════════════════════════

def main():
    if '--parse' in sys.argv:
        idx = sys.argv.index('--parse')
        path = sys.argv[idx + 1] if idx + 1 < len(sys.argv) else None
        log_path = find_latest_power_log(path)
        if not log_path:
            print("❌ 未找到 Power.log")
            sys.exit(1)
        parse_file(log_path)
        return

    path = sys.argv[1] if len(sys.argv) > 1 and not sys.argv[1].startswith('--') else None
    log_path = find_latest_power_log(path)
    if not log_path:
        print("❌ 未找到 Power.log")
        print("用法: python bg_parser.py [Power.log路径]")
        sys.exit(1)
    tail_log(log_path)


if __name__ == '__main__':
    main()
