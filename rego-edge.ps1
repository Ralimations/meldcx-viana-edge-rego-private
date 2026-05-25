param (
    [Parameter(Position=0)]
    [ValidateSet("status", "logs", "diag", "version", "help")]
    $Command = "help",

    [Parameter(Position=1)]
    $Target = "agent", # For logs

    [switch]$Raw
)

$PSScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Definition

function Write-Header {
    Write-Host @"
    ____  ______ ______ ____  
   / __ \/ ____// ____// __ \ 
  / /_/ // __/  / / __ / / / / 
 / _, _// /___ / /_/ // /_/ /  
/_/ |_|/_____/ \____/ \____/   
 ----------------------------------------------
  VianaRego EDGE Diagnostic CLI      v1.0
 ----------------------------------------------
"@ -ForegroundColor Cyan
    Write-Host ""
}

function Get-RegistryValue($name) {
    try {
        $val = Get-ItemProperty -Path "HKLM:\SOFTWARE\MeldCX\VianaRego" -Name $name -ErrorAction SilentlyContinue
        return $val.$name
    } catch { return $null }
}

function Test-TcpEndpoint($remoteHost, $port) {
    if ([string]::IsNullOrWhiteSpace($remoteHost)) {
        return $false
    }

    try {
        return Test-NetConnection -ComputerName $remoteHost -Port $port -InformationLevel Quiet -WarningAction SilentlyContinue
    } catch {
        return $false
    }
}

function Get-UrlHostPort($url) {
    if ([string]::IsNullOrWhiteSpace($url)) {
        return $null
    }

    $uri = $null

    if (-not [Uri]::TryCreate($url, [UriKind]::Absolute, [ref]$uri)) {
        $normalized = if ($url -match "^[a-zA-Z][a-zA-Z0-9+.-]*://") { $url } else { "http://$url" }
        if (-not [Uri]::TryCreate($normalized, [UriKind]::Absolute, [ref]$uri)) {
            return $null
        }
    }

    $port = if ($uri.IsDefaultPort) {
        if ($uri.Scheme -eq "https") { 443 } else { 80 }
    } else {
        $uri.Port
    }

    return @{
        Host = $uri.Host
        Port = $port
    }
}

function Show-Status {
    Write-Host "[System Status]" -ForegroundColor Yellow
    
    # 1. Service Status
    $svc = Get-Service "VianaRego" -ErrorAction SilentlyContinue
    if ($svc) {
        $color = if ($svc.Status -eq "Running") { "Green" } else { "Red" }
        Write-Host "  Service: VianaRego is $($svc.Status)" -ForegroundColor $color
    } else {
        Write-Host "  Service: VianaRego NOT INSTALLED" -ForegroundColor Red
    }

    # 2. Process Tree
    Write-Host "`n[Process Tree]" -ForegroundColor Yellow
    $processes = @("VianaRego.Edge.Agent", "VianaRego.Edge.Watchdog", "VianaRego.Edge.Telemetry")
    foreach ($p in $processes) {
        $proc = Get-Process $p -ErrorAction SilentlyContinue
        if ($proc) {
            Write-Host "  RUNNING: $($p) (PID: $($proc.Id), Mem: $([Math]::Round($proc.WorkingSet64 / 1MB, 1)) MB)" -ForegroundColor Green
        } else {
            Write-Host "  STOPPED: $($p)" -ForegroundColor Gray
        }
    }

    # 3. Resource Usage
    Write-Host "`n[Resource Usage]" -ForegroundColor Yellow
    $cpu = Get-Counter '\Processor(_Total)\% Processor Time' -ErrorAction SilentlyContinue
    $mem = Get-CimInstance Win32_OperatingSystem
    Write-Host "  CPU Usage:    $([Math]::Round($cpu.CounterSamples[0].CookedValue, 1))%"
    Write-Host "  Free Memory:  $([Math]::Round($mem.FreePhysicalMemory / 1MB, 2)) GB / $([Math]::Round($mem.TotalVisibleMemorySize / 1MB, 2)) GB"
}

function Show-Logs($target) {
    # Resolve log path
    # Look in the install directory first, then fallback to common data paths
    $logDir = "C:\Program Files\MeldCX\VianaRego\logs"
    if (!(Test-Path $logDir)) {
        $logDir = Join-Path $PSScriptRoot "logs"
    }

    $logFile = ""
    $filter = $null
    
    switch ($target.ToLower()) {
        "agent"     { $logFile = Join-Path $logDir "EdgeAgent.log" }
        "watchdog"  { 
            $f = Get-ChildItem -Path $logDir -Filter "*Watchdog*.log" -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName 
            if ($f) { $logFile = $f } else { $logFile = Join-Path $logDir "EdgeAgent.log"; $filter = "\[Watchdog\]" }
        }
        "telemetry" { 
            $f = Get-ChildItem -Path $logDir -Filter "*Telemetry*.log" -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName 
            if ($f) { $logFile = $f } else { $logFile = Join-Path $logDir "EdgeAgent.log"; $filter = "\[Telemetry\]" }
        }
        Default     { 
            # Search for module logs in data folder if possible
            $logFile = Get-ChildItem -Path $logDir -Filter "*$target*.log" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
        }
    }

    if ($logFile -and (Test-Path $logFile)) {
        Write-Host "[Logs] Tailing $logFile" -NoNewline -ForegroundColor Cyan
        if ($filter) { Write-Host " (Filter: '$filter')" -NoNewline -ForegroundColor Yellow }
        if (-not $Raw -and $target.ToLower() -eq "agent") { Write-Host " (Quiet Mode)" -NoNewline -ForegroundColor DarkGray }
        Write-Host " (Ctrl+C to stop)..." -ForegroundColor Cyan

        $excludePatterns = @()
        if (-not $Raw -and $target.ToLower() -eq "agent") {
            $excludePatterns += "\[Watchdog\] Performance: CPU .* RAM .*"
            $excludePatterns += "\[Reconciler\] Reconciling State \(Version: .*"
            $excludePatterns += "\[Reconciler\] Reconciliation Complete for Version .*"
            $excludePatterns += "\[HardwareCollector\] Hardware already scanned .* Skipping\."
            $excludePatterns += "\[HardwareCollector\] Starting hardware collection\.\.\."
            $excludePatterns += "\[Telemetry\] Edge Telemetry Service starting .*"
            $excludePatterns += "\[Watchdog\] Edge Watchdog \(Supervision Mode\) starting\.\.\."
        }

        Get-Content $logFile -Wait -Tail 20 | ForEach-Object {
            $line = "$_"
            if ($filter -and $line -notmatch $filter) { return }

            foreach ($pattern in $excludePatterns) {
                if ($line -match $pattern) {
                    return
                }
            }

            Write-Output $line
        }
    } else {
        Write-Host "[Error] Could not find log file for '$target' in $logDir" -ForegroundColor Red
        Write-Host "Available logs:" -ForegroundColor Gray
        Get-ChildItem -Path $logDir -Filter "*.log" | Select-Object Name | ForEach-Object { Write-Host "  - $($_.Name)" }
    }
}

