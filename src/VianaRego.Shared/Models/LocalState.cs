using System.ComponentModel.DataAnnotations;

namespace VianaRego.Shared.Models;

public class LocalModuleState
{
    [Key]
    public string ModuleName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Status { get; set; } = "Stopped"; // Running, Stopped, Downloading, Error
    public int ProcessId { get; set; }
    public string StartCommand { get; set; } = string.Empty;
    public string LaunchMode { get; set; } = "service"; // service, active
    // True when module content is managed by Edge (downloaded package we own).
    // False for adopted/local processes we should never uninstall.
    public bool IsManaged { get; set; } = false;
    public long DownloadProgress { get; set; }
    public long TotalSize { get; set; }
    public DateTime LastUpdatedAt { get; set; }
}

public class PeripheralInfo
{
    [Key]
    public string DeviceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty; // USB, Network, Serial
    public string Status { get; set; } = "Online";
}

public class DeviceSystemStatus
{
    [Key]
    public string Id { get; set; } = "system";
    public string SystemType { get; set; } = "Unknown"; // Laptop, MiniPC, etc.
    public string DeviceType { get; set; } = ""; // NEW: "Windows Laptop", "Windows Desktop", etc.
    public string MacAddress { get; set; } = ""; // NEW: Primary Hardware Identity
    public string Manufacturer { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string OsVersion { get; set; } = string.Empty;
    public string CPUUsage { get; set; } = "0%";
    public string RAMUsage { get; set; } = "0%";
    public DateTime? LastHardwareScanAt { get; set; } // NEW: Track when hardware was last scanned
    public DateTime LastReportedAt { get; set; }
}
