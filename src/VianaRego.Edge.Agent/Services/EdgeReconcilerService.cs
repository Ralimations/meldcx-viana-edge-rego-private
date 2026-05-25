using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection; // Add this
using System.Text.Json;
using Microsoft.Extensions.Logging;
using VianaRego.Shared.Data;
using VianaRego.Shared.Models;
using VianaRego.Shared.Logging;
using System.IO.Compression;
using System.Diagnostics;
using System.Threading;

namespace VianaRego.Edge.Agent.Services;

public class EdgeReconcilerService
{
    private readonly ILogger<EdgeReconcilerService> _logger;
    private readonly SharedLogger _sharedLogger;
    private readonly ResumableDownloader _downloader;
    private readonly IServiceScopeFactory _scopeFactory;
    private long? _lastLoggedDesiredVersion;
    private readonly SemaphoreSlim _applyLock = new(1, 1);

    public EdgeReconcilerService(
        ILogger<EdgeReconcilerService> logger, 
        ResumableDownloader downloader,
        IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _sharedLogger = new SharedLogger("EdgeAgent.log", "Reconciler");
        _downloader = downloader;
        _scopeFactory = scopeFactory;
    }

    public async Task ApplyDesiredStateAsync(string desiredStateJson)
    {
        await _applyLock.WaitAsync();
        try
        {
            _logger.LogDebug("ApplyDesiredStateAsync started.");
            _logger.LogDebug("Payload: {Payload}", desiredStateJson);

            var desired = JsonSerializer.Deserialize<DesiredState>(desiredStateJson);
            if (desired == null)
            {
                return;
            }

            var shouldLogReconcileTransition = _lastLoggedDesiredVersion != desired.Version;
            if (shouldLogReconcileTransition)
            {
                _logger.LogInformation("Reconciling state (Version: {Version})...", desired.Version);
                _sharedLogger.LogInfo($"Reconciling State (Version: {desired.Version})...");
            }

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();

            // 1. Process Desired Modules (Add/Update)
            var desiredModuleNames = desired.Modules.Select(m => m.Name).ToList();
            foreach (var module in desired.Modules)
            {
                await ApplyModuleAsync(module, db);
            }

            // 2. Identify Orphaned Modules (Present in DB but NOT in Desired List)
            var existingModules = await db.ModuleStates.ToListAsync();
            var orphanedModules = existingModules.Where(m => !desiredModuleNames.Contains(m.ModuleName)).ToList();

            foreach (var orphaned in orphanedModules)
            {
                await RemoveOrphanedModuleAsync(orphaned, db);
            }

            await db.SaveChangesAsync();

            if (shouldLogReconcileTransition)
            {
                _logger.LogInformation("Reconciliation complete for Version {Version}", desired.Version);
                _sharedLogger.LogSuccess($"Reconciliation Complete for Version {desired.Version}");
                _lastLoggedDesiredVersion = desired.Version;
            }
        }
        finally
        {
            _applyLock.Release();
        }
    }

    public async Task<bool> StartModuleAsync(string moduleName, string? launchMode = null)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();

        var module = await db.ModuleStates.FindAsync(moduleName);
        if (module == null)
        {
            _logger.LogWarning("Start requested for unknown module {ModuleName}", moduleName);
            return false;
        }

        var requestedLaunchMode = NormalizeLaunchMode(string.IsNullOrWhiteSpace(launchMode) ? module.LaunchMode : launchMode);
        var previousLaunchMode = NormalizeLaunchMode(module.LaunchMode);
        var launchModeChanged = !string.Equals(previousLaunchMode, requestedLaunchMode, StringComparison.OrdinalIgnoreCase);

        // If mode changes, force a clean restart so Watchdog relaunches with the new mode.
        // Also guard against stale state where DB says "active" but tracked PID is still in Session 0.
        var shouldForceRelaunch = launchModeChanged;
        if (!shouldForceRelaunch && module.ProcessId > 0)
        {
            try
            {
                var tracked = Process.GetProcessById(module.ProcessId);
                if (!tracked.HasExited)
                {
                    if (requestedLaunchMode == "active" && tracked.SessionId == 0)
                    {
                        shouldForceRelaunch = true;
                    }
                    else if (requestedLaunchMode == "service" && tracked.SessionId != 0)
                    {
                        shouldForceRelaunch = true;
                    }
                }
            }
            catch
            {
                // Process no longer exists; Watchdog relaunches naturally.
            }
        }

