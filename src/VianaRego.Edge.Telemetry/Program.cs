using Microsoft.EntityFrameworkCore;
using VianaRego.Shared.Data;
using MQTTnet;
using MQTTnet.Client;
using System.Text.Json;
using VianaRego.Shared.Configuration;
using VianaRego.Shared.Models;
using VianaRego.Shared.Logging;
using System.Diagnostics;

Console.WriteLine("Edge Telemetry Service starting (Offline Buffering + Log Aggregation)...");

var sharedLogger = new SharedLogger("EdgeAgent.log", "Telemetry");
sharedLogger.LogInfo("Edge Telemetry Service starting (Offline Buffering + Log Aggregation)...");

// ... (Configuration loading same as before)
string mqttHost = "";
string mqttPortStr = "8883";
string mqttUser = "";
string mqttPass = "";
string? registryDeviceId = null;
string? registryDeviceToken = null;

if (OperatingSystem.IsWindows())
{
    mqttHost = RegistryConfig.GetValue("MQTT_HOST") ?? "";
    registryDeviceId = RegistryConfig.GetValue("DEVICE_ID");
    registryDeviceToken = RegistryConfig.GetValue("DEVICE_TOKEN");

    mqttPortStr = RegistryConfig.GetValue("MQTT_PORT") ?? "8883";
    mqttUser = RegistryConfig.GetValue("MQTT_USER") ?? "";
    mqttPass = RegistryConfig.GetValue("MQTT_PASSWORD") ?? "";
}

// Fallback to .env
var current = Directory.GetCurrentDirectory();
while (current != null && !File.Exists(Path.Combine(current, ".env")))
    current = Directory.GetParent(current)?.FullName;

if (current != null) DotNetEnv.Env.Load(Path.Combine(current, ".env"));

if (string.IsNullOrEmpty(mqttHost)) mqttHost = Environment.GetEnvironmentVariable("MQTT_HOST") ?? "";
if (string.IsNullOrEmpty(mqttUser)) mqttUser = Environment.GetEnvironmentVariable("MQTT_USER") ?? "";
if (string.IsNullOrEmpty(mqttPass)) mqttPass = Environment.GetEnvironmentVariable("MQTT_PASSWORD") ?? "";

var configuredDeviceId = !string.IsNullOrWhiteSpace(registryDeviceId)
    ? registryDeviceId
    : Environment.GetEnvironmentVariable("DEVICE_ID");
var deviceToken = !string.IsNullOrWhiteSpace(registryDeviceToken)
    ? registryDeviceToken
    : Environment.GetEnvironmentVariable("DEVICE_TOKEN");
var deviceId = DeviceIdentity.ResolveStableDeviceId(configuredDeviceId, deviceToken);

