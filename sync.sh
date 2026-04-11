#!/bin/bash
# 同步远程改动，同时保留本地 app.py 和 RatingTracker.cs 中的配置
set -e

LOCKED_FILES=("league/app.py" "HDT_BGTracker/RatingTracker.cs")

echo "🔓 解锁保护文件..."
for f in "${LOCKED_FILES[@]}"; do
    git update-index --no-skip-worktree "$f"
done

echo "📦 暂存本地修改..."
git stash

echo "⬇️  拉取远程..."
git pull origin claw_version

echo "📦 恢复本地修改..."
git stash pop || echo "⚠️  没有可恢复的本地修改，跳过"

echo "🔒 重新锁定保护文件..."
for f in "${LOCKED_FILES[@]}"; do
    git update-index --skip-worktree "$f"
done

echo "✅ 同步完成"
