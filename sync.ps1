# Sync remote changes while preserving local app.py and RatingTracker.cs
$ErrorActionPreference = "Stop"

$lockedFiles = @("league/app.py", "HDT_BGTracker/RatingTracker.cs")

Write-Host "[1/5] Unlocking protected files..." -ForegroundColor Cyan
foreach ($f in $lockedFiles) {
    git update-index --no-skip-worktree $f
}

Write-Host "[2/5] Stashing local changes..." -ForegroundColor Cyan
git stash

Write-Host "[3/5] Pulling from remote..." -ForegroundColor Cyan
# Windows 下文件可能被 IDE/dotnet 构建进程占用导致 unlink 失败
try { Stop-Process -Name "MSBuild" -Force -ErrorAction SilentlyContinue } catch {}
git pull origin claw_version

Write-Host "[4/5] Restoring local changes..." -ForegroundColor Cyan
$stashResult = git stash pop 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "  No local changes to restore, skipping." -ForegroundColor Yellow
}

Write-Host "[5/5] Locking protected files..." -ForegroundColor Cyan
foreach ($f in $lockedFiles) {
    git update-index --skip-worktree $f
}

Write-Host "Done!" -ForegroundColor Green
