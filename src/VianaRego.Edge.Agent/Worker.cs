using VianaRego.Edge.Agent.Services;

namespace VianaRego.Edge.Agent;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly MqttService _mqttService;
    private readonly ProcessOrchestrator _orchestrator;
    private readonly EdgeReconcilerService _reconcilerService;

    public Worker(
        ILogger<Worker> logger, 
        MqttService mqttService, 
        ProcessOrchestrator orchestrator,
        EdgeReconcilerService reconcilerService)
    {
        _logger = logger;
        _mqttService = mqttService;
        _orchestrator = orchestrator;
        _reconcilerService = reconcilerService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield immediately to allow the service host to signal "Started" to Windows
        await Task.Yield();

        _logger.LogInformation("Edge Agent Orchestrator started at: {time}", DateTimeOffset.Now);

        // 1. Launch Sidecars
        _orchestrator.LaunchSidecars();

        // 2. Start MQTT Connection
        await _mqttService.ConnectAsync(stoppingToken);

        // 3. Periodic Reconciliation Loop (Retry Logic)
        var lastReconcile = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            if ((DateTime.UtcNow - lastReconcile).TotalSeconds >= 30)
            {
                try 
                {
                    var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "desired_state.json");
                    if (File.Exists(path))
                    {
                        var json = await File.ReadAllTextAsync(path, stoppingToken);
                        if (!string.IsNullOrWhiteSpace(json))
                        {
                            // This ensures that if a step fails (like download), it will be retried automatically.
                            // The Reconciler is idempotent (checks existing state/version) so this is safe.
                            _logger.LogDebug("Periodic reconciliation check...");
                            await _reconcilerService.ApplyDesiredStateAsync(json);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in periodic reconciliation loop");
                }
                lastReconcile = DateTime.UtcNow;
            }
            
            await Task.Delay(1000, stoppingToken);
        }
    }
}

