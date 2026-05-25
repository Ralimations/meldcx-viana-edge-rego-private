namespace VianaRego.Shared.Models;

public class DesiredState
{
    public long Version { get; set; }
    public List<ModuleDefinition> Modules { get; set; } = new();
}

public class ModuleDefinition
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public string StartCommand { get; set; } = string.Empty;
    public string LaunchMode { get; set; } = "service"; // "service" or "active"
    public string DesiredStatus { get; set; } = "Running"; // "Running" or "Stopped"
    public bool IsManaged { get; set; } = true; // true = remote (rollback enabled), false = adopted (monitoring only)
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();
}

