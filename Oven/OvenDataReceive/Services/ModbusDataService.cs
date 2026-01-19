using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;
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
        private readonly int _readIntervalMs;      // 讀取週期（毫秒）
        private readonly int _sensorTimeoutMs;     // 單一感測器超時（毫秒）

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

        // 溫度感測器資訊對應表（站號 1-12）
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
            _ipAddress = _configuration["Modbus:IpAddress"] ?? "192.168.61.144";
            _port = int.Parse(_configuration["Modbus:Port"] ?? "4196");
            _diUnitId = byte.Parse(_configuration["Modbus:DiUnitId"] ?? "255");
            _tempSensorCount = int.Parse(_configuration["Modbus:TempSensorCount"] ?? "12");
            _tempUnitId = byte.Parse(_configuration["Modbus:TempUnitId"] ?? "1");
            _tempRegisterAddr = int.Parse(_configuration["Modbus:TempRegisterAddr"] ?? "0");
            _tempRegisterStride = int.Parse(_configuration["Modbus:TempRegisterStride"] ?? "1");

            var logsDir = Path.Combine(Directory.GetCurrentDirectory(), "Logs");
            Directory.CreateDirectory(logsDir);
            _tempLogPath = Path.Combine(logsDir, "TemperatureRecording.txt");
            _rawLogPath = Path.Combine(logsDir, "RawData.txt");
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
            
            // 讀取週期：預設 2000ms (2秒)
            _readIntervalMs = Math.Clamp(
                int.Parse(_configuration["Modbus:ReadIntervalMs"] ?? "2000"), 
                500, 10000);
            
            // 單一感測器超時：預設 1000ms (1秒)
            _sensorTimeoutMs = Math.Clamp(
                int.Parse(_configuration["Modbus:SensorTimeoutMs"] ?? "1000"), 
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
            _logger.LogInformation($"  溫度裝置 UnitID: {_tempUnitId}");
            _logger.LogInformation($"  溫度寄存器: 基底地址={_tempRegisterAddr}, 步進={_tempRegisterStride}");
            _logger.LogInformation($"  讀取週期: {_readIntervalMs}ms");
            _logger.LogInformation($"  感測器超時: {_sensorTimeoutMs}ms");
            _logger.LogInformation("========================================");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // 等待應用程式完全啟動
            await Task.Delay(1000, stoppingToken);
            
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Step 1: 確保連線
                    if (!EnsureConnected())
                    {
                        await Task.Delay(2000, stoppingToken);
                        continue;
                    }

                    // Step 2: 讀取 DI 狀態
                    var diResult = ReadDiStatus();
                    
                    // Step 3: 背景持續輪詢所有站點溫度
                    var tempResult = await ReadTemperaturesAsync(stoppingToken);
                    
                    // Step 4: 更新數據並通知 UI
                    UpdateData(diResult, tempResult.Temperatures, tempResult.Errors);
                    
                    IsConnected = true;
                    ErrorMessage = null;
                }
                catch (Exception ex)
                {
                    _logger.LogError($"❌ 執行異常: {ex.Message}");
                    IsConnected = false;
                    ErrorMessage = ex.Message;
                    DisconnectClient();
                }

                // 等待下一個讀取週期
                await Task.Delay(_readIntervalMs, stoppingToken);
            }
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
                _logger.LogError($"❌ 連線失敗: {ex.Message}");
                DisconnectClient();
            }
            
            return false;
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
            }

            return result;
        }

        /// <summary>
        /// 背景持續輪詢溫度（不依賴 DI 狀態）
        /// </summary>
        private async Task<(List<double> Temperatures, List<string?> Errors)> ReadTemperaturesAsync(CancellationToken stoppingToken)
        {
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
                    // 閘道器映射架構：
                    // - 所有 CM1 溫度表映射到同一個 UnitID
                    // - 不同感測器對應不同的寄存器地址
                    // - 地址 = 基底地址 + (感測器序號 - 1) * 步進
                    int registerAddr = _tempRegisterAddr + ((sensorId - 1) * _tempRegisterStride);
                    bool hadException = false;

                    try
                    {
                        // 嘗試讀取 Holding Register (功能碼 03)
                        var data = _client!.ReadHoldingRegisters<ushort>(_tempUnitId, registerAddr, 1);
                        var raw = data.ToArray()[0];
                        AppendRawLog(raw, sensorId);
                        return (ConvertRawTemperature(raw, sensorId), (string?)null);
                    }
                    catch (Exception ex1)
                    {
                        hadException = true;
                        // 嘗試 Input Register (功能碼 04)
                        try
                        {
                            var data = _client!.ReadInputRegisters<ushort>(_tempUnitId, registerAddr, 1);
                            var raw = data.ToArray()[0];
                            AppendRawLog(raw, sensorId);
                            return (ConvertRawTemperature(raw, sensorId), (string?)null);
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
                    return new TemperatureReadResult(0, $"讀取超時({_sensorTimeoutMs}ms)");
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug($"{GetSensorName(sensorId)} 讀取異常: {ex.Message}");
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
            }
        }

        /// <summary>
        /// 將原始 16-bit 整數轉換為實際溫度
        /// ADTEK CM1 系列數位溫度表：
        /// - 設備直接回傳溫度值 * 10（例如 1525 = 152.5°C）
        /// - 公式: 實際溫度 = RawValue / 10.0
        /// - 有效範圍: -1999 ~ 9999（對應 -199.9°C ~ 999.9°C）
        /// - 若 RawValue < -1999，代表感測器異常
        /// </summary>
        private double ConvertRawTemperature(ushort rawUnsigned, int sensorId)
        {
            // 將 unsigned 轉換為 signed（因為溫度可為負值）
            short raw = unchecked((short)rawUnsigned);
            
            // 錯誤處理：若 RawValue < -1999，代表感測器異常
            if (raw < -1999)
            {
                _logger.LogWarning($"[TempDebug] DI{sensorId:D2} Raw: {raw} (unsigned: {rawUnsigned}) → 感測器異常，設為 0");
                AppendTemperatureLog(raw, 0, sensorId);
                return 0;
            }
            
            // ADTEK CM1 換算公式：實際溫度 = RawValue / 10.0
            double temperature = raw / 10.0;
            
            _logger.LogInformation($"[TempDebug] DI{sensorId:D2} Raw: {raw}, Calculated: {temperature:F1}°C");
            AppendTemperatureLog(raw, temperature, sensorId);
            
            // 四捨五入到小數點後 1 位
            return Math.Round(temperature, 1);
        }

        private void AppendTemperatureLog(short raw, double temperature, int sensorId)
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var logLine = $"[{timestamp}] [TempDebug] DI{sensorId:D2} Raw: {raw}, Calculated: {temperature:F1}°C";
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

        public override void Dispose()
        {
            DisconnectClient();
            base.Dispose();
        }

        private record TemperatureReadResult(double Temperature, string? ErrorMessage);
    }
}
