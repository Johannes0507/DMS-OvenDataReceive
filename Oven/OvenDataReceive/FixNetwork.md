# 網路設定修正指南

## 問題確認
- 目標設備: 192.168.61.144:4196
- 本機網段: 192.168.211.x (Wi-Fi) 和 192.168.56.x (乙太網路)
- **設備與本機不在同一網段，無法直接通訊**

---

## 解決方案

### 方案 A：修改本機 IP 到 192.168.61.x 網段（推薦）

**步驟：**

1. **開啟網路設定**
   - 按 `Win + R`，輸入 `ncpa.cpl`，按 Enter
   - 或：設定 → 網路和網際網路 → 進階網路設定 → 更多網路介面卡選項

2. **選擇連接到設備的網路介面**
   - 如果設備透過有線連接：選擇「乙太網路 4」或其他乙太網路介面
   - 如果設備透過 Wi-Fi：選擇「Wi-Fi」

3. **設定靜態 IP**
   - 右鍵點擊網路介面 → 內容
   - 選擇「Internet Protocol Version 4 (TCP/IPv4)」→ 內容
   - 選擇「使用下列的 IP 位址」

   **建議設定：**
   ```
   IP 位址：      192.168.61.100
   子網路遮罩：    255.255.255.0
   預設閘道：      192.168.61.1 或 192.168.61.254 (視網路環境而定)

   慣用 DNS 伺服器：8.8.8.8
   其他 DNS 伺服器：8.8.4.4
   ```

4. **確認設定**
   ```powershell
   # 檢查新的 IP
   ipconfig

   # 測試連線
   ping 192.168.61.144

   # 測試 Port
   Test-NetConnection -ComputerName 192.168.61.144 -Port 4196
   ```

---

### 方案 B：新增靜態路由

如果您需要保持現有 IP，可以新增路由：

```powershell
# 以系統管理員身分執行 PowerShell
# 新增到 192.168.61.0/24 網段的路由
route add 192.168.61.0 mask 255.255.255.0 192.168.211.254

# 查看路由表
route print

# 測試連線
ping 192.168.61.144
```

**注意**：此方法需要您的閘道器 (192.168.211.254) 能夠路由到 192.168.61.x 網段。

---

### 方案 C：確認設備 IP 設定

可能設備的 IP 設定有誤，請確認：

1. **檢查設備實際 IP**
   - 查看設備面板或設定介面
   - 檢查設備說明書

2. **可能的正確 IP：**
   - 如果設備在 Wi-Fi 網段：192.168.211.x
   - 如果設備在有線網段：192.168.56.x

3. **修改 appsettings.json**
   ```json
   {
     "Modbus": {
       "IpAddress": "192.168.211.xxx",  // 修改為實際 IP
       "Port": 4196
     }
   }
   ```

---

### 方案 D：使用網路掃描找出設備

如果不確定設備實際 IP：

```powershell
# 掃描所有網段的 4196 Port
# 在 192.168.211.x 網段掃描
1..254 | ForEach-Object {
    $ip = "192.168.211.$_"
    Test-NetConnection -ComputerName $ip -Port 4196 -InformationLevel Quiet -WarningAction SilentlyContinue |
    Where-Object { $_ -eq $true } | ForEach-Object { Write-Host "找到設備: $ip" }
}

# 在 192.168.56.x 網段掃描
1..254 | ForEach-Object {
    $ip = "192.168.56.$_"
    Test-NetConnection -ComputerName $ip -Port 4196 -InformationLevel Quiet -WarningAction SilentlyContinue |
    Where-Object { $_ -eq $true } | ForEach-Object { Write-Host "找到設備: $ip" }
}
```

---

## 驗證步驟

完成網路設定後，執行以下測試：

```powershell
# 1. Ping 測試
ping 192.168.61.144 -n 4

# 2. TCP Port 測試
Test-NetConnection -ComputerName 192.168.61.144 -Port 4196

# 3. 啟動程式測試
cd "C:\Users\Keith.Lee\Diamond Groups\Source Code\IoT\OvenDataReceive\Oven\OvenDataReceive"
dotnet run

# 4. 查看日誌
cat Logs\Error.txt
```

---

## 常見問題

**Q: 修改 IP 後無法上網？**
A: 設定正確的預設閘道和 DNS 伺服器

**Q: 還是無法連線？**
A:
1. 確認設備電源已開啟
2. 檢查網路線連接
3. 確認設備 Modbus TCP 服務已啟動
4. 檢查防火牆設定

**Q: 如何還原網路設定？**
A: 在網路介面內容中，選擇「自動取得 IP 位址」
