using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using VianaRego.Shared.Configuration;

namespace VianaRego.Edge.Agent.Services;

public class LogService
{
    private readonly ILogger<LogService> _logger;
    private readonly string _baseDir;

    public LogService(ILogger<LogService> logger)
    {
        _logger = logger;
        _baseDir = AppDomain.CurrentDomain.BaseDirectory;
    }

    public (List<string> lines, int totalLines) GetLogLines(string moduleName, int count = 100, int offset = 0)
    {
        try
        {
            string logFilePath = FindLogFile(moduleName);
            if (string.IsNullOrEmpty(logFilePath) || !File.Exists(logFilePath))
            {
                // Fallback: Search EdgeAgent.log for entries related to this module
                // This covers cases where the module hasn't started yet (Downloading, Extracting)
                var edgeAgentLog = Path.Combine(_baseDir, "logs", "EdgeAgent.log");
                if (File.Exists(edgeAgentLog))
                {
                    var fallbackLines = new List<string>();
                    using (var edgeFs = new FileStream(edgeAgentLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var r = new StreamReader(edgeFs))
                    { // Simple grep
                        string? line;
                        while ((line = r.ReadLine()) != null)
                        {
                            if (line.Contains(moduleName, StringComparison.OrdinalIgnoreCase))
                            { // Include surrounding lines or just matching? Just matching for now to be safe.
                              // Actually, the log format is [Component] Message.
                              // If message contains module name, good.
                                fallbackLines.Add(line);
                            }
                        }
                    }

                    if (fallbackLines.Any())
                    {
                        var total = fallbackLines.Count;
                        return (fallbackLines.Skip(offset).Take(count).ToList(), total);
                    }
                }

                return (new List<string> { $"Error: No log file found for module '{moduleName}' and no entries in EdgeAgent.log" }, 1);
            }

            // Efficiently read all lines with shared read access
            using var fs = new FileStream(logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            
            var allLines = reader.ReadToEnd().Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var totalLines = allLines.Length;
            
            // Skip offset lines, then take count lines
            var subset = allLines.Skip(offset).Take(count).ToList();
            
            return (subset, totalLines);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read logs for {Module}", moduleName);
            return (new List<string> { $"Error: {ex.Message}" }, 1);
        }
    }

    private string FindLogFile(string moduleName)
    {
        // Support unified EdgeAgent log
        if (moduleName.Equals("EdgeAgent", StringComparison.OrdinalIgnoreCase))
        {
            var unifiedLog = Path.Combine(_baseDir, "logs", "EdgeAgent.log");
            if (File.Exists(unifiedLog)) return unifiedLog;
        }
        
        if (moduleName.Equals("edge", StringComparison.OrdinalIgnoreCase) || 
            moduleName.Equals("agent", StringComparison.OrdinalIgnoreCase) ||
            moduleName.Equals("orchestrator", StringComparison.OrdinalIgnoreCase))
        {
            // The agent uses daily rotated logs: edge20260206.log
            // We should find the most recent one in the data directory.
            var dataDir = DataPath.GetPath();
            var agentLogs = Directory.GetFiles(dataDir, "edge*.log");
            if (agentLogs.Length > 0)
            {
                return agentLogs.OrderByDescending(f => File.GetLastWriteTime(f)).First();
            }
            
            // Fallback to the legacy single file
            return DataPath.GetPath("edge.log");
        }

        // Try standard data log path
        var dataLogPath = DataPath.GetPath($"logs/{moduleName}.log");
        if (File.Exists(dataLogPath)) return dataLogPath;

        // Try searching in the module's directory
        var moduleDir = Path.Combine(_baseDir, "modules", moduleName);
        if (Directory.Exists(moduleDir))
        {
            // Search for any .log file in the module folder (recursive search for safety)
            var logs = Directory.GetFiles(moduleDir, "*.log", SearchOption.AllDirectories);
            if (logs.Length > 0) return logs.OrderByDescending(f => File.GetLastWriteTime(f)).First();
        }
        
        // Final fallback: maybe it's an adopted service? 
        // We don't know where it logs, but we could search for {name}.log in common places
        // For now, let's just search the whole data directory for anything matching
        var globalSearch = Directory.GetFiles(DataPath.GetPath(), $"*{moduleName}*.log", SearchOption.AllDirectories);
        if (globalSearch.Length > 0) return globalSearch.First();

        return "";
    }
}
