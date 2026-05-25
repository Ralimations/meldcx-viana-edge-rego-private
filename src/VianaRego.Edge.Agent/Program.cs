using VianaRego.Edge.Agent;
using VianaRego.Shared.Data;
using VianaRego.Edge.Agent.Services;
using Serilog;
using Serilog.Events;
using Microsoft.EntityFrameworkCore;
using VianaRego.Shared.Configuration;
using System.Net.Http.Json;
using System.Runtime.InteropServices;

var builder = Host.CreateApplicationBuilder(args);

// Enable Windows Service lifetime
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "VianaRego Edge Orchestrator";
});

// 1. Load from Registry (Production / MSI)
if (OperatingSystem.IsWindows() && builder.Configuration["NO_REGISTRY"] != "true")
{
    var regKeys = new[] { "ORG_ID", "DEVICE_ID", "DEVICE_TOKEN", "MQTT_HOST", "MQTT_PORT", "MQTT_USER", "MQTT_PASSWORD" };
    foreach (var key in regKeys)
    {
        var val = RegistryConfig.GetValue(key);
        if (!string.IsNullOrEmpty(val))
        {
            builder.Configuration[key] = val;
        }
    }
}

// 2. Load from .env (Dev Fallback) - only if registry didn't mandate everything
// Search upwards for .env file
var currentDir = Directory.GetCurrentDirectory();
while (!string.IsNullOrEmpty(currentDir))
{
    var envPath = Path.Combine(currentDir, ".env");
    if (File.Exists(envPath))
    {
        DotNetEnv.Env.Load(envPath);
        break;
    }
    currentDir = Directory.GetParent(currentDir)?.FullName;
}
builder.Configuration.AddEnvironmentVariables();

// 3. Resolve a stable per-machine DEVICE_ID for MQTT
// Device will auto-register via MQTT discovery when it sends its first heartbeat
var registryDeviceId = OperatingSystem.IsWindows() ? RegistryConfig.GetValue("DEVICE_ID") : null;
var configuredDeviceId = builder.Configuration["DEVICE_ID"] ?? registryDeviceId;

var stableDeviceId = DeviceIdentity.ResolveStableDeviceId(
    configuredDeviceId,
    builder.Configuration["DEVICE_TOKEN"]);

builder.Configuration["DEVICE_ID"] = stableDeviceId;

// Persist to registry for future runs
if (OperatingSystem.IsWindows() &&
    !string.Equals(registryDeviceId, stableDeviceId, StringComparison.OrdinalIgnoreCase))
{
    RegistryConfig.SetValue("DEVICE_ID", stableDeviceId);
}

// Add Serilog
var logPath = DataPath.GetPath("edge.log");
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning)
    .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
    .WriteTo.Console()
    .WriteTo.File(logPath, rollingInterval: RollingInterval.Day)
    .CreateLogger();
builder.Logging.ClearProviders();
builder.Logging.AddSerilog();

// Database
builder.Services.AddDbContext<LocalDbContext>(options =>
{
    var dbPath = DataPath.GetPath("viana-edge.db");
    options.UseSqlite($"Data Source={dbPath}");
});

builder.Services.AddHttpClient<ResumableDownloader>();
builder.Services.AddSingleton<ProcessOrchestrator>();

builder.Services.AddSingleton<LogService>();
builder.Services.AddSingleton<SignatureVerifier>();
builder.Services.AddSingleton<EdgeReconcilerService>();
builder.Services.AddSingleton<MqttService>();
builder.Services.AddHostedService<HardwareCollectorService>(); // One-time hardware collection
builder.Services.AddHostedService<Worker>();

var host = builder.Build();

// Ensure Database is created
using (var scope = host.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();
    db.EnsureCreatedAndPatched();
}

host.Run();
