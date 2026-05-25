using System.Reflection;

namespace VianaRego.Shared.Configuration;

public static class DataPath
{
    private static string? _cachedPath;

    public static string GetPath(string fileName = "")
    {
        if (_cachedPath == null)
        {
            // 1. Check Environment Variable (Production / Docker)
            var envPath = Environment.GetEnvironmentVariable("VIANA_DATA_PATH");
            if (!string.IsNullOrEmpty(envPath))
            {
                _cachedPath = envPath;
            }
            else
            {
                // 2. Default to Solution Root / data for local dev
                // We go up from artifacts/bin/Project... to find solution root
                var entryDir = AppDomain.CurrentDomain.BaseDirectory;
                var di = new DirectoryInfo(entryDir);
                
                // If we are in artifacts/bin/..., go up to solution root
                // artifacts/bin/VianaRego.Edge.Agent/debug/win-x64/ -> solution root/data
                // A safer way is to find a marker like the .slnx or artifacts/ folder
                var root = di;
                while (root != null && !Directory.Exists(Path.Combine(root.FullName, "artifacts")))
                {
                    root = root.Parent;
                }

                if (root != null)
                {
                    _cachedPath = Path.Combine(root.FullName, "data");
                }
                else
                {
                    // Fallback to base directory
                    _cachedPath = Path.Combine(entryDir, "data");
                }
            }

            if (!Directory.Exists(_cachedPath))
            {
                Directory.CreateDirectory(_cachedPath);
            }
        }

        return string.IsNullOrEmpty(fileName) ? _cachedPath : Path.Combine(_cachedPath, fileName);
    }
}