        if (shouldForceRelaunch)
        {
            _logger.LogInformation(
                "Launch mode transition for {ModuleName}: {PreviousMode} -> {RequestedMode}. Forcing restart.",
                moduleName,
                previousLaunchMode,
                requestedLaunchMode);
            _sharedLogger.LogInfo($"Launch mode transition for {moduleName}: {previousLaunchMode} -> {requestedLaunchMode}. Forcing restart.");

            StopTrackedProcess(module.ModuleName, module.ProcessId);
            KillProcessesByNameUnified(module.ModuleName, module.StartCommand);
            module.ProcessId = 0;
        }

        module.Status = "Running";
        module.LaunchMode = requestedLaunchMode;
        module.LastUpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        _logger.LogInformation("Set module {ModuleName} intention to Running (LaunchMode: {LaunchMode})", moduleName, module.LaunchMode);
        _sharedLogger.LogInfo($"Set module {moduleName} intention to Running");
        return true;
    }

    public async Task<bool> StopModuleAsync(string moduleName)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();

        var module = await db.ModuleStates.FindAsync(moduleName);
        if (module == null)
        {
            _logger.LogWarning("Stop requested for unknown module {ModuleName}", moduleName);
            return false;
        }

        module.Status = "Stopped";
        module.LastUpdatedAt = DateTime.UtcNow;
        // Don't set ProcessId to 0 yet, let StopTrackedProcess use it
        await db.SaveChangesAsync();

        _logger.LogInformation("Set module {ModuleName} intention to Stopped. Now killing process tree (PID {Pid})...", moduleName, module.ProcessId);
        _sharedLogger.LogInfo($"Set module {moduleName} intention to Stopped. Now killing process tree (PID {module.ProcessId})...");

        StopTrackedProcess(module.ModuleName, module.ProcessId);
        
        // Aggressive cleanup for browsers/multi-process apps
        KillProcessesByNameUnified(module.ModuleName, module.StartCommand);
        
        // Now that we've attempted termination, the Watchdog will handle the rest or we can clear it
        module.ProcessId = 0; 
        await db.SaveChangesAsync();

        _logger.LogInformation("Set module {ModuleName} intention to Stopped", moduleName);
        _sharedLogger.LogInfo($"Set module {moduleName} intention to Stopped");
        return true;
    }

    private async Task ApplyModuleAsync(ModuleDefinition module, LocalDbContext db)
    {
        _logger.LogDebug("Checking module: {ModuleName} v{Version}", module.Name, module.Version);

        // Check if already running the correct version AND status
        var targetStatus = !string.IsNullOrEmpty(module.DesiredStatus) ? module.DesiredStatus : "Running";
        var targetLaunchMode = NormalizeLaunchMode(module.LaunchMode);
        
        var existing = await db.ModuleStates.FindAsync(module.Name);
        var sameVersion =
            existing != null &&
            string.Equals(existing.Version, module.Version, StringComparison.OrdinalIgnoreCase);
        var installInProgress =
            existing != null &&
            (string.Equals(existing.Status, "Downloading", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(existing.Status, "Extracting", StringComparison.OrdinalIgnoreCase));
        var launchModeTransition =
            existing != null &&
            !string.Equals(existing.LaunchMode, targetLaunchMode, StringComparison.OrdinalIgnoreCase);

        if (existing != null &&
            sameVersion &&
            string.Equals(existing.LaunchMode, targetLaunchMode, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(existing.Status, targetStatus, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Module {ModuleName} is already in target state ({Status}).", module.Name, targetStatus);
            return;
        }

        if (installInProgress)
        {
            _logger.LogDebug("Module {ModuleName} install already in progress (Status: {Status}). Skipping duplicate apply.", module.Name, existing!.Status);
            return;
        }

        if (launchModeTransition && string.Equals(targetStatus, "Running", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "Desired launch mode changed for {ModuleName}: {PreviousMode} -> {TargetMode}. Restarting process to apply mode.",
                module.Name,
                NormalizeLaunchMode(existing!.LaunchMode),
                targetLaunchMode);
            _sharedLogger.LogInfo($"Desired launch mode changed for {module.Name}: {NormalizeLaunchMode(existing!.LaunchMode)} -> {targetLaunchMode}. Restarting process.");

            StopTrackedProcess(existing.ModuleName, existing.ProcessId);
            KillProcessesByNameUnified(existing.ModuleName, existing.StartCommand);
            existing.ProcessId = 0;
        }

        // Install/Update
        var installDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "modules", module.Name);
        if (!Directory.Exists(installDir)) Directory.CreateDirectory(installDir);

        var isZip = !string.IsNullOrEmpty(module.DownloadUrl) && module.DownloadUrl.Contains(".zip", StringComparison.OrdinalIgnoreCase);
        var tempFile = Path.Combine(installDir, isZip ? "package.zip" : "app.exe");
        var isDownloadableManaged = module.IsManaged && !string.IsNullOrWhiteSpace(module.DownloadUrl);
        var canReuseInstalledContent =
            existing != null &&
            sameVersion &&
            existing.IsManaged == module.IsManaged &&
            (!isDownloadableManaged || Directory.Exists(installDir));

        // ADOPTION LOGIC: Skip download if no URL is provided, or if the StartCommand is an absolute path that exists
        bool skipDownload = string.IsNullOrWhiteSpace(module.DownloadUrl);
        if (!skipDownload && canReuseInstalledContent)
        {
            _logger.LogInformation("Version unchanged for {ModuleName} ({Version}). Reusing installed content (no re-download).", module.Name, module.Version);
            _sharedLogger.LogInfo($"Version unchanged for {module.Name} ({module.Version}). Reusing installed content (no re-download).");
            skipDownload = true;
        }
        
        if (!skipDownload && TryResolveLocalExecutablePath(module.StartCommand, out var potentialPath))
        {
            _logger.LogInformation("Module found at local path {Path}. Skipping download.", potentialPath);
            _sharedLogger.LogInfo($"Module found at local path {potentialPath}. Skipping download");
            skipDownload = true;
        }

        var performedContentUpdate = !skipDownload;
        if (!skipDownload)
        {
            // Prevent Watchdog from relaunching the module while package files are being replaced.
            if (existing == null)
            {
                existing = new LocalModuleState { ModuleName = module.Name };
                db.ModuleStates.Add(existing);
            }

            existing.Version = module.Version;
            existing.Status = "Downloading";
            existing.StartCommand = !string.IsNullOrEmpty(module.StartCommand) ? module.StartCommand : "app.exe";
            existing.LaunchMode = targetLaunchMode;
            existing.IsManaged = module.IsManaged;
            existing.LastUpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            _logger.LogInformation("Downloading {ModuleName} from {Url}...", module.Name, module.DownloadUrl);
            _sharedLogger.LogInfo($"Downloading {module.Name} from {module.DownloadUrl}...");
            
            // --- UPDATED: Save intermediate status with progress callback ---
            var lastUpdate = DateTime.MinValue;
            await _downloader.DownloadAsync(module.DownloadUrl, tempFile, CancellationToken.None, (current, total) => {
                // Throttle DB updates to once per 2 seconds during download
                    if ((DateTime.UtcNow - lastUpdate).TotalSeconds >= 2) {
                        lastUpdate = DateTime.UtcNow;
                        
                        // Calculate percentage
                        var percent = total > 0 ? (int)((double)current / total * 100) : 0;
                        var msg = $"Downloading {module.Name}: {percent}% ({current / 1024 / 1024}MB / {total / 1024 / 1024}MB)";
                        
                        _logger.LogInformation("{Message}", msg);
                        _sharedLogger.LogInfo(msg);

                        // We need a fresh scope/DB context for these async background updates
                        _ = Task.Run(async () => {
                            using var scope = _scopeFactory.CreateScope();
                            var db2 = scope.ServiceProvider.GetRequiredService<LocalDbContext>();
                            await UpdateModuleStatusAsync(module.Name, "Downloading", module.Version, db2, current, total, createIfMissing: false);
                        });
                    }
            });

            if (isZip)
            {
                _logger.LogInformation("Extracting ZIP package...");
                _sharedLogger.LogInfo("Extracting ZIP package...");
                
                // --- UPDATED: Save intermediate status for visibility ---
                await UpdateModuleStatusAsync(module.Name, "Extracting", module.Version, db, createIfMissing: false);
                try 
                {
                    // Clear existing files before extraction to avoid conflicts
                    foreach (var file in Directory.GetFiles(installDir))
                    {
                        if (Path.GetFileName(file) != "package.zip") File.Delete(file);
                    }
                    foreach (var dir in Directory.GetDirectories(installDir))
                    {
                        Directory.Delete(dir, true);
                    }

                    ZipFile.ExtractToDirectory(tempFile, installDir, overwriteFiles: true);
                    _logger.LogInformation("Extraction complete.");
                    _sharedLogger.LogSuccess("Extraction complete");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Extraction failed for module {ModuleName}", module.Name);
                    _sharedLogger.LogError($"Extraction failed: {ex.Message}");
                    
                    // --- UPDATED: Delete corrupted ZIP so next run retries from scratch ---
                    if (File.Exists(tempFile)) 
                    {
                        try { File.Delete(tempFile); } catch { /* ignore */ }
                    }
                    
                    await UpdateModuleStatusAsync(module.Name, "Error", module.Version, db);
                    return;
                }
            }
        }
        else 
        {
            if (string.IsNullOrWhiteSpace(module.DownloadUrl))
            {
                _logger.LogInformation("Skipping download phase (local adoption).");
                _sharedLogger.LogInfo("Skipping download phase (Local Adoption)");
            }
            else
            {
                _logger.LogInformation("Skipping download phase (content already present).");
                _sharedLogger.LogInfo("Skipping download phase (content already present)");
            }
        }

        _logger.LogInformation("Updating intention for {ModuleName}...", module.Name);
        _sharedLogger.LogInfo($"Updating Intention for {module.Name}...");
        
        // Update SQLite so External Watchdog picks it up
        if (existing == null)
        {
            existing = await db.ModuleStates.FindAsync(module.Name);
        }

        if (existing == null) {
            existing = new LocalModuleState { ModuleName = module.Name };
            db.ModuleStates.Add(existing);
        }
        
        existing.Version = module.Version;
        existing.Status = !string.IsNullOrEmpty(module.DesiredStatus) ? module.DesiredStatus : "Running";
        // Pass the start command (e.g. "app.exe" or "java -jar app.jar")
        existing.StartCommand = !string.IsNullOrEmpty(module.StartCommand) ? module.StartCommand : "app.exe";
        existing.LaunchMode = targetLaunchMode;
        existing.IsManaged = module.IsManaged;
        existing.LastUpdatedAt = DateTime.UtcNow;
        
        // ROLLBACK SUPPORT: Save version history for managed modules
        if (performedContentUpdate && module.IsManaged && !string.IsNullOrWhiteSpace(module.DownloadUrl))
        {
            var history = new ModuleVersionHistory
            {
                ModuleName = module.Name,
                Version = module.Version,
                DownloadUrl = module.DownloadUrl,
                StartCommand = module.StartCommand,
                InstalledAt = DateTime.UtcNow,
                IsRollback = false
            };
            db.ModuleVersionHistory.Add(history);
            
            // Keep only last 3 versions per module
            var allHistory = await db.ModuleVersionHistory
                .Where(h => h.ModuleName == module.Name)
                .OrderByDescending(h => h.InstalledAt)
                .ToListAsync();
            
            if (allHistory.Count > 3)
            {
                var toRemove = allHistory.Skip(3);
                db.ModuleVersionHistory.RemoveRange(toRemove);
                _logger.LogInformation("Cleaned up old version history (kept 3 most recent).");
                _sharedLogger.LogInfo("Cleaned up old version history (kept 3 most recent)");
            }
        }
        
        await db.SaveChangesAsync();
        _logger.LogInformation("State saved. External Watchdog should launch it shortly.");
        _sharedLogger.LogSuccess($"Module {module.Name} state saved. Watchdog should launch it shortly");
    }

    private async Task RemoveOrphanedModuleAsync(LocalModuleState orphaned, LocalDbContext db)
    {
        var history = await db.ModuleVersionHistory
            .Where(h => h.ModuleName == orphaned.ModuleName)
            .ToListAsync();

        // Check if a managed directory exists for this module
        var installDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "modules", orphaned.ModuleName);
        var isManagedDir = Directory.Exists(installDir);

        // Only uninstall modules that are owned by Edge (downloaded/managed/in-modules-folder).
        // Adopted/local apps are detached from management only.
        var isOwnedByEdge = orphaned.IsManaged || history.Count > 0 || isManagedDir;

        if (isOwnedByEdge)
        {
            _logger.LogWarning("Module {ModuleName} is no longer in desired state. Uninstalling local copy...", orphaned.ModuleName);
            _sharedLogger.LogWarning($"Module {orphaned.ModuleName} is no longer in Desired State. Uninstalling local copy...");

            // Persist STOPPED intent first so Watchdog will not relaunch during uninstall.
            orphaned.Status = "Stopped";
            orphaned.LastUpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            StopTrackedProcess(orphaned.ModuleName, orphaned.ProcessId);
            KillProcessesByNameUnified(orphaned.ModuleName, orphaned.StartCommand ?? string.Empty);
            orphaned.ProcessId = 0;
            orphaned.LastUpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();


            try
            {
                if (Directory.Exists(installDir))
                {
                    var deleted = false;
                    for (var attempt = 1; attempt <= 5 && !deleted; attempt++)
                    {
                        try
                        {
                            Directory.Delete(installDir, recursive: true);
                            deleted = true;
                        }
                        catch (IOException) when (attempt < 5)
                        {
                            // Retry after forcing one more sweep and a short wait.
                            KillProcessesByNameUnified(orphaned.ModuleName, orphaned.StartCommand ?? string.Empty);
                            await Task.Delay(250 * attempt);
                        }
                        catch (UnauthorizedAccessException) when (attempt < 5)
                        {
                            KillProcessesByNameUnified(orphaned.ModuleName, orphaned.StartCommand ?? string.Empty);
                            await Task.Delay(250 * attempt);
                        }
                    }

                    if (deleted)
                    {
                        _logger.LogInformation("Deleted module directory: {Path}", installDir);
                        _sharedLogger.LogInfo($"Deleted module directory: {installDir}");
                    }
                    else
                    {
                        _logger.LogWarning("Module directory still locked after retries: {Path}", installDir);
                        _sharedLogger.LogWarning($"Module directory still locked after retries: {installDir}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete module directory {Path}", installDir);
                _sharedLogger.LogWarning($"Failed to delete module directory {installDir}: {ex.Message}");
            }

            if (history.Count > 0)
            {
                db.ModuleVersionHistory.RemoveRange(history);
            }
        }
        else
        {
            _logger.LogInformation("Module {ModuleName} was adopted/local. Removing management only.", orphaned.ModuleName);
            _sharedLogger.LogInfo($"Module {orphaned.ModuleName} was adopted/local. Removing management only.");
        }

        db.ModuleStates.Remove(orphaned);
    }

    private void StopTrackedProcess(string moduleName, int processId)
    {
        if (processId <= 0)
        {
            return;
        }

        try
        {
            var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return;
            }

            process.Kill(entireProcessTree: true);
            _logger.LogInformation("Stopped module process {ModuleName} (PID {Pid})", moduleName, processId);
            _sharedLogger.LogInfo($"Stopped module process {moduleName} (PID {processId})");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stop module process {ModuleName} (PID {Pid})", moduleName, processId);
            _sharedLogger.LogWarning($"Failed to stop module process {moduleName} (PID {processId}): {ex.Message}");
        }
    }

    private static bool TryResolveLocalExecutablePath(string? startCommand, out string path)
    {
        path = string.Empty;

        if (string.IsNullOrWhiteSpace(startCommand))
        {
            return false;
        }

        var command = startCommand.Trim();

        if (command.StartsWith("\"", StringComparison.Ordinal))
        {
            var closingQuote = command.IndexOf('"', 1);
            if (closingQuote > 1)
            {
                var quotedPath = command.Substring(1, closingQuote - 1);
                if (File.Exists(quotedPath))
                {
                    path = quotedPath;
                    return true;
                }
            }
        }

        var trimmed = command.Trim('"');
        if (File.Exists(trimmed))
        {
            path = trimmed;
            return true;
        }

        for (var i = command.Length - 1; i > 0; i--)
        {
            if (command[i] != ' ')
            {
                continue;
            }

            var candidate = command[..i].Trim().Trim('"');
            if (File.Exists(candidate))
            {
                path = candidate;
                return true;
            }
        }

        return false;
    }

    private void KillProcessesByNameUnified(string moduleName, string startCommand)
    {
        try
        {
            var targetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Try to extract from startCommand
            if (!string.IsNullOrEmpty(startCommand))
            {
                if (TryResolveLocalExecutablePath(startCommand, out var resolvedPath))
                {
                    targetNames.Add(Path.GetFileNameWithoutExtension(resolvedPath));
                    
                    // NEW: Also kill by directory
                    var resolvedDirectory = Path.GetDirectoryName(resolvedPath);
                    if (!string.IsNullOrWhiteSpace(resolvedDirectory))
                    {
                        KillProcessesInDirectory(resolvedDirectory);
                    }
                }
                else
                {
                    // Fallback for simple commands
                    var firstPart = startCommand.Split(' ')[0].Replace("\"", string.Empty);
                    var candidate = Path.GetFileNameWithoutExtension(firstPart);
                    if (!string.IsNullOrEmpty(candidate) && !candidate.Equals("Program", StringComparison.OrdinalIgnoreCase))
                    {
                        targetNames.Add(candidate);
                    }
                }
            }

            // 2. Always try the moduleName as well (covers case where command is malformed or path-based)
            if (!string.IsNullOrEmpty(moduleName))
            {
                targetNames.Add(moduleName);
                
                // NEW: Also check the module's installation directory
                var moduleDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "modules", moduleName);
                if (Directory.Exists(moduleDir))
                {
                   KillProcessesInDirectory(moduleDir);
                }
            }

            foreach (var exeName in targetNames)
            {
                _logger.LogInformation("Aggressive cleanup for {ModuleName}: Killing all '{ExeName}' processes...", moduleName, exeName);

                var processes = Process.GetProcessesByName(exeName);
                foreach (var p in processes)
                {
                    try
                    {
                        _logger.LogInformation("   Killing stray process: {ProcessName} (PID {Pid})", p.ProcessName, p.Id);
                        p.Kill(entireProcessTree: true);
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Aggressive cleanup failed for {ModuleName}: {Message}", moduleName, ex.Message);
        }
    }

    private void KillProcessesInDirectory(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath)) return;
        
        try 
        {
            var normalizedDir = Path.GetFullPath(directoryPath).TrimEnd(Path.DirectorySeparatorChar);
            var allProcesses = Process.GetProcesses();
            
            foreach (var p in allProcesses)
            {
                try 
                {
                    if (p.HasExited) continue;
                    
                    var mainModuleParams = p.MainModule;
                    if (mainModuleParams != null && !string.IsNullOrEmpty(mainModuleParams.FileName))
                    {
                        var exePath = Path.GetDirectoryName(mainModuleParams.FileName);
                        if (exePath != null && exePath.StartsWith(normalizedDir, StringComparison.OrdinalIgnoreCase))
                        {
                             _logger.LogInformation("   Killing process in module directory: {ProcessName} (PID {Pid})", p.ProcessName, p.Id);
                             p.Kill(entireProcessTree: true);
                        }
                    }
                }
                catch { /* Access denied or exited */ }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Directory-based cleanup failed for {Path}: {Message}", directoryPath, ex.Message);
        }
    }
    public async Task<bool> KillModuleAdHocAsync(string processName)
    {
        _logger.LogInformation("Ad-hoc kill requested for process/module: {ProcessName}", processName);
        _sharedLogger.LogWarning($"Ad-hoc kill requested for process: {processName}");

        // 1. If it happens to be a known module, we might have its start command for better resolution
        string startCommand = string.Empty;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();
            var module = await db.ModuleStates.FindAsync(processName);
            if (module != null)
            {
                startCommand = module.StartCommand;
            }
        }

        // 2. Perform aggressive cleanup
        KillProcessesByNameUnified(processName, startCommand);

        return true;
    }

    public async Task<bool> ReviveModuleAdHocAsync(string moduleName)
    {
        _logger.LogInformation("Revive (one-off start) requested for module: {ModuleName}", moduleName);
        _sharedLogger.LogWarning($"Revive (one-off start) requested for module: {moduleName}");

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();
        
        var module = await db.ModuleStates.FindAsync(moduleName);
        if (module == null)
        {
            _logger.LogWarning("Revive failed: Module {ModuleName} not found in local registry.", moduleName);
            return false;
        }

        // 1. Set Status to "Manual" so Watchdog ignores it (won't kill, won't auto-restart)
        module.Status = "Manual";
        module.LastUpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        // 2. Launch the process manually
        try 
        {
            var installDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "modules", module.ModuleName);
            string exePath;
            
            // Resolve Exe
            if (TryResolveLocalExecutablePath(module.StartCommand, out var resolvedExe))
            {
                exePath = resolvedExe;
                // If it's an absolute path, use its directory. If relative, use module dir.
                if (Path.IsPathRooted(exePath)) 
                    installDir = Path.GetDirectoryName(exePath) ?? installDir;
            }
            else 
            {
                // Fallback convention
                exePath = Path.Combine(installDir, "app.exe");
            }

            if (!File.Exists(exePath))
            {
                _logger.LogError("Revive failed: Executable not found at {Path}", exePath);
                return false;
            }

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = installDir,
                UseShellExecute = true,
                CreateNoWindow = false
            };

            // Argument parsing logic (simplified from Watchdog)
            if (!string.IsNullOrEmpty(module.StartCommand) && module.StartCommand.Contains(" "))
            {
                 var cmdLower = module.StartCommand.ToLower();
                 var exeLower = exePath.ToLower();
                 if (cmdLower.Contains(exeLower))
                 {
                     psi.Arguments = module.StartCommand.Substring(module.StartCommand.IndexOf(exePath, StringComparison.OrdinalIgnoreCase) + exePath.Length).Trim();
                 }
                 else 
                 {
                     var parts = module.StartCommand.Split(' ');
                     if (parts.Length > 1) psi.Arguments = string.Join(" ", parts.Skip(1));
                 }
            }

            var proc = Process.Start(psi);
            if (proc != null)
            {
                module.ProcessId = proc.Id;
                await db.SaveChangesAsync();
                _logger.LogInformation("Revived {ModuleName} (PID {Pid}). Status is 'Manual'.", moduleName, proc.Id);
                _sharedLogger.LogSuccess($"Revived {moduleName} (PID {proc.Id}). Status is 'Manual'.");
                return true;
            }
        }
        catch (Exception ex)
        {
             _logger.LogError(ex, "Failed to launch revived process for module {ModuleName}", moduleName);
             return false;
        }

        return false;
    }



    private async Task UpdateModuleStatusAsync(
        string moduleName,
        string status,
        string version,
        LocalDbContext db,
        long progress = 0,
        long total = 0,
        bool createIfMissing = true)
    {
        var existing = await db.ModuleStates.FirstOrDefaultAsync(m => m.ModuleName == moduleName);
        if (existing == null)
        {
            if (!createIfMissing)
            {
                return;
            }

            existing = new LocalModuleState { ModuleName = moduleName };
            db.ModuleStates.Add(existing);
        }

        var incomingIsTransient =
            string.Equals(status, "Downloading", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "Extracting", StringComparison.OrdinalIgnoreCase);
        if (incomingIsTransient && string.Equals(existing.Status, "Running", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        existing.Status = status;
        existing.Version = version;
        existing.DownloadProgress = progress;
        existing.TotalSize = total;
        existing.LastUpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    private static string NormalizeLaunchMode(string? launchMode)
    {
        return string.Equals(launchMode, "active", StringComparison.OrdinalIgnoreCase) ? "active" : "service";
    }
}


