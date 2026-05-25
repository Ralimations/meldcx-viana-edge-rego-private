using Microsoft.Win32;
using System.Runtime.Versioning;

namespace VianaRego.Shared.Configuration;

public static class RegistryConfig
{
    private const string REGISTRY_PATH = @"Software\MeldCX\VianaRego";

    [SupportedOSPlatform("windows")]
    public static string? GetValue(string key)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var subKey = baseKey.OpenSubKey(REGISTRY_PATH);
            
            if (subKey != null)
            {
                var val = subKey.GetValue(key);
                return val?.ToString();
            }
        }
        catch 
        {
            // Ignore registry errors, fallback to env vars
        }
        return null;
    }

    [SupportedOSPlatform("windows")]
    public static bool SetValue(string key, string value)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var subKey = baseKey.CreateSubKey(REGISTRY_PATH, writable: true);
            subKey?.SetValue(key, value, RegistryValueKind.String);
            return true;
        }
        catch
        {
            // Ignore registry write errors (non-admin/dev contexts)
            return false;
        }
    }
}
