using Microsoft.AspNetCore.Mvc;
using OvenDataReceive.Services;

namespace OvenDataReceive.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class TemperatureController : ControllerBase
    {
        private readonly ModbusDataService _modbusService;
        private readonly IConfiguration _configuration;

        public TemperatureController(ModbusDataService modbusService, IConfiguration configuration)
        {
            _modbusService = modbusService;
            _configuration = configuration;
        }

        /// <summary>
        /// 取得即時溫度資料 (JSON 格式)
        /// </summary>
        /// <returns>包含 12 個溫度感測器的資料</returns>
        [HttpGet]
        public IActionResult GetTemperatures()
        {
            var temperatures = _modbusService.Temperatures;
            var errors = _modbusService.TemperatureErrors;

            var response = new
            {
                id = _configuration["Modbus:TempUnitId"] ?? "1",
                sn = _configuration["Device:SerialNumber"] ?? "SHX32rDtIQc4ahtc",
                data = new
                {
                    temp1 = temperatures.Count > 0 ? temperatures[0] : 0.0,
                    temp2 = temperatures.Count > 1 ? temperatures[1] : 0.0,
                    temp3 = temperatures.Count > 2 ? temperatures[2] : 0.0,
                    temp4 = temperatures.Count > 3 ? temperatures[3] : 0.0,
                    temp5 = temperatures.Count > 4 ? temperatures[4] : 0.0,
                    temp6 = temperatures.Count > 5 ? temperatures[5] : 0.0,
                    temp7 = temperatures.Count > 6 ? temperatures[6] : 0.0,
                    temp8 = temperatures.Count > 7 ? temperatures[7] : 0.0,
                    temp9 = temperatures.Count > 8 ? temperatures[8] : 0.0,
                    temp10 = temperatures.Count > 9 ? temperatures[9] : 0.0,
                    temp11 = temperatures.Count > 10 ? temperatures[10] : 0.0,
                    temp12 = temperatures.Count > 11 ? temperatures[11] : 0.0
                },
                isConnected = _modbusService.IsConnected,
                lastUpdate = _modbusService.LastUpdateTime,
                errors = errors
            };

            return Ok(response);
        }

        /// <summary>
        /// 取得詳細的溫度資料（包含感測器名稱和位置）
        /// </summary>
        [HttpGet("detailed")]
        public IActionResult GetDetailedTemperatures()
        {
            var temperatures = _modbusService.Temperatures;
            var errors = _modbusService.TemperatureErrors;

            var sensorData = new List<object>();

            for (int i = 0; i < temperatures.Count; i++)
            {
                int sensorId = i + 1;
                var sensorInfo = ModbusDataService.GetSensorInfo(sensorId);

                sensorData.Add(new
                {
                    sensorId = sensorId,
                    name = ModbusDataService.GetSensorName(sensorId),
                    device = sensorInfo?.Device,
                    position = sensorInfo?.Position,
                    category = sensorInfo?.Category,
                    temperature = temperatures[i],
                    error = i < errors.Count ? errors[i] : null
                });
            }

            var response = new
            {
                id = _configuration["Modbus:TempUnitId"] ?? "1",
                sn = _configuration["Device:SerialNumber"] ?? "SHX32rDtIQc4ahtc",
                isConnected = _modbusService.IsConnected,
                lastUpdate = _modbusService.LastUpdateTime,
                sensors = sensorData
            };

            return Ok(response);
        }

        /// <summary>
        /// 取得連線狀態
        /// </summary>
        [HttpGet("status")]
        public IActionResult GetStatus()
        {
            return Ok(new
            {
                isConnected = _modbusService.IsConnected,
                lastUpdate = _modbusService.LastUpdateTime,
                errorMessage = _modbusService.ErrorMessage,
                ipAddress = _configuration["Modbus:IpAddress"],
                port = _configuration["Modbus:Port"]
            });
        }
    }
}
