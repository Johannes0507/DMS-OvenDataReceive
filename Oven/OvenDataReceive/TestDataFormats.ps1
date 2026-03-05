# Modbus 資料格式自動測試腳本

$configFile = "appsettings.json"
$backupFile = "appsettings.json.backup.test"

# 備份原始設定
Copy-Item $configFile $backupFile -Force
Write-Host "已備份原始設定檔" -ForegroundColor Green

# 測試組合
$testCases = @(
    @{
        Name = "測試 1: Unsigned + Scale 0.01"
        TempUseSigned = $false
        TempSwapBytes = $false
        TempScale = 0.01
    },
    @{
        Name = "測試 2: Signed + SwapBytes + Scale 0.1"
        TempUseSigned = $true
        TempSwapBytes = $true
        TempScale = 0.1
    },
    @{
        Name = "測試 3: Unsigned + SwapBytes + Scale 0.1"
        TempUseSigned = $false
        TempSwapBytes = $true
        TempScale = 0.1
    },
    @{
        Name = "測試 4: Unsigned + Scale 0.1 (目前設定改 Unsigned)"
        TempUseSigned = $false
        TempSwapBytes = $false
        TempScale = 0.1
    }
)

foreach ($test in $testCases) {
    Write-Host "`n========================================" -ForegroundColor Cyan
    Write-Host $test.Name -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan

    # 讀取設定檔
    $json = Get-Content $configFile -Raw | ConvertFrom-Json

    # 修改設定
    $json.Modbus.TempUseSigned = $test.TempUseSigned
    $json.Modbus.TempSwapBytes = $test.TempSwapBytes
    $json.Modbus.TempScale = $test.TempScale

    # 寫回設定檔
    $json | ConvertTo-Json -Depth 10 | Set-Content $configFile

    Write-Host "設定:" -ForegroundColor Yellow
    Write-Host "  TempUseSigned: $($test.TempUseSigned)" -ForegroundColor Gray
    Write-Host "  TempSwapBytes: $($test.TempSwapBytes)" -ForegroundColor Gray
    Write-Host "  TempScale: $($test.TempScale)" -ForegroundColor Gray

    Write-Host "`n啟動程式測試 (10 秒)..." -ForegroundColor Yellow

    # 啟動程式
    $process = Start-Process -FilePath "dotnet" -ArgumentList "run --no-restore" -PassThru -NoNewWindow -RedirectStandardOutput "test_output.txt" -RedirectStandardError "test_error.txt"

    # 等待 10 秒
    Start-Sleep -Seconds 10

    # 停止程式
    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue

    # 顯示結果
    Write-Host "`n結果:" -ForegroundColor Yellow
    $output = Get-Content "test_output.txt" -Tail 20 -ErrorAction SilentlyContinue
    $output | Where-Object { $_ -match "TempDebug|溫度" } | ForEach-Object {
        if ($_ -match "Calculated: (\d+\.?\d*)") {
            Write-Host $_ -ForegroundColor White
        }
    }

    Write-Host "`n按 Enter 繼續下一個測試..." -ForegroundColor Gray
    Read-Host
}

# 還原設定
Copy-Item $backupFile $configFile -Force
Write-Host "`n已還原原始設定檔" -ForegroundColor Green
