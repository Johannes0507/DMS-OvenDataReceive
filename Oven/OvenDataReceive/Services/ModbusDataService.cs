using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using FluentModbus;

namespace OvenDataReceive.Services
{
    public class ModbusDataService : BackgroundService
    {
        private readonly ILogger<ModbusDataService> _logger;
        private readonly IConfiguration _configuration;
        private ModbusTcpClient? _client;
        
        // 連線設定
        private readonly string _ipAddress;
        private readonly int _port;
        private readonly byte _diUnitId;           // DI 採集器的 UnitID
        private readonly int _tempSensorCount;     // 溫度感測器數量（1-12）
        private readonly byte _tempUnitId;         // 溫度裝置 UnitID（閘道器映射後的 ID）
        private readonly int _tempRegisterAddr;    // 溫度寄存器基底地址（閘道器映射後的地址）
        private readonly int _tempRegisterStride;  // 溫度寄存器步進（每一感測器偏移）
        private readonly int _diReadIntervalMs;    // DI 讀取週期（毫秒）
        private readonly int _tempReadIntervalMs;  // 溫度讀取週期（毫秒）
        private readonly int _sensorTimeoutMs;     // 單一感測器超時（毫秒）
        private readonly byte[] _tcpQueryCommand;  // 溫度查詢指令 (TCP Hex)
        private readonly double _tempScale;        // 溫度縮放比例（Raw * Scale）
        private readonly bool _tempUseSigned;      // 是否以 signed 解析 Raw
        private readonly bool _tempSwapBytes;      // 是否交換高低位元組
        private readonly bool _tempBulkRead;       // 是否優先使用批次讀取
        private readonly bool _registerScanEnabled;
        private readonly int _registerScanStart;
        private readonly int _registerScanCount;
        private readonly string _registerScanPath;
        private bool _registerScanExecuted;
        private readonly List<int> _tempRegisterAddrCandidates;
        private int _effectiveRegisterAddr;  // 生效中的基底地址，-1 表示未定

        // 數據模型
        private readonly object _dataLock = new object();
        private List<bool> _diStatus = new(); 
        private List<double> _temperatures = new(); 
        private List<string?> _temperatureErrors = new();
        private bool _isConnected = false;
        private DateTime _lastUpdateTime = DateTime.MinValue;
        private string? _errorMessage = null;
        private const int TempLogMaxEntries = 200;
        private readonly object _tempLogLock = new object();
        private readonly Queue<string> _tempLogBuffer = new Queue<string>();
        private readonly string _tempLogPath;
        private const int RawLogMaxEntries = 200;
        private readonly object _rawLogLock = new object();
        private readonly Queue<string> _rawLogBuffer = new Queue<string>();
        private readonly string _rawLogPath;
        private const int DiLogMaxEntries = 200;
        private readonly object _diLogLock = new object();
        private readonly Queue<string> _diLogBuffer = new Queue<string>();
        private readonly string _diLogPath;
        private const int ErrorLogMaxEntries = 200;
        private readonly object _errorLogLock = new object();
        private readonly Queue<string> _errorLogBuffer = new Queue<string>();
        private readonly string _errorLogPath;
        private readonly ConcurrentDictionary<int, bool> _diStatusCache = new();
        private readonly ConcurrentDictionary<int, double> _temperatureCache = new();
        private readonly ConcurrentDictionary<int, string?> _temperatureErrorCache = new();

        // 公開屬性
        public List<bool> DiStatus 
        { 
            get { lock (_dataLock) return new List<bool>(_diStatus); } 
        }

        public List<double> Temperatures 
        { 
            get { lock (_dataLock) return new List<double>(_temperatures); } 
        }

        public List<string?> TemperatureErrors
        {
            get { lock (_dataLock) return new List<string?>(_temperatureErrors); }
        }

        public IReadOnlyDictionary<int, bool> DiStatusCache => _diStatusCache;
        public IReadOnlyDictionary<int, double> TemperatureCache => _temperatureCache;
        public IReadOnlyDictionary<int, string?> TemperatureErrorCache => _temperatureErrorCache;

        public bool IsConnected 
        { 
            get { lock (_dataLock) return _isConnected; }
            private set { lock (_dataLock) _isConnected = value; }
        }

        public DateTime LastUpdateTime 
        { 
            get { lock (_dataLock) return _lastUpdateTime; }
            private set { lock (_dataLock) _lastUpdateTime = value; }
        }

        public string? ErrorMessage 
        { 
            get { lock (_dataLock) return _errorMessage; }
            private set { lock (_dataLock) _errorMessage = value; }
        }

        public record DiReadResult(List<bool> Status, string? ErrorMessage, DateTime Timestamp);

        /// <summary>
        /// SHZK-DI 設備資訊 (PDF 第 4.7 節 通訊參數寄存器表)
        /// 寄存器地址 2000-2002、2010-2011 (FC03 讀取)
        /// </summary>
        public record DeviceInfo(
            int DeviceAddress,
            int Rs232BaudCode,
            int Rs485BaudCode,
            int ActiveUploadFlag,
            int ActiveUploadTimeUnit,
            string? ErrorMessage,
            DateTime Timestamp)
        {
            // PDF 表格：波特率代碼對照
            private static readonly string[] BaudRateLabels = new[]
            {
                "9600", "1200", "2400", "4800", "9600", "19200", "38400", "57600", "115200"
            };

            public string Rs232BaudLabel => BaudCode(Rs232BaudCode);
            public string Rs485BaudLabel => BaudCode(Rs485BaudCode);

            public string ActiveUploadDescription => ActiveUploadFlag switch
            {
                1 => "繼電器狀態主動回傳",
                2 => "開關量輸入狀態主動回傳",
                3 => "繼電器+開關量都回傳",
                _ => "關閉"
            };

            public string ActiveUploadTimeDescription
                => ActiveUploadTimeUnit == 0 ? "未設定" : $"{ActiveUploadTimeUnit * 0.1:F1} 秒";

            private static string BaudCode(int code)
                => code >= 0 && code < BaudRateLabels.Length ? BaudRateLabels[code] : $"未知({code})";
        }

        public List<string> ErrorLogs
        {
            get
            {
                lock (_errorLogLock)
                {
                    return _errorLogBuffer.ToList();
                }
            }
        }

