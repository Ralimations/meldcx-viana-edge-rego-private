using System.ComponentModel.DataAnnotations;

namespace VianaRego.Shared.Models;

public class LogEntry
{
    [Key]
    public int Id { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Component { get; set; } = string.Empty; // 'Reconciler', 'Watchdog', 'Telemetry', etc.
    public string Level { get; set; } = "INFO"; // 'INFO', 'WARN', 'ERROR'
    public string Message { get; set; } = string.Empty;
    public string? Details { get; set; } // JSON for structured data (optional)
    public string Emoji { get; set; } = string.Empty;
}

