namespace VianaRego.Shared.Models;

public class ModuleVersionHistory
{
    public int Id { get; set; }
    public string ModuleName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public string StartCommand { get; set; } = string.Empty;
    public DateTime InstalledAt { get; set; }
    public bool IsRollback { get; set; } // true if this version was installed via rollback
}
