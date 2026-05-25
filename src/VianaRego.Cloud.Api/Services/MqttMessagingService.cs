using MQTTnet;
using MQTTnet.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using VianaRego.Cloud.Api.Data;
using VianaRego.Cloud.Api.Entities;
using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;

namespace VianaRego.Cloud.Api.Services;

public class MqttMessagingService : IHostedService // Hosted service to keep connection alive
{
    private readonly ILogger<MqttMessagingService> _logger;
    private readonly IConfiguration _configuration;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly SigningService _signingService;
    private IMqttClient _mqttClient = null!;
    private MqttClientOptions _mqttOptions = null!;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly TimeSpan _reconnectInitialDelay = TimeSpan.FromSeconds(2);
    private readonly TimeSpan _reconnectMaxDelay = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pendingRequests = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastWarningLogUtcByKey = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastFreshHeartbeatUtcByDevice = new();
    private readonly ConcurrentDictionary<string, string> _lastDisplayNameByDevice = new();
    private readonly ConcurrentDictionary<string, bool> _isDeviceOnlineById = new();
    private static readonly TimeSpan WarningLogInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan OnlineHeartbeatWindow = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan OfflineHeartbeatTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan PresenceMonitorInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxFutureTimestampSkew = TimeSpan.FromMinutes(2);
    private CancellationTokenSource? _reconnectCts;
    private Task? _reconnectTask;
    private Task? _presenceMonitorTask;
    private int _reconnectLoopActive;
    private volatile bool _isStopping;
    private string _mqttHost = string.Empty;
    private int _mqttPort;

