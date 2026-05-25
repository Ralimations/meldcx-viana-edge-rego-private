using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using VianaRego.Shared.Data;
using VianaRego.Shared.Models;
using VianaRego.Shared.Logging;
using VianaRego.Shared.Configuration;

namespace VianaRego.Edge.Agent.Services;

/// <summary>
/// One-time hardware collection service that runs on Agent startup.
/// Collects static hardware information (Manufacturer, Model, Chassis Type, Peripherals)
/// and caches it in the database. Only re-scans when explicitly triggered or after 7 days.
/// </summary>
[SupportedOSPlatform("windows")]
public class HardwareCollectorService : IHostedService
{
    private readonly SharedLogger _logger;
    private readonly DbContextOptions<LocalDbContext> _dbOptions;

    public HardwareCollectorService()
    {
        _logger = new SharedLogger("EdgeAgent.log", "HardwareCollector");
        
        var dbPath = DataPath.GetPath("viana-edge.db");
        _dbOptions = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInfo("Starting hardware collection...");

        try
        {
            using var db = new LocalDbContext(_dbOptions);
            db.EnsureCreatedAndPatched();

            // Check if already collected recently (within 7 days)
            var status = await db.SystemStatus.FindAsync("system");
            if (status?.LastHardwareScanAt != null &&
                !string.IsNullOrEmpty(status.MacAddress) && // NEW: Force scan if MAC is missing
                (DateTime.UtcNow - status.LastHardwareScanAt.Value).TotalDays < 7)
            {
                _logger.LogInfo($"Hardware already scanned {(DateTime.UtcNow - status.LastHardwareScanAt.Value).TotalDays:F1} days ago. Skipping.");
                return;
            }

            await CollectHardwareAsync(db);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Hardware collector startup failed: {ex.Message}");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public async Task CollectHardwareAsync(LocalDbContext db)
    {
        try
        {
            // Collect static hardware info
            var (manufacturer, model, chassisType, osVersion) = GetSystemInfo();
            var macAddress = DeviceIdentity.GetMacAddress(); // NEW: Collect MAC
            var deviceType = ClassifyDeviceType(chassisType);
            var peripherals = GetPeripherals();

            // Update or create system status
            var status = await db.SystemStatus.FindAsync("system");
            if (status == null)
            {
                status = new DeviceSystemStatus { Id = "system" };
                db.SystemStatus.Add(status);
            }

            status.Manufacturer = manufacturer;
            status.Model = model;
            status.SystemType = deviceType; // Legacy field
            status.DeviceType = deviceType; // New field with proper classification
            status.MacAddress = macAddress; // NEW: Save MAC
            status.OsVersion = osVersion;
            status.LastHardwareScanAt = DateTime.UtcNow;
            status.LastReportedAt = DateTime.UtcNow;

            // Update peripherals
            var existing = await db.Peripherals.ToListAsync();
            db.Peripherals.RemoveRange(existing);
            await db.SaveChangesAsync();

            foreach (var peripheral in peripherals)
            {
                db.Peripherals.Add(peripheral);
            }

            await db.SaveChangesAsync();

            _logger.LogSuccess($"Hardware collected: {deviceType} ({manufacturer} {model}), MAC: {macAddress}, {peripherals.Count} peripherals");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Hardware collection failed: {ex.Message}");
        }
    }

    private (string Manufacturer, string Model, int ChassisType, string OsVersion) GetSystemInfo()
    {
        string manufacturer = "Unknown";
        string model = "Unknown";
        int chassisType = 0;

        try
        {
            // Get manufacturer and model
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_ComputerSystem");
            foreach (ManagementObject obj in searcher.Get())
            {
                manufacturer = obj["Manufacturer"]?.ToString() ?? "Unknown";
                model = obj["Model"]?.ToString() ?? "Unknown";
            }

            // Get chassis type from Win32_SystemEnclosure
            using var chassisSearcher = new ManagementObjectSearcher("SELECT * FROM Win32_SystemEnclosure");
            foreach (ManagementObject obj in chassisSearcher.Get())
            {
                var chassisTypes = obj["ChassisTypes"] as ushort[];
                if (chassisTypes != null && chassisTypes.Length > 0)
                {
                    chassisType = chassisTypes[0];
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"WMI query failed: {ex.Message}");
        }

        var osVersion = Environment.OSVersion.ToString();
        return (manufacturer, model, chassisType, osVersion);
    }

    private string ClassifyDeviceType(int chassisType)
    {
        return chassisType switch
        {
            3 or 4 or 5 or 6 or 7 => "Windows Desktop",
            8 or 9 or 10 or 11 or 14 => "Windows Laptop",
            12 => "Windows Docking Station",
            13 => "Windows All-in-One",
            15 or 16 => "Windows Sealed Mini PC",
            17 or 23 => "Windows Rackmount Server",
            30 or 31 or 32 => "Windows Tablet",
            34 or 35 => "Windows Mini PC",
            _ => "Windows Device"
        };
    }

    private List<PeripheralInfo> GetPeripherals()
    {
        var list = new List<PeripheralInfo>();
        
        try
        {
            // Get USB devices, Ports, and Imaging devices
            string query = "SELECT * FROM Win32_PnPEntity WHERE PNPClass='Image' OR PNPClass='USB' OR PNPClass='Ports'";
            using var searcher = new ManagementObjectSearcher(query);

            foreach (ManagementObject obj in searcher.Get())
            {
                string name = obj["Name"]?.ToString() ?? "Unknown";
                string deviceId = obj["DeviceID"]?.ToString() ?? Guid.NewGuid().ToString();
                string pnpClass = obj["PNPClass"]?.ToString() ?? "Device";
                string status = obj["Status"]?.ToString() ?? "Unknown";

                list.Add(new PeripheralInfo
                {
                    DeviceId = deviceId,
                    Name = name,
                    Type = pnpClass,
                    Status = status
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Peripheral scan failed: {ex.Message}");
        }

        return list;
    }
}


