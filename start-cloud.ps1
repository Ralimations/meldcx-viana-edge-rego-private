<#
.SYNOPSIS
    Run Cloud API Only (for testing against MSI-installed Edge Agent)
.DESCRIPTION
    Builds and runs only the Cloud API component.
    Use this when testing the MSI-installed Edge Agent against the local Cloud.
#>

Write-Host "Cleanup stale processes..." -ForegroundColor Yellow
$project = Join-Path $PSScriptRoot "src/VianaRego.Cloud.Api/VianaRego.Cloud.Api.csproj"
$cloudPort = 5074

# --- Setup Watcher ---
$watcher = New-Object System.IO.FileSystemWatcher
$watcher.Path = (Get-Item (Join-Path $PSScriptRoot "src")).FullName
$watcher.Filter = "*.*" # Catch all for .cs, .csproj, .json
$watcher.IncludeSubdirectories = $true
$watcher.EnableRaisingEvents = $true

$global:needsRestart = $false
$onChange = {
    $path = $Event.SourceEventArgs.FullPath
    # Filter for source files
    if ($path -match "\.(cs|csproj|json|ps1)$") {
        # Check if it's not a build artifact
        if ($path -notmatch "\\(bin|obj)\\" -and $path -notmatch "viana-cloud\.db") {
            $global:needsRestart = $true
            # Write-Host "`a" -NoNewline # Subtle beep
        }
    }
}

$handlers = @()
$handlers += Register-ObjectEvent $watcher "Changed" -Action $onChange
$handlers += Register-ObjectEvent $watcher "Created" -Action $onChange
$handlers += Register-ObjectEvent $watcher "Deleted" -Action $onChange

Write-Host "--- Cloud API Watcher ---" -ForegroundColor Cyan
Write-Host "   - Watching: $($watcher.Path)"
Write-Host "   - Port:     $cloudPort"
Write-Host "   - Mode:     RESTART ON SOURCE CHANGE (no error retry loop)"
Write-Host ""

try {
    while ($true) {
        $global:needsRestart = $false
        
        # 1. Kill stale processes
        $staleProcesses = Get-NetTCPConnection -LocalPort $cloudPort -State Listen -ErrorAction SilentlyContinue | Select-Object -ExpandProperty OwningProcess
        foreach ($procId in $staleProcesses) {
            Stop-Process -Id $procId -Force -ErrorAction SilentlyContinue
        }

        # 2. Aggressive Cleanup
        Write-Host "[$(Get-Date -Format 'HH:mm:ss')] [Cleanup] Cleaning and Building..." -ForegroundColor Gray
        $cleanArgs = @("clean", "`"$project`"", "-v", "quiet", "-nologo")
        & dotnet $cleanArgs
        
        # 3. Start the API
        # Use the 'http' profile to ensure it listens on 5074
        $runArgs = @("run", "--project", "`"$project`"", "--launch-profile", "http")
        $process = Start-Process dotnet -ArgumentList $runArgs -PassThru -NoNewWindow
         
        # 4. Wait for change or exit
        $exitedWithoutChange = $false
        while (!$global:needsRestart) {
            if ($process.HasExited) {
                $exitedWithoutChange = $true
                break
            }
            Start-Sleep -Milliseconds 250
        }

        if ($exitedWithoutChange -and !$global:needsRestart) {
            $exitCode = $process.ExitCode
            if ($exitCode -eq 0) {
                Write-Host "[Stopped] Cloud API exited. Waiting for source changes before restart..." -ForegroundColor DarkYellow
            } else {
                Write-Host "[Error] Cloud API exited (code: $exitCode). Waiting for source changes before retry..." -ForegroundColor Red
            }

            while (!$global:needsRestart) {
                Start-Sleep -Milliseconds 250
            }
        }

        if ($global:needsRestart) {
            Write-Host "`n[RESTART] Change detected! Killing process tree and restarting...`n" -ForegroundColor Yellow
        }

        # 5. Cleanup children
        if (!$process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
        
        # Kill everything on the port again to be sure (dotnet run often leaves children)
        $staleProcesses = Get-NetTCPConnection -LocalPort $cloudPort -State Listen -ErrorAction SilentlyContinue | Select-Object -ExpandProperty OwningProcess
        foreach ($procId in $staleProcesses) {
            Stop-Process -Id $procId -Force -ErrorAction SilentlyContinue
        }
    }
}
finally {
    # Cleanup Watcher
    $watcher.EnableRaisingEvents = $false
    $watcher.Dispose()
    foreach ($h in $handlers) { Unregister-Event -SourceIdentifier $h.Name -ErrorAction SilentlyContinue }
}
