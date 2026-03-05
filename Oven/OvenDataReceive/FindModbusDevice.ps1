# Modbus TCP 設備掃描工具
# 掃描本機所有網段，尋找 Port 4196 的設備

param(
    [int]$Port = 4196,
    [int]$TimeoutMs = 100
)

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Modbus TCP 設備掃描" -ForegroundColor Cyan
Write-Host "掃描 Port: $Port" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

# 取得本機所有 IPv4 位址
$localIPs = Get-NetIPAddress -AddressFamily IPv4 |
    Where-Object { $_.InterfaceAlias -notlike "*Loopback*" -and $_.IPAddress -notlike "169.254.*" }

$networksToScan = @()

foreach ($ip in $localIPs) {
    $ipBytes = $ip.IPAddress.Split('.')
    $network = "$($ipBytes[0]).$($ipBytes[1]).$($ipBytes[2])"

    if ($networksToScan -notcontains $network) {
        $networksToScan += $network
        Write-Host "將掃描網段: $network.0/24" -ForegroundColor Yellow
    }
}

# 新增目標網段（如果不在本機網段中）
$targetNetwork = "192.168.61"
if ($networksToScan -notcontains $targetNetwork) {
    $networksToScan += $targetNetwork
    Write-Host "將掃描目標網段: $targetNetwork.0/24" -ForegroundColor Yellow
}

Write-Host "`n開始掃描...`n" -ForegroundColor Green

$foundDevices = @()
$totalScanned = 0

foreach ($network in $networksToScan) {
    Write-Host "掃描 $network.0/24..." -ForegroundColor Cyan

    $jobs = @()

    # 並行掃描 1-254
    1..254 | ForEach-Object {
        $ip = "$network.$_"
        $totalScanned++

        $job = Start-Job -ScriptBlock {
            param($targetIP, $targetPort, $timeout)
            try {
                $tcpClient = New-Object System.Net.Sockets.TcpClient
                $asyncResult = $tcpClient.BeginConnect($targetIP, $targetPort, $null, $null)
                $wait = $asyncResult.AsyncWaitHandle.WaitOne($timeout, $false)

                if ($wait) {
                    try {
                        $tcpClient.EndConnect($asyncResult)
                        $tcpClient.Close()
                        return @{IP=$targetIP; Success=$true}
                    } catch {
                        return $null
                    }
                } else {
                    $tcpClient.Close()
                    return $null
                }
            } catch {
                return $null
            }
        } -ArgumentList $ip, $Port, $TimeoutMs

        $jobs += $job
    }

    # 等待所有 Job 完成
    $jobs | Wait-Job -Timeout 30 | Out-Null

    # 收集結果
    foreach ($job in $jobs) {
        $result = Receive-Job -Job $job -ErrorAction SilentlyContinue
        if ($result -and $result.Success) {
            $foundDevices += $result.IP
            Write-Host "  ✅ 找到設備: $($result.IP):$Port" -ForegroundColor Green
        }
        Remove-Job -Job $job -Force -ErrorAction SilentlyContinue
    }

    Write-Host ""
}

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "掃描完成" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "共掃描: $totalScanned 個 IP" -ForegroundColor Gray
Write-Host "找到設備: $($foundDevices.Count) 個`n" -ForegroundColor Gray

if ($foundDevices.Count -gt 0) {
    Write-Host "發現以下 Modbus TCP 設備：" -ForegroundColor Green
    foreach ($device in $foundDevices) {
        Write-Host "  • $device`:$Port" -ForegroundColor Cyan
    }

    Write-Host "`n建議：" -ForegroundColor Yellow
    Write-Host "1. 更新 appsettings.json 中的 IpAddress" -ForegroundColor Gray
    Write-Host "2. 執行程式測試連線" -ForegroundColor Gray
} else {
    Write-Host "❌ 未找到任何設備在 Port $Port" -ForegroundColor Red
    Write-Host "`n可能原因：" -ForegroundColor Yellow
    Write-Host "1. 設備未開機或未連接網路" -ForegroundColor Gray
    Write-Host "2. 設備使用不同的 Port（常見：502, 503, 4196）" -ForegroundColor Gray
    Write-Host "3. 設備在不同的網段且無路由" -ForegroundColor Gray
    Write-Host "4. 防火牆阻擋連線" -ForegroundColor Gray

    Write-Host "`n建議：" -ForegroundColor Yellow
    Write-Host "1. 確認設備電源和網路連接" -ForegroundColor Gray
    Write-Host "2. 嘗試掃描其他常見 Port：" -ForegroundColor Gray
    Write-Host "   .\FindModbusDevice.ps1 -Port 502" -ForegroundColor Cyan
    Write-Host "   .\FindModbusDevice.ps1 -Port 503" -ForegroundColor Cyan
    Write-Host "3. 查看設備說明書確認 IP 和 Port" -ForegroundColor Gray
}

Write-Host ""
