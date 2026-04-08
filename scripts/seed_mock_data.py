#!/usr/bin/env python3
"""
向 MongoDB 注入模拟数据
在 MongoDB 所在机器上运行：python3 seed_mock_data.py
需要 pymongo：pip install pymongo
"""

from pymongo import MongoClient
from datetime import datetime, timedelta

MONGO_URL = "mongodb://YOUR_MONGO_HOST:27017"
DB_NAME = "hearthstone"

client = MongoClient(MONGO_URL)
db = client[DB_NAME]

# ── 清空旧数据 ──────────────────────────────────────
db.bg_ratings.drop()
db.league_matches.drop()
print("已清空 bg_ratings 和 league_matches")

# ── 模拟玩家 ────────────────────────────────────────
PLAYERS = [
    {"battleTag": "南怀北瑾丨少头脑#5267", "accountIdLo": "1708070391", "hero": "阿莱克丝塔萨"},
    {"battleTag": "疾风剑豪#8842",        "accountIdLo": "2831045762", "hero": "挂机的阿凯"},
    {"battleTag": "暗夜精灵#3351",        "accountIdLo": "5520198374", "hero": "尤朵拉船长"},
    {"battleTag": "冰霜法师#6677",        "accountIdLo": "8843012456", "hero": "苔丝·格雷迈恩"},
    {"battleTag": "烈焰凤凰#1199",        "accountIdLo": "3367201845", "hero": "阮大师"},
    {"battleTag": "星辰大海#4420",        "accountIdLo": "7712034589", "hero": "诺兹多姆"},
    {"battleTag": "雷霆战神#9908",        "accountIdLo": "1245890367", "hero": "伊瑟拉"},
    {"battleTag": "月光骑士#5563",        "accountIdLo": "4490127835", "hero": "永恒者托奇"},
    {"battleTag": "影舞者#2276",          "accountIdLo": "6634019278", "hero": "舞者达瑞尔"},
    {"battleTag": "圣光守护#7734",        "accountIdLo": "9901234567", "hero": "奥拉基尔"},
    {"battleTag": "虚空行者#8891",        "accountIdLo": "3345678901", "hero": "穆克拉"},
    {"battleTag": "龙裔战士#1102",        "accountIdLo": "5578901234", "hero": "希尔瓦娜斯"},
]

# ── 注入 bg_ratings（模拟插件原来的评分数据）────
import random
random.seed(42)

bg_docs = []
for p in PLAYERS:
    rating = random.randint(5000, 9000)
    games = random.randint(10, 30)
    changes = [random.randint(-40, 40) for _ in range(games)]
    placements = [random.randint(1, 8) for _ in range(games)]

    bg_docs.append({
        "playerId": p["battleTag"],
        "accountIdLo": p["accountIdLo"],
        "rating": rating,
        "lastRating": rating - changes[-1] if changes else rating,
        "ratingChange": changes[-1] if changes else 0,
        "ratingChanges": changes,
        "placements": placements,
        "gameCount": games,
        "mode": "solo",
        "timestamp": datetime.utcnow().isoformat() + "Z",
        "region": "CN",
        "games": []
    })

db.bg_ratings.insert_many(bg_docs)
print(f"bg_ratings: 插入 {len(bg_docs)} 条")

# ── 注入 league_matches ────────────────────────────
BASE_TIME = datetime(2026, 4, 8, 4, 0, 0)  # UTC
HEROES = ["阿莱克丝塔萨", "苔丝·格雷迈恩", "阮大师", "诺兹多姆", "舞者达瑞尔",
          "奥拉基尔", "亚煞极", "沙德沃克", "永恒者托奇", "穆克拉",
          "希尔瓦娜斯", "伊瑟拉", "挂机的阿凯", "塔姆辛·罗姆", "疯狂菌子塔"]

match_docs = []

# 8 场已完成的比赛
for i in range(8):
    sampled = random.sample(PLAYERS, 8)
    started = BASE_TIME + timedelta(hours=i * 1.5, minutes=random.randint(0, 20))
    ended = started + timedelta(minutes=random.randint(12, 20))

    players = []
    for rank, p in enumerate(sampled, 1):
        points = 9 if rank == 1 else max(1, 9 - rank)
        players.append({
            "accountIdLo": p["accountIdLo"],
            "battleTag": p["battleTag"],
            "displayName": p["battleTag"].split("#")[0],
            "heroName": random.choice(HEROES),
            "placement": rank,
            "points": points,
        })

    match_docs.append({
        "gameUuid": f"mock-completed-{i+1:03d}-{random.randint(1000,9999)}",
        "players": players,
        "region": "CN",
        "mode": "solo",
        "startedAt": started.isoformat() + "Z",
        "endedAt": ended.isoformat() + "Z",
    })

# 2 场进行中的比赛（endedAt=null，placement=null）
for i in range(2):
    sampled = random.sample(PLAYERS, 8)
    started = BASE_TIME + timedelta(hours=12 + i * 0.5, minutes=random.randint(0, 10))

    players = []
    for p in sampled:
        players.append({
            "accountIdLo": p["accountIdLo"],
            "battleTag": p["battleTag"],
            "displayName": p["battleTag"].split("#")[0],
            "heroName": random.choice(HEROES),
            "placement": None,
            "points": None,
        })

    match_docs.append({
        "gameUuid": f"mock-active-{i+1:03d}-{random.randint(1000,9999)}",
        "players": players,
        "region": "CN",
        "mode": "solo",
        "startedAt": started.isoformat() + "Z",
        "endedAt": None,
    })

db.league_matches.insert_many(match_docs)
print(f"league_matches: 插入 {len(match_docs)} 条（8 已完成 + 2 进行中）")

# ── 创建索引 ────────────────────────────────────────
db.league_matches.create_index("gameUuid", unique=True)
db.league_matches.create_index("endedAt")
db.league_matches.create_index("players.placement")
print("索引创建完成")

# ── 验证 ────────────────────────────────────────────
completed = db.league_matches.count_documents({"endedAt": {"$ne": None}})
active = db.league_matches.count_documents({"endedAt": None})
print(f"\n验证: league_matches 已完成={completed} 进行中={active}")
print(f"验证: bg_ratings={db.bg_ratings.count_documents({})} 条")

client.close()
print("\n✅ 完成")
