#!/usr/bin/env python3
"""
test_lobby_reader.py — 通过 pythonnet + HearthMirror 读取 BG 大厅玩家信息

用法：
  pip install pythonnet
  python test_lobby_reader.py --hdt "D:\HearthstoneDeckTracker"

需要：
  - Windows 系统
  - 炉石客户端正在运行（至少进入 BG 选英雄阶段）
  - pythonnet（pip install pythonnet）
  - .NET Framework 4.7.2（Win10/11 自带）
"""

import sys
import os
import argparse


def main():
    parser = argparse.ArgumentParser(description="读取 BG 大厅对手信息")
    parser.add_argument("--hdt", required=True, help="HDT 安装目录路径")
    args = parser.parse_args()

    hdt_dir = args.hdt
    if not os.path.isdir(hdt_dir):
        print(f"❌ HDT 目录不存在: {hdt_dir}")
        sys.exit(1)

    hm_dll = os.path.join(hdt_dir, "HearthMirror.dll")
    if not os.path.isfile(hm_dll):
        print(f"❌ 未找到 HearthMirror.dll: {hm_dll}")
        sys.exit(1)

    print(f"✅ HDT 目录: {hdt_dir}")
    print(f"✅ HearthMirror.dll: {hm_dll}\n")

    # ── 加载 .NET 运行时 ──
    print("--- 加载 pythonnet ---")
    try:
        import clr
    except ImportError:
        print("❌ 未安装 pythonnet，请运行: pip install pythonnet")
        sys.exit(1)

    # ── 加载 HearthMirror ──
    print("--- 加载 HearthMirror.dll ---")
    try:
        clr.AddReference(hm_dll)
        print("  ✅ 加载成功")
    except Exception as e:
        print(f"  ❌ 加载失败: {e}")
        sys.exit(1)

    # ── 检查 Hearthstone 进程 ──
    print("\n--- 检查 Hearthstone 进程 ---")
    import System.Diagnostics as Diag
    hs_procs = Diag.Process.GetProcessesByName("Hearthstone")
    if hs_procs.Length == 0:
        print("  ❌ 未找到 Hearthstone 进程，请先启动游戏")
        sys.exit(1)
    print(f"  ✅ 进程 PID={hs_procs[0].Id}")

    # ── 方式 1: Reflection 直接读 ──
    print("\n--- 方式 1: HearthMirror.Reflection ---")
    try:
        import HearthMirror
        from HearthMirror import Reflection

        # 尝试 BattlegroundsLobbyInfo
        lobby = Reflection.BattlegroundsLobbyInfo
        if lobby is None:
            print("  ⚠️ BattlegroundsLobbyInfo 为 null（可能不在 BG 游戏中）")
        else:
            print(f"  ✅ 获取成功，类型: {lobby.GetType().FullName}")
            print_lobby_players(lobby)
    except Exception as e:
        print(f"  ❌ {type(e).__name__}: {e}")

    # ── 方式 2: 尝试 Reflection 的其他入口 ──
    print("\n--- 方式 2: 反射探索 HearthMirror API ---")
    try:
        import System.Reflection as Refl

        hm_assembly = Refl.Assembly.GetAssembly(HearthMirror.Reflection)
        print(f"  Assembly: {hm_assembly.FullName}")

        # 列出所有公开类型
        types = hm_assembly.GetExportedTypes()
        for t in sorted(types, key=lambda x: x.FullName):
            print(f"  📋 {t.FullName}")
    except Exception as e:
        print(f"  ❌ {type(e).__name__}: {e}")

    # ── 方式 3: 尝试 LobbyPlayerList ──
    print("\n--- 方式 3: LobbyPlayerList ---")
    try:
        from HearthMirror.Objects import LobbyPlayerList

        lobby_players = LobbyPlayerList()
        if lobby_players.Players is not None:
            print(f"  ✅ Players 数量: {lobby_players.Players.Count}")
            for p in lobby_players.Players:
                print(f"     Name={p.Name}, Lo={p.AccountId.Lo}, Hero={p.HeroCardId}")
        else:
            print("  ⚠️ Players 为 null")
    except Exception as e:
        print(f"  ❌ {type(e).__name__}: {e}")

    print("\n=== 测试结束 ===")


def print_lobby_players(lobby):
    """打印大厅玩家信息"""
    try:
        players = lobby.Players
        if players is None or players.Count == 0:
            print("  ⚠️ 没有玩家数据")
            return

        print(f"\n  📊 大厅玩家 ({players.Count} 人):")
        for i, p in enumerate(players):
            name = p.Name if p.Name else "?"
            hero = p.HeroCardId if p.HeroCardId else "?"

            # AccountId 可能为 null
            lo = "?"
            hi = "?"
            if p.AccountId is not None:
                lo = str(p.AccountId.Lo)
                hi = str(p.AccountId.Hi)

            print(f"     [{i + 1}] Name={name}, AccountId(Hi={hi}, Lo={lo}), Hero={hero}")
    except Exception as e:
        print(f"  ❌ 打印失败: {type(e).__name__}: {e}")


if __name__ == "__main__":
    main()