        // 溫度感測器資訊對應表（站號 1-12）
        // ★ 硬體接線注意事項：
        // 現場採用 RS485 串聯（Daisy Chain），其中站號 2、3（藥水箱）為線路前段關鍵節點。
        // 如果這兩個站點斷電或斷線，會導致後續串聯的站點也無法通訊。
        public static readonly Dictionary<int, SensorInfo> TempSensors = new()
        {
            // 高速加熱定型機
            { 1, new SensorInfo("高速加熱定型機", "設備溫度", "vulcanizer") },
            { 2, new SensorInfo("藥水箱上", "大底溫度", "chemical") },
            { 3, new SensorInfo("藥水箱下", "鞋面溫度", "chemical") },
            
            // 膠水活化/乾燥設備
            { 4, new SensorInfo("一次膠上", "大底溫度", "glue") },
            { 5, new SensorInfo("一次膠下", "鞋面溫度", "glue") },
            { 6, new SensorInfo("二次膠上", "大底溫度", "glue") },
            { 7, new SensorInfo("二次膠下", "鞋面溫度", "glue") },
            
            // 冷卻與定型設備
            { 8, new SensorInfo("冷凍機", "設備溫度", "freezer") },
            { 9, new SensorInfo("後跟定型/熱定型", "右", "molding-hot") },
            { 10, new SensorInfo("後跟定型/冷定型", "右", "molding-cold") },
            { 11, new SensorInfo("後跟定型/冷定型", "左", "molding-cold") },
            { 12, new SensorInfo("後跟定型/熱定型", "左", "molding-hot") }
        };

        public record SensorInfo(string Device, string Position, string Category);

        public static string GetSensorName(int sensorId)
        {
            if (TempSensors.TryGetValue(sensorId, out var info))
                return info.Position == "主機" ? info.Device : $"{info.Device}（{info.Position}）";
            return $"感測器 {sensorId}";
        }

        public static SensorInfo? GetSensorInfo(int sensorId)
        {
            return TempSensors.TryGetValue(sensorId, out var info) ? info : null;
        }

        // 事件通知
        public event Action? DataUpdated;

        public ModbusDataService(ILogger<ModbusDataService> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
            
            // 從配置文件讀取設置
            _ipAddress = _configuration["Modbus:IpAddress"] ?? "192.168.61.74";
            _port = int.Parse(_configuration["Modbus:Port"] ?? "4196");
            _diUnitId = byte.Parse(_configuration["Modbus:DiUnitId"] ?? "255");
            _tempSensorCount = int.Parse(_configuration["Modbus:TempSensorCount"] ?? "12");
            _tempUnitId = byte.Parse(_configuration["Modbus:TempUnitId"] ?? "1");
            _tempRegisterAddr = int.Parse(_configuration["Modbus:TempRegisterAddr"] ?? "0");
            _tempRegisterStride = int.Parse(_configuration["Modbus:TempRegisterStride"] ?? "1");
            _tempScale = ReadTempScale(_configuration["Modbus:TempScale"]);
            _tempUseSigned = ReadBoolSetting(_configuration["Modbus:TempUseSigned"], true);
            _tempSwapBytes = ReadBoolSetting(_configuration["Modbus:TempSwapBytes"], false);
            _tempBulkRead = ReadBoolSetting(_configuration["Modbus:TempBulkRead"], true);
            _tcpQueryCommand = ParseHexCommand(_configuration["Modbus:TcpQueryHex"], "12FF0030");
            _registerScanEnabled = ReadBoolSetting(_configuration["Modbus:RegisterScanEnabled"], false);
            _registerScanStart = Math.Max(0, int.Parse(_configuration["Modbus:RegisterScanStart"] ?? "0"));
            _registerScanCount = Math.Clamp(int.Parse(_configuration["Modbus:RegisterScanCount"] ?? "64"), 1, 128);
            _tempRegisterAddrCandidates = ParseRegisterAddrCandidates(_configuration["Modbus:TempRegisterAddrCandidates"]);
            _effectiveRegisterAddr = -1;

            var logsDir = Path.Combine(Directory.GetCurrentDirectory(), "Logs");
            Directory.CreateDirectory(logsDir);
            _registerScanPath = Path.Combine(logsDir, "RegisterScan.txt");
            _tempLogPath = Path.Combine(logsDir, "TemperatureRecording.txt");
            _rawLogPath = Path.Combine(logsDir, "RawData.txt");
            _diLogPath = Path.Combine(logsDir, "DiDiagnostic.txt");
            _errorLogPath = Path.Combine(logsDir, "Error.txt");
            if (File.Exists(_tempLogPath))
            {
                var existingLines = File.ReadAllLines(_tempLogPath);
                foreach (var line in existingLines.TakeLast(TempLogMaxEntries))
                {
                    _tempLogBuffer.Enqueue(line);
                }
            }
            else
            {
                File.WriteAllText(_tempLogPath, string.Empty);
            }
            if (File.Exists(_rawLogPath))
            {
                var existingLines = File.ReadAllLines(_rawLogPath);
                foreach (var line in existingLines.TakeLast(RawLogMaxEntries))
                {
                    _rawLogBuffer.Enqueue(line);
                }
            }
            else
            {
                File.WriteAllText(_rawLogPath, string.Empty);
            }
            if (File.Exists(_diLogPath))
            {
                var existingLines = File.ReadAllLines(_diLogPath);
                foreach (var line in existingLines.TakeLast(DiLogMaxEntries))
                {
                    _diLogBuffer.Enqueue(line);
                }
            }
            else
            {
                File.WriteAllText(_diLogPath, string.Empty);
            }
            if (File.Exists(_errorLogPath))
            {
                var existingLines = File.ReadAllLines(_errorLogPath);
                foreach (var line in existingLines.TakeLast(ErrorLogMaxEntries))
                {
                    _errorLogBuffer.Enqueue(line);
                }
            }
            else
            {
                File.WriteAllText(_errorLogPath, string.Empty);
            }
            
            // 讀取週期
            _diReadIntervalMs = Math.Clamp(
                int.Parse(_configuration["Modbus:DiReadIntervalMs"] ?? "1000"),
                200, 10000);
            _tempReadIntervalMs = Math.Clamp(
                int.Parse(_configuration["Modbus:TempReadIntervalMs"] ?? "500"),
                100, 10000);
            
            // 溫度讀取超時：預設 3000ms (3秒)
            _sensorTimeoutMs = Math.Clamp(
                int.Parse(_configuration["Modbus:SensorTimeoutMs"] ?? "3000"), 
                200, 5000);
            
            // 初始化溫度陣列（全部設為 0）
            for (int i = 0; i < _tempSensorCount; i++)
                _temperatures.Add(0);
            for (int i = 0; i < _tempSensorCount; i++)
                _temperatureErrors.Add(null);
            
            // 初始化 DI 陣列
            for (int i = 0; i < 32; i++)
                _diStatus.Add(false);
            
            _logger.LogInformation("========================================");
            _logger.LogInformation("Modbus 監控服務啟動 (ADTEK CM1 溫度表)");
            _logger.LogInformation($"  連線目標: {_ipAddress}:{_port}");
            _logger.LogInformation($"  DI 採集器 UnitID: {_diUnitId}");
            _logger.LogInformation($"  溫度感測器: {_tempSensorCount} 個");
            _logger.LogInformation($"  溫度讀取模式: TCP JSON");
            _logger.LogInformation($"  溫度查詢指令: {BitConverter.ToString(_tcpQueryCommand)}");
            _logger.LogInformation($"  暫存器掃描診斷(僅 Modbus 溫度): {(_registerScanEnabled ? $"開啟 (起點={_registerScanStart}, 數量={_registerScanCount})" : "關閉")}");
            _logger.LogInformation($"  DI 讀取週期: {_diReadIntervalMs}ms");
            _logger.LogInformation($"  溫度讀取週期: {_tempReadIntervalMs}ms");
            _logger.LogInformation($"  感測器超時: {_sensorTimeoutMs}ms");
            _logger.LogInformation("========================================");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // 等待應用程式完全啟動
            await Task.Delay(1000, stoppingToken);

            await Task.WhenAll(
                RunDiLoopAsync(stoppingToken),
                RunTempPushAsync(stoppingToken)
            );
        }

