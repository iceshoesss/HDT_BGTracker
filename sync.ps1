# 同步远程改动，同时保留本地 app.py 中的数据库地址
$ErrorActionPreference = "Stop"

Write-Host "🔓 解锁 app.py..." -ForegroundColor Cyan
git update-index --no-skip-worktree league/app.py

Write-Host "📦 暂存本地修改..." -ForegroundColor Cyan
git stash

Write-Host "⬇️  拉取远程..." -ForegroundColor Cyan
git pull origin claw_version

Write-Host "📦 恢复本地修改..." -ForegroundColor Cyan
git stash pop 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Host "⚠️  没有可恢复的本地修改，跳过" -ForegroundColor Yellow
}

Write-Host "🔒 重新锁定 app.py" -ForegroundColor Cyan
git update-index --skip-worktree league/app.py

Write-Host "✅ 同步完成" -ForegroundColor Green
