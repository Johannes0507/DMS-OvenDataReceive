# 掃描本機 Wi-Fi 網段的 Modbus TCP 設備
Write-Host "掃描 192.168.211.x 網段的 Modbus TCP 設備 (Port 502)..." -ForegroundColor Cyan
Write-Host "這可能需要幾分鐘..." -ForegroundColor Yellow
Write-Host ""

$found = @()
$baseIP = "192.168.211"

# 掃描常用的 IP 範圍（避免掃描全部 254 個）
$ranges = @(
    1..10,      # 路由器、閘道器
    100..150,   # 常用設備範圍
    200..210    # 高編號範圍
)

$total = ($ranges | ForEach-Object { $_ }).Count
$current = 0

foreach ($range in $ranges) {
    foreach ($i in $range) {
        $current++
        $ip = "$baseIP.$i"
        Write-Progress -Activity "掃描網段" -Status "測試 $ip" -PercentComplete (($current / $total) * 100)

        $result = Test-NetConnection -ComputerName $ip -Port 502 -WarningAction SilentlyContinue -InformationLevel Quiet -ErrorAction SilentlyContinue

        if ($result) {
            Write-Host "✅ 找到設備: $ip:502" -ForegroundColor Green
            $found += $ip
        }
    }
}

Write-Progress -Activity "掃描網段" -Completed

Write-Host ""
if ($found.Count -gt 0) {
    Write-Host "========================================" -ForegroundColor Green
    Write-Host "找到 $($found.Count) 個 Modbus TCP 設備:" -ForegroundColor Green
    $found | ForEach-Object { Write-Host "  $_:502" -ForegroundColor Cyan }
    Write-Host "========================================" -ForegroundColor Green
    Write-Host ""
    Write-Host "請更新 appsettings.json 中的 IpAddress 為上述 IP" -ForegroundColor Yellow
} else {
    Write-Host "❌ 未找到 Modbus TCP 設備" -ForegroundColor Red
    Write-Host ""
    Write-Host "可能原因:" -ForegroundColor Yellow
    Write-Host "  1. 設備未開機或未連接網路" -ForegroundColor Gray
    Write-Host "  2. 設備在其他網段" -ForegroundColor Gray
    Write-Host "  3. 設備使用非標準 Port (不是 502)" -ForegroundColor Gray
    Write-Host "  4. 防火牆阻擋" -ForegroundColor Gray
}
