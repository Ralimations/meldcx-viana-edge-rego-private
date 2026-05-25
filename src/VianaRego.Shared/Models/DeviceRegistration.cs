using System;

namespace VianaRego.Shared.Models;

public class DeviceRegistration
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? MacAddress { get; set; }
    public string? Company { get; set; }
    public string? Site { get; set; }
    public string? IpAddress { get; set; }
    public string? OsVersion { get; set; }
}
