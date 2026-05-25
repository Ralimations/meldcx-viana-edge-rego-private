using Microsoft.EntityFrameworkCore;
using VianaRego.Shared.Data;
using System.Diagnostics;
using System.Management;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using VianaRego.Shared.Models;
using VianaRego.Shared.Configuration;
using VianaRego.Shared.Logging;

Console.WriteLine("Edge Watchdog (Supervision Mode) starting...");

var sharedLogger = new SharedLogger("EdgeAgent.log", "Watchdog");
sharedLogger.LogInfo("Edge Watchdog (Supervision Mode) starting...");

// DB Options
var dbPath = DataPath.GetPath("viana-edge.db");
var options = new DbContextOptionsBuilder<LocalDbContext>()
    .UseSqlite($"Data Source={dbPath}")
    .Options;

// Ensure DB created
using (var db = new LocalDbContext(options))
{
    db.EnsureCreatedAndPatched();
}

// BOOT LOOP DETECTION: Track crash timestamps per module
var crashHistory = new Dictionary<string, List<DateTime>>();

// PERFORMANCE MONITORING: Track when we last polled CPU/RAM
var lastPerfCheck = DateTime.MinValue;
var lastPerfLog = DateTime.MinValue;


// Helper function to get CPU/RAM from WMI
[SupportedOSPlatform("windows")]
static (float cpuUsage, float ramUsage) GetPerformanceMetrics()
{
    float cpu = 0f;
    float ram = 0f;
    try { using var cpuSearcher = new ManagementObjectSearcher("SELECT LoadPercentage FROM Win32_Processor"); foreach (ManagementObject obj in cpuSearcher.Get()) { cpu = Convert.ToSingle(obj["LoadPercentage"]); break; } using var ramSearcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem"); foreach (ManagementObject obj in ramSearcher.Get()) { var total = Convert.ToDouble(obj["TotalVisibleMemorySize"]); var free = Convert.ToDouble(obj["FreePhysicalMemory"]); ram = (float)(((total - free) / total) * 100); break; } } catch { }
    return (cpu, ram);
}

while (true)
{
    try 
    {
        using var db = new LocalDbContext(options);
        var modules = await db.ModuleStates.ToListAsync();

        foreach (var m in modules)
        {
            // Only care if intention is "Running"
            if (m.Status == "Running")
            {
                bool isRunning = false;
                if (m.ProcessId > 0)
                {
                    try {
                        var p = Process.GetProcessById(m.ProcessId);
                        if (!p.HasExited) isRunning = true;
                    } catch { /* Not running */ }
                }

                if (!isRunning)
                {
                    // Track crash for boot loop detection
                    if (m.ProcessId > 0) // Only track if it was previously running
                    {
                        TrackCrash(m.ModuleName, crashHistory, sharedLogger);
                        
                        // Check if boot loop detected
                        if (IsBootLoop(m.ModuleName, crashHistory))
                        {
                            Console.WriteLine($"BOOT LOOP DETECTED for {m.ModuleName}! Initiating rollback...");
                            sharedLogger.LogWarning($"BOOT LOOP DETECTED for {m.ModuleName}! Initiating rollback...");
                            await TriggerRollback(m.ModuleName, db, options, sharedLogger);
                            crashHistory[m.ModuleName].Clear(); // Reset after rollback
                            continue; // Skip launching, let reconciler apply rollback
                        }
                    }
                    
                    Console.WriteLine($"Module {m.ModuleName} is NOT running. Launching...");
                    sharedLogger.LogWarning($"Module {m.ModuleName} is NOT running. Launching...");
                    LaunchModule(m, db, sharedLogger);
                }
            }
            // Active enforcement for Stopped status
            else if (m.Status == "Stopped")
            {
                Console.WriteLine($"Processing STOPPED module: {m.ModuleName}");
                if (m.ProcessId > 0)
                {
                    try {
                        var p = Process.GetProcessById(m.ProcessId);
                        if (!p.HasExited)
                        {
                            Console.WriteLine($"Module {m.ModuleName} is marked STOPPED but PID {m.ProcessId} is still running. Killing tree...");
                            KillProcessTree(p);
                        }
                        
                        KillProcessesByName(m.ModuleName, m.StartCommand, sharedLogger);
                        m.ProcessId = 0;
                        db.SaveChanges();
                    } catch { 
                        KillProcessesByName(m.ModuleName, m.StartCommand, sharedLogger);
                        m.ProcessId = 0;
                        db.SaveChanges();
                    }
                }
                else 
                {
                    KillProcessesByName(m.ModuleName, m.StartCommand, sharedLogger);
                }
            }
            else 
            {
                Console.WriteLine($"Module {m.ModuleName} has status: {m.Status}. Skipping.");
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error in Watchdog cycle: {ex.Message}");
        sharedLogger.LogError($"Error in Watchdog cycle: {ex.Message}");
    }

    // Poll CPU/RAM every 30 seconds
    if ((DateTime.UtcNow - lastPerfCheck).TotalSeconds >= 30)
    {
        try
        {
            var (cpuUsage, ramUsage) = GetPerformanceMetrics();
            
            using var perfDb = new LocalDbContext(options);
            var status = await perfDb.SystemStatus.FindAsync("system");
            if (status != null)
            {
                status.CPUUsage = $"{cpuUsage:F1}%";
                status.RAMUsage = $"{ramUsage:F1}%";
                status.LastReportedAt = DateTime.UtcNow;
                await perfDb.SaveChangesAsync();
            }
            
            if ((DateTime.UtcNow - lastPerfLog).TotalMinutes >= 5)
            {
                sharedLogger.LogInfo($"Performance: CPU {cpuUsage:F1}%, RAM {ramUsage:F1}%");
                lastPerfLog = DateTime.UtcNow;
            }
            lastPerfCheck = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            sharedLogger.LogWarning($"Performance check failed: {ex.Message}");
        }
    }

    await Task.Delay(5000); // Check every 5s
}

void LaunchModule(LocalModuleState m, LocalDbContext db, SharedLogger logger)
{
    try
    {
        // 1. Determine Path. 
        var moduleDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "modules", m.ModuleName);
        var exePath = "";
        var resolvedArgs = "";

        if (TryResolveExecutableFromStartCommand(m.StartCommand, out var resolvedExePath, out resolvedArgs))
        {
            exePath = resolvedExePath;
            moduleDir = Path.GetDirectoryName(exePath) ?? moduleDir;
        }

        if (string.IsNullOrEmpty(exePath))
        {
            exePath = Path.Combine(moduleDir, "app.exe"); // Default convention
            
            if (!File.Exists(exePath))
            {
                // Fallback: search for filename in module dir
                if (!string.IsNullOrEmpty(m.StartCommand))
                {
                    var searchPattern = m.StartCommand.Split(' ')[0].Replace("\"", string.Empty);
                    if (!Path.IsPathRooted(searchPattern) && Directory.Exists(moduleDir))
                    {
                        var files = Directory.GetFiles(moduleDir, searchPattern, SearchOption.AllDirectories);
                        if (files.Length > 0)
                        {
                            exePath = files[0];
                            moduleDir = Path.GetDirectoryName(exePath);
                        }
                    }
                }
            }
        }

        // 2. Prepare Process
        var psi = new ProcessStartInfo();
        
        if (File.Exists(exePath))
        {
             psi.FileName = exePath;
             psi.WorkingDirectory = moduleDir;

             if (!string.IsNullOrWhiteSpace(resolvedArgs))
             {
                 psi.Arguments = resolvedArgs;
             }
             // Extract arguments from StartCommand if it was a path + args
             else if (!string.IsNullOrEmpty(m.StartCommand) && m.StartCommand.Contains(" "))
             {
                 var cmdLower = m.StartCommand.ToLower();
                 var exeLower = exePath.ToLower();
                 if (cmdLower.Contains(exeLower))
                 {
                     psi.Arguments = m.StartCommand.Substring(m.StartCommand.IndexOf(exePath, StringComparison.OrdinalIgnoreCase) + exePath.Length).Trim();
                 }
                 else 
                 {
                     // Simple split if the path itself doesn't match perfectly (e.g. short paths)
                     var parts = m.StartCommand.Split(' ');
                     if (parts.Length > 1) psi.Arguments = string.Join(" ", parts.Skip(1));
                 }
             }

             Console.WriteLine($"   Executing binary: {exePath} {psi.Arguments}");
             logger.LogInfo($"Executing binary for {m.ModuleName}: '{exePath}' with args '{psi.Arguments}' in dir '{moduleDir}'");
             psi.CreateNoWindow = false;
             psi.UseShellExecute = true;
        }
        else
        {
             Console.WriteLine($"   Binary not found. Using simulation command: {m.StartCommand}");
             logger.LogWarning($"Binary not found for {m.ModuleName}. Using simulation: {m.StartCommand}");
             psi.FileName = "cmd.exe";
             psi.Arguments = $"/c {m.StartCommand} && timeout /t 30";
             psi.CreateNoWindow = false;
             psi.UseShellExecute = true;
        }

        var launchMode = NormalizeLaunchMode(m.LaunchMode);

        // 3. Launch
        if (launchMode == "active" && File.Exists(exePath))
        {
            Console.WriteLine($"   LaunchMode=active. Starting in active user session: {exePath} {psi.Arguments}");
            logger.LogInfo($"LaunchMode=active for {m.ModuleName}. Starting in active user session.");

            if (TryLaunchInActiveUserSession(exePath, psi.Arguments ?? string.Empty, psi.WorkingDirectory, out var activePid, out var activeError))
            {
                m.ProcessId = activePid;
                m.LastUpdatedAt = DateTime.UtcNow;
                db.SaveChanges();
                Console.WriteLine($"   Started PID {activePid} (active user session)");
                logger.LogSuccess($"Started {m.ModuleName} (PID: {activePid}) in active user session");
            }
            else
            {
                Console.WriteLine($"   Failed active-session launch: {activeError}");
                logger.LogError($"Failed active-session launch for {m.ModuleName}: {activeError}");
            }

            return;
        }

        var proc = Process.Start(psi);
        if (proc != null)
        {
            m.ProcessId = proc.Id;
            m.LastUpdatedAt = DateTime.UtcNow;
            
            db.SaveChanges();
            Console.WriteLine($"   Started PID {proc.Id}");
            logger.LogSuccess($"Started {m.ModuleName} (PID: {proc.Id})");
        }
    }
    catch(Exception ex)
    {
        Console.WriteLine($"   Failed to launch: {ex.Message}");
        logger.LogError($"Failed to launch {m.ModuleName}: {ex.Message}");
    }
}

void TrackCrash(string moduleName, Dictionary<string, List<DateTime>> crashHistory, SharedLogger logger)
{
    if (!crashHistory.ContainsKey(moduleName))
        crashHistory[moduleName] = new List<DateTime>();
    
    crashHistory[moduleName].Add(DateTime.UtcNow);
    
    // Clean old crashes (> 60s ago)
    crashHistory[moduleName] = crashHistory[moduleName]
        .Where(t => (DateTime.UtcNow - t).TotalSeconds < 60)
        .ToList();
    
    Console.WriteLine($"   Crash tracked for {moduleName} ({crashHistory[moduleName].Count} crashes in last 60s)");
    logger.LogWarning($"Crash tracked for {moduleName} ({crashHistory[moduleName].Count} crashes in last 60s)");
}

bool IsBootLoop(string moduleName, Dictionary<string, List<DateTime>> crashHistory)
{
    if (!crashHistory.ContainsKey(moduleName))
        return false;
    
    return crashHistory[moduleName].Count >= 3;
}

async Task TriggerRollback(string moduleName, LocalDbContext currentDb, DbContextOptions<LocalDbContext> options, SharedLogger logger)
{
    try
    {
        Console.WriteLine($"   Searching for previous version of {moduleName}...");
        logger.LogInfo($"Searching for previous version of {moduleName}...");
        
        // Query previous version from history
        var previousVersion = await currentDb.ModuleVersionHistory
            .Where(h => h.ModuleName == moduleName && !h.IsRollback)
            .OrderByDescending(h => h.InstalledAt)
            .Skip(1) // Skip current version
            .FirstOrDefaultAsync();
        
        if (previousVersion == null)
        {
            Console.WriteLine($"   No previous version found for {moduleName}. Cannot rollback.");
            logger.LogError($"No previous version found for {moduleName}. Cannot rollback");
            return;
        }
        
        Console.WriteLine($"   Rolling back {moduleName} from current to v{previousVersion.Version}");
        logger.LogInfo($"Rolling back {moduleName} from current to v{previousVersion.Version}");
        
        // Update module state to previous version
        var module = await currentDb.ModuleStates.FindAsync(moduleName);
        if (module != null)
        {
            module.Version = previousVersion.Version;
            module.StartCommand = previousVersion.StartCommand;
            module.Status = "Stopped"; // Stop it so reconciler can re-download
            module.ProcessId = 0;
            module.LastUpdatedAt = DateTime.UtcNow;
            
            // Mark rollback in history
            var rollbackEntry = new ModuleVersionHistory
            {
                ModuleName = moduleName,
                Version = previousVersion.Version,
                DownloadUrl = previousVersion.DownloadUrl,
                StartCommand = previousVersion.StartCommand,
                InstalledAt = DateTime.UtcNow,
                IsRollback = true
            };
            currentDb.ModuleVersionHistory.Add(rollbackEntry);
            
            await currentDb.SaveChangesAsync();
            Console.WriteLine($"   Rollback complete. Module will be re-reconciled to v{previousVersion.Version}");
            logger.LogSuccess($"Rollback complete. Module {moduleName} will be re-reconciled to v{previousVersion.Version}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"   Rollback failed: {ex.Message}");
        logger.LogError($"Rollback failed for {moduleName}: {ex.Message}");
    }
}

bool TryResolveExecutableFromStartCommand(string? startCommand, out string exePath, out string args)
{
    exePath = string.Empty;
    args = string.Empty;

    if (string.IsNullOrWhiteSpace(startCommand))
    {
        return false;
    }

    var command = startCommand.Trim();

    // Quoted command path: "C:\Path With Spaces\app.exe" --flags
    if (command.StartsWith("\"", StringComparison.Ordinal))
    {
        var closingQuote = command.IndexOf('"', 1);
        if (closingQuote > 1)
        {
            var quotedPath = command.Substring(1, closingQuote - 1);
            if (File.Exists(quotedPath))
            {
                exePath = quotedPath;
                args = command[(closingQuote + 1)..].Trim();
                return true;
            }
        }
    }

    // Entire command may already be a full path with spaces and no args.
    var trimmed = command.Trim('"');
    if (File.Exists(trimmed))
    {
        exePath = trimmed;
        return true;
    }

    // Unquoted path fallback: find the longest prefix that is an existing file.
    // This handles paths with spaces like C:\Program Files\App.exe --arg
    var parts = command.Split(' ');
    for (var i = parts.Length; i > 0; i--)
    {
        var candidate = string.Join(" ", parts.Take(i)).Trim().Trim('"');
        if (File.Exists(candidate))
        {
            exePath = candidate;
            args = string.Join(" ", parts.Skip(i)).Trim();
            return true;
        }
    }

    return false;
}

void KillProcessTree(Process p)
{
    try
    {
        p.Kill(entireProcessTree: true);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"   Failed to kill process tree for PID {p.Id}: {ex.Message}");
    }
}

void KillProcessesByName(string moduleName, string startCommand, SharedLogger logger)
{
    try
    {
        Console.WriteLine($"   KillProcessesByName for {moduleName} with cmd: {startCommand}");
        var targetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Try to extract from startCommand
        if (!string.IsNullOrEmpty(startCommand))
        {
            if (TryResolveExecutableFromStartCommand(startCommand, out var resolvedPath, out _))
            {
                targetNames.Add(Path.GetFileNameWithoutExtension(resolvedPath));
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

        // 2. Always try the moduleName as well
        if (!string.IsNullOrEmpty(moduleName))
        {
            targetNames.Add(moduleName);
        }

        foreach (var exeName in targetNames)
        {
            var processes = Process.GetProcessesByName(exeName);
            if (processes.Length > 0)
            {
                Console.WriteLine($"   Aggressive sweep for {moduleName}: Found {processes.Length} stray '{exeName}' processes. Killing...");
                logger.LogWarning($"Aggressive sweep for {moduleName}: Found {processes.Length} stray '{exeName}' processes. Killing...");
                
                foreach (var p in processes)
                {
                    try {
                        p.Kill(entireProcessTree: true);
                    } catch { }
                }
            }
        }
    }
    catch { }
}

string NormalizeLaunchMode(string? launchMode)
{
    return string.Equals(launchMode, "active", StringComparison.OrdinalIgnoreCase) ? "active" : "service";
}

bool TryLaunchInActiveUserSession(string exePath, string args, string? workingDirectory, out int processId, out string error)
{
    processId = 0;
    error = string.Empty;

    IntPtr userToken = IntPtr.Zero;
    IntPtr primaryToken = IntPtr.Zero;
    IntPtr environmentBlock = IntPtr.Zero;
    Win32Native.PROCESS_INFORMATION processInfo = default;

    try
    {
        var sessionId = Win32Native.WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF)
        {
            error = "No active console session found.";
            return false;
        }

        if (!Win32Native.WTSQueryUserToken(sessionId, out userToken) || userToken == IntPtr.Zero)
        {
            error = $"WTSQueryUserToken failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
            return false;
        }

        const uint desiredAccess =
            Win32Native.TOKEN_ASSIGN_PRIMARY |
            Win32Native.TOKEN_DUPLICATE |
            Win32Native.TOKEN_QUERY |
            Win32Native.TOKEN_ADJUST_DEFAULT |
            Win32Native.TOKEN_ADJUST_SESSIONID;

        if (!Win32Native.DuplicateTokenEx(
                userToken,
                desiredAccess,
                IntPtr.Zero,
                Win32Native.SecurityImpersonation,
                Win32Native.TokenPrimary,
                out primaryToken))
        {
            error = $"DuplicateTokenEx failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
            return false;
        }

        if (!Win32Native.CreateEnvironmentBlock(out environmentBlock, primaryToken, false))
        {
            environmentBlock = IntPtr.Zero;
        }

        var startInfo = new Win32Native.STARTUPINFO
        {
            cb = Marshal.SizeOf<Win32Native.STARTUPINFO>(),
            lpDesktop = @"winsta0\default"
        };

        var launchDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? (Path.GetDirectoryName(exePath) ?? AppDomain.CurrentDomain.BaseDirectory)
            : workingDirectory;

        var commandLine = QuoteCommandLinePath(exePath);
        if (!string.IsNullOrWhiteSpace(args))
        {
            commandLine = $"{commandLine} {args}";
        }

        const uint creationFlags = Win32Native.CREATE_UNICODE_ENVIRONMENT | Win32Native.CREATE_NEW_CONSOLE;

        var started = Win32Native.CreateProcessAsUser(
            primaryToken,
            null,
            commandLine,
            IntPtr.Zero,
            IntPtr.Zero,
            false,
            creationFlags,
            environmentBlock,
            launchDirectory,
            ref startInfo,
            out processInfo);

        if (!started)
        {
            error = $"CreateProcessAsUser failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
            return false;
        }

        processId = (int)processInfo.dwProcessId;
        return true;
    }
    catch (Exception ex)
    {
        error = ex.Message;
        return false;
    }
    finally
    {
        if (processInfo.hProcess != IntPtr.Zero) Win32Native.CloseHandle(processInfo.hProcess);
        if (processInfo.hThread != IntPtr.Zero) Win32Native.CloseHandle(processInfo.hThread);
        if (environmentBlock != IntPtr.Zero) Win32Native.DestroyEnvironmentBlock(environmentBlock);
        if (primaryToken != IntPtr.Zero) Win32Native.CloseHandle(primaryToken);
        if (userToken != IntPtr.Zero) Win32Native.CloseHandle(userToken);
    }
}

static string QuoteCommandLinePath(string path)
{
    if (string.IsNullOrWhiteSpace(path))
    {
        return "\"\"";
    }

    if (path.StartsWith('"') && path.EndsWith('"'))
    {
        return path;
    }

    return $"\"{path}\"";
}

static class Win32Native
{
    public const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
    public const uint TOKEN_DUPLICATE = 0x0002;
    public const uint TOKEN_QUERY = 0x0008;
    public const uint TOKEN_ADJUST_DEFAULT = 0x0080;
    public const uint TOKEN_ADJUST_SESSIONID = 0x0100;
    public const uint CREATE_NEW_CONSOLE = 0x00000010;
    public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    public const int SecurityImpersonation = 2;
    public const int TokenPrimary = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    public static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("Wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool DuplicateTokenEx(
        IntPtr existingToken,
        uint desiredAccess,
        IntPtr tokenAttributes,
        int impersonationLevel,
        int tokenType,
        out IntPtr newToken);

    [DllImport("userenv.dll", SetLastError = true)]
    public static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    public static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CreateProcessAsUser(
        IntPtr token,
        string? applicationName,
        string commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref STARTUPINFO startupInfo,
        out PROCESS_INFORMATION processInformation);
}


