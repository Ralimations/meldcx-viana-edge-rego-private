using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace VianaRego.Edge.Agent.Services;

public class ProcessOrchestrator : IDisposable
{
    private readonly ILogger<ProcessOrchestrator> _logger;
    private readonly Dictionary<int, string> _runningProcesses = new();
    private readonly string _basePath;
    private volatile bool _isStopping;

    private readonly TimeSpan _restartDelay = TimeSpan.FromSeconds(2);

    public ProcessOrchestrator(ILogger<ProcessOrchestrator> logger)
    {
        _logger = logger;
        _basePath = AppDomain.CurrentDomain.BaseDirectory;
    }

    public void LaunchSidecars()
    {
        string[] binaries = { "VianaRego.Edge.Telemetry.exe", "VianaRego.Edge.Watchdog.exe" };
        foreach (var bin in binaries)
        {
            LaunchSingleSidecar(bin);
        }
    }

    private void LaunchSingleSidecar(string binName)
    {
        if (_isStopping)
        {
            return;
        }

        try
        {
            var binPath = ResolveBinaryPath(binName);
            if (binPath == null)
            {
                return;
            }

            CleanupOrphans(binName);
            _logger.LogInformation("Launching sidecar: {Bin}", binName);

            var psi = new ProcessStartInfo
            {
                FileName = binPath,
                WorkingDirectory = Path.GetDirectoryName(binPath) ?? _basePath,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.Exited += (sender, args) =>
            {
                if (_isStopping)
                {
                    return;
                }

                _logger.LogWarning("Sidecar {Bin} exited. Restarting in {DelaySeconds}s...", binName, _restartDelay.TotalSeconds);
                lock (_runningProcesses)
                {
                    _runningProcesses.Remove(process.Id);
                }

                Task.Delay(_restartDelay).ContinueWith(_ => LaunchSingleSidecar(binName));
            };

            if (process.Start())
            {
                lock (_runningProcesses)
                {
                    _runningProcesses[process.Id] = binName;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch sidecar: {Bin}", binName);
        }
    }

    private string? ResolveBinaryPath(string bin)
    {
        var localPath = Path.Combine(_basePath, bin);
        if (File.Exists(localPath))
        {
            return localPath;
        }

        var artifactsBin = Path.GetFullPath(Path.Combine(_basePath, "..", ".."));
        if (Directory.Exists(artifactsBin))
        {
            var potentialPaths = Directory.GetFiles(artifactsBin, bin, SearchOption.AllDirectories);
            if (potentialPaths.Length > 0)
            {
                return potentialPaths[0];
            }
        }

        _logger.LogWarning("Sidecar {Bin} not found. Skipping.", bin);
        return null;
    }

    private void CleanupOrphans(string binName)
    {
        var procName = Path.GetFileNameWithoutExtension(binName);
        foreach (var existing in Process.GetProcessesByName(procName))
        {
            try
            {
                lock (_runningProcesses)
                {
                    if (_runningProcesses.ContainsKey(existing.Id))
                    {
                        continue;
                    }
                }

                _logger.LogInformation("Cleaning up orphaned sidecar instance: {Name} (PID: {Id})", procName, existing.Id);
                existing.Kill(true);
            }
            catch
            {
                // Ignore process cleanup failures.
            }
        }
    }

    public void StopAll()
    {
        _isStopping = true;
        lock (_runningProcesses)
        {
            foreach (var pid in _runningProcesses.Keys.ToList())
            {
                try
                {
                    var process = Process.GetProcessById(pid);
                    process.EnableRaisingEvents = false;
                    if (!process.HasExited)
                    {
                        process.Kill();
                    }
                }
                catch
                {
                    // Ignore shutdown cleanup failures.
                }
            }

            _runningProcesses.Clear();
        }
    }

    public void Dispose()
    {
        StopAll();
    }
}
