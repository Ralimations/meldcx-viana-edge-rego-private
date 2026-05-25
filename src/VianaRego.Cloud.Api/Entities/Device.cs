using System.ComponentModel.DataAnnotations;

namespace VianaRego.Cloud.Api.Entities;

public class Device
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string Name { get; set; } = string.Empty;
    
    public int SequentialId { get; set; }

    public string? MacAddress { get; set; }

    public string? Company { get; set; }

    public string? Site { get; set; }

    public string? Description { get; set; }

    public DateTime LastSeenAt { get; set; }

    public string? IpAddress { get; set; }

    public string? OsVersion { get; set; }

    // JSON blob representing the current installed modules reported by the device
    public string? ReportedStateJson { get; set; }

    // JSON blob representing what *should* be installed
    public string? DesiredStateJson { get; set; }
}
