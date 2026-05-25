using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VianaRego.Cloud.Api.Data;
using VianaRego.Cloud.Api.Entities;
using VianaRego.Cloud.Api.Services;
using System.Text.Json;
using VianaRego.Shared.Models;

namespace VianaRego.Cloud.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DevicesController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<DevicesController> _logger;
    private readonly MqttMessagingService _mqttService;

    public DevicesController(ApplicationDbContext context, ILogger<DevicesController> logger, MqttMessagingService mqttService)
    {
        _context = context;
        _logger = logger;
        _mqttService = mqttService;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<Device>>> GetDevices()
    {
        return await _context.Devices.ToListAsync();
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Device>> GetDevice(string id)
    {
        var device = await _context.Devices.FindAsync(id);

        if (device == null)
        {
            return NotFound();
        }

        return device;
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteDevice(string id)
    {
        var device = await _context.Devices.FindAsync(id);
        if (device == null)
        {
            return NotFound();
        }

        try
        {
            // Wipe MQTT retained status before clearing from DB
            await _mqttService.WipeDeviceStatusAsync(id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to wipe MQTT status for device {DeviceId}. Proceeding with deletion from DB.", id);
        }

        _context.Devices.Remove(device);
        await _context.SaveChangesAsync();
        _logger.LogInformation("Removed device {DeviceId} from cloud registry and wiped MQTT retain", id);

        return NoContent();
    }
 
    [HttpPost("register")]
    public async Task<ActionResult<Device>> RegisterDevice([FromBody] DeviceRegistration registration)
    {
        Device? existingDevice = null;

        // 1. Try Lookup by MAC Address (Primary Hardware Identity)
        if (!string.IsNullOrEmpty(registration.MacAddress))
        {
            existingDevice = await _context.Devices
                .FirstOrDefaultAsync(d => d.MacAddress == registration.MacAddress);
        }

        // 2. Fallback to GUID Id (Legacy/Manual Identity)
        if (existingDevice == null && !string.IsNullOrEmpty(registration.Id))
        {
            existingDevice = await _context.Devices.FindAsync(registration.Id);
        }

        if (existingDevice != null)
        {
            // Update metadata and last seen
            existingDevice.LastSeenAt = DateTime.UtcNow;
            existingDevice.IpAddress = registration.IpAddress;
            existingDevice.OsVersion = registration.OsVersion;
            
            if (!string.IsNullOrEmpty(registration.Name) && registration.Name != "Unknown")
                existingDevice.Name = registration.Name;
                
            if (!string.IsNullOrEmpty(registration.Company))
                existingDevice.Company = registration.Company;

            if (!string.IsNullOrEmpty(registration.Site))
                existingDevice.Site = registration.Site;

            await _context.SaveChangesAsync();
            _logger.LogInformation("Updated existing device: {Name} (Sequential: {Seq}, MAC: {MAC})", 
                existingDevice.Name, existingDevice.SequentialId, existingDevice.MacAddress);
            return existingDevice;
        }

        // 3. Create New Device
        var maxSeq = await _context.Devices.AnyAsync() 
            ? await _context.Devices.MaxAsync(d => d.SequentialId) 
            : 0;

        var device = new Device
        {
            Id = registration.Id ?? Guid.NewGuid().ToString(),
            Name = registration.Name ?? "New Device",
            SequentialId = maxSeq + 1,
            MacAddress = registration.MacAddress,
            Company = registration.Company,
            Site = registration.Site,
            IpAddress = registration.IpAddress,
            OsVersion = registration.OsVersion,
            LastSeenAt = DateTime.UtcNow
        };

        _context.Devices.Add(device);
        await _context.SaveChangesAsync();
        
        _logger.LogInformation("Registered NEW device: {Name} (Sequential: {Seq}, MAC: {MAC})", 
            device.Name, device.SequentialId, device.MacAddress);

        return CreatedAtAction(nameof(GetDevice), new { id = device.Id }, device);
    }

    [HttpPut("{id}/desired-state")]
    public async Task<IActionResult> SetDesiredState(string id, [FromBody] string desiredStateJson)
    {
        var device = await _context.Devices.FindAsync(id);
        if (device == null) return NotFound();

        device.DesiredStateJson = desiredStateJson;
        await _context.SaveChangesAsync();

        // Publish to device
        await _mqttService.PublishDesiredStateAsync(id, desiredStateJson);

        return NoContent();
    }

    [HttpPost("{id}/modules")]
    public async Task<IActionResult> AddOrUpdateModule(string id, [FromBody] ModuleDefinition module)
    {
        var device = await _context.Devices.FindAsync(id);
        if (device == null) return NotFound();

        var state = !string.IsNullOrEmpty(device.DesiredStateJson) 
            ? System.Text.Json.JsonSerializer.Deserialize<DesiredState>(device.DesiredStateJson) 
            : new DesiredState { Modules = new List<ModuleDefinition>() };

        if (state == null) state = new DesiredState { Modules = new List<ModuleDefinition>() };

        var existing = state.Modules.FirstOrDefault(m => m.Name == module.Name);
        if (existing != null) state.Modules.Remove(existing);
        
        state.Modules.Add(module);
        state.Version++;

        var json = System.Text.Json.JsonSerializer.Serialize(state);
        device.DesiredStateJson = json;
        await _context.SaveChangesAsync();

        await _mqttService.PublishDesiredStateAsync(id, json);
        return Ok(state);
    }

    [HttpDelete("{id}/modules/{moduleName}")]
    public async Task<IActionResult> RemoveModule(string id, string moduleName)
    {
        var device = await _context.Devices.FindAsync(id);
        if (device == null) return NotFound();
        if (string.IsNullOrEmpty(device.DesiredStateJson)) return NoContent();

        var state = System.Text.Json.JsonSerializer.Deserialize<DesiredState>(device.DesiredStateJson);
        if (state == null) return NoContent();

        var module = state.Modules.FirstOrDefault(m => m.Name == moduleName);
        if (module != null)
        {
            state.Modules.Remove(module);
            state.Version++;
            var json = System.Text.Json.JsonSerializer.Serialize(state);
            device.DesiredStateJson = json;
            await _context.SaveChangesAsync();
            await _mqttService.PublishDesiredStateAsync(id, json);
        }

        return NoContent();
    }

    [HttpDelete("{id}/modules")]
    public async Task<IActionResult> ClearAllModules(string id)
    {
        var device = await _context.Devices.FindAsync(id);
        if (device == null) return NotFound();

        var state = new DesiredState { Version = DateTime.UtcNow.Ticks, Modules = new List<ModuleDefinition>() };
        var json = System.Text.Json.JsonSerializer.Serialize(state);
        
        device.DesiredStateJson = json;
        await _context.SaveChangesAsync();
        await _mqttService.PublishDesiredStateAsync(id, json);

        return NoContent();
    }

    [HttpPost("{id}/modules/{moduleName}/start")]
    public async Task<IActionResult> StartModule(string id, string moduleName, [FromQuery] string? launchMode = null)
    {
        var device = await _context.Devices.FindAsync(id);
        if (device == null) return NotFound();
        if (string.IsNullOrWhiteSpace(device.DesiredStateJson))
        {
            return NotFound(new { error = $"Module '{moduleName}' is not configured in desired state for device '{id}'." });
        }

        var state = JsonSerializer.Deserialize<DesiredState>(device.DesiredStateJson);
        if (state == null)
        {
            return BadRequest(new { error = "Desired state is invalid JSON." });
        }

        var module = state.Modules.FirstOrDefault(m => string.Equals(m.Name, moduleName, StringComparison.OrdinalIgnoreCase));
        if (module == null)
        {
            return NotFound(new { error = $"Module '{moduleName}' is not configured in desired state for device '{id}'." });
        }

        var normalizedLaunchMode = NormalizeLaunchMode(launchMode);
        module.DesiredStatus = "Running";
        module.LaunchMode = normalizedLaunchMode;
        device.DesiredStateJson = JsonSerializer.Serialize(state);
        await _context.SaveChangesAsync();

        await _mqttService.PublishDesiredStateAsync(id, device.DesiredStateJson);
        await _mqttService.PublishModuleControlAsync(id, module.Name, "start", normalizedLaunchMode);
        return Accepted(new { message = $"Start requested and persisted for module '{module.Name}' on device '{id}' (launch mode: {normalizedLaunchMode})." });
    }

    [HttpPost("{id}/modules/{moduleName}/stop")]
    public async Task<IActionResult> StopModule(string id, string moduleName)
    {
        var device = await _context.Devices.FindAsync(id);
        if (device == null) return NotFound();
        if (string.IsNullOrWhiteSpace(device.DesiredStateJson))
        {
            return NotFound(new { error = $"Module '{moduleName}' is not configured in desired state for device '{id}'." });
        }

        var state = JsonSerializer.Deserialize<DesiredState>(device.DesiredStateJson);
        if (state == null)
        {
            return BadRequest(new { error = "Desired state is invalid JSON." });
        }

        var module = state.Modules.FirstOrDefault(m => string.Equals(m.Name, moduleName, StringComparison.OrdinalIgnoreCase));
        if (module == null)
        {
            return NotFound(new { error = $"Module '{moduleName}' is not configured in desired state for device '{id}'." });
        }

        module.DesiredStatus = "Stopped";
        device.DesiredStateJson = JsonSerializer.Serialize(state);
        await _context.SaveChangesAsync();

        await _mqttService.PublishDesiredStateAsync(id, device.DesiredStateJson);
        await _mqttService.PublishModuleControlAsync(id, module.Name, "stop");
        return Accepted(new { message = $"Stop requested and persisted for module '{module.Name}' on device '{id}'." });
    }

    [HttpGet("{id}/logs/{module}")]
    public async Task<IActionResult> GetLogs(string id, string module, [FromQuery] int lines = 100, [FromQuery] int offset = 0)
    {
        try 
        {
            var logsJson = await _mqttService.RequestLogsAsync(id, module, lines, offset);
            return Ok(logsJson);
        }
        catch (TimeoutException)
        {
            return StatusCode(504, new { error = "Device timed out while responding to log request." });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
    [HttpPost("{id}/modules/{moduleName}/kill")]
    public async Task<IActionResult> KillModule(string id, string moduleName)
    {
        var device = await _context.Devices.FindAsync(id);
        if (device == null) return NotFound();

        // One-time action: Publish "kill" control but do NOT update DesiredStateJson
        await _mqttService.PublishModuleControlAsync(id, moduleName, "kill");
        
        _logger.LogInformation("Ad-hoc kill requested for process '{ModuleName}' on device '{Id}' (Persistent state unchanged)", moduleName, id);
        return Accepted(new { message = $"One-time kill requested for process '{moduleName}' on device '{id}'. Persistent state was NOT modified." });
    }

    [HttpPost("{id}/modules/{moduleName}/revive")]
    public async Task<IActionResult> ReviveModule(string id, string moduleName)
    {
        var device = await _context.Devices.FindAsync(id);
        if (device == null) return NotFound();

        // 1. Update Persistent State to "Manual"
        // This ensures the Watchdog won't kill it (since it's not "Stopped") 
        // and won't restart it (since it's not "Running")
        if (!string.IsNullOrWhiteSpace(device.DesiredStateJson))
        {
            var state = JsonSerializer.Deserialize<DesiredState>(device.DesiredStateJson);
            if (state != null)
            {
                var module = state.Modules.FirstOrDefault(m => string.Equals(m.Name, moduleName, StringComparison.OrdinalIgnoreCase));
                if (module != null)
                {
                    module.DesiredStatus = "Manual";
                    device.DesiredStateJson = JsonSerializer.Serialize(state);
                    await _context.SaveChangesAsync();
                    
                    // Publish updated state so Reconciler knows it's Manual
                    await _mqttService.PublishDesiredStateAsync(id, device.DesiredStateJson);
                }
            }
        }

        // 2. Publish "revive" control action
        await _mqttService.PublishModuleControlAsync(id, moduleName, "revive");
        
        _logger.LogInformation("Revive requested for module '{ModuleName}' on device '{Id}' (Status set to Manual)", moduleName, id);
        return Accepted(new { message = $"Revive requested for module '{moduleName}'. Persistent status set to 'Manual'." });
    }

    private static string NormalizeLaunchMode(string? launchMode)
    {
        return string.Equals(launchMode, "active", StringComparison.OrdinalIgnoreCase) ? "active" : "service";
    }
}
