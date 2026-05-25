using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using System.Text.Json;
using VianaRego.Shared.Models;

namespace VianaRego.Edge.Agent.Services;

public class MqttService
{
    private readonly ILogger<MqttService> _logger;
    private readonly IConfiguration _configuration;
    private readonly EdgeReconcilerService _reconcilerService;
    private readonly LogService _logService;
    private readonly SignatureVerifier _signatureVerifier;
    private readonly object _logThrottleLock = new();
    private DateTime _lastDisconnectLogUtc = DateTime.MinValue;
    private DateTime _lastReconnectFailureLogUtc = DateTime.MinValue;
    private static readonly TimeSpan MqttRepeatLogInterval = TimeSpan.FromSeconds(30);

    private IMqttClient _mqttClient = null!;
    private MqttClientOptions _mqttOptions = null!;

    public MqttService(
        ILogger<MqttService> logger,
        IConfiguration configuration,
        EdgeReconcilerService reconcilerService,
        LogService logService,
        SignatureVerifier signatureVerifier)
    {
        _logger = logger;
        _configuration = configuration;
        _reconcilerService = reconcilerService;
        _logService = logService;
        _signatureVerifier = signatureVerifier;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var mqttFactory = new MqttFactory();
        _mqttClient = mqttFactory.CreateMqttClient();

        var mqttHost = _configuration["MQTT_HOST"];

        var rawPort = _configuration["MQTT_PORT"];
        var mqttPort = 8883;
        if (!string.IsNullOrWhiteSpace(rawPort) && int.TryParse(rawPort, out var parsedPort))
        {
            mqttPort = parsedPort;
        }

        var mqttUser = _configuration["MQTT_USER"];
        var mqttPassword = _configuration["MQTT_PASSWORD"];
        var useTls = bool.Parse(_configuration["MQTT_USE_TLS"] ?? "true");

        var clientId = _configuration["DEVICE_ID"];
        if (string.IsNullOrWhiteSpace(clientId))
        {
            clientId = _configuration["DEVICE_TOKEN"];
        }

        if (string.IsNullOrWhiteSpace(clientId))
        {
            clientId = $"edge-device-{Environment.MachineName}";
        }

        clientId = clientId?.Trim() ?? $"edge-device-{Environment.MachineName}";

        _logger.LogInformation("Starting MQTT with ClientId: {ClientId} on {Host}:{Port}", clientId, mqttHost, mqttPort);

        if (string.IsNullOrWhiteSpace(mqttHost))
        {
            _logger.LogError("MQTT host is missing. Edge Agent cannot connect.");
            return;
        }

        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(mqttHost, mqttPort)
            .WithCredentials(mqttUser, mqttPassword)
            .WithClientId(clientId)
            .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V311)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(60))
            .WithCleanSession(false);

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
            _logger.LogInformation("Connected to MQTT broker as {ClientId}", clientId);

            try
            {
                var desiredStateTopic = $"device/{clientId}/desired-state";
                await _mqttClient.SubscribeAsync(desiredStateTopic);
                _logger.LogDebug("Subscribed to {Topic}", desiredStateTopic);

                var logTopic = $"device/{clientId}/logs/request";
                await _mqttClient.SubscribeAsync(logTopic);
                _logger.LogDebug("Subscribed to {Topic}", logTopic);

                var moduleControlTopic = $"device/{clientId}/modules/control";
                await _mqttClient.SubscribeAsync(moduleControlTopic);
                _logger.LogDebug("Subscribed to {Topic}", moduleControlTopic);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to subscribe.");
            }
        };

        _mqttClient.ApplicationMessageReceivedAsync += async e =>
        {
            try
            {
                if (e.ApplicationMessage is null)
                {
                    return;
                }

                var topic = e.ApplicationMessage.Topic;
                var payload = e.ApplicationMessage.ConvertPayloadToString();

                _logger.LogDebug("Received message on {Topic}", topic);

                if (topic.EndsWith("/desired-state", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("Received desired state payload.");
                    if (!string.IsNullOrEmpty(payload))
                    {
                        if (!TryGetVerifiedPayload(payload, out var actualPayload))
                        {
                            return;
                        }

                        _logger.LogDebug("Applying desired state via reconciler.");

                        try
                        {
                            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "desired_state.json");
                            await File.WriteAllTextAsync(path, actualPayload);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to persist desired_state.json");
                        }

                        await _reconcilerService.ApplyDesiredStateAsync(actualPayload);
                        _logger.LogDebug("Reconciler call finished.");
                    }
                }
                else if (topic.EndsWith("/logs/request", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleLogRequestAsync(clientId, payload);
                }
                else if (topic.EndsWith("/modules/control", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleModuleControlAsync(payload);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing incoming MQTT message");
            }
        };

        _mqttClient.DisconnectedAsync += async e =>
        {
            if (ShouldLogWithInterval(ref _lastDisconnectLogUtc, MqttRepeatLogInterval))
            {
                _logger.LogWarning("MQTT disconnected. Reason: {Reason}", e.Reason);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                await _mqttClient.ConnectAsync(_mqttOptions, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Ignore cancellation on shutdown.
            }
            catch (Exception ex)
            {
                if (ShouldLogWithInterval(ref _lastReconnectFailureLogUtc, MqttRepeatLogInterval))
                {
                    var reason = ex.GetBaseException().Message;
                    _logger.LogWarning("MQTT reconnect failed ({Host}:{Port}): {Reason}", mqttHost, mqttPort, reason);
                }
            }
        };

        try
        {
            var result = await _mqttClient.ConnectAsync(_mqttOptions, cancellationToken);
            _logger.LogDebug("MQTT connection result: {ResultCode} ({ReasonString})", result.ResultCode, result.ReasonString);
        }
        catch (Exception ex)
        {
            var reason = ex.GetBaseException().Message;
            _logger.LogWarning("Initial MQTT connection failed ({Host}:{Port}): {Reason}", mqttHost, mqttPort, reason);
        }
    }

    private async Task HandleLogRequestAsync(string clientId, string payload)
    {
        try
        {
            var request = JsonSerializer.Deserialize<LogRequest>(payload);
            if (request is null)
            {
                return;
            }

            _logger.LogDebug(
                "Log request {Id} for module {Module}, line count {Lines}, offset {Offset}",
                request.RequestId,
                request.ModuleName,
                request.Lines,
                request.Offset);

            var (lines, totalLines) = _logService.GetLogLines(
                request.ModuleName,
                request.Lines > 0 ? request.Lines : 100,
                request.Offset);

            var response = new
            {
                RequestId = request.RequestId,
                ModuleName = request.ModuleName,
                Lines = lines,
                TotalLines = totalLines,
                HasMore = (request.Offset + lines.Count) < totalLines,
                Timestamp = DateTime.UtcNow
            };

            var responseTopic = $"device/{clientId}/logs/response";
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(responseTopic)
                .WithPayload(JsonSerializer.Serialize(response))
                .Build();

            await _mqttClient.PublishAsync(message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling log request");
        }
    }

    private async Task HandleModuleControlAsync(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return;
        }

        try
        {
            if (!TryGetVerifiedPayload(payload, out var actualPayload))
            {
                return;
            }

            var request = JsonSerializer.Deserialize<ModuleControlRequest>(actualPayload);
            if (request is null || string.IsNullOrWhiteSpace(request.ModuleName) || string.IsNullOrWhiteSpace(request.Action))
            {
                _logger.LogWarning("Invalid module control request payload.");
                return;
            }

            var action = request.Action.Trim().ToLowerInvariant();
            var updated = action switch
            {
                "start" => await _reconcilerService.StartModuleAsync(request.ModuleName, request.LaunchMode),
                "stop" => await _reconcilerService.StopModuleAsync(request.ModuleName),
                "kill" => await _reconcilerService.KillModuleAdHocAsync(request.ModuleName),
                "revive" => await _reconcilerService.ReviveModuleAdHocAsync(request.ModuleName),
                _ => false
            };

            if (!updated)
            {
                if (action != "start" && action != "stop")
                {
                    _logger.LogWarning("Unsupported module control action '{Action}' for module {ModuleName}", request.Action, request.ModuleName);
                    return;
                }

                _logger.LogWarning("Module control request ignored. Module '{ModuleName}' not found.", request.ModuleName);
                return;
            }

            _logger.LogInformation("Applied module control action '{Action}' for {ModuleName}", action, request.ModuleName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling module control request");
        }
    }

    private bool TryGetVerifiedPayload(string payload, out string actualPayload)
    {
        actualPayload = payload;
        SignedMessage? signedMessage = null;

        try
        {
            signedMessage = JsonSerializer.Deserialize<SignedMessage>(payload);
            if (signedMessage != null && !string.IsNullOrEmpty(signedMessage.Payload))
            {
                actualPayload = signedMessage.Payload;
            }
        }
        catch
        {
            _logger.LogDebug("Message is not in signed format.");
        }

        if (!_signatureVerifier.Verify(actualPayload, signedMessage?.Signature))
        {
            _logger.LogError("Rejected message: signature verification failed.");
            return false;
        }

        _logger.LogDebug("Signature verified successfully.");
        return true;
    }

    private bool ShouldLogWithInterval(ref DateTime lastLoggedUtc, TimeSpan interval)
    {
        var nowUtc = DateTime.UtcNow;
        lock (_logThrottleLock)
        {
            if (nowUtc - lastLoggedUtc < interval)
            {
                return false;
            }

            lastLoggedUtc = nowUtc;
            return true;
        }
    }

    public class LogRequest
    {
        public string RequestId { get; set; } = "";
        public string ModuleName { get; set; } = "";
        public int Lines { get; set; } = 100;
        public int Offset { get; set; } = 0;
    }

    public class ModuleControlRequest
    {
        public string ModuleName { get; set; } = "";
        public string Action { get; set; } = "";
        public string? LaunchMode { get; set; }
    }
}
