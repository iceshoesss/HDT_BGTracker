#!/bin/bash
# 同步远程改动，同时保留本地 app.py 中的数据库地址
set -e

echo "🔓 解锁 app.py..."
git update-index --no-skip-worktree league/app.py

echo "📦 暂存本地修改..."
git stash

echo "⬇️  拉取远程..."
git pull origin claw_version

echo "📦 恢复本地修改..."
git stash pop || echo "⚠️  没有可恢复的本地修改，跳过"

echo "🔒 重新锁定 app.py"
git update-index --skip-worktree league/app.py

echo "✅ 同步完成"
