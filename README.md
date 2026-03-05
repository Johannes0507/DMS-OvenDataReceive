# 烤箱數據監控系統 (Oven Data Receive)

一個基於 Blazor Server 的 Modbus TCP 數據監控系統，用於即時監控烤箱設備的開關量狀態和溫度數據。支援**集中式模組**架構，可從單一端點一次讀取全部 12 路溫度。

## 📋 目錄

- [功能特點](#功能特點)
- [系統需求](#系統需求)
- [硬體連接](#硬體連接)
- [安裝與配置](#安裝與配置)
- [運行應用](#運行應用)
- [使用說明](#使用說明)
- [配置參數](#配置參數)
- [注意事項](#注意事項)
- [技術架構](#技術架構)
- [除錯與故障排除](#除錯與故障排除)

## ✨ 功能特點

- ✅ **即時數據監控** - 支援 Modbus TCP 協議，即時讀取設備數據
- ✅ **開關量輸入 (DI)** - 監控 32 路離散輸入狀態
- ✅ **溫度監控** - 支援 12 路溫度感測器（集中式模組批次讀取）
- ✅ **自動重連** - 連接斷開時自動重試連接
- ✅ **即時更新** - 可配置的數據讀取間隔（預設 2000ms）
- ✅ **錯誤記錄** - 自動寫入 `Logs/Error.txt`（保留最近 200 筆）
- ✅ **暫存器掃描診斷** - 可選啟用，掃描 Holding/Input 暫存器並輸出至 `Logs/RegisterScan.txt`
- ✅ **多組暫存器候選** - 可依序嘗試多組地址，自動選擇有效位址

## 💻 系統需求

- .NET 9.0 SDK 或更高版本
- Windows 10/11 或 Linux 作業系統
- 支援 Modbus TCP 的設備（如 SHZK-DI 開關量采集設備）
- 網路連接（設備與電腦需在同一網路）

## 🔌 硬體連接

### 1. 網路連接

將 Modbus 設備的網口連接到與電腦相同的區域網路：

```
設備網口 ──→ 交換機/路由器 ──→ 電腦網卡
```

### 2. 電源供應

- **工作電壓**：DC 6-35V
- **工作電流**：≥50mA
- **功耗**：12V 工作電壓下，最大 1W

### 3. 設備配置

確保設備已正確配置：
- **IP 地址**：預設為 `192.168.61.74`（可透過 `appsettings.json` 修改）
- **Modbus TCP 端口**：預設為 `4196`
- **設備地址 (Unit ID)**：DI 與溫度共用，預設為 `255`

### 4. RS485 串聯架構（重要）

現場採用 **RS485 串聯 (Daisy Chain)**，其中**藥水箱（站號 2、3）**為線路前段關鍵節點。

**若站號 2 或 3 斷電或斷線，將導致後續串聯的站點無法通訊。** 請在現場佈線與維修時特別注意此兩站狀態。

## 📦 安裝與配置

### 1. 克隆專案

```bash
git clone <repository-url>
cd OvenDataReceive/Oven/OvenDataReceive
```

### 2. 還原 NuGet 套件

```bash
dotnet restore
```

### 3. 配置應用程式

編輯 `Oven/OvenDataReceive/appsettings.json` 檔案：

```json
{
  "Modbus": {
    "IpAddress": "192.168.61.74",       // Modbus TCP 閘道器 IP 地址
    "Port": 4196,                       // Modbus TCP 端口
    "DiUnitId": 255,                    // DI 採集器 Unit ID
    "TempSensorCount": 12,              // 溫度感測器數量
    "TempUnitId": 255,                  // 溫度裝置 Unit ID（與 DI 共用站號）
    "TempRegisterAddr": 40001,          // 溫度寄存器基底地址（集中式模組常用 40001）
    "TempRegisterAddrCandidates": "40001,40002,0,30001",  // 候選地址，依序嘗試直到讀到非零
    "TempRegisterStride": 1,            // 每感測器寄存器步進
    "TempBulkRead": true,               // 是否優先批次讀取
    "TempScale": 0.1,                   // 溫度換算係數（Raw * Scale）
    "TempUseSigned": false,             // 是否以有符號整數解析 Raw
    "TempSwapBytes": true,              // 是否交換高低位元組
    "ReadIntervalMs": 2000,
    "SensorTimeoutMs": 1000,
    "RegisterScanEnabled": false,       // 暫存器掃描診斷（啟用後寫入 Logs/RegisterScan.txt）
    "RegisterScanStart": 0,
    "RegisterScanCount": 64
  }
}
```

### 4. 配置參數說明

| 參數 | 說明 | 預設值 | 範圍 |
|------|------|--------|------|
| `IpAddress` | Modbus TCP 閘道器 IP 地址 | `192.168.61.74` | - |
| `Port` | Modbus TCP 端口 | `4196` | 1-65535 |
| `DiUnitId` | DI 採集器 Unit ID | `255` | 1-255 |
| `TempUnitId` | 溫度裝置 Unit ID | `255` | 1-255 |
| `TempSensorCount` | 溫度感測器數量 | `12` | 1-32 |
| `TempRegisterAddr` | 溫度寄存器基底地址（40001 = Holding 第一個） | `40001` | 0-65535 |
| `TempRegisterAddrCandidates` | 暫存器候選地址，逗號分隔，依序嘗試 | `"40001,40002,0,30001"` | - |
| `TempRegisterStride` | 每感測器寄存器步進 | `1` | 1-16 |
| `TempBulkRead` | 是否優先批次讀取（一次讀 12 筆） | `true` | true/false |
| `TempScale` | 溫度換算係數（實際溫度 = Raw × Scale） | `0.1` | > 0 |
| `TempUseSigned` | 是否以有符號整數解析 Raw | `false` | true/false |
| `TempSwapBytes` | 是否交換高低位元組 | `true` | true/false |
| `RegisterScanEnabled` | 啟用暫存器掃描診斷（寫入 RegisterScan.txt） | `false` | true/false |
| `RegisterScanStart` | 掃描起始地址（0-based） | `0` | 0-65535 |
| `RegisterScanCount` | 掃描寄存器數量 | `64` | 1-128 |
| `ReadIntervalMs` | 數據讀取間隔（毫秒） | `2000` | 500-10000 |
| `SensorTimeoutMs` | 單一感測器超時（毫秒） | `1000` | 200-5000 |

## 🚀 運行應用

### 開發模式

```bash
cd Oven/OvenDataReceive
dotnet run
```

應用程式預設在 **Port 5133** 啟動，例如：
- HTTP: `http://localhost:5133`
- 區域網路: `http://<本機IP>:5133`

### 生產模式

```bash
dotnet build -c Release
dotnet run -c Release
```

### 發布應用

```bash
dotnet publish -c Release -o ./publish
```

## 📖 使用說明

### 1. 訪問應用

開啟瀏覽器，訪問 `http://localhost:5133`（或本機實際 IP 與端口）

### 2. 監控頁面

導航至「監控」頁面 (`/monitor`)，您將看到：

#### **站點監控**
- 顯示 12 個溫度感測站的即時狀態
- DI 狀態指示：Running / Standby
- 按設備類別分組顯示：
  - 後跟定型（冷/熱）
  - 高速加熱定型機
  - 藥水箱與膠水活化
  - 冷凍系統

#### **溫度顯示**
- 根據溫度範圍顯示不同顏色：
  - 🔵 正常：< 100°C
  - 🟡 溫熱：100-150°C
  - 🟠 中等：150-200°C
  - 🔴 高溫：≥ 200°C
  - 🩵 低溫：< 0°C（冷凍設備）

### 3. 連接狀態與提示

頁面頂部顯示設備連接狀態：
- 🟢 **已連線** - 設備正常連接
- 🔴 **未連線** - 無法連接到設備

當未連線或藥水箱（站號 2、3）發生錯誤時，會顯示 **硬體接線提示**：提醒藥水箱為 RS485 串聯關鍵節點，斷線將導致後續站點無法通訊。

若有錯誤記錄，會顯示 **最近錯誤紀錄**（最新 5 筆），來源為 `Logs/Error.txt`。

### 4. DI 對照測試

新增「DI 對照測試」頁面 (`/di-compare`)，可同時查看：
- **背景輪詢（Service）** 的 DI 狀態
- **即時直連（FC02 / 起始 0 / 32）** 的 DI 狀態

此頁面會將直連結果寫入 `Logs/DiDiagnostic.txt` 供診斷比對。

## ⚙️ 配置參數

### 溫度感測器與集中式模組

本系統支援 **集中式 Modbus TCP 模組**，從單一端點一次讀取全部 12 路溫度（基底地址 40001 起，每路 1 個寄存器）。

**Modbus 通訊規格：**
- 寄存器：Holding (4xxxx, FC03) 或 Input (3xxxx, FC04)，可透過候選地址自動嘗試
- 數據類型：16 位元整數（可選 signed/unsigned、SwapBytes）

**溫度換算（可配置）：**
```
實際溫度 = RawValue × TempScale
```
預設 `TempScale = 0.1`，即 Raw 256 → 25.6°C。

**範例（TempScale=0.1）：**
| 原始值 (Raw) | 實際溫度 |
|-------------|---------|
| 768 | 76.8°C |
| 256 | 25.6°C |
| 1024 | 102.4°C |

**暫存器掃描診斷：**
若 `RegisterScanEnabled=true`，啟動後會掃描 Holding 與 Input 暫存器並寫入 `Logs/RegisterScan.txt`，可依此找出實際有資料的位址，再更新 `TempRegisterAddr` 或 `TempRegisterAddrCandidates`。

**Log 檔案：**
| 檔案 | 說明 |
|------|------|
| `Logs/RawData.txt` | 溫度 Raw 值記錄（最近 200 筆） |
| `Logs/TemperatureRecording.txt` | 溫度換算記錄 |
| `Logs/Error.txt` | 錯誤記錄（最近 200 筆） |
| `Logs/DiDiagnostic.txt` | DI 直連診斷記錄（最近 200 筆） |
| `Logs/RegisterScan.txt` | 暫存器掃描結果（啟用掃描時產生） |

## ⚠️ 注意事項

### 1. 硬體架構說明

本系統使用以下設備：
- **SHZK-DI 開關量采集器**：負責讀取 32 路 DI 狀態
- **集中式溫度模組**：彙整 12 路溫度，從單一端點以 Modbus TCP 提供（基底地址 40001）
- **RS485 串聯**：藥水箱（站號 2、3）為前段關鍵節點，此兩站斷線將影響後續站點通訊

### 2. 網路連接

- 確保設備與電腦在同一區域網路
- 檢查防火牆設定，確保 Modbus 端口 `4196` 與 Web 端口 `5133` 未被阻擋
- 使用 `Test-NetConnection` 測試 Modbus 連線：
  ```powershell
  Test-NetConnection -ComputerName 192.168.61.74 -Port 4196
  ```

### 3. 讀取間隔設定

- **建議值**：2000ms（2 秒）
- **最小值**：500ms（過低可能導致設備響應不及）
- **最大值**：10000ms（過高會影響數據即時性）

### 4. 錯誤處理

- 連接失敗時，系統會自動重試（間隔 2 秒）
- 讀取失敗時，會保留上次成功讀取的數據
- 錯誤會自動寫入 `Logs/Error.txt`（最近 200 筆）
- 監控頁面會顯示最近 5 筆錯誤紀錄

## 🏗️ 技術架構

### 技術棧

- **框架**：ASP.NET Core 9.0
- **UI**：Blazor Server
- **Modbus 通訊**：FluentModbus 5.0.0
- **後端服務**：Background Service

### 專案結構

```
Oven/
└── OvenDataReceive/
    ├── Components/
    │   ├── Pages/
    │   │   ├── Monitor.razor      # 監控頁面
    │   │   ├── DiMonitor.razor    # DI 監控頁面
    │   │   └── DiCompare.razor    # DI 對照測試頁面
    │   └── Layout/
    │       └── MainLayout.razor   # 主佈局
    ├── Services/
    │   └── ModbusDataService.cs   # Modbus 數據服務
    ├── Program.cs                 # 應用程式入口
    ├── appsettings.json          # 配置文件
    └── Logs/                     # 執行時產生
        ├── RawData.txt           # 溫度 Raw 值
        ├── TemperatureRecording.txt
        ├── Error.txt             # 錯誤記錄
        └── RegisterScan.txt      # 暫存器掃描（啟用時）
```

### 核心服務

**ModbusDataService** (`Services/ModbusDataService.cs`)
- 負責 Modbus TCP 連接管理
- 批次或逐筆讀取 12 路溫度（可配置）
- 支援暫存器候選地址自動探測
- 可選暫存器掃描診斷模式
- 錯誤寫入 Error.txt，提供 ErrorLogs 供 UI 顯示

## 🔍 除錯與故障排除

### 問題：Port 5133 已被佔用 (address already in use)

**解決方案：**
```powershell
netstat -ano | findstr :5133    # 查詢佔用行程 PID
taskkill /PID <PID> /F          # 終止該行程
```

### 問題：無法連接到設備

**解決方案：**
1. 檢查設備 IP 地址是否正確（預設 192.168.61.74）
2. 確認設備已開機並連接到網路
3. 檢查 Modbus 端口是否為 4196
4. 使用 `Test-NetConnection -ComputerName <IP> -Port 4196` 測試 TCP 連線
5. 使用 Modbus 測試工具（如 Modbus Poll）驗證設備連接

### 問題：溫度數據全部顯示為 0

**解決方案：**
1. 檢視 `Logs/RawData.txt`：若 Raw 全為 0，可能是暫存器地址錯誤
2. 啟用暫存器掃描：設定 `RegisterScanEnabled: true`，重啟後查看 `Logs/RegisterScan.txt` 找出有非零值的位址
3. 調整 `TempRegisterAddrCandidates`：依掃描結果加入正確地址（如 `"40002,40001,30001"`）
4. 確認 `TempUnitId` 與設備站號一致（預設 255）
5. 向廠商索取完整 Modbus 暫存器對應表（Register Map）

### 問題：數據更新延遲

**解決方案：**
1. 降低 `ReadIntervalMs` 值（建議不低於 500ms）
2. 檢查網路延遲
3. 確認設備響應速度

## 📞 技術支援

如有問題或建議，請聯繫開發團隊。

## 📄 授權

本專案為內部使用專案。

---

**最後更新**：2026年3月