if (OperatingSystem.IsWindows() &&
    !string.Equals(registryDeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
{
    RegistryConfig.SetValue("DEVICE_ID", deviceId);
}

var mqttFactory = new MqttFactory();
using var mqttClient = mqttFactory.CreateMqttClient();

var mqttOptions = new MqttClientOptionsBuilder()
    .WithTcpServer(mqttHost, int.Parse(mqttPortStr))
    .WithCredentials(mqttUser, mqttPass)
    .WithTls(new MqttClientOptionsBuilderTlsParameters { UseTls = true })
    .WithCleanSession()
    .Build();

var dbPath = DataPath.GetPath("viana-edge.db");
var optionsBuilder = new DbContextOptionsBuilder<LocalDbContext>();
optionsBuilder.UseSqlite($"Data Source={dbPath}");

// Ensure DB Created
using (var db = new LocalDbContext(optionsBuilder.Options)) {
    db.EnsureCreatedAndPatched();
}

// Track last log offset for aggregation
int lastLogOffset = 0;

while (true)
{
    try 
    {
        // 1. Ensure Connected
        if (!mqttClient.IsConnected)
        {
            Console.WriteLine($"Connecting to MQTT {mqttHost}...");
            try { 
                await mqttClient.ConnectAsync(mqttOptions); 
                Console.WriteLine("Connected.");
            } 
            catch { Console.WriteLine("Connection failed. Will buffer."); }
        }

        using var db = new LocalDbContext(optionsBuilder.Options);

        // 2. Fetch State to Publish (The "Current" state)
        var status = await db.SystemStatus.FirstOrDefaultAsync(s => s.Id == "system");
        var peripherals = await db.Peripherals.ToListAsync();
        var moduleStates = await db.ModuleStates.ToListAsync();

        // ADOPTION DISCOVERY: Scan for interesting processes running on the host
        var discovered = Process.GetProcesses()
            .Where(p => {
                try {
                    var name = p.ProcessName.ToLower();
                    return name.Contains("viana") || name.Contains("meldcx") || name.Contains("coatro");
                } catch { return false; }
            })
            .Select(p => {
                try {
                    return new {
                        Name = p.ProcessName,
                        Path = p.MainModule?.FileName,
                        Id = p.Id
                    };
                } catch {
                    return new { Name = p.ProcessName, Path = (string?)null, Id = p.Id };
                }
            })
            .ToList();

        // ALL PROCESSES: For debugging and full visibility (--all flag)
        var allProcesses = Process.GetProcesses()
            .Select(p => {
                try {
                    return new {
                        Name = p.ProcessName,
                        Path = p.MainModule?.FileName,
                        Id = p.Id
                    };
                } catch {
                    return new { Name = p.ProcessName, Path = (string?)null, Id = p.Id };
                }
            })
            .ToList();

        // LOG AGGREGATION: Fetch critical logs since last offset
        var criticalLogs = await db.Logs
            .Where(l => l.Id > lastLogOffset && (l.Level == "ERROR" || l.Level == "WARN"))
            .OrderBy(l => l.Id)
            .Take(50)
            .ToListAsync();
        
        if (criticalLogs.Any())
        {
            lastLogOffset = criticalLogs.Max(l => l.Id);
        }

        // HEALTH MONITORING: Check if Agent and Watchdog are running
        var agentRunning = Process.GetProcessesByName("VianaRego.Edge.Agent").Any();
        var watchdogRunning = Process.GetProcessesByName("VianaRego.Edge.Watchdog").Any();
        
        var healthStatus = new {
            AgentRunning = agentRunning,
            WatchdogRunning = watchdogRunning,
            Healthy = agentRunning && watchdogRunning
        };

        var payloadObj = new {
            DeviceId = deviceId,
            Timestamp = DateTime.UtcNow,
            System = status,
            Peripherals = peripherals,
            Modules = moduleStates,
            DiscoveredProcesses = discovered,
            AllProcesses = allProcesses,
            Health = healthStatus,
            CriticalLogs = criticalLogs.Select(l => new { l.Timestamp, l.Component, l.Level, l.Message, l.Emoji }).ToList()
        };

        string topic = $"device/{deviceId}/status";
        string payloadJson = JsonSerializer.Serialize(payloadObj);

        // 3. Try to Publish (Flush Queue + Current)
        if (mqttClient.IsConnected)
        {
            // Flush Queue First
            var queue = await db.TelemetryQueue.OrderBy(x => x.CreatedAt).Take(50).ToListAsync(); // Process batch
            if (queue.Any())
            {
                Console.WriteLine($"Flushing {queue.Count} buffered messages...");
                foreach (var msg in queue)
                {
                    try {
                        await mqttClient.PublishAsync(new MqttApplicationMessageBuilder()
                            .WithTopic(msg.Topic).WithPayload(msg.Payload).Build());
                        db.TelemetryQueue.Remove(msg);
                    } catch { break; } // Stop flushing on error
                }
                await db.SaveChangesAsync();
            }

            // Publish Current
            await mqttClient.PublishAsync(new MqttApplicationMessageBuilder()
                .WithTopic(topic).WithPayload(payloadJson).WithRetainFlag().Build());
            
            Console.WriteLine($"[{DateTime.Now}] Telemetry published (Logs: {criticalLogs.Count}, Health: {(healthStatus.Healthy ? "OK" : "DEGRADED")}).");
        }
        else
        {
            // 4. Buffer if Offline
            Console.WriteLine("Offline. Buffering message.");
            db.TelemetryQueue.Add(new TelemetryMessage {
                Topic = topic,
                Payload = payloadJson,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error in Telemetry: {ex.Message}");
        sharedLogger.LogError($"Error in Telemetry: {ex.Message}");
    }

    await Task.Delay(3000); // Publish every 3s (faster updates for download progress)
}


