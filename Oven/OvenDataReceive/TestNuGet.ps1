# NuGet 連線測試
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "NuGet 連線診斷" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

# 1. 測試 NuGet API 連線
Write-Host "[1] 測試 api.nuget.org 連線" -ForegroundColor Yellow
$nugetTest = Test-NetConnection -ComputerName api.nuget.org -Port 443 -WarningAction SilentlyContinue
if ($nugetTest.TcpTestSucceeded) {
    Write-Host "✅ NuGet API 可連線" -ForegroundColor Green
} else {
    Write-Host "❌ NuGet API 無法連線" -ForegroundColor Red
}

# 2. 檢查 Proxy 設定
Write-Host "`n[2] Proxy 設定" -ForegroundColor Yellow
$proxy = netsh winhttp show proxy
Write-Host $proxy

# 3. 檢查 NuGet 快取
Write-Host "`n[3] NuGet 快取位置" -ForegroundColor Yellow
$nugetCache = "$env:USERPROFILE\.nuget\packages"
if (Test-Path $nugetCache) {
    Write-Host "✅ 快取目錄存在: $nugetCache" -ForegroundColor Green
    $cacheSize = (Get-ChildItem $nugetCache -Recurse -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum / 1MB
    Write-Host "   快取大小: $([math]::Round($cacheSize, 2)) MB" -ForegroundColor Gray
} else {
    Write-Host "❌ 快取目錄不存在" -ForegroundColor Red
}

# 4. 測試套件還原
Write-Host "`n[4] 測試套件還原" -ForegroundColor Yellow
Write-Host "執行: dotnet restore --dry-run" -ForegroundColor Gray
$restoreTest = dotnet restore --dry-run 2>&1
if ($LASTEXITCODE -eq 0) {
    Write-Host "✅ 套件還原測試成功" -ForegroundColor Green
} else {
    Write-Host "❌ 套件還原測試失敗" -ForegroundColor Red
    Write-Host $restoreTest -ForegroundColor Gray
}

Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "診斷完成" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

if (-not $nugetTest.TcpTestSucceeded) {
    Write-Host "`n建議：" -ForegroundColor Yellow
    Write-Host "1. 檢查網路連線" -ForegroundColor Gray
    Write-Host "2. 如果在企業環境，設定 Proxy" -ForegroundColor Gray
    Write-Host "3. 使用離線還原：dotnet restore --packages ./packages" -ForegroundColor Gray
}