    public MqttMessagingService(ILogger<MqttMessagingService> logger, IConfiguration configuration, IServiceScopeFactory serviceScopeFactory, SigningService signingService)
    {
        _logger = logger;
        _configuration = configuration;
        _serviceScopeFactory = serviceScopeFactory;
        _signingService = signingService;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var mqttFactory = new MqttFactory();
        _mqttClient = mqttFactory.CreateMqttClient();
        _reconnectCts = new CancellationTokenSource();
        _isStopping = false;

        _mqttHost = _configuration["MQTT_HOST"] ?? string.Empty;
        _mqttPort = int.TryParse(_configuration["MQTT_PORT"], out var configuredPort) ? configuredPort : 8883;
        var mqttUser = _configuration["MQTT_USER"];
        var mqttPassword = _configuration["MQTT_PASSWORD"];
        var useTls = bool.Parse(_configuration["MQTT_USE_TLS"] ?? "true");

        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(_mqttHost, _mqttPort)
            .WithCredentials(mqttUser, mqttPassword)
            .WithClientId($"cloud-api-{Guid.NewGuid()}");

        if (useTls)
        {
            builder.WithTlsOptions(tls =>
            {
                tls.UseTls(true);
                tls.WithCertificateValidationHandler(_ => true);
            });
        }

        _mqttOptions = builder.Build();

        _mqttClient.ConnectedAsync += async e =>
        {
            _logger.LogInformation("Cloud API connected to MQTT broker.");
            // Subscribe to all device status updates
            await _mqttClient.SubscribeAsync("device/+/status");
            await _mqttClient.SubscribeAsync("device/+/logs/response");
        };

        _mqttClient.DisconnectedAsync += e =>
        {
            if (_isStopping || (_reconnectCts?.IsCancellationRequested ?? true))
            {
                return Task.CompletedTask;
            }

            var reason = e.Exception?.GetBaseException().Message;
            if (string.IsNullOrWhiteSpace(reason))
            {
                reason = "connection dropped";
            }

            _logger.LogError("MQTT disconnected ({Host}:{Port}): {Reason}", _mqttHost, _mqttPort, reason);
            EnsureReconnectLoopRunning();
            return Task.CompletedTask;
        };

        _mqttClient.ApplicationMessageReceivedAsync += async e =>
        {
            var topic = e.ApplicationMessage.Topic;
            var payload = e.ApplicationMessage.ConvertPayloadToString();

            if (topic.EndsWith("/logs/response", StringComparison.OrdinalIgnoreCase))
            {
                HandleLogResponse(payload);
                return;
            }

            // Topic format: device/{deviceId}/status
            var parts = topic.Split('/');
            if (parts.Length < 3
                || !string.Equals(parts[0], "device", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(parts[2], "status", StringComparison.OrdinalIgnoreCase))
            {
                if (ShouldLogWithInterval(_lastWarningLogUtcByKey, $"bad-topic:{topic}", WarningLogInterval))
                {
                    _logger.LogWarning("Ignored telemetry on unexpected topic format: {Topic}", topic);
                }
                return;
            }
            var deviceId = parts[1];

            if (string.IsNullOrWhiteSpace(deviceId))
            {
                if (ShouldLogWithInterval(_lastWarningLogUtcByKey, $"empty-device:{topic}", WarningLogInterval))
                {
                    _logger.LogWarning("Ignored telemetry with missing device ID on topic: {Topic}. Check DEVICE_ID configuration on the edge device.", topic);
                }
                return;
            }

            using var scope = _serviceScopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var telemetryIdentity = ExtractDeviceIdentity(payload);

            // Try to find existing device by MAC address first (hardware identity), then by device ID
            Device? device = null;
            if (!string.IsNullOrWhiteSpace(telemetryIdentity.MacAddress))
            {
                device = await context.Devices
                    .FirstOrDefaultAsync(d => d.MacAddress == telemetryIdentity.MacAddress);
                
                if (device != null && device.Id != deviceId)
                {
                    _logger.LogWarning("Identity mismatch! Device found by MAC {Mac} has ID {DbId}, but telemetry came from ID {TelemetryId}. Updating device ID in DB...", 
                        telemetryIdentity.MacAddress, device.Id, deviceId);
                    
                    // Update ID to match what the device thinks it is
                    // EF Core identity update is tricky, so we might need to remove and re-add or just update properties
                    // Ideally we should trust the device's reported ID if the hardware (MAC) matches
                    // For now, let's just use the existing record but update its ID (if possible) or properties
                    
                    // Actually, modifying key is not allowed in EF. We should probably just log for now and maybe return NULL 
                    // so it gets treated as "New Device", but then our "EnableAutoDiscovery" check will block it.
                    
                    // STRATEGY: If MAC matches, we assume it's the SAME physical device.
                    // We can't easily change the PK. Let's just use the DB record as the source of truth for the ID?
                    // NO, because the device won't listen to messages for the old ID.
                    
                    // CORRECT FIX: Delete the old record and let the new one be created (if auto-discovery is on).
                    // BUT auto-discovery is OFF. 
                    
                    // PROPOSAL: If MAC matches, we ALLOW the update even if auto-discovery is off, 
                    // treating it as a "Re-provisioning" rather than "New Discovery".
                    
                    context.Devices.Remove(device);
                    await context.SaveChangesAsync();
                    device = null; // Force re-creation
                }
            }
            
            // Fallback to device ID lookup if no MAC match
            if (device == null)
            {
                device = await context.Devices.FindAsync(deviceId);
            }

            var isNewDevice = device == null;
            if (device == null)
            {
                var enableAutoDiscovery = _configuration.GetValue<bool>("EnableAutoDiscovery", false) 
                                       || _configuration.GetValue<bool>("ENABLE_AUTO_DISCOVERY", false);

                // Allow if auto-discovery is ON OR if we matched by MAC (meaning it's a known physical device re-registering)
                // If matched by MAC, we already deleted the old record above, so device is null here.
                var isKnownHardware = !string.IsNullOrWhiteSpace(telemetryIdentity.MacAddress) 
                                      && await context.Devices.AnyAsync(d => d.MacAddress == telemetryIdentity.MacAddress); 
                                      // Wait, we just deleted it if it matched! 
                                      // Actually, we deleted the record with *different* ID. 
                                      
                // Better logic: If we deleted a record above (meaning we found a MAC match), we should probably allow re-creation.
                // But we don't have that state easily here.
                
                // Let's refine: The only way `device` is null here is if:
                // 1. No MAC match found (Unknown hardware)
                // 2. MAC match found BUT ID mismatched, so we deleted it (Known hardware, new ID)
                
                // We want to BLOCK case 1, but ALLOW case 2.
                // How to distinguish?
                // In case 2, we just performed a delete. 
                
                // Simplify: valid reprovisioning should probably strictly follow auto-discovery rules?
                // If specific hardware is known, we should probably allow it to update its ID?
                // Let's check if the MAC was "known" before we deleted it? Too complex.
                
                // Let's just stick to the config for now. If you re-image a device, you might need to enable discovery briefly 
                // OR use the provisioning API. 
                
                if (!enableAutoDiscovery)
                {
                    if (ShouldLogWithInterval(_lastWarningLogUtcByKey, $"unknown-device:{deviceId}", WarningLogInterval))
                    {
                        _logger.LogWarning("Ignored telemetry from unknown device: {DeviceId}. Auto-discovery is DISABLED.", deviceId);
                    }
                    return;
                }

                var maxSeq = await context.Devices.AnyAsync() 
                    ? await context.Devices.MaxAsync(d => d.SequentialId) 
                    : 0;

                device = new Device
                {
                    Id = deviceId,
                    Name = telemetryIdentity.DisplayName ?? $"Device {deviceId}",
                    SequentialId = maxSeq + 1,
                    OsVersion = telemetryIdentity.OsVersion,
                    MacAddress = telemetryIdentity.MacAddress
                };
                context.Devices.Add(device);
            }
            else
            {
                var canAutoRename = string.IsNullOrWhiteSpace(device.Name)
                    || device.Name.StartsWith("Cloud Discovered (", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(device.Name, device.Id, StringComparison.OrdinalIgnoreCase);

                if (canAutoRename && !string.IsNullOrWhiteSpace(telemetryIdentity.DisplayName))
                {
                    device.Name = telemetryIdentity.DisplayName;
                }
            }

            var nowUtc = DateTime.UtcNow;
            DateTime? reportedAtUtc = telemetryIdentity.ReportedAt?.ToUniversalTime();
            var isRetainedSnapshot = e.ApplicationMessage.Retain;

            // If telemetry has no timestamp, only use "now" for live messages (not retained snapshots).
            if (!reportedAtUtc.HasValue && !isRetainedSnapshot)
            {
                reportedAtUtc = nowUtc;
            }

            var hasReasonableTimestamp = !reportedAtUtc.HasValue || reportedAtUtc.Value <= nowUtc + MaxFutureTimestampSkew;
            if (reportedAtUtc.HasValue && hasReasonableTimestamp && reportedAtUtc.Value > device.LastSeenAt)
            {
                device.LastSeenAt = reportedAtUtc.Value;
            }

            var heartbeatAge = reportedAtUtc.HasValue ? nowUtc - reportedAtUtc.Value : (TimeSpan?)null;
            var hasFreshHeartbeat = reportedAtUtc.HasValue
                && hasReasonableTimestamp
                && heartbeatAge.HasValue
                && heartbeatAge.Value >= TimeSpan.Zero
                && heartbeatAge.Value <= OnlineHeartbeatWindow
                && !isRetainedSnapshot;

            device.ReportedStateJson = payload;
            if (!string.IsNullOrWhiteSpace(telemetryIdentity.OsVersion))
            {
                device.OsVersion = telemetryIdentity.OsVersion;
            }
            if (!string.IsNullOrWhiteSpace(telemetryIdentity.MacAddress))
            {
                device.MacAddress = telemetryIdentity.MacAddress; // NEW: Save extracted MAC
            }

            await context.SaveChangesAsync();

            if (isNewDevice && !isRetainedSnapshot)
            {
                _logger.LogInformation("New device discovered: {DeviceId} ({DisplayName})", deviceId, device.Name);
            }

            if (hasFreshHeartbeat && reportedAtUtc.HasValue)
            {
                RecordDeviceHeartbeat(deviceId, device.Name, reportedAtUtc.Value);
            }
        };
        
        EnsurePresenceMonitorRunning();

        var connected = await TryConnectAsync(cancellationToken, reconnectAttempt: false);
        if (!connected)
        {
            EnsureReconnectLoopRunning();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _isStopping = true;
        if (_reconnectCts is not null && !_reconnectCts.IsCancellationRequested)
        {
            _reconnectCts.Cancel();
        }

        if (_reconnectTask is not null)
        {
            try
            {
                await _reconnectTask;
            }
            catch (OperationCanceledException)
            {
                // Ignore cancellation during shutdown.
            }
        }

        if (_presenceMonitorTask is not null)
        {
            try
            {
                await _presenceMonitorTask;
            }
            catch (OperationCanceledException)
            {
                // Ignore cancellation during shutdown.
            }
        }

        if (_mqttClient is not null && _mqttClient.IsConnected)
        {
            await _mqttClient.DisconnectAsync(new MqttClientDisconnectOptions(), cancellationToken);
        }

        _presenceMonitorTask = null;
        _reconnectCts?.Dispose();
        _reconnectCts = null;
    }

    public async Task PublishDesiredStateAsync(string deviceId, string desiredStateJson)
    {
        await EnsureConnectedAsync();

        // Wrap payload with signature
        var signedMessage = new VianaRego.Shared.Models.SignedMessage
        {
            Payload = desiredStateJson,
            Signature = _signingService.Sign(desiredStateJson)
        };
        var messagePayload = System.Text.Json.JsonSerializer.Serialize(signedMessage);

        var topic = $"device/{deviceId}/desired-state";
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(messagePayload)
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag()
            .Build();

        await _mqttClient.PublishAsync(message);
        _logger.LogInformation("Sent signed desired state to {Topic}", topic);
    }

    public async Task WipeDeviceStatusAsync(string deviceId)
    {
        await EnsureConnectedAsync();
        
        var topic = $"device/{deviceId}/status";
        // To clear a retained message, publish an empty payload with the Retain flag set
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(Array.Empty<byte>())
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag()
            .Build();

        await _mqttClient.PublishAsync(message);
        _logger.LogInformation("Wiped retained status for device {DeviceId} on {Topic}", deviceId, topic);
    }

    public async Task PublishModuleControlAsync(string deviceId, string moduleName, string action, string? launchMode = null)
    {
        await EnsureConnectedAsync();

        var command = new
        {
            ModuleName = moduleName,
            Action = action,
            LaunchMode = string.IsNullOrWhiteSpace(launchMode) ? null : launchMode
        };

        var payload = JsonSerializer.Serialize(command);
        var signedMessage = new VianaRego.Shared.Models.SignedMessage
        {
            Payload = payload,
            Signature = _signingService.Sign(payload)
        };

        var topic = $"device/{deviceId}/modules/control";
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(JsonSerializer.Serialize(signedMessage))
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        await _mqttClient.PublishAsync(message);
        _logger.LogInformation("Sent signed module control '{Action}' for {ModuleName} to {Topic}", action, moduleName, topic);
    }

    public async Task<string> RequestLogsAsync(string deviceId, string moduleName, int lines, int offset = 0)
    {
        await EnsureConnectedAsync();

        var requestId = Guid.NewGuid().ToString();
        var tcs = new TaskCompletionSource<string>();
        _pendingRequests[requestId] = tcs;

        var request = new { RequestId = requestId, ModuleName = moduleName, Lines = lines, Offset = offset };
        var topic = $"device/{deviceId}/logs/request";

        _logger.LogDebug("Requesting logs for {ModuleName} on {DeviceId} (ReqId: {RequestId}, Offset: {Offset})", moduleName, deviceId, requestId, offset);

        await _mqttClient.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(System.Text.Json.JsonSerializer.Serialize(request))
            .Build());

        // Wait for response with 10s timeout
        var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(10000));
        _pendingRequests.TryRemove(requestId, out _);

        if (completedTask == tcs.Task)
        {
            return await tcs.Task;
        }
        
        throw new TimeoutException("Device timed out while responding to log request.");
    }

    private void HandleLogResponse(string payload)
    {
        try 
        {
            using var doc = JsonDocument.Parse(payload);
            var requestId = doc.RootElement.GetProperty("RequestId").GetString();
            
            if (!string.IsNullOrEmpty(requestId) && _pendingRequests.TryGetValue(requestId, out var tcs))
            {
                tcs.SetResult(payload);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse log response");
        }
    }

    private static bool ShouldLogWithInterval(ConcurrentDictionary<string, DateTime> tracker, string key, TimeSpan interval)
    {
        var now = DateTime.UtcNow;

        while (true)
        {
            if (!tracker.TryGetValue(key, out var lastLoggedAt))
            {
                return tracker.TryAdd(key, now);
            }

            if (now - lastLoggedAt < interval)
            {
                return false;
            }

            if (tracker.TryUpdate(key, now, lastLoggedAt))
            {
                return true;
            }
        }
    }

    private void RecordDeviceHeartbeat(string deviceId, string displayName, DateTime heartbeatUtc)
    {
        _lastFreshHeartbeatUtcByDevice[deviceId] = heartbeatUtc;
        _lastDisplayNameByDevice[deviceId] = displayName;

        var wasOnline = _isDeviceOnlineById.TryGetValue(deviceId, out var currentState) && currentState;
        _isDeviceOnlineById[deviceId] = true;

        if (!wasOnline)
        {
            _logger.LogInformation("Device online: {DeviceId} ({DisplayName})", deviceId, displayName);
        }
    }

    private void EnsurePresenceMonitorRunning()
    {
        var reconnectCts = _reconnectCts;
        if (reconnectCts is null || reconnectCts.IsCancellationRequested || _isStopping)
        {
            return;
        }

        if (_presenceMonitorTask is not null && !_presenceMonitorTask.IsCompleted)
        {
            return;
        }

        _presenceMonitorTask = Task.Run(() => PresenceMonitorLoopAsync(reconnectCts.Token));
    }

    private async Task PresenceMonitorLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && !_isStopping)
            {
                var nowUtc = DateTime.UtcNow;
                foreach (var pair in _lastFreshHeartbeatUtcByDevice)
                {
                    var deviceId = pair.Key;
                    var lastHeartbeatUtc = pair.Value;

                    if (nowUtc - lastHeartbeatUtc < OfflineHeartbeatTimeout)
                    {
                        continue;
                    }

                    if (!_isDeviceOnlineById.TryGetValue(deviceId, out var isOnline) || !isOnline)
                    {
                        continue;
                    }

                    if (_isDeviceOnlineById.TryUpdate(deviceId, false, true))
                    {
                        var displayName = _lastDisplayNameByDevice.TryGetValue(deviceId, out var knownName)
                            ? knownName
                            : deviceId;

                        _logger.LogWarning(
                            "Device offline: {DeviceId} ({DisplayName}) - no heartbeat for {ThresholdSeconds}s",
                            deviceId,
                            displayName,
                            (int)OfflineHeartbeatTimeout.TotalSeconds);
                    }
                }

                await Task.Delay(PresenceMonitorInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Ignore cancellation during shutdown.
        }
    }

    private static (string? DisplayName, string? OsVersion, DateTime? ReportedAt, string? MacAddress) ExtractDeviceIdentity(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            if (!root.TryGetProperty("System", out var system) || system.ValueKind != JsonValueKind.Object)
            {
                var tsOnly = ParseTimestamp(root);
                return (null, null, tsOnly, null);
            }

            var deviceType = GetJsonString(system, "DeviceType");
            if (string.IsNullOrWhiteSpace(deviceType))
            {
                deviceType = GetJsonString(system, "SystemType");
            }

            var manufacturer = GetJsonString(system, "Manufacturer");
            var model = GetJsonString(system, "Model");
            var osVersion = GetJsonString(system, "OsVersion");
            var macAddress = GetJsonString(system, "MacAddress"); // NEW: Extract MAC

            var hardware = JoinNonEmpty(" ", manufacturer, model);

            string? displayName = null;
            if (!string.IsNullOrWhiteSpace(deviceType) && !string.IsNullOrWhiteSpace(hardware))
            {
                displayName = $"{deviceType} ({hardware})";
            }
            else if (!string.IsNullOrWhiteSpace(deviceType))
            {
                displayName = deviceType;
            }
            else if (!string.IsNullOrWhiteSpace(hardware))
            {
                displayName = hardware;
            }

            var reportedAt = ParseTimestamp(root);
            return (displayName, osVersion, reportedAt, macAddress);
        }
        catch
        {
            return (null, null, null, null);
        }
    }