        /// <summary>
        /// DI 輪詢迴圈：每 DiReadIntervalMs 讀一次
        /// </summary>
        private async Task RunDiLoopAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (!EnsureConnected())
                    {
                        await Task.Delay(2000, stoppingToken);
                        continue;
                    }

                    if (_registerScanEnabled && !_registerScanExecuted)
                    {
                        RunRegisterScan();
                        _registerScanExecuted = true;
                    }

                    var diStatus = ReadDiStatus();
                    UpdateDiData(diStatus);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    SafeLogError($"❌ DI 讀取異常: {ex.Message}");
                    AppendErrorLog($"DI 讀取異常: {ex.Message}");
                    IsConnected = false;
                    ErrorMessage = ex.Message;
                    DisconnectClient();
                }

                await Task.Delay(_diReadIntervalMs, stoppingToken);
            }
        }

        /// <summary>
        /// 溫度推播迴圈：連線後持續接收設備主動推播的 JSON，斷線自動重連
        /// </summary>
        private async Task RunTempPushAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TcpClient? tcpClient = null;
                try
                {
                    tcpClient = new TcpClient();
                    _logger.LogInformation($"溫度推播：連線至 {_ipAddress}:{_port}...");
                    await tcpClient.ConnectAsync(_ipAddress, _port)
                        .WaitAsync(TimeSpan.FromMilliseconds(_sensorTimeoutMs), stoppingToken);

                    _logger.LogInformation("✅ 溫度推播連線成功，持續接收中");

                    var stream = tcpClient.GetStream();
                    stream.ReadTimeout = 30_000; // 30 秒無資料視為斷線

                    while (!stoppingToken.IsCancellationRequested)
                    {
                        // timeoutMs = 0 → 不設 deadline，靠 stream.ReadTimeout 30s 控制斷線
                        var payload = await ReadJsonPayloadAsync(stream, stoppingToken, timeoutMs: 0);
                        var (temperatures, errors) = ParseTemperaturePayload(payload);
                        UpdateTempData(temperatures, errors);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning($"⚠️ 溫度推播中斷: {ex.Message}，2 秒後重連...");
                    AppendErrorLog($"溫度推播中斷: {ex.Message}");
                }
                finally
                {
                    tcpClient?.Dispose();
                }

                await Task.Delay(2000, stoppingToken);
            }
        }

        private (List<double> Temperatures, List<string?> Errors) ParseTemperaturePayload(string payload)
        {
            var temperatures = Enumerable.Repeat(0d, _tempSensorCount).ToList();
            var errors = Enumerable.Repeat<string?>(null, _tempSensorCount).ToList();

            using var doc = JsonDocument.Parse(payload);
            var source = doc.RootElement;
            if (source.TryGetProperty("data", out var dataNode) && dataNode.ValueKind == JsonValueKind.Object)
                source = dataNode;

            int successCount = 0;
            for (int i = 1; i <= _tempSensorCount; i++)
            {
                if (TryReadTempField(source, $"temp{i}", out var value))
                {
                    temperatures[i - 1] = Math.Round(value, 1);
                    successCount++;
                }
                else
                {
                    errors[i - 1] = $"找不到 temp{i}";
                }
            }

            if (successCount == 0)
            {
                const string msg = "JSON 解析成功，但沒有任何 temp 欄位";
                for (int i = 0; i < errors.Count; i++) errors[i] = msg;
                AppendErrorLog(msg);
            }
            else
            {
                _logger.LogInformation($"✅ 溫度推播: {successCount}/{_tempSensorCount}");
            }

            return (temperatures, errors);
        }

        /// <summary>
        /// 確保 TCP 連線已建立
        /// </summary>
        private bool EnsureConnected()
        {
            if (_client != null && _client.IsConnected)
                return true;

            try
            {
                DisconnectClient();
                
                _client = new ModbusTcpClient();
                _client.ReadTimeout = _sensorTimeoutMs;
                
                _logger.LogInformation($"正在連線至 {_ipAddress}:{_port}...");
                _client.Connect(new IPEndPoint(IPAddress.Parse(_ipAddress), _port));
                
                if (_client.IsConnected)
                {
                    _logger.LogInformation($"✅ TCP 連線成功");
                    return true;
                }
            }
            catch (Exception ex)
            {
                SafeLogError($"❌ 連線失敗: {ex.Message}");
                AppendErrorLog($"連線失敗: {ex.Message}");
                DisconnectClient();
            }
            
            return false;
        }

        /// <summary>
        /// 暫存器掃描診斷：掃描 Holding 與 Input 暫存器範圍，輸出 Raw 值至 Logs/RegisterScan.txt
        /// </summary>
        private void RunRegisterScan()
        {
            if (_client == null || !_client.IsConnected)
                return;

            var lines = new List<string>
            {
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 暫存器掃描診斷 (UnitID={_tempUnitId}, 起點={_registerScanStart}, 數量={_registerScanCount})",
                ""
            };

            ushort[] holdingValues = Array.Empty<ushort>();
            ushort[] inputValues = Array.Empty<ushort>();

            try
            {
                holdingValues = _client.ReadHoldingRegisters<ushort>(_tempUnitId, _registerScanStart, _registerScanCount).ToArray();
            }
            catch (Exception ex)
            {
                lines.Add($"Holding 讀取失敗: {ex.Message}");
            }

            try
            {
                inputValues = _client.ReadInputRegisters<ushort>(_tempUnitId, _registerScanStart, _registerScanCount).ToArray();
            }
            catch (Exception ex)
            {
                lines.Add($"Input 讀取失敗: {ex.Message}");
            }

            int maxLen = Math.Max(holdingValues.Length, inputValues.Length);
            for (int i = 0; i < maxLen; i++)
            {
                int holdingAddr = 40001 + _registerScanStart + i;
                int inputAddr = 30001 + _registerScanStart + i;
                ushort h = i < holdingValues.Length ? holdingValues[i] : (ushort)0;
                ushort inp = i < inputValues.Length ? inputValues[i] : (ushort)0;
                lines.Add($"[{holdingAddr}] Holding: 0x{h:X4} ({h}) | Input: 0x{inp:X4} ({inp})");
            }

            try
            {
                File.WriteAllLines(_registerScanPath, lines);
                _logger.LogInformation($"✅ 暫存器掃描完成，結果已寫入 {_registerScanPath}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"暫存器掃描結果寫入失敗: {ex.Message}");
            }
        }

        /// <summary>
        /// 讀取 DI 狀態（32 路）
        /// </summary>
        private List<bool> ReadDiStatus()
        {
            var result = new List<bool>();
            
            // 預設 32 個 false
            for (int i = 0; i < 32; i++)
                result.Add(false);

            if (_client == null || !_client.IsConnected)
                return result;

            try
            {
                var diData = _client.ReadDiscreteInputs(_diUnitId, 0, 32);
                var bytes = diData.ToArray();
                
                result.Clear();
                foreach (var b in bytes)
                {
                    for (int i = 0; i < 8; i++)
                    {
                        result.Add((b & (1 << i)) != 0);
                    }
                }
                
                // 確保只有 32 個
                if (result.Count > 32)
                    result = result.Take(32).ToList();
                
                // 記錄啟動的 DI
                var activeDIs = result
                    .Select((value, index) => new { value, index })
                    .Where(x => x.value)
                    .Select(x => $"DI{x.index + 1:D2}")
                    .ToList();
                
                _logger.LogInformation($"✅ DI 讀取成功: {(activeDIs.Count > 0 ? string.Join(", ", activeDIs) : "無啟動")}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"⚠️ DI 讀取失敗: {ex.Message}");
                AppendErrorLog($"DI 讀取失敗: {ex.Message}");
            }

            return result;
        }

        public Task<DiReadResult> ReadDiStatusDirectAsync(int startAddress, int count)
        {
            var timestamp = DateTime.Now;
            var result = new List<bool>();
            int safeCount = Math.Clamp(count, 1, 64);
            for (int i = 0; i < safeCount; i++)
                result.Add(false);

            try
            {
                using var client = new ModbusTcpClient();
                client.ReadTimeout = _sensorTimeoutMs;
                client.Connect(new IPEndPoint(IPAddress.Parse(_ipAddress), _port));
                if (!client.IsConnected)
                {
                    AppendDiDiagnosticLog("Direct", result, "連線失敗", timestamp);
                    return Task.FromResult(new DiReadResult(result, "連線失敗", timestamp));
                }

                var diData = client.ReadDiscreteInputs(_diUnitId, startAddress, safeCount);
                var bytes = diData.ToArray();
                result.Clear();
                foreach (var b in bytes)
                {
                    for (int i = 0; i < 8; i++)
                    {
                        result.Add((b & (1 << i)) != 0);
                    }
                }

                if (result.Count > safeCount)
                    result = result.Take(safeCount).ToList();

                AppendDiDiagnosticLog("Direct", result, null, timestamp);
                return Task.FromResult(new DiReadResult(result, null, timestamp));
            }
            catch (Exception ex)
            {
                AppendDiDiagnosticLog("Direct", result, ex.Message, timestamp);
                return Task.FromResult(new DiReadResult(result, ex.Message, timestamp));
            }
        }

        /// <summary>
        /// 讀取 SHZK-DI 設備資訊 (PDF 4.7 節) - FC03 寄存器 2000~2011
        /// </summary>
        public Task<DeviceInfo> ReadDeviceInfoAsync()
        {
            var timestamp = DateTime.Now;
            try
            {
                using var client = new ModbusTcpClient();
                client.ReadTimeout = _sensorTimeoutMs;
                client.Connect(new IPEndPoint(IPAddress.Parse(_ipAddress), _port));
                if (!client.IsConnected)
                    return Task.FromResult(new DeviceInfo(0, 0, 0, 0, 0, "連線失敗", timestamp));

                // 寄存器 2000~2002: 裝置地址、RS232/RS485 鮑率代碼
                var regs2000 = client.ReadHoldingRegisters<short>(_diUnitId, 2000, 3).ToArray();
                int devAddr   = regs2000[0];
                int rs232Baud = regs2000[1];
                int rs485Baud = regs2000[2];

                // 寄存器 2010~2011: 主動上傳旗標 & 時間單位
                var regs2010 = client.ReadHoldingRegisters<short>(_diUnitId, 2010, 2).ToArray();
                int uploadFlag = regs2010[0];
                int uploadTime = regs2010[1];

                return Task.FromResult(new DeviceInfo(devAddr, rs232Baud, rs485Baud, uploadFlag, uploadTime, null, timestamp));
            }
            catch (Exception ex)
            {
                return Task.FromResult(new DeviceInfo(0, 0, 0, 0, 0, ex.Message, timestamp));
            }
        }

        /// <summary>
        /// 透過 TCP 指令讀取 JSON 溫度資料（SSCOM 同型流程）
        /// 指令預設: 12 FF 00 30
        /// </summary>
        private async Task<(List<double> Temperatures, List<string?> Errors)> ReadTemperatureViaTcpJsonAsync(CancellationToken stoppingToken)
        {
            var temperatures = Enumerable.Repeat(0d, _tempSensorCount).ToList();
            var errors = Enumerable.Repeat<string?>(null, _tempSensorCount).ToList();

            try
            {
                using var tcpClient = new TcpClient();
                await tcpClient.ConnectAsync(_ipAddress, _port).WaitAsync(
                    TimeSpan.FromMilliseconds(_sensorTimeoutMs),
                    stoppingToken);

                using var stream = tcpClient.GetStream();
                stream.ReadTimeout = _sensorTimeoutMs;
                stream.WriteTimeout = _sensorTimeoutMs;

                await stream.WriteAsync(_tcpQueryCommand, stoppingToken);
                await stream.FlushAsync(stoppingToken);

                string payload = await ReadJsonPayloadAsync(stream, stoppingToken);
                using var doc = JsonDocument.Parse(payload);
                var source = doc.RootElement;
                if (source.TryGetProperty("data", out var dataNode) && dataNode.ValueKind == JsonValueKind.Object)
                {
                    source = dataNode;
                }

                int successCount = 0;
                for (int i = 1; i <= _tempSensorCount; i++)
                {
                    if (TryReadTempField(source, $"temp{i}", out var value))
                    {
                        temperatures[i - 1] = Math.Round(value, 1);
                        successCount++;
                    }
                    else
                    {
                        errors[i - 1] = $"找不到 temp{i}";
                    }
                }

                if (successCount == 0)
                {
                    const string message = "JSON 解析成功，但沒有任何 temp 欄位";
                    for (int i = 0; i < errors.Count; i++)
                        errors[i] = message;
                    AppendErrorLog(message);
                }
                else
                {
                    _logger.LogInformation($"✅ TCP JSON 溫度讀取成功: {successCount}/{_tempSensorCount}");
                }
            }
            catch (Exception ex)
            {
                var message = $"TCP JSON 溫度讀取失敗: {ex.Message}";
                for (int i = 0; i < errors.Count; i++)
                    errors[i] = message;
                AppendErrorLog(message);
                _logger.LogWarning(message);
            }

            return (temperatures, errors);
        }

        /// <summary>
        /// 背景持續輪詢溫度（舊版 Modbus 方式）
        /// </summary>
        private async Task<(List<double> Temperatures, List<string?> Errors)> ReadTemperaturesAsync(CancellationToken stoppingToken)
        {
            if (_tempBulkRead)
            {
                var bulkResult = ReadTemperatureBlockWithTimeout();
                if (bulkResult.Success)
                {
                    await Task.Delay(1200, stoppingToken);
                    _logger.LogInformation($"✅ 溫度批次讀取: {bulkResult.Temperatures.Count}/{_tempSensorCount} 成功");
                    return (bulkResult.Temperatures, bulkResult.Errors);
                }

                _logger.LogWarning($"⚠️ 溫度批次讀取失敗，改用逐筆讀取: {bulkResult.ErrorMessage}");
                AppendErrorLog($"溫度批次讀取失敗: {bulkResult.ErrorMessage}");
            }

            var result = new List<double>();
            var errors = new List<string?>();
            int successCount = 0;
            var tempLog = new List<string>();

            _logger.LogDebug($"開始讀取溫度 (共 {_tempSensorCount} 個感測器)...");

            for (int i = 0; i < _tempSensorCount; i++)
            {
                int sensorId = i + 1;
                _logger.LogDebug($"  #{sensorId} {GetSensorName(sensorId)}: 讀取中...");
                
                var readResult = ReadSingleTemperatureWithTimeout(sensorId);
                result.Add(readResult.Temperature);
                errors.Add(readResult.ErrorMessage);
                
                if (readResult.ErrorMessage == null)
                {
                    successCount++;
                    tempLog.Add($"{GetSensorName(sensorId)}:{readResult.Temperature:F1}°C");
                    _logger.LogDebug($"  #{sensorId} 讀取成功: {readResult.Temperature:F1}°C");
                }
                else
                {
                    _logger.LogWarning($"  #{sensorId} {GetSensorName(sensorId)} 讀取失敗: {readResult.ErrorMessage}");
                    AppendTemperatureErrorLog(readResult.ErrorMessage, sensorId);
                    AppendErrorLog($"溫度讀取失敗 DI{sensorId:D2}: {readResult.ErrorMessage}");
                }

                // 讀取間隔，避免 TCP 封包黏滯
                await Task.Delay(20, stoppingToken);
            }

            // 整輪讀取完畢後，固定休眠再進入下一輪
            await Task.Delay(1200, stoppingToken);

            // 顯示結果摘要
            if (successCount > 0)
            {
                _logger.LogInformation($"✅ 溫度讀取: {successCount}/{_tempSensorCount} 成功 → {string.Join(", ", tempLog)}");
            }
            else
            {
                _logger.LogWarning($"⚠️ 溫度讀取: 0/{_tempSensorCount} 成功");
            }

            return (result, errors);
        }

        private (List<double> Temperatures, List<string?> Errors, bool Success, string? ErrorMessage) ReadTemperatureBlockWithTimeout()
        {
            var result = new List<double>(_tempSensorCount);
            var errors = new List<string?>(_tempSensorCount);

            if (_client == null || !_client.IsConnected)
            {
                if (!EnsureConnected())
                {
                    for (int i = 0; i < _tempSensorCount; i++)
                        errors.Add("連線失敗");
                    return (result, errors, false, "連線失敗");
                }
            }

            var addressesToTry = GetAddressesToTry().ToList();
            foreach (var addr in addressesToTry)
            {
                var (temps, errs, ok, errMsg) = TryReadTemperatureBlockAt(addr);
                if (!ok)
                    continue;
                if (temps.Any(t => t != 0))
                {
                    if (_effectiveRegisterAddr != addr)
                    {
                        _effectiveRegisterAddr = addr;
                        _logger.LogInformation($"✅ 溫度暫存器候選生效: 基底地址 {addr}");
                    }
                    return (temps, errs, true, null);
                }
            }
            var fallbackAddr = addressesToTry.Count > 0 ? addressesToTry[0] : _tempRegisterAddr;
            var (ft, fe, fok, ferr) = TryReadTemperatureBlockAt(fallbackAddr);
            if (fok && _effectiveRegisterAddr < 0)
                _effectiveRegisterAddr = fallbackAddr;
            return (ft, fe, fok, ferr);
        }

        private IEnumerable<int> GetAddressesToTry()
        {
            if (_effectiveRegisterAddr >= 0)
                return new[] { _effectiveRegisterAddr };
            if (_tempRegisterAddrCandidates.Count > 0)
                return _tempRegisterAddrCandidates;
            return new[] { _tempRegisterAddr };
        }

        private (List<double> Temperatures, List<string?> Errors, bool Success, string? ErrorMessage) TryReadTemperatureBlockAt(int address)
        {
            var result = new List<double>(_tempSensorCount);
            var errors = new List<string?>(_tempSensorCount);

            try
            {
                var (useInput, offset) = ResolveRegisterAddress(address);
                int stride = Math.Max(1, _tempRegisterStride);
                int registerCount = _tempSensorCount * stride;

                var readTask = Task.Run(() =>
                {
                    try
                    {
                        if (useInput)
                        {
                            var data = _client!.ReadInputRegisters<ushort>(_tempUnitId, offset, registerCount);
                            return (data.ToArray(), (string?)null);
                        }
                        try
                        {
                            var data = _client!.ReadHoldingRegisters<ushort>(_tempUnitId, offset, registerCount);
                            return (data.ToArray(), (string?)null);
                        }
                        catch (Exception ex1)
                        {
                            try
                            {
                                var data = _client!.ReadInputRegisters<ushort>(_tempUnitId, offset, registerCount);
                                return (data.ToArray(), (string?)null);
                            }
                            catch (Exception ex2)
                            {
                                return (Array.Empty<ushort>(), $"HR:{ex1.Message} / IR:{ex2.Message}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        return (Array.Empty<ushort>(), ex.Message);
                    }
                });

                if (!readTask.Wait(_sensorTimeoutMs))
                {
                    for (int i = 0; i < _tempSensorCount; i++)
                        errors.Add($"讀取超時({_sensorTimeoutMs}ms)");
                    return (result, errors, false, $"讀取超時({_sensorTimeoutMs}ms)");
                }

                var (rawValues, error) = readTask.Result;
                if (error != null)
                {
                    for (int i = 0; i < _tempSensorCount; i++)
                        errors.Add(error);
                    return (result, errors, false, error);
                }

                int strideValue = Math.Max(1, _tempRegisterStride);
                for (int i = 0; i < _tempSensorCount; i++)
                {
                    int rawIndex = i * strideValue;
                    int sensorId = i + 1;
                    if (rawIndex >= rawValues.Length)
                    {
                        result.Add(0);
                        errors.Add("資料不足");
                        continue;
                    }

                    var raw = rawValues[rawIndex];
                    AppendRawLog(raw, sensorId);
                    result.Add(ConvertRawTemperature(raw, sensorId));
                    errors.Add(null);
                }

                return (result, errors, true, null);
            }
            catch (Exception ex)
            {
                for (int i = 0; i < _tempSensorCount; i++)
                    errors.Add($"讀取異常: {ex.Message}");
                return (result, errors, false, $"讀取異常: {ex.Message}");
            }
        }

        /// <summary>
        /// 讀取單一感測器溫度（帶超時保護，不會卡住）
        /// </summary>
        private TemperatureReadResult ReadSingleTemperatureWithTimeout(int sensorId)
        {
            if (_client == null || !_client.IsConnected)
            {
                if (!EnsureConnected())
                    return new TemperatureReadResult(0, "連線失敗");
            }
            
            try
            {
                // 使用 Task 包裝，確保超時能生效
                var readTask = Task.Run(() =>
                {
                    int baseAddr = _effectiveRegisterAddr >= 0 ? _effectiveRegisterAddr : _tempRegisterAddr;
                    var (useInput, baseOffset) = ResolveRegisterAddress(baseAddr);
                    int registerAddr = baseOffset + ((sensorId - 1) * _tempRegisterStride);
                    bool hadException = false;

                    try
                    {
                        var regData = useInput
                            ? _client!.ReadInputRegisters<ushort>(_tempUnitId, registerAddr, 1)
                            : _client!.ReadHoldingRegisters<ushort>(_tempUnitId, registerAddr, 1);
                        var regRaw = regData.ToArray()[0];
                        AppendRawLog(regRaw, sensorId);
                        return (ConvertRawTemperature(regRaw, sensorId), (string?)null);
                    }
                    catch (Exception ex1)
                    {
                        hadException = true;
                        try
                        {
                            var regData = _client!.ReadInputRegisters<ushort>(_tempUnitId, registerAddr, 1);
                            var regRaw = regData.ToArray()[0];
                            AppendRawLog(regRaw, sensorId);
                            return (ConvertRawTemperature(regRaw, sensorId), (string?)null);
                        }
                        catch (Exception ex2)
                        {
                            return (0.0, $"HR:{ex1.Message} / IR:{ex2.Message}");
                        }
                    }
                    finally
                    {
                        if (hadException)
                        {
                            DisconnectClient();
                        }
                    }
                });

                // 等待結果，超時則返回 0
                if (readTask.Wait(_sensorTimeoutMs))
                {
                    var (temp, error) = readTask.Result;
                    if (error != null)
                    {
                        _logger.LogDebug($"{GetSensorName(sensorId)} 錯誤: {error}");
                    }
                    return new TemperatureReadResult(temp, error);
                }
                else
                {
                    _logger.LogDebug($"{GetSensorName(sensorId)} 讀取超時 ({_sensorTimeoutMs}ms)");
                    AppendErrorLog($"溫度讀取超時 DI{sensorId:D2}: {_sensorTimeoutMs}ms");
                    return new TemperatureReadResult(0, $"讀取超時({_sensorTimeoutMs}ms)");
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug($"{GetSensorName(sensorId)} 讀取異常: {ex.Message}");
                AppendErrorLog($"溫度讀取異常 DI{sensorId:D2}: {ex.Message}");
                DisconnectClient();
                return new TemperatureReadResult(0, $"讀取異常: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新數據並通知 UI
        /// </summary>
        private void UpdateData(List<bool> diStatus, List<double> temperatures, List<string?> errors)
        {
            lock (_dataLock)
            {
                _diStatus = diStatus;
                _temperatures = temperatures;
                _temperatureErrors = errors;
                _lastUpdateTime = DateTime.Now;
            }

            for (int i = 0; i < diStatus.Count; i++)
            {
                _diStatusCache[i + 1] = diStatus[i];
            }

            for (int i = 0; i < temperatures.Count; i++)
            {
                _temperatureCache[i + 1] = temperatures[i];
                _temperatureErrorCache[i + 1] = i < errors.Count ? errors[i] : null;
            }

            // 通知 UI 更新
            try
            {
                DataUpdated?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogDebug($"UI 更新通知異常: {ex.Message}");
            }
        }

        private void UpdateDiData(List<bool> diStatus)
        {
            lock (_dataLock)
            {
                _diStatus = diStatus;
                _lastUpdateTime = DateTime.Now;
                _isConnected = true;
                _errorMessage = null;
            }

            for (int i = 0; i < diStatus.Count; i++)
                _diStatusCache[i + 1] = diStatus[i];

            try { DataUpdated?.Invoke(); }
            catch (Exception ex) { _logger.LogDebug($"UI 更新通知異常: {ex.Message}"); }
        }

        private void UpdateTempData(List<double> temperatures, List<string?> errors)
        {
            lock (_dataLock)
            {
                _temperatures = temperatures;
                _temperatureErrors = errors;
                _lastUpdateTime = DateTime.Now;
            }

            for (int i = 0; i < temperatures.Count; i++)
            {
                _temperatureCache[i + 1] = temperatures[i];
                _temperatureErrorCache[i + 1] = i < errors.Count ? errors[i] : null;
            }

            try { DataUpdated?.Invoke(); }
            catch (Exception ex) { _logger.LogDebug($"UI 更新通知異常: {ex.Message}"); }
        }

        /// <summary>
        /// 安全斷開連線
        /// </summary>
        private void DisconnectClient()
        {
            try
            {
                _client?.Disconnect();
            }
            catch { }
            finally
            {
                _client = null;
                _effectiveRegisterAddr = -1;
            }
        }

        /// <summary>
        /// 將原始 16-bit 整數轉換為實際溫度
        /// ADTEK CM1 系列數位溫度表：
        /// - 設備回傳溫度原始值，可依需求設定縮放比例
        /// - 公式: 實際溫度 = RawValue * Scale
        /// - 有效範圍: -1999 ~ 9999（對應 -199.9°C ~ 999.9°C）
        /// - 若 RawValue < -1999，代表感測器異常
        /// </summary>
        private double ConvertRawTemperature(ushort rawUnsigned, int sensorId)
        {
            ushort normalizedRaw = _tempSwapBytes
                ? (ushort)((rawUnsigned >> 8) | (rawUnsigned << 8))
                : rawUnsigned;

            int rawValue = _tempUseSigned
                ? unchecked((short)normalizedRaw)
                : normalizedRaw;
            
            // 錯誤處理：若 RawValue < -1999，代表感測器異常
            if (_tempUseSigned && rawValue < -1999)
            {
                _logger.LogWarning($"[TempDebug] DI{sensorId:D2} Raw: {rawValue} (unsigned: {rawUnsigned}) → 感測器異常，設為 0");
                AppendTemperatureLog(rawValue, 0, sensorId);
                return 0;
            }
            
            // 依設定縮放比例換算溫度
            double temperature = rawValue * _tempScale;
            
            _logger.LogInformation($"[TempDebug] DI{sensorId:D2} Raw: {rawValue}, Calculated: {temperature:F1}°C");
            AppendTemperatureLog(rawValue, temperature, sensorId);
            
            // 四捨五入到小數點後 1 位
            return Math.Round(temperature, 1);
        }

        private void AppendTemperatureLog(int rawValue, double temperature, int sensorId)
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var logLine = $"[{timestamp}] [TempDebug] DI{sensorId:D2} Raw: {rawValue}, Calculated: {temperature:F1}°C";
            try
            {
                lock (_tempLogLock)
                {
                    _tempLogBuffer.Enqueue(logLine);
                    while (_tempLogBuffer.Count > TempLogMaxEntries)
                    {
                        _tempLogBuffer.Dequeue();
                    }
                    File.WriteAllLines(_tempLogPath, _tempLogBuffer);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug($"溫度記錄寫入失敗: {ex.Message}");
            }
        }

        private static byte[] ParseHexCommand(string? value, string defaultHex)
        {
            var normalized = (string.IsNullOrWhiteSpace(value) ? defaultHex : value)
                .Replace(" ", string.Empty, StringComparison.Ordinal)
                .Trim();

            if (normalized.Length % 2 != 0)
                throw new InvalidOperationException($"TcpQueryHex 長度必須為偶數: {normalized}");

            var command = new byte[normalized.Length / 2];
            for (int i = 0; i < command.Length; i++)
            {
                command[i] = Convert.ToByte(normalized.Substring(i * 2, 2), 16);
            }
            return command;
        }

        private static bool TryReadTempField(JsonElement source, string propertyName, out double value)
        {
            value = 0;
            if (!source.TryGetProperty(propertyName, out var node))
                return false;

            if (node.ValueKind == JsonValueKind.Number && node.TryGetDouble(out var numeric))
            {
                value = numeric;
                return true;
            }

            if (node.ValueKind == JsonValueKind.String &&
                double.TryParse(node.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                value = parsed;
                return true;
            }

            return false;
        }

        private async Task<string> ReadJsonPayloadAsync(NetworkStream stream, CancellationToken stoppingToken,
            int timeoutMs = 0)
        {
            var buffer = new byte[4096];
            var sb = new StringBuilder();
            // Push 模式下傳入 0 表示不設 deadline，靠 stoppingToken 和 stream.ReadTimeout 控制
            var deadline = timeoutMs > 0
                ? DateTime.UtcNow.AddMilliseconds(timeoutMs)
                : DateTime.MaxValue;
            bool foundJsonStart = false;
            int braceDepth = 0;
            char prevChar = '\0';

            while (!stoppingToken.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), stoppingToken);
                if (read <= 0)
                    break;

                var chunk = Encoding.UTF8.GetString(buffer, 0, read);
                sb.Append(chunk);

                foreach (char ch in chunk)
                {
                    if (!foundJsonStart)
                    {
                        // 只有 '{"' 連續出現才視為 JSON 物件開始（避免 binary 前綴干擾）
                        if (prevChar == '{' && ch == '"')
                        {
                            foundJsonStart = true;
                            braceDepth = 1; // 已計入前面的 '{'
                        }
                        prevChar = ch;
                    }
                    else
                    {
                        if (ch == '{') braceDepth++;
                        else if (ch == '}')
                        {
                            braceDepth--;
                            if (braceDepth == 0)
                                return ExtractJsonObject(sb.ToString());
                        }
                    }
                }
            }

            var raw = sb.ToString();
            if (string.IsNullOrWhiteSpace(raw))
                throw new InvalidOperationException("未收到任何回應資料");

            return ExtractJsonObject(raw);
        }

        private static string ExtractJsonObject(string raw)
        {
            // 以 '{"' 找 JSON 物件起點，避免被 binary 前綴中的 '{' 誤導
            int start = raw.IndexOf("{\"");
            if (start < 0) start = raw.IndexOf('{');
            int end = raw.LastIndexOf('}');
            if (start < 0 || end <= start)
                throw new InvalidOperationException($"回應不是有效 JSON: {raw}");
            return raw.Substring(start, end - start + 1);
        }

        private static double ReadTempScale(string? value)
        {
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var scale) && scale > 0)
            {
                return scale;
            }
            return 0.1;
        }

        private static bool ReadBoolSetting(string? value, bool defaultValue)
        {
            return bool.TryParse(value, out var parsed) ? parsed : defaultValue;
        }

        private static int NormalizeHoldingRegisterAddress(int address)
        {
            if (address >= 40001)
            {
                return address - 40001;
            }
            return address;
        }

        /// <summary>
        /// 解析暫存器候選地址字串，例如 "40001,40002,0,30001"
        /// </summary>
        private static List<int> ParseRegisterAddrCandidates(string? value)
        {
            var list = new List<int>();
            if (string.IsNullOrWhiteSpace(value))
                return list;
            foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(part.Trim(), out var addr))
                    list.Add(addr);
            }
            return list;
        }

        /// <summary>
        /// 解析 Modbus 地址為 (是否用 Input Register, 0-based 偏移)
        /// </summary>
        private static (bool UseInput, int Offset) ResolveRegisterAddress(int address)
        {
            if (address >= 30001 && address < 40001)
                return (true, address - 30001);
            if (address >= 40001)
                return (false, address - 40001);
            return (false, address);
        }

        private void SafeLogError(string message)
        {
            try
            {
                _logger.LogError(message);
            }
            catch
            {
                // Ignore logging failures to avoid crashing background service.
            }
        }

        private void AppendTemperatureErrorLog(string? error, int sensorId)
        {
            if (string.IsNullOrWhiteSpace(error))
                return;

            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var logLine = $"[{timestamp}] [TempError] DI{sensorId:D2} {GetSensorName(sensorId)} Error: {error}";
            try
            {
                lock (_tempLogLock)
                {
                    _tempLogBuffer.Enqueue(logLine);
                    while (_tempLogBuffer.Count > TempLogMaxEntries)
                    {
                        _tempLogBuffer.Dequeue();
                    }
                    File.WriteAllLines(_tempLogPath, _tempLogBuffer);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug($"溫度錯誤記錄寫入失敗: {ex.Message}");
            }
        }

        private void AppendRawLog(ushort rawUnsigned, int sensorId)
        {
            short rawSigned = unchecked((short)rawUnsigned);
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var logLine = $"[{timestamp}] [RawData] DI{sensorId:D2} Raw(unsigned): {rawUnsigned}, Raw(signed): {rawSigned}";
            try
            {
                lock (_rawLogLock)
                {
                    _rawLogBuffer.Enqueue(logLine);
                    while (_rawLogBuffer.Count > RawLogMaxEntries)
                    {
                        _rawLogBuffer.Dequeue();
                    }
                    File.WriteAllLines(_rawLogPath, _rawLogBuffer);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug($"Raw 記錄寫入失敗: {ex.Message}");
            }
        }

        private void AppendDiDiagnosticLog(string source, List<bool> statuses, string? errorMessage, DateTime timestamp)
        {
            var active = statuses
                .Select((value, index) => new { value, index })
                .Where(x => x.value)
                .Select(x => $"DI{x.index + 1:D2}")
                .ToList();
            var statusSummary = active.Count > 0 ? string.Join(", ", active) : "無啟動";
            var logLine = $"[{timestamp:yyyy-MM-dd HH:mm:ss}] [DiDiag:{source}] {(string.IsNullOrWhiteSpace(errorMessage) ? $"Active: {statusSummary}" : $"Error: {errorMessage}")}";
            try
            {
                lock (_diLogLock)
                {
                    _diLogBuffer.Enqueue(logLine);
                    while (_diLogBuffer.Count > DiLogMaxEntries)
                    {
                        _diLogBuffer.Dequeue();
                    }
                    File.WriteAllLines(_diLogPath, _diLogBuffer);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug($"DI 記錄寫入失敗: {ex.Message}");
            }
        }

        private void AppendErrorLog(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var logLine = $"[{timestamp}] [Error] {message}";
            try
            {
                lock (_errorLogLock)
                {
                    _errorLogBuffer.Enqueue(logLine);
                    while (_errorLogBuffer.Count > ErrorLogMaxEntries)
                    {
                        _errorLogBuffer.Dequeue();
                    }
                    File.WriteAllLines(_errorLogPath, _errorLogBuffer);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug($"Error 記錄寫入失敗: {ex.Message}");
            }
        }

        public override void Dispose()
        {
            DisconnectClient();
            base.Dispose();
        }

        private record TemperatureReadResult(double Temperature, string? ErrorMessage);
    }
}
