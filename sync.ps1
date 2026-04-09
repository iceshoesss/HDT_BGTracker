# Sync remote changes while preserving local app.py (MongoDB address)
$ErrorActionPreference = "Stop"

Write-Host "[1/5] Unlocking app.py..." -ForegroundColor Cyan
git update-index --no-skip-worktree league/app.py

Write-Host "[2/5] Stashing local changes..." -ForegroundColor Cyan
git stash

Write-Host "[3/5] Pulling from remote..." -ForegroundColor Cyan
git pull origin claw_version

Write-Host "[4/5] Restoring local changes..." -ForegroundColor Cyan
$stashResult = git stash pop 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "  No local changes to restore, skipping." -ForegroundColor Yellow
}

Write-Host "[5/5] Locking app.py" -ForegroundColor Cyan
git update-index --skip-worktree league/app.py

Write-Host "Done!" -ForegroundColor Green
