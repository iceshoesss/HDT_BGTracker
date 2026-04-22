#!/usr/bin/env python3
"""
从 HearthstoneJSON API 拉取酒馆战棋英雄数据，生成 bg_heroes.json。
新英雄发布时运行一次即可。

用法：
  python update_heroes.py            # 默认输出到同目录 bg_heroes.json
  python update_heroes.py out.json   # 指定输出路径
"""

import json
import sys
import urllib.request

API_URL = "https://api.hearthstonejson.com/v1/latest/zhCN/cards.json"

def main():
    out_path = sys.argv[1] if len(sys.argv) > 1 else "bg_heroes.json"

    print(f"正在从 {API_URL} 下载卡牌数据...")
    with urllib.request.urlopen(API_URL) as resp:
        cards = json.loads(resp.read().decode("utf-8"))

    bg_heroes = {}
    for c in cards:
        if c.get("type") != "HERO":
            continue
        cid = c.get("id", "")
        if "Bacon" in cid or cid.startswith("BG") or cid.startswith("BGDUO"):
            name = c.get("name", "")
            if name:
                bg_heroes[cid] = name

    sorted_heroes = dict(sorted(bg_heroes.items()))

    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(sorted_heroes, f, ensure_ascii=False, indent=2)

    print(f"✅ 已写入 {len(sorted_heroes)} 个英雄 → {out_path} ({len(json.dumps(sorted_heroes, ensure_ascii=False))} bytes)")

if __name__ == "__main__":
    main()
