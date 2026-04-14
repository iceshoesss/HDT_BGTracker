#!/usr/bin/env python3
"""
toggle-test-mode.py — 切换 C# 插件测试/正常模式

测试模式：所有对局都强制标记为联赛对局（跳过等待组匹配）
正常模式：未匹配到等待组的对局视为普通天梯局

用法：
  python toggle-test-mode.py          # 显示当前状态
  python toggle-test-mode.py test     # 切换到测试模式
  python toggle-test-mode.py normal   # 切换到正常模式
  python toggle-test-mode.py flip     # 翻转

工作原理：基于代码中的 BEGIN/END TEST_MODE 标记进行整块替换。
"""

import sys
import os

os.chdir(os.path.dirname(os.path.abspath(__file__)) or ".")

BEGIN = "BEGIN TEST_MODE"
END = "END TEST_MODE"

CS_PATH = "HDT_BGTracker/RatingTracker.cs"

CS_NORMAL = '''\
                            // >>> BEGIN TEST_MODE
                            else
                            {
                                _isLeagueGame = false;
                                Log("CheckLeagueQueue: 未匹配到等待组，普通天梯局");
                            }
                            // <<< END TEST_MODE'''

CS_TEST = '''\
                            // >>> BEGIN TEST_MODE
                            else
                            {
                                // [TESTING] 暂时跳过联赛判断，所有对局都当联赛处理
                                _isLeagueGame = true;
                                Log("CheckLeagueQueue: [TESTING] 跳过等待组匹配，强制标记为联赛对局");
                            }
                            // <<< END TEST_MODE'''


def find_marker_block(content: str) -> str | None:
    """返回两个标记之间的完整文本（含标记行），找不到返回 None"""
    begin_idx = content.find(BEGIN)
    end_idx = content.find(END)
    if begin_idx < 0 or end_idx < 0:
        return None
    block_start = content.rfind("\n", 0, begin_idx) + 1
    end_line_end = content.find("\n", end_idx)
    if end_line_end < 0:
        end_line_end = len(content)
    return content[block_start:end_line_end]


def detect_mode(content: str) -> str | None:
    block = find_marker_block(content)
    if block is None:
        return None
    def strip_markers(b):
        return "\n".join(
            line for line in b.split("\n")
            if BEGIN not in line and END not in line
        )
    block_core = strip_markers(block)
    if block_core == strip_markers(CS_NORMAL):
        return "normal"
    if block_core == strip_markers(CS_TEST):
        return "test"
    return None


def replace_block(content: str, new_block: str) -> str:
    old_block = find_marker_block(content)
    if old_block is None:
        raise ValueError(f"找不到 {BEGIN}/{END} 标记")
    return content.replace(old_block, new_block, 1)


def main():
    if not os.path.exists(CS_PATH):
        print(f"⚠ 找不到 {CS_PATH}")
        sys.exit(1)

    with open(CS_PATH, "r", encoding="utf-8") as f:
        cs_content = f.read()

    mode = detect_mode(cs_content)
    if mode is None:
        print(f"⚠ {CS_PATH} 无法识别模式，TEST_MODE 标记可能被修改")
        sys.exit(1)

    print(f"[插件] 当前模式: {mode}")

    args = sys.argv[1:]
    if not args:
        sys.exit(0)

    target = args[0]
    if target == "flip":
        target = "test" if mode == "normal" else "normal"
    if target not in ("test", "normal"):
        print(f"用法: {sys.argv[0]} [test|normal|flip]")
        sys.exit(1)

    if target == mode:
        print(f"已经是 {target} 模式，无需切换")
        sys.exit(0)

    print(f"[插件] 切换到: {target} 模式")

    new_content = replace_block(cs_content, CS_TEST if target == "test" else CS_NORMAL)
    with open(CS_PATH, "w", encoding="utf-8") as f:
        f.write(new_content)

    print(f"✅ 插件已切换到 {target} 模式")
    os.system(f"git diff --stat {CS_PATH}")


if __name__ == "__main__":
    main()
