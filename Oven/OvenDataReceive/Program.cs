using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using OvenDataReceive.Components;
using OvenDataReceive.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// 註冊 Modbus 數據服務
builder.Services.AddSingleton<ModbusDataService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<ModbusDataService>());

var localIp = GetLocalIPv4();
var port = 5133;
if (!string.IsNullOrWhiteSpace(localIp))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}", $"http://{localIp}:{port}");
}
else
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

var app = builder.Build();

if (!string.IsNullOrWhiteSpace(localIp))
{
    app.Logger.LogInformation("Local IP detected: {LocalIp}", localIp);
    app.Logger.LogInformation("Listening on: http://{LocalIp}:{Port}", localIp, port);
}
else
{
    app.Logger.LogWarning("Local IP not detected, binding to 0.0.0.0 only.");
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static string? GetLocalIPv4()
{
    try
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            var ipProps = ni.GetIPProperties();
            foreach (var ip in ipProps.UnicastAddresses)
            {
                if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    var address = ip.Address.ToString();
                    if (!IPAddress.IsLoopback(ip.Address))
                    {
                        return address;
                    }
                }
            }
        }
    }
    catch
    {
        // Ignore and fallback to 0.0.0.0 binding.
    }

    return null;
}
