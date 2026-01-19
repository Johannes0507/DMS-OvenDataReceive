using System.Net;
using FluentModbus;

namespace OvenDataReceive.Services
{
    public class ModbusDataService : BackgroundService
    {
        private readonly ILogger<ModbusDataService> _logger;
        private ModbusTcpClient? _client;
        private readonly string _ipAddress = "192.168.61.144";
        private readonly int _port = 4196;
        private readonly byte _unitId = 1;

        // 數據模型
        public List<bool> DiStatus { get; private set; } = new();
        public List<double> Temperatures { get; private set; } = new();
        public bool IsConnected { get; private set; }
        public DateTime LastUpdateTime { get; private set; } = DateTime.MinValue;
        public string? ErrorMessage { get; private set; }

        // 事件通知
        public event Action? DataUpdated;

        public ModbusDataService(ILogger<ModbusDataService> logger)
        {
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Modbus 數據服務啟動中...");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (_client == null || !_client.IsConnected)
                    {
                        await ConnectAsync();
                    }

                    if (_client != null && _client.IsConnected)
                    {
                        await ReadDataAsync();
                        IsConnected = true;
                        ErrorMessage = null;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "讀取數據時發生錯誤");
                    IsConnected = false;
                    ErrorMessage = ex.Message;
                    _client?.Disconnect();
                    _client = null;
                }

                await Task.Delay(1000, stoppingToken); // 每秒更新一次
            }
        }

        private async Task ConnectAsync()
        {
            try
            {
                _client?.Disconnect();
                _client = new ModbusTcpClient();
                await Task.Run(() =>
                {
                    _client.Connect(new IPEndPoint(IPAddress.Parse(_ipAddress), _port));
                });
                _logger.LogInformation($"已連線至 {_ipAddress}:{_port}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "連線失敗");
                throw;
            }
        }

        private async Task ReadDataAsync()
        {
            if (_client == null || !_client.IsConnected) return;

            await Task.Run(() =>
            {
                // 讀取 DI 狀態
                var diData = _client.ReadDiscreteInputs(_unitId, 0, 16);
                DiStatus = diData.ToArray().Select(b => b == 1).ToList();

                // 讀取溫度數據
                try
                {
                    var tempRegisters = _client.ReadHoldingRegisters<ushort>(_unitId, 1000, 10);
                    Temperatures = tempRegisters.ToArray().Select(t => t / 10.0).ToList();
                }
                catch
                {
                    // 嘗試輸入寄存器
                    try
                    {
                        var tempRegisters = _client.ReadInputRegisters<ushort>(_unitId, 3000, 10);
                        Temperatures = tempRegisters.ToArray().Select(t => t / 10.0).ToList();
                    }
                    catch
                    {
                        // 如果都失敗，保持現有數據
                    }
                }

                LastUpdateTime = DateTime.Now;
                DataUpdated?.Invoke();
            });
        }

        public override void Dispose()
        {
            _client?.Disconnect();
            base.Dispose();
        }
    }
}