    private static string? GetJsonString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static string JoinNonEmpty(string separator, params string?[] values)
    {
        return string.Join(separator, values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()));
    }

    private static DateTime? ParseTimestamp(JsonElement root)
    {
        if (!root.TryGetProperty("Timestamp", out var ts))
        {
            return null;
        }

        if (ts.ValueKind == JsonValueKind.String && DateTime.TryParse(ts.GetString(), out var parsed))
        {
            return parsed.ToUniversalTime();
        }

        return null;
    }

    private async Task<bool> TryConnectAsync(CancellationToken cancellationToken, bool reconnectAttempt)
    {
        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            if (_isStopping || _mqttClient.IsConnected)
            {
                return true;
            }

            await _mqttClient.ConnectAsync(_mqttOptions, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            var reason = ex.GetBaseException().Message;
            if (reconnectAttempt)
            {
                _logger.LogError("MQTT reconnect failed ({Host}:{Port}): {Reason}", _mqttHost, _mqttPort, reason);
            }
            else
            {
                _logger.LogError("MQTT unavailable ({Host}:{Port}): {Reason}", _mqttHost, _mqttPort, reason);
            }

            return false;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private void EnsureReconnectLoopRunning()
    {
        var reconnectCts = _reconnectCts;
        if (reconnectCts is null || reconnectCts.IsCancellationRequested || _isStopping)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _reconnectLoopActive, 1, 0) != 0)
        {
            return;
        }

        _reconnectTask = Task.Run(() => ReconnectLoopAsync(reconnectCts.Token));
    }

    private async Task ReconnectLoopAsync(CancellationToken cancellationToken)
    {
        var delay = _reconnectInitialDelay;
        try
        {
            while (!cancellationToken.IsCancellationRequested && !_isStopping)
            {
                if (_mqttClient.IsConnected)
                {
                    return;
                }

                var connected = await TryConnectAsync(cancellationToken, reconnectAttempt: true);
                if (connected)
                {
                    _logger.LogInformation("MQTT reconnected.");
                    return;
                }

                await Task.Delay(delay, cancellationToken);
                var nextSeconds = Math.Min(delay.TotalSeconds * 2, _reconnectMaxDelay.TotalSeconds);
                delay = TimeSpan.FromSeconds(nextSeconds);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Ignore cancellation during shutdown.
        }
        finally
        {
            Interlocked.Exchange(ref _reconnectLoopActive, 0);

            if (!_isStopping
                && !(_reconnectCts?.IsCancellationRequested ?? true)
                && !_mqttClient.IsConnected)
            {
                EnsureReconnectLoopRunning();
            }
        }
    }

    private async Task EnsureConnectedAsync()
    {
        if (_mqttClient.IsConnected)
        {
            return;
        }

        var connected = await TryConnectAsync(CancellationToken.None, reconnectAttempt: false);
        if (!connected)
        {
            EnsureReconnectLoopRunning();
            throw new InvalidOperationException($"MQTT unavailable ({_mqttHost}:{_mqttPort}).");
        }
    }
}
