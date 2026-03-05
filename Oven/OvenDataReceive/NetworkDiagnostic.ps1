# Modbus TCP 網路診斷工具
# 用途：診斷 192.168.61.144:4196 的連線問題

$targetIP = "192.168.61.144"
$targetPort = 4196

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Modbus TCP 網路診斷" -ForegroundColor Cyan
Write-Host "目標: $targetIP`:$targetPort" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

# 1. 檢查本機網路介面
Write-Host "[1] 本機網路介面" -ForegroundColor Yellow
Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.InterfaceAlias -notlike "*Loopback*" } |
    Select-Object InterfaceAlias, IPAddress, PrefixLength | Format-Table -AutoSize

# 2. Ping 測試
Write-Host "`n[2] Ping 測試" -ForegroundColor Yellow
$pingResult = Test-Connection -ComputerName $targetIP -Count 4 -ErrorAction SilentlyContinue
if ($pingResult) {
    Write-Host "✅ Ping 成功" -ForegroundColor Green
    $pingResult | Select-Object Address, ResponseTime, Status | Format-Table -AutoSize
} else {
    Write-Host "❌ Ping 失敗 - 設備可能離線或防火牆阻擋 ICMP" -ForegroundColor Red
}

# 3. 路由追蹤
Write-Host "`n[3] 路由追蹤 (前 5 跳)" -ForegroundColor Yellow
try {
    $tracert = Test-NetConnection -ComputerName $targetIP -TraceRoute -Hops 5 -WarningAction SilentlyContinue
    if ($tracert.TraceRoute) {
        $tracert.TraceRoute | ForEach-Object { Write-Host "  → $_" }
    }
} catch {
    Write-Host "路由追蹤失敗: $($_.Exception.Message)" -ForegroundColor Red
}

# 4. TCP Port 測試
Write-Host "`n[4] TCP Port $targetPort 測試" -ForegroundColor Yellow
$tcpTest = Test-NetConnection -ComputerName $targetIP -Port $targetPort -WarningAction SilentlyContinue
if ($tcpTest.TcpTestSucceeded) {
    Write-Host "✅ TCP Port $targetPort 可連線" -ForegroundColor Green
} else {
    Write-Host "❌ TCP Port $targetPort 無法連線" -ForegroundColor Red
    Write-Host "   可能原因：" -ForegroundColor Yellow
    Write-Host "   1. 設備未開機或未連線網路" -ForegroundColor Gray
    Write-Host "   2. Port 設定錯誤（檢查 appsettings.json）" -ForegroundColor Gray
    Write-Host "   3. 防火牆阻擋（本機或設備端）" -ForegroundColor Gray
    Write-Host "   4. VLAN 或網段隔離" -ForegroundColor Gray
}

# 5. 防火牆規則檢查
Write-Host "`n[5] Windows 防火牆規則檢查" -ForegroundColor Yellow
$firewallRules = Get-NetFirewallRule | Where-Object {
    $_.Enabled -eq $true -and
    $_.Direction -eq "Outbound" -and
    ($_.Action -eq "Block")
} | Select-Object -First 5

if ($firewallRules) {
    Write-Host "發現啟用的出站阻擋規則：" -ForegroundColor Yellow
    $firewallRules | Select-Object DisplayName, Action | Format-Table -AutoSize
} else {
    Write-Host "未發現明顯的出站阻擋規則" -ForegroundColor Green
}

# 6. ARP 快取檢查
Write-Host "`n[6] ARP 快取" -ForegroundColor Yellow
$arpCache = Get-NetNeighbor -IPAddress $targetIP -ErrorAction SilentlyContinue
if ($arpCache) {
    Write-Host "✅ ARP 快取中找到設備" -ForegroundColor Green
    $arpCache | Select-Object IPAddress, LinkLayerAddress, State | Format-Table -AutoSize
} else {
    Write-Host "❌ ARP 快取中未找到設備 - 設備可能不在同一網段" -ForegroundColor Red
}

# 7. 網段檢查
Write-Host "`n[7] 網段分析" -ForegroundColor Yellow
$localIPs = Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.InterfaceAlias -notlike "*Loopback*" }
$targetIPObj = [System.Net.IPAddress]::Parse($targetIP)
$targetBytes = $targetIPObj.GetAddressBytes()
$targetNetwork = "$($targetBytes[0]).$($targetBytes[1]).$($targetBytes[2])"

$inSameNetwork = $false
foreach ($ip in $localIPs) {
    $localBytes = $ip.IPAddress.GetAddressBytes()
    $localNetwork = "$($localBytes[0]).$($localBytes[1]).$($localBytes[2])"

    if ($localNetwork -eq $targetNetwork) {
        Write-Host "✅ 本機 IP $($ip.IPAddress) 與目標設備在同一網段 ($targetNetwork.0/24)" -ForegroundColor Green
        $inSameNetwork = $true
    }
}

if (-not $inSameNetwork) {
    Write-Host "⚠️ 本機與目標設備不在同一網段，需要路由或閘道" -ForegroundColor Yellow
    Write-Host "   目標網段: $targetNetwork.0/24" -ForegroundColor Gray
}

# 8. 診斷結論
Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "診斷結論" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

if ($tcpTest.TcpTestSucceeded) {
    Write-Host "✅ 網路連線正常，Modbus TCP 服務可連線" -ForegroundColor Green
} elseif ($pingResult) {
    Write-Host "⚠️ Ping 成功但 Port $targetPort 無法連線" -ForegroundColor Yellow
    Write-Host "   建議：" -ForegroundColor Yellow
    Write-Host "   1. 檢查設備的 Modbus TCP 服務是否啟動" -ForegroundColor Gray
    Write-Host "   2. 確認 Port 設定是否正確 (appsettings.json)" -ForegroundColor Gray
    Write-Host "   3. 檢查設備端防火牆設定" -ForegroundColor Gray
} else {
    Write-Host "❌ 設備完全不可達" -ForegroundColor Red
    Write-Host "   建議：" -ForegroundColor Yellow
    Write-Host "   1. 確認設備電源是否開啟" -ForegroundColor Gray
    Write-Host "   2. 檢查網路線是否連接" -ForegroundColor Gray
    Write-Host "   3. 確認 IP 位址是否正確" -ForegroundColor Gray
    Write-Host "   4. 檢查交換機或路由器設定" -ForegroundColor Gray
    Write-Host "   5. 確認是否有 VLAN 隔離" -ForegroundColor Gray
}

Write-Host "`n按任意鍵結束..."
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