function Run-Diag {
    Write-Host "[Diagnostics]" -ForegroundColor Yellow

    $mqttHost = Get-RegistryValue "MQTT_HOST"
    $mqttPortRaw = Get-RegistryValue "MQTT_PORT"
    $mqttUser = Get-RegistryValue "MQTT_USER"
    $mqttPass = Get-RegistryValue "MQTT_PASSWORD"
    $deviceId = Get-RegistryValue "DEVICE_ID"
    $deviceToken = Get-RegistryValue "DEVICE_TOKEN"

    $mqttPort = 8883
    if ($mqttPortRaw) {
        $parsedPort = 0
        if ([int]::TryParse($mqttPortRaw, [ref]$parsedPort)) {
            $mqttPort = $parsedPort
        }
    }

    # 1. Runtime config check (current edge behavior)
    Write-Host "  1. Runtime Config:         " -NoNewline
    if ([string]::IsNullOrWhiteSpace($mqttHost)) {
        Write-Host "FAILED (MQTT_HOST missing in HKLM\SOFTWARE\MeldCX\VianaRego)" -ForegroundColor Red
    } else {
        $identitySource = if ($deviceId) { "DEVICE_ID" } elseif ($deviceToken) { "DEVICE_TOKEN (legacy)" } else { "auto-generated machine ID" }
        Write-Host "OK (MQTT: $mqttHost`:$mqttPort, Identity: $identitySource)" -ForegroundColor Green
        if ([string]::IsNullOrWhiteSpace($mqttUser) -or [string]::IsNullOrWhiteSpace($mqttPass)) {
            Write-Host "     Note: MQTT_USER/MQTT_PASSWORD not found in registry." -ForegroundColor Yellow
        }
    }

    # 2. MQTT reachability
    Write-Host "  2. MQTT Connectivity:      " -NoNewline
    if ([string]::IsNullOrWhiteSpace($mqttHost)) {
        Write-Host "SKIPPED (missing MQTT_HOST)" -ForegroundColor Yellow
    } else {
        $mqttReachable = Test-TcpEndpoint $mqttHost $mqttPort
        if ($mqttReachable) {
            Write-Host "OK ($mqttHost`:$mqttPort reachable)" -ForegroundColor Green
        } else {
            Write-Host "FAILED ($mqttHost`:$mqttPort unreachable)" -ForegroundColor Red
        }
    }

    # 3. Data storage probe
    Write-Host "  3. Data Storage:           " -NoNewline
    $candidateDirs = @(
        (Join-Path $PSScriptRoot "data"),
        (Join-Path $env:ProgramData "MeldCX\VianaRego"),
        (Join-Path $env:LOCALAPPDATA "MeldCX\VianaRego")
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    $writablePath = $null
    foreach ($dir in $candidateDirs) {
        try {
            if (-not (Test-Path $dir)) {
                New-Item -Path $dir -ItemType Directory -Force | Out-Null
            }

            $probe = Join-Path $dir "diag_write_probe.tmp"
            "ok" | Set-Content -Path $probe -Encoding ascii -ErrorAction Stop
            Remove-Item $probe -Force -ErrorAction SilentlyContinue
            $writablePath = $dir
            break
        } catch {
            continue
        }
    }

    if ($writablePath) {
        Write-Host "OK (Writable: $writablePath)" -ForegroundColor Green
    } else {
        Write-Host "FAILED (No writable data directory found)" -ForegroundColor Red
    }
}

switch ($Command) {
    "status"  { Show-Status }
    "logs"    { Show-Logs $Target }
    "diag"    { Run-Diag }
    "version" { Write-Header; Write-Host "  Version: 1.0.0-edge`n" }
    "help"    { 
        Write-Header
        Write-Host "Available Commands:" -ForegroundColor Yellow
        Write-Host "  status        - Check service and process health"
        Write-Host "  logs [name]   - Tail local logs (agent, watchdog, telemetry)"
        Write-Host "  logs agent    - Tail EdgeAgent.log in quiet mode (default)"
        Write-Host "  logs agent -Raw - Tail EdgeAgent.log without quiet filtering"
        Write-Host "  diag          - Run connectivity and configuration tests"
        Write-Host ""
    }
}
