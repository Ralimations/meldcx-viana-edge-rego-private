using System.ComponentModel.DataAnnotations;

namespace VianaRego.Shared.Models;

public class TelemetryMessage
{
    [Key]
    public int Id { get; set; }
    public string Topic { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
