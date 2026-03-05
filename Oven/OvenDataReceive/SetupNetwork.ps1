# 網路設定腳本：將本機加入 192.168.61.x 網段
# 需要以系統管理員身分執行

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "設定本機網路以連接 192.168.61.139" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# 檢查是否以系統管理員執行
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "❌ 此腳本需要以系統管理員身分執行" -ForegroundColor Red
    Write-Host ""
    Write-Host "請：" -ForegroundColor Yellow
    Write-Host "  1. 右鍵點擊 PowerShell" -ForegroundColor Gray
    Write-Host "  2. 選擇「以系統管理員身分執行」" -ForegroundColor Gray
    Write-Host "  3. 重新執行此腳本" -ForegroundColor Gray
    Write-Host ""
    Read-Host "按 Enter 鍵結束"
    exit
}

Write-Host "✅ 已確認系統管理員權限" -ForegroundColor Green
Write-Host ""

# 列出所有網路介面
Write-Host "目前的網路介面：" -ForegroundColor Yellow
Get-NetAdapter | Where-Object {$_.Status -eq 'Up'} | ForEach-Object {
    $adapter = $_
    $ip = Get-NetIPAddress -InterfaceIndex $adapter.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue
    Write-Host "  [$($adapter.ifIndex)] $($adapter.Name) - $($ip.IPAddress)" -ForegroundColor Cyan
}
Write-Host ""

# 選擇要設定的介面
Write-Host "請選擇要設定的網路介面：" -ForegroundColor Yellow
Write-Host "  [1] 使用乙太網路（有線，推薦）" -ForegroundColor Gray
Write-Host "  [2] 使用 Wi-Fi（無線）" -ForegroundColor Gray
Write-Host "  [3] 手動輸入介面索引" -ForegroundColor Gray
Write-Host "  [0] 取消" -ForegroundColor Gray
Write-Host ""

$choice = Read-Host "選擇"

$adapterIndex = $null

switch ($choice) {
    "1" {
        $ethernet = Get-NetAdapter | Where-Object {$_.Status -eq 'Up' -and $_.Name -like '*乙太*'} | Select-Object -First 1
        if ($ethernet) {
            $adapterIndex = $ethernet.ifIndex
            Write-Host "✅ 選擇：$($ethernet.Name)" -ForegroundColor Green
        } else {
            Write-Host "❌ 找不到已連接的乙太網路介面" -ForegroundColor Red
            Read-Host "按 Enter 鍵結束"
            exit
        }
    }
    "2" {
        $wifi = Get-NetAdapter | Where-Object {$_.Status -eq 'Up' -and $_.Name -like '*Wi-Fi*'} | Select-Object -First 1
        if ($wifi) {
            $adapterIndex = $wifi.ifIndex
            Write-Host "✅ 選擇：$($wifi.Name)" -ForegroundColor Green
        } else {
            Write-Host "❌ 找不到已連接的 Wi-Fi 介面" -ForegroundColor Red
            Read-Host "按 Enter 鍵結束"
            exit
        }
    }
    "3" {
        $adapterIndex = Read-Host "請輸入介面索引"
    }
    "0" {
        Write-Host "已取消" -ForegroundColor Yellow
        Read-Host "按 Enter 鍵結束"
        exit
    }
    default {
        Write-Host "❌ 無效的選擇" -ForegroundColor Red
        Read-Host "按 Enter 鍵結束"
        exit
    }
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "將設定為：" -ForegroundColor Yellow
Write-Host "  IP 位址：      192.168.61.100" -ForegroundColor Cyan
Write-Host "  子網路遮罩：    255.255.255.0" -ForegroundColor Cyan
Write-Host "  預設閘道：      192.168.61.1" -ForegroundColor Cyan
Write-Host "  DNS：          8.8.8.8, 8.8.4.4" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "⚠️  警告：這將修改網路設定，可能會暫時中斷網路連線" -ForegroundColor Yellow
Write-Host ""

$confirm = Read-Host "確定要繼續嗎？(Y/N)"

if ($confirm -ne 'Y' -and $confirm -ne 'y') {
    Write-Host "已取消" -ForegroundColor Yellow
    Read-Host "按 Enter 鍵結束"
    exit
}

Write-Host ""
Write-Host "正在設定網路..." -ForegroundColor Yellow

try {
    # 移除現有 IP 設定（DHCP）
    Remove-NetIPAddress -InterfaceIndex $adapterIndex -Confirm:$false -ErrorAction SilentlyContinue
    Remove-NetRoute -InterfaceIndex $adapterIndex -Confirm:$false -ErrorAction SilentlyContinue

    # 設定新的靜態 IP
    New-NetIPAddress -InterfaceIndex $adapterIndex -IPAddress "192.168.61.100" -PrefixLength 24 -DefaultGateway "192.168.61.1" -ErrorAction Stop | Out-Null

    # 設定 DNS
    Set-DnsClientServerAddress -InterfaceIndex $adapterIndex -ServerAddresses ("8.8.8.8", "8.8.4.4") -ErrorAction Stop

    Write-Host ""
    Write-Host "✅ 網路設定完成！" -ForegroundColor Green
    Write-Host ""

    # 測試連線
    Write-Host "測試連線到 192.168.61.139..." -ForegroundColor Yellow
    $pingResult = Test-Connection -ComputerName "192.168.61.139" -Count 2 -Quiet -ErrorAction SilentlyContinue

    if ($pingResult) {
        Write-Host "✅ Ping 成功！" -ForegroundColor Green
    } else {
        Write-Host "⚠️  Ping 失敗，但這可能是因為設備阻擋 ICMP" -ForegroundColor Yellow
    }

    Write-Host ""
    Write-Host "測試 Modbus TCP Port 502..." -ForegroundColor Yellow
    $portResult = Test-NetConnection -ComputerName "192.168.61.139" -Port 502 -WarningAction SilentlyContinue

    if ($portResult.TcpTestSucceeded) {
        Write-Host "✅ Port 502 連線成功！設備可達！" -ForegroundColor Green
    } else {
        Write-Host "❌ Port 502 無法連線" -ForegroundColor Red
        Write-Host "   請確認：" -ForegroundColor Yellow
        Write-Host "     1. 設備電源已開啟" -ForegroundColor Gray
        Write-Host "     2. 設備 Modbus TCP 服務已啟動" -ForegroundColor Gray
        Write-Host "     3. 設備 IP 確實是 192.168.61.139" -ForegroundColor Gray
    }

    Write-Host ""
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "當前網路設定：" -ForegroundColor Yellow
    Get-NetIPAddress -InterfaceIndex $adapterIndex -AddressFamily IPv4 | Format-Table -Property IPAddress, PrefixLength, InterfaceAlias
    Write-Host "========================================" -ForegroundColor Cyan

} catch {
    Write-Host "❌ 設定失敗：$($_.Exception.Message)" -ForegroundColor Red
}

Write-Host ""
Write-Host "如需還原為 DHCP（自動取得 IP），請執行：" -ForegroundColor Yellow
Write-Host "  Set-NetIPInterface -InterfaceIndex $adapterIndex -Dhcp Enabled" -ForegroundColor Gray
Write-Host ""

Read-Host "按 Enter 鍵結束"
