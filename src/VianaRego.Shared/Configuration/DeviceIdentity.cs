using System.Security.Cryptography;
using System.Text;
using System.Net.NetworkInformation;
using System.Linq;
using Microsoft.Win32;

namespace VianaRego.Shared.Configuration;

public static class DeviceIdentity
{
    // Resolve a stable per-machine ID.
    // Precedence: explicit DEVICE_ID > DEVICE_TOKEN > machine fingerprint hash > machine name hash.
    public static string ResolveStableDeviceId(string? configuredDeviceId, string? deviceToken)
    {
        if (!string.IsNullOrWhiteSpace(configuredDeviceId))
        {
            return Sanitize(configuredDeviceId);
        }

        if (!string.IsNullOrWhiteSpace(deviceToken))
        {
            return Sanitize(deviceToken);
        }

        var machineGuid = TryGetMachineGuid();
        if (!string.IsNullOrWhiteSpace(machineGuid))
        {
            return $"edge-{ShortHash(machineGuid)}";
        }

        return $"edge-{ShortHash(Environment.MachineName)}";
    }

    private static string? TryGetMachineGuid()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var subKey = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return subKey?.GetValue("MachineGuid")?.ToString();
        }
        catch
        {
            return null;
        }
    }

    public static string GetMacAddress()
    {
        try
        {
            // Find the first operational interface that isn't a loopback or virtual
            var ni = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up && 
                              nic.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                              !nic.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(nic => nic.Speed)
                .FirstOrDefault();

            if (ni != null)
            {
                return ni.GetPhysicalAddress().ToString();
            }
        }
        catch { /* Fallback */ }

        // Last resort stable ID if no MAC found
        var machineGuid = TryGetMachineGuid();
        return ShortHash(machineGuid ?? Environment.MachineName);
    }

    private static string Sanitize(string value)
    {
        var input = value.Trim().ToLowerInvariant();
        if (input.Length == 0)
        {
            return "edge-unknown";
        }

        var sb = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '-' || ch == '_')
            {
                sb.Append(ch);
            }
        }

        return sb.Length > 0 ? sb.ToString() : "edge-unknown";
    }

    private static string ShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..12];
    }
}
