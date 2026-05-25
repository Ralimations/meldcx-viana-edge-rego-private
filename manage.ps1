param (
    [Parameter(Position=0)]
    [ValidateSet(
        "list", "get", "add-module", "start-module", "stop-module", "kill-process", "revive-module", "remove-module", "clear-modules",
        "dashboard", "inventory", "adopt-local", "adopt-discovered", 
        "rollback", "convert-to-managed", "logs", "clone-state", "hardware", "prune-devices",
        "start", "run-cloud", "version", "help"
    )]$Command = "help",


    [Parameter(Position=1)]
    $DeviceId,

    [Parameter(Position=2)]
    $Arg1, 
    [Parameter(Position=3)]
    $Arg2, 
    [Parameter(Position=4)]
    $Arg3,
    [Parameter(Position=5)]
    $Arg4, 
    [Parameter(Position=6)]
    $Arg5,
    
    [switch]$Follow,
    [switch]$All,
    [switch]$Apply,
    [switch]$Active,
    [switch]$Replace,
    [switch]$v
)

$ProgressPreference = 'SilentlyContinue'

$BaseUrl = $env:VIANA_API_URL
if (!$BaseUrl) {
    $BaseUrl = "http://localhost:5074/api/devices"
}

function Write-Header {
    Write-Host @"
    ____  ______ ______ ____  
   / __ \/ ____// ____// __ \ 
  / /_/ // __/  / / __ / / / / 
 / _, _// /___ / /_/ // /_/ /  
/_/ |_|/_____/ \____/ \____/   
"@ -ForegroundColor Cyan
    Write-Host " ----------------------------------------------" -ForegroundColor DarkGray
    Write-Host "  VianaRego Management CLI          " -ForegroundColor Gray -NoNewline
    Write-Host " v1.2" -ForegroundColor DarkGray
    Write-Host " ----------------------------------------------" -ForegroundColor DarkGray
    Write-Host ""
}

function Get-FriendlyDate ($dateStr) {
    if (!$dateStr) { return "Never" }
    try {
        # Force the parser to assume UTC if no offset is present
        $date = [DateTime]::Parse($dateStr, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::AdjustToUniversal -bor [System.Globalization.DateTimeStyles]::AssumeUniversal)
        $diff = [DateTime]::UtcNow - $date
        
        if ($diff.TotalSeconds -lt 20) { return "Just now" }
        if ($diff.TotalMinutes -lt 1) { return "$([int]$diff.TotalSeconds)s ago" }
        if ($diff.TotalHours -lt 1) { return "$([int]$diff.TotalMinutes)m ago" }
        return $date.ToString("yyyy-MM-dd HH:mm:ss")
    } catch {
        return "Unknown"
    }
}

function Get-Status ($dateStr) {
    if (!$dateStr) { return "UNKNOWN" }
    try {
        # Force the parser to assume UTC if no offset is present
        $date = [DateTime]::Parse($dateStr, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::AdjustToUniversal -bor [System.Globalization.DateTimeStyles]::AssumeUniversal)
        $diff = [DateTime]::UtcNow - $date
        if ($diff.TotalMinutes -lt 1) { return "ONLINE" }
        return "OFFLINE"
    } catch {
        return "ERROR"
    }
}

function Parse-UtcDate($dateStr) {
    if (!$dateStr) { return $null }
    try {
        return [DateTime]::Parse($dateStr, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::AdjustToUniversal -bor [System.Globalization.DateTimeStyles]::AssumeUniversal)
    } catch {
        return $null
    }
}

function Resolve-DeviceId ($id) {
    if (!$id) { return $null }
    
    # Try to fetch device list to resolve alias
    try {
        $devices = Invoke-RestMethod -Method GET -Uri $BaseUrl -ErrorAction SilentlyContinue
        if (!$devices) { return $id }

        # 1. Match Sequential ID (Exact)
        $match = $devices | Where-Object { "$($_.sequentialId)" -eq "$id" } | Select-Object -First 1
        if ($match) { return $match.id }
        
        # 2. Match Name (Case-insensitive)
        $match = $devices | Where-Object { $_.name -eq "$id" } | Select-Object -First 1
        if ($match) { return $match.id }

        # 3. Match MAC (Case-insensitive)
        $match = $devices | Where-Object { $_.macAddress -eq "$id" } | Select-Object -First 1
        if ($match) { return $match.id }

        # 4. Fallback: Return original (API will 404 if invalid UUID)
        return $id
    } catch {
        return $id
    }
}

function Get-StartCommandParts($startCommand) {
    $command = "$startCommand".Trim()
    if ([string]::IsNullOrWhiteSpace($command)) {
        return [PSCustomObject]@{ Executable = ""; Arguments = "" }
    }

    if ($command -match '^\s*"([^"]+)"\s*(.*)$') {
        return [PSCustomObject]@{
            Executable = "$($matches[1])".Trim()
            Arguments  = "$($matches[2])".Trim()
        }
    }

    if ($command -match '^\s*(.+?\.(?:exe|cmd|bat|ps1|com))\s*(.*)$') {
        return [PSCustomObject]@{
            Executable = "$($matches[1])".Trim()
            Arguments  = "$($matches[2])".Trim()
        }
    }

    if ($command -match '^\s*(\S+)\s*(.*)$') {
        return [PSCustomObject]@{
            Executable = "$($matches[1])".Trim()
            Arguments  = "$($matches[2])".Trim()
        }
    }

    return [PSCustomObject]@{ Executable = $command; Arguments = "" }
}

function Build-StartCommand([string]$executable, [string]$arguments) {
    if ([string]::IsNullOrWhiteSpace($executable)) {
        return ""
    }

    $exePart = if ($executable -match '\s' -and $executable -notmatch '^".*"$') {
        '"' + $executable + '"'
    } else {
        $executable
    }

    if ([string]::IsNullOrWhiteSpace($arguments)) {
        return $exePart
    }

    return "$exePart $arguments".Trim()
}

function Get-DeviceProcessCatalog($device) {
    if (!$device -or [string]::IsNullOrWhiteSpace($device.reportedStateJson)) {
        return @()
    }

    try {
        $status = $device.reportedStateJson | ConvertFrom-Json
        $raw = @()
        if ($status.AllProcesses) { $raw += @($status.AllProcesses) }
        if ($status.DiscoveredProcesses) { $raw += @($status.DiscoveredProcesses) }

        $dedupe = @{}
        foreach ($proc in $raw) {
            $path = "$($proc.Path)".Trim()
            if ([string]::IsNullOrWhiteSpace($path)) { continue }

            $key = $path.ToLowerInvariant()
            if ($dedupe.ContainsKey($key)) { continue }

            $dedupe[$key] = [PSCustomObject]@{
                Name     = "$($proc.Name)".Trim()
                Path     = $path
                FileName = [System.IO.Path]::GetFileName($path)
                BaseName = [System.IO.Path]::GetFileNameWithoutExtension($path)
            }
        }

        return @($dedupe.Values)
    } catch {
        return @()
    }
}

function Resolve-AdoptedCommandForTarget($module, $targetProcesses) {
    $moduleName = "$($module.Name)".Trim()
    $parts = Get-StartCommandParts $module.StartCommand
    $sourceExe = "$($parts.Executable)".Trim()
    $sourceExeName = if ([string]::IsNullOrWhiteSpace($sourceExe)) { "" } else { [System.IO.Path]::GetFileName($sourceExe) }
    $sourceExeBase = if ([string]::IsNullOrWhiteSpace($sourceExeName)) { "" } else { [System.IO.Path]::GetFileNameWithoutExtension($sourceExeName) }

    $candidatePaths = @()
    foreach ($proc in $targetProcesses) {
        $procName = "$($proc.Name)".Trim()
        $procFile = "$($proc.FileName)".Trim()
        $procBase = "$($proc.BaseName)".Trim()

        $isMatch = $false
        if (-not [string]::IsNullOrWhiteSpace($moduleName)) {
            if ($procName -ieq $moduleName -or $procBase -ieq $moduleName) {
                $isMatch = $true
            }
        }

        if (-not $isMatch -and -not [string]::IsNullOrWhiteSpace($sourceExeName)) {
            if ($procFile -ieq $sourceExeName) {
                $isMatch = $true
            }
        }

        if (-not $isMatch -and -not [string]::IsNullOrWhiteSpace($sourceExeBase)) {
            if ($procBase -ieq $sourceExeBase -or $procName -ieq $sourceExeBase) {
                $isMatch = $true
            }
        }

        if ($isMatch) {
            $candidatePaths += "$($proc.Path)"
        }
    }

    $uniquePaths = @($candidatePaths | Sort-Object -Unique)
    if ($uniquePaths.Count -eq 1) {
        return [PSCustomObject]@{
            Success      = $true
            StartCommand = Build-StartCommand -executable $uniquePaths[0] -arguments $parts.Arguments
            MatchedPath  = $uniquePaths[0]
            Reason       = ""
        }
    }

    if ($uniquePaths.Count -eq 0) {
        return [PSCustomObject]@{
            Success      = $false
            StartCommand = $null
            MatchedPath  = $null
            Reason       = "no matching process was found on target"
        }
    }

    $preview = ($uniquePaths | Select-Object -First 3) -join ", "
    return [PSCustomObject]@{
        Success      = $false
        StartCommand = $null
        MatchedPath  = $null
        Reason       = "multiple matching paths found: $preview"
    }
}

function Show-Help {
    Write-Host "Available Commands:" -ForegroundColor Yellow
    Write-Host "  list                          - Show all registered devices" -ForegroundColor White
    Write-Host "  start cloud                   - Launch Cloud API dev environment" -ForegroundColor White
    Write-Host "  dashboard                     - Real-time system overview" -ForegroundColor White
    Write-Host "  get [id]                      - Detailed statistics and modules" -ForegroundColor White
    Write-Host "  inventory [id]                - Scan for unmanaged processes" -ForegroundColor White
    Write-Host "  logs [id] [module] [lines]            - Fetch remote logs" -ForegroundColor White
    Write-Host "  logs [id] [module] [lines] --follow   - Stream remote logs continuously" -ForegroundColor White
    Write-Host "  prune-devices [hrs] [--apply] - Remove stale offline devices" -ForegroundColor White
    Write-Host "  clone-state [src] [dst...] [--replace] - Copy module configuration to devices" -ForegroundColor White
    Write-Host "  hardware [id]                 - Show detailed hardware specs" -ForegroundColor White
    Write-Host ""
    Write-Host "  -- Application Management --" -ForegroundColor Cyan
    Write-Host "  add-module [id] [name] [v]... - Manually register a module" -ForegroundColor Gray
    Write-Host "  start-module [id] [name] [--active] - Start module (service mode by default)" -ForegroundColor Gray
    Write-Host "  stop-module [id] [name]       - Stop a managed module process" -ForegroundColor Gray
    Write-Host "  kill-process [id] [name]      - Kill process (ad-hoc, no config change)" -ForegroundColor Gray
    Write-Host "  revive-module [id] [name]     - Start module once (sets status to 'Manual', ignores Watchdog)" -ForegroundColor Gray
    Write-Host "  remove-module [id] [name]     - Uninstall specific module (use --all to remove all)" -ForegroundColor Gray

    Write-Host ""
    Write-Host "  -- Advanced / Recovery --" -ForegroundColor Cyan
    Write-Host "  adopt-local [id] [name] [path?]- Adopt local app (auto-detect path if omitted)" -ForegroundColor DarkGray
    Write-Host "  adopt-discovered [id] [proc]  - Manage a process found by inventory" -ForegroundColor DarkGray
    Write-Host "  rollback [id] [module]        - Revert module to previous version" -ForegroundColor DarkGray
    Write-Host "  convert-to-managed [id]...    - Upload local module to enable updates" -ForegroundColor DarkGray
    Write-Host ""
    Write-Host "Example:" -ForegroundColor Gray
    Write-Host "  .\manage.ps1 start cloud"
}

try {
    # Handle version flags
    if ($v -or $args -contains "-v" -or $args -contains "--version" -or $Command -in "version") {
        Write-Header
        Write-Host " VianaRego CLI (rego) Version: 1.2" -ForegroundColor White
        Write-Host " Build: 2026.02.13-stable-refined" -ForegroundColor Black
        Write-Host ""
        return
    }

    switch ($Command) {
        { $_ -in "start", "run-cloud" } {
            if ($Command -eq "run-cloud" -or $DeviceId -eq "cloud") {
                Write-Header
                Write-Host "[Start] Launching Viana Cloud Development Environment..." -ForegroundColor Green
                & "$PSScriptRoot\start-cloud.ps1"
                return
            }
            Write-Host "Usage: .\manage.ps1 start [target]" -ForegroundColor Cyan
            Write-Host "Available targets: cloud" -ForegroundColor Gray
            return
        }

        "list" {
            Write-Host "Active Device Registry" -ForegroundColor Yellow
            try {
                $devices = Invoke-RestMethod -Method GET -Uri $BaseUrl -ErrorAction Stop
                
                $devices | Select-Object @{Name="STATUS"; Expression={ Get-Status $_.lastSeenAt }},
                    @{Name="ID"; Expression={ $_.sequentialId }},
                    @{Name="MAC"; Expression={ $_.macAddress }},
                    @{Name="NAME"; Expression={ $_.name }},
                    @{Name="LAST SEEN"; Expression={ Get-FriendlyDate $_.lastSeenAt }} | Format-Table -AutoSize -Wrap
            } catch {
                Write-Host "Error connecting to Cloud API: $($_.Exception.Message)" -ForegroundColor Red
            }
        }

        "dashboard" {
            while($true) {
                Write-Host "LIVE SYSTEM DASHBOARD (Ctrl+C to exit)" -ForegroundColor Yellow
                Write-Host "Time: $(Get-Date -Format 'HH:mm:ss')" -ForegroundColor Gray
                Write-Host ""
                
                $devices = Invoke-RestMethod -Method GET -Uri $BaseUrl -ErrorAction Ignore
                if ($devices) {
                    $devices | Select-Object @{Name="STATUS"; Expression={ Get-Status $_.lastSeenAt }},
                        @{Name="ID"; Expression={ $_.sequentialId }},
                        @{Name="MAC"; Expression={ $_.macAddress }},
                        @{Name="NAME"; Expression={ $_.name }},
                        @{Name="LAST SEEN"; Expression={ Get-FriendlyDate $_.lastSeenAt }} | Sort-Object ID | Format-Table -AutoSize -Wrap
                } else {
                    Write-Host "Waiting for data..." -ForegroundColor Gray
                }
                
                Start-Sleep -Seconds 3
            }
        }

        "version" {
            Write-Host "VianaRego CLI (rego) Version: 1.2"
            return
        }

        "logs" {
            if (!$DeviceId) { 
            Write-Host "Usage: .\manage.ps1 logs [device-id] [module-name] [lines] [--follow]" -ForegroundColor Cyan
                Write-Host "Examples:" -ForegroundColor Gray
                Write-Host "  .\manage.ps1 logs device-01                    # View EdgeAgent logs (default)" -ForegroundColor Gray
                Write-Host "  .\manage.ps1 logs device-01 MyApp              # View specific module logs" -ForegroundColor Gray
                Write-Host "  .\manage.ps1 logs device-01 --follow           # Stream EdgeAgent logs in real-time" -ForegroundColor Gray
                Write-Host "  .\manage.ps1 logs device-01 agent 200 --follow # Stream specific module logs" -ForegroundColor Gray
                return 
            }
            
            $DeviceId = Resolve-DeviceId $DeviceId

            $rawLogArgs = @($Arg1, $Arg2, $Arg3, $Arg4, $Arg5) | Where-Object { $_ -ne $null }
            $positionalLogArgs = $rawLogArgs | Where-Object { "$_" -notmatch '^(?i)--?follow$' }
            $hasFollowToken = ($rawLogArgs | Where-Object { "$_" -match '^(?i)--?follow$' }).Count -gt 0
             
            # 1. Determine module name
            # If first arg is numeric, it's the line count and module stays default.
            $moduleName = "EdgeAgent"
            if ($positionalLogArgs.Count -ge 1 -and "$($positionalLogArgs[0])" -notmatch "^\d+$") {
                $moduleName = "$($positionalLogArgs[0])"
            }

            # 2. Determine lines count
            $linesCount = 100
            if ($positionalLogArgs.Count -ge 1 -and "$($positionalLogArgs[0])" -match "^\d+$") {
                $linesCount = [int]$positionalLogArgs[0]
            } elseif ($positionalLogArgs.Count -ge 2 -and "$($positionalLogArgs[1])" -match "^\d+$") {
                $linesCount = [int]$positionalLogArgs[1]
            }

            # 3. Determine if following
            $shouldFollow = $Follow.IsPresent -or $hasFollowToken
            
            if ($shouldFollow) {
                Write-Host "[Stream] Streaming logs from '$DeviceId' ($moduleName)... Press Ctrl+C to stop." -ForegroundColor Yellow
                Write-Host ""
                
                $followBatchSize = 50
                if ($linesCount -gt $followBatchSize) { $followBatchSize = $linesCount }
                $currentOffset = 0

                try {
                    # Bootstrap near end so follow mode does not replay the entire log history.
                    $metaUrl = "{0}/{1}/logs/{2}?offset=0&lines=1" -f $BaseUrl, $DeviceId, $moduleName
                    $meta = Invoke-RestMethod -Method GET -Uri $metaUrl -ErrorAction SilentlyContinue
                    if ($meta -and "$($meta.TotalLines)" -match "^\d+$") {
                        $totalLines = [int]$meta.TotalLines
                        $currentOffset = [Math]::Max($totalLines - $linesCount, 0)
                    }

                    while ($true) {
                        # Explicitly build the URL to prevent interpolation issues
                        $u = "{0}/{1}/logs/{2}?offset={3}&lines={4}" -f $BaseUrl, $DeviceId, $moduleName, $currentOffset, $followBatchSize
                        $url = $u
                        $data = Invoke-RestMethod -Method GET -Uri $url -ErrorAction SilentlyContinue
                        
                        if ($data -and $data.Lines -and $data.Lines.Count -gt 0) {
                            foreach ($line in $data.Lines) {
                                if ([string]::IsNullOrWhiteSpace($line)) { continue }
                                if ($line -match "Log request .* for module .* offset") { continue }
                                if ($line -match "Received message on .*/logs/request") { continue }
                                if ($line -match "Executed DbCommand") { continue }
                                if ($line -match "^\s*SELECT\s+" -or $line -match "^\s*FROM\s+") { continue }
                                if ($line -match "^MQTTnet\.Exceptions\." -or $line -match "^System\.Net\.Sockets\.SocketException") { continue }
                                if ($line -match "^\s*at\s+" -or $line -match "^\s*--- End of" -or $line -match "^ ---> ") { continue }

                                # Color code based on component (if present in line)
                                if ($line -match "\[Reconciler\]") {
                                    Write-Host $line -ForegroundColor Cyan
                                } elseif ($line -match "\[Watchdog\]") {
                                    Write-Host $line -ForegroundColor Yellow
                                } elseif ($line -match "\[StatusPublisher\]") {
                                    Write-Host $line -ForegroundColor Green
                                } elseif ($line -match "\[Reporter\]") {
                                    Write-Host $line -ForegroundColor Magenta
                                } else {
                                    Write-Host $line
                                }
                            }
                        }

                        if ($data -and "$($data.TotalLines)" -match "^\d+$") {
                            $currentOffset = [int]$data.TotalLines
                        }
                        
                        Start-Sleep -Seconds 1
                    }
                } catch {
                    if ($_.Exception.Message -notlike "*canceled*") {
                        Write-Host "Error: $($_.Exception.Message)" -ForegroundColor Red
                    }
                }
            } else {
                Write-Host "Requesting last $linesCount lines for '$moduleName' from '$DeviceId'...  " -ForegroundColor Yellow
                
                try {
                    $u = "{0}/{1}/logs/{2}?lines={3}" -f $BaseUrl, $DeviceId, $moduleName, $linesCount
                    $url = $u # assignment
                    $data = Invoke-RestMethod -Method GET -Uri $url -ErrorAction Stop
                    
                    if (!$data) {
                        Write-Host "Error: Received empty response from server." -ForegroundColor Red
                        return
                    }

                    if ($data.error) {
                        Write-Host "Device Error: $($data.error)" -ForegroundColor Red
                        return
                    }

                    $receivedCount = 0
                    if ($data.Lines) { $receivedCount = $data.Lines.Count }

                    Write-Host "--- LOG START ($receivedCount lines) ---" -ForegroundColor Gray
                    if ($data.Lines) {
                        foreach ($line in $data.Lines) {
                            Write-Host $line
                        }
                    }
                    Write-Host "--- LOG END ---" -ForegroundColor Gray
                } catch {
                    Write-Host "Error: $($_.Exception.Message)" -ForegroundColor Red
                    if ($_.Exception.Message -like "*ConvertFrom-Json*") {
                        Write-Host "Raw response may not be valid JSON." -ForegroundColor Gray
                    }
                }
            }
        }

        "provision" {
            $org = $DeviceId
            $token = $Arg1
            $hostVal = $Arg2
            $userVal = $Arg3
            $passVal = $Arg4

            if (!$org -or !$token) {
                Write-Host "Usage: .\manage.ps1 provision [OrgId] [DeviceToken] [MqttHost] [MqttUser] [MqttPass]" -ForegroundColor Cyan
                return
            }

            Write-Host "Provisioning local device registry..." -ForegroundColor Yellow
            $regPath = "HKLM:\SOFTWARE\MeldCX\VianaRego"
            if (!(Test-Path $regPath)) { New-Item -Path $regPath -Force | Out-Null }
            
            Set-ItemProperty -Path $regPath -Name "ORG_ID" -Value $org -Force
            Set-ItemProperty -Path $regPath -Name "DEVICE_TOKEN" -Value $token -Force
            if ($hostVal) { Set-ItemProperty -Path $regPath -Name "MQTT_HOST" -Value $hostVal -Force }
            if ($userVal) { Set-ItemProperty -Path $regPath -Name "MQTT_USER" -Value $userVal -Force }
            if ($passVal) { Set-ItemProperty -Path $regPath -Name "MQTT_PASSWORD" -Value $passVal -Force }
            Set-ItemProperty -Path $regPath -Name "DEVICE_ID" -Value $token -Force

            Write-Host "[Success] Registry updated. Please restart 'VianaRego' service." -ForegroundColor Green
        }

        "adopt-local" {
            if (!$DeviceId -or !$Arg1) { 
                Write-Host "Usage: .\manage.ps1 adopt-local [id] [name] [path(optional)]" -ForegroundColor Cyan
                Write-Host "Example auto-detect: .\manage.ps1 adopt-local 1 brave" -ForegroundColor Gray
                Write-Host "Example explicit:    .\manage.ps1 adopt-local 1 brave `"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe`"" -ForegroundColor Gray
                return 
            }
            $DeviceId = Resolve-DeviceId $DeviceId
            $moduleName = "$Arg1"

            # If a path is supplied, use it directly. Otherwise auto-detect from reported process inventory.
            $fullPath = $null
            if ($Arg2) {
                $fullPath = "$Arg2"
                if ($Arg3) { $fullPath += " $Arg3" }
                if ($Arg4) { $fullPath += " $Arg4" }
                if ($Arg5) { $fullPath += " $Arg5" }
                $fullPath = $fullPath.Trim()
            } else {
                $d = Invoke-RestMethod -Method GET -Uri "$BaseUrl/$DeviceId" -ErrorAction Stop
                if (!$d.reportedStateJson) {
                    Write-Host "No process inventory available for this device yet." -ForegroundColor Yellow
                    Write-Host "Ensure the app is running, then retry." -ForegroundColor Gray
                    return
                }

                $status = $d.reportedStateJson | ConvertFrom-Json
                $searchPool = @()
                if ($status.AllProcesses) { $searchPool += @($status.AllProcesses) }
                if ($status.DiscoveredProcesses) { $searchPool += @($status.DiscoveredProcesses) }

                if ($searchPool.Count -eq 0) {
                    Write-Host "No process data available to auto-detect path." -ForegroundColor Yellow
                    Write-Host "Ensure the app is running, then retry." -ForegroundColor Gray
                    return
                }

                $moduleNameLc = $moduleName.ToLowerInvariant()
                $exact = @($searchPool | Where-Object {
                    $name = "$($_.Name)"
                    $path = "$($_.Path)"
                    $exe = if ($path) { [System.IO.Path]::GetFileNameWithoutExtension($path) } else { "" }
                    ($name -and $name.ToLowerInvariant() -eq $moduleNameLc) -or
                    ($exe -and $exe.ToLowerInvariant() -eq $moduleNameLc)
                })
                $candidates = if ($exact.Count -gt 0) { $exact } else {
                    @($searchPool | Where-Object {
                        $name = "$($_.Name)"
                        $path = "$($_.Path)"
                        $exe = if ($path) { [System.IO.Path]::GetFileNameWithoutExtension($path) } else { "" }
                        ($name -and $name.ToLowerInvariant().Contains($moduleNameLc)) -or
                        ($path -and $path.ToLowerInvariant().Contains($moduleNameLc)) -or
                        ($exe -and $exe.ToLowerInvariant().Contains($moduleNameLc))
                    })
                }

                $candidates = @($candidates | Where-Object { $_.Path })
                if ($candidates.Count -eq 0) {
                    Write-Host "Could not auto-detect a path for '$moduleName' on device '$DeviceId'." -ForegroundColor Yellow
                    Write-Host "Ensure it is running, or pass explicit path: adopt-local [id] [name] [path]" -ForegroundColor Gray
                    return
                }

                $uniqueMatches = @($candidates | Group-Object -Property Path | ForEach-Object { $_.Group[0] })
                if ($uniqueMatches.Count -gt 1) {
                    Write-Host "Multiple candidate paths found for '$moduleName'. Please choose explicit path:" -ForegroundColor Yellow
                    $uniqueMatches | Select-Object Name, Path | Format-Table -AutoSize -Wrap
                    return
                }

                $fullPath = "$($uniqueMatches[0].Path)".Trim()
                Write-Host "Auto-detected path: $fullPath" -ForegroundColor DarkGray
            }
            
            $module = @{ 
                Name=$moduleName
                Version="1.0.0-adopted"
                DownloadUrl=""
                StartCommand=$fullPath
                IsManaged=$false
            }
            Write-Host "Adopting local service '$moduleName' at '$fullPath'..." -ForegroundColor Green
            Invoke-RestMethod -Method POST -Uri "$BaseUrl/$DeviceId/modules" -Body ($module | ConvertTo-Json) -ContentType "application/json" -ErrorAction Stop
            Write-Host "Adoption intent sent. Watchdog will now track this path." -ForegroundColor Gray
        }

        "adopt-discovered" {
            if (!$DeviceId -or !$Arg1) { 
                Write-Host "Usage: .\manage.ps1 adopt-discovered [device-id] [process-name]" -ForegroundColor Cyan
                Write-Host "Example: .\manage.ps1 adopt-discovered device-02 MyApp" -ForegroundColor DarkGray
                return 
            }
            
            $DeviceId = Resolve-DeviceId $DeviceId
            $processName = $Arg1
            $d = Invoke-RestMethod -Method GET -Uri "$BaseUrl/$DeviceId" -ErrorAction Stop
            
            if (!$d.reportedStateJson) {
                Write-Host "???????????? No inventory data available for this device yet." -ForegroundColor Yellow
                return
            }

            $status = $d.reportedStateJson | ConvertFrom-Json

            $searchPool = @()
            if ($status.DiscoveredProcesses) { $searchPool += @($status.DiscoveredProcesses) }
            if ($status.AllProcesses) { $searchPool += @($status.AllProcesses) }

            if ($searchPool.Count -eq 0) {
                Write-Host "???????????? No process inventory available yet. Try running 'inventory $DeviceId --all' first." -ForegroundColor Yellow
                return
            }

            # Match by process name OR executable path (case-insensitive).
            $found = $searchPool | Where-Object {
                $name = "$($_.Name)"
                $path = "$($_.Path)"
                $exe = ""
                if ($path) { $exe = [System.IO.Path]::GetFileNameWithoutExtension($path) }
                $name -like "*$processName*" -or $path -like "*$processName*" -or $exe -like "*$processName*"
            }
            
            if (!$found) {
                Write-Host "?????? No process found matching '$processName'" -ForegroundColor Red
                Write-Host "Available processes:" -ForegroundColor Gray
                $searchPool | Where-Object { $_.Name } | Select-Object -ExpandProperty Name -Unique | Select-Object -First 10 | ForEach-Object { Write-Host "  - $_" -ForegroundColor DarkGray }
                return
            }

            $foundWithPath = @($found | Where-Object { $_.Path })
            if ($foundWithPath.Count -eq 0) {
                Write-Host "?????? Process '$processName' was found, but none of the matches expose an executable path." -ForegroundColor Red
                Write-Host "Try running manage.ps1 from an elevated shell, then retry." -ForegroundColor Gray
                return
            }

            # Multiple PIDs can share one executable path (e.g. browser subprocesses).
            # Collapse to unique path so adoption can still proceed.
            $uniqueMatches = @($foundWithPath | Group-Object -Property Path | ForEach-Object { $_.Group[0] })

            if ($uniqueMatches.Count -gt 1) {
                Write-Host "???????????? Multiple processes match '$processName':" -ForegroundColor Yellow
                $uniqueMatches | Select-Object Name, Path | Format-Table -AutoSize -Wrap
                Write-Host "Please be more specific or use 'adopt-local' with the exact path." -ForegroundColor Gray
                return
            }

            $process = $uniqueMatches[0]

            Write-Host "????????? Found: $($process.Name) at $($process.Path)" -ForegroundColor Cyan
            $module = @{ 
                Name = $process.Name; 
                Version = "1.0.0-discovered"; 
                DownloadUrl = ""; 
                StartCommand = $process.Path;
                IsManaged = $false
            }
            Write-Host "Adopting '$($process.Name)'..." -ForegroundColor Green
            Invoke-RestMethod -Method POST -Uri "$BaseUrl/$DeviceId/modules" -Body ($module | ConvertTo-Json) -ContentType "application/json" -ErrorAction Stop
            Write-Host "??????? Adoption complete. Watchdog will now monitor this process." -ForegroundColor Green
        }

        "rollback" {
            if (!$DeviceId -or !$Arg1) {
                Write-Host "Usage: .\manage.ps1 rollback [device-id] [module-name]" -ForegroundColor Cyan
                Write-Host "Example: .\manage.ps1 rollback device-02 MyApp" -ForegroundColor DarkGray
                return
            }
            $DeviceId = Resolve-DeviceId $DeviceId
            $moduleName = $Arg1
            Write-Host "?????????? Initiating manual rollback for '$moduleName'..." -ForegroundColor Yellow

            # This updates the module's desired state to trigger reconciler
            # The actual version history query happens Edge-side
            # For now, send a special "rollback" request
            try {
                $body = @{ action = "rollback"; moduleName = $moduleName } | ConvertTo-Json
                Invoke-RestMethod -Method POST -Uri "$BaseUrl/$DeviceId/rollback" -Body $body -ContentType "application/json" -ErrorAction Stop
                Write-Host "??????? Rollback request sent. Check device logs for details." -ForegroundColor Green
            } catch {
                Write-Host "?????? Rollback request failed: $($_.Exception.Message)" -ForegroundColor Red
                Write-Host "Note: Rollback API endpoint needs to be implemented in Cloud API" -ForegroundColor Gray
            }
        }

        "convert-to-managed" {
            if (!$DeviceId -or !$Arg1 -or !$Arg2) {
                Write-Host "Usage: .\manage.ps1 convert-to-managed [device-id] [module-name] [download-url]" -ForegroundColor Cyan
                Write-Host "Example: .\manage.ps1 convert-to-managed device-02 MyApp https://storage/myapp-v1.0.0.zip" -ForegroundColor DarkGray
                Write-Host "" -ForegroundColor Gray
                Write-Host "This uploads the current adopted module version to enable remote updates and rollback." -ForegroundColor Gray
                return
            }

            $DeviceId = Resolve-DeviceId $DeviceId
            $moduleName = $Arg1
            $downloadUrl = $Arg2

            Write-Host "?????????? Converting '$moduleName' from adopted to managed..." -ForegroundColor Yellow

            # Update the module to set IsManaged=true and add downloadUrl
            $module = @{
                Name = $moduleName
                Version = "1.0.0"  # First managed version
                DownloadUrl = $downloadUrl
                StartCommand = $Arg3 -or "app.exe"
                IsManaged = $true
            }

            Write-Host "Updating module definition..." -ForegroundColor Green
            Invoke-RestMethod -Method POST -Uri "$BaseUrl/$DeviceId/modules" -Body ($module | ConvertTo-Json) -ContentType "application/json" -ErrorAction Stop
            Write-Host "??????? Conversion complete. Module is now managed with rollback support." -ForegroundColor Green
        }

        "inventory" {
            if (!$DeviceId) { Write-Host "Error: Device ID required" -ForegroundColor Red; return }
            $DeviceId = Resolve-DeviceId $DeviceId
            $d = Invoke-RestMethod -Method GET -Uri "$BaseUrl/$DeviceId" -ErrorAction Stop
            
            if (!$d.reportedStateJson) {
                Write-Host "???????????? No inventory data available for this device yet." -ForegroundColor Yellow
                return
            }

            $status = $d.reportedStateJson | ConvertFrom-Json
            $showAll = $All.IsPresent -or $Arg1 -eq "--all" -or $Arg1 -eq "-a"
            
            if ($showAll) {
                if (!$status.AllProcesses -or $status.AllProcesses.Count -eq 0) {
                    Write-Host "???????????? No process data available." -ForegroundColor Yellow
                    return
                }
                Write-Host ""
                Write-Host "?????????? All Running Processes ($($status.AllProcesses.Count))" -ForegroundColor Cyan
                Write-Host "??????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????" -ForegroundColor DarkGray
                $status.AllProcesses | Select-Object Name, Path, Id | Format-Table -AutoSize -Wrap
            } else {
                if (!$status.DiscoveredProcesses -or $status.DiscoveredProcesses.Count -eq 0) {
                    Write-Host "????????? No discovered processes (use --all to see all)" -ForegroundColor DarkGray
                    return
                }
                Write-Host ""
                Write-Host "????????? Discovered Processes (not yet managed)" -ForegroundColor Yellow
                Write-Host "Tip: Use 'inventory $DeviceId --all' to see all processes" -ForegroundColor DarkGray
                Write-Host ""
                $status.DiscoveredProcesses | Select-Object Name, Path, Id | Format-Table -AutoSize -Wrap
            }
        }

        "get" {
            if (!$DeviceId) { Write-Host "Error: Device ID required" -ForegroundColor Red; return }
            $DeviceId = Resolve-DeviceId $DeviceId
            $d = Invoke-RestMethod -Method GET -Uri "$BaseUrl/$DeviceId" -ErrorAction Stop
            
            Write-Host "Device Details: $($d.name) [$($d.id)]" -ForegroundColor Yellow
            Write-Host "------------------------------------------------"
            Write-Host "Status:      $(Get-Status $d.lastSeenAt)"
            Write-Host "Last Seen:   $(Get-FriendlyDate $d.lastSeenAt)"
            Write-Host "OS Version:  $($d.osVersion)"
            Write-Host ""

            if ($d.reportedStateJson) {
                $status = $d.reportedStateJson | ConvertFrom-Json
                Write-Host "Hardware Context:" -ForegroundColor Green
                
                $mem = "Unknown"
                if ($status.System.TotalMemoryGb) { $mem = "$($status.System.TotalMemoryGb) GB" }
                
                Write-Host "  RAM:       $mem"
                Write-Host "  Model:     $($status.System.Model)"
                if ($status.DiscoveredProcesses) {
                    Write-Host "  Inventory: $($status.DiscoveredProcesses.Count) unmanaged items" -ForegroundColor DarkGray
                }
                Write-Host ""
            }

            if ($d.desiredStateJson) { 
                $state = $d.desiredStateJson | ConvertFrom-Json
                Write-Host "Managed Modules (v$($state.Version))" -ForegroundColor Yellow
                $state.Modules | Select-Object @{Name="MODULE"; Expression={ $_.name }},
                    @{Name="VERSION"; Expression={ $_.version }},
                    @{Name="COMMAND / PATH"; Expression={ $_.startCommand }} | Format-Table -AutoSize -Wrap
            }
        }

        "add-module" {
            if (!$DeviceId -or !$Arg1) { Write-Host "Error: DeviceId and ModuleName required" -ForegroundColor Red; return }
            $DeviceId = Resolve-DeviceId $DeviceId
            $module = @{ Name=$Arg1; Version=$Arg2; DownloadUrl=$Arg3; StartCommand=$Arg4 }
            Write-Host "Adding module '$Arg1' to $DeviceId..." -ForegroundColor Green
            Invoke-RestMethod -Method POST -Uri "$BaseUrl/$DeviceId/modules" -Body ($module | ConvertTo-Json) -ContentType "application/json" -ErrorAction Stop
        }

        "start-module" {
            if (!$DeviceId -or !$Arg1) { Write-Host "Error: DeviceId and ModuleName required" -ForegroundColor Red; return }
            $DeviceId = Resolve-DeviceId $DeviceId
            $moduleName = [uri]::EscapeDataString("$Arg1")
            $launchMode = "service"

            if ($Active.IsPresent -or ($Arg2 -and "$Arg2".ToLowerInvariant() -in @("--active", "-active", "active"))) {
                $launchMode = "active"
            } elseif ($Arg2 -and "$Arg2".ToLowerInvariant() -in @("--service", "-service", "service")) {
                $launchMode = "service"
            }

            $startUri = "$BaseUrl/$DeviceId/modules/$moduleName/start"
            if ($launchMode -eq "active") {
                $startUri += "?launchMode=active"
            }

            Write-Host "Starting module '$Arg1' on $DeviceId (mode: $launchMode)..." -ForegroundColor Green
            Invoke-RestMethod -Method POST -Uri $startUri -ErrorAction Stop | Out-Null
            Write-Host "Start request sent (launch mode: $launchMode)." -ForegroundColor Gray
        }

        "stop-module" {
            if (!$DeviceId -or !$Arg1) { Write-Host "Error: DeviceId and ModuleName required" -ForegroundColor Red; return }
            $DeviceId = Resolve-DeviceId $DeviceId
            $moduleName = [uri]::EscapeDataString("$Arg1")
            Write-Host "Stopping module '$Arg1' on $DeviceId..." -ForegroundColor Yellow
            Invoke-RestMethod -Method POST -Uri "$BaseUrl/$DeviceId/modules/$moduleName/stop" -ErrorAction Stop | Out-Null
            Write-Host "Stop request sent." -ForegroundColor Gray
        }

        "kill-process" {
            if (!$DeviceId -or !$Arg1) { Write-Host "Error: DeviceId and ProcessName required" -ForegroundColor Red; return }
            $DeviceId = Resolve-DeviceId $DeviceId
            $processName = [uri]::EscapeDataString("$Arg1")
            Write-Host "Sending ad-hoc kill request for process '$Arg1' on $DeviceId..." -ForegroundColor Red
            Invoke-RestMethod -Method POST -Uri "$BaseUrl/$DeviceId/modules/$processName/kill" -ErrorAction Stop | Out-Null
            Write-Host "Kill request sent. (Persistent state unchanged)" -ForegroundColor Gray
        }

        "revive-module" {
            if (!$DeviceId -or !$Arg1) { Write-Host "Error: DeviceId and ModuleName required" -ForegroundColor Red; return }
            $DeviceId = Resolve-DeviceId $DeviceId
            $moduleName = [uri]::EscapeDataString("$Arg1")
            Write-Host "Reviving module '$Arg1' on $DeviceId (Status -> Manual, Single start)..." -ForegroundColor Yellow
            Invoke-RestMethod -Method POST -Uri "$BaseUrl/$DeviceId/modules/$moduleName/revive" -ErrorAction Stop | Out-Null
            Write-Host "Revive signal sent." -ForegroundColor Green
        }

        "remove-module" {
            if (!$DeviceId) { Write-Host "Error: DeviceId required" -ForegroundColor Red; return }
            $DeviceId = Resolve-DeviceId $DeviceId

            # Check for --all flag or if module name is --all
            if ($Arg1 -eq "--all" -or $Arg1 -eq "-a" -or $All.IsPresent) {
                Write-Host "Clearing all modules from $DeviceId..." -ForegroundColor DarkRed
                Invoke-RestMethod -Method DELETE -Uri "$BaseUrl/$DeviceId/modules" -ErrorAction Stop
                return
            }

            if (!$Arg1) { Write-Host "Error: ModuleName required (or use --all)" -ForegroundColor Red; return }
            
            Write-Host "Removing module '$Arg1' from $DeviceId..." -ForegroundColor Red
            Invoke-RestMethod -Method DELETE -Uri "$BaseUrl/$DeviceId/modules/$Arg1" -ErrorAction Stop
        }

        "hardware" {
            if (!$DeviceId) { Write-Host "Error: Device ID required" -ForegroundColor Red; return }
            $DeviceId = Resolve-DeviceId $DeviceId
            $d = Invoke-RestMethod -Method GET -Uri "$BaseUrl/$DeviceId" -ErrorAction Stop
            
            if (!$d.reportedStateJson) {
                Write-Host "???????????? No hardware data available for this device yet." -ForegroundColor Yellow
                return
            }

            $status = $d.reportedStateJson | ConvertFrom-Json
            
            Write-Host ""
            Write-Host "???????????????  Hardware Profile: $($d.name)" -ForegroundColor Cyan
            Write-Host "??????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????" -ForegroundColor DarkGray
            Write-Host ""
            
            # System Information
            if ($status.System) {
                Write-Host "????????? System Information" -ForegroundColor Green
                Write-Host "  Manufacturer:  $($status.System.Manufacturer)" -ForegroundColor White
                Write-Host "  Model:         $($status.System.Model)" -ForegroundColor White
                Write-Host "  OS:            $($status.System.OsVersion)" -ForegroundColor White
                $cpuColor = if ($status.System.CPUUsage -and [int]($status.System.CPUUsage -replace '%','') -gt 80) { 'Red' } else { 'White' }
                $ramColor = if ($status.System.RAMUsage -and [int]($status.System.RAMUsage -replace '%','') -gt 80) { 'Red' } else { 'White' }
                Write-Host "  CPU Usage:     $($status.System.CPUUsage)" -ForegroundColor $cpuColor
                Write-Host "  RAM Usage:     $($status.System.RAMUsage)" -ForegroundColor $ramColor
                Write-Host ""
            }

            # Peripherals
            if ($status.Peripherals -and $status.Peripherals.Count -gt 0) {
                Write-Host "????????? Connected Peripherals ($($status.Peripherals.Count))" -ForegroundColor Green
                $status.Peripherals | Select-Object @{Name="TYPE"; Expression={ $_.type }}, @{Name="NAME"; Expression={ $_.name }}, @{Name="STATUS"; Expression={ $_.status }} | Format-Table -AutoSize -Wrap
            } else {
                Write-Host "????????? Connected Peripherals" -ForegroundColor Green
                Write-Host "  (No peripherals detected)" -ForegroundColor DarkGray
                Write-Host ""
            }

            # Module States
            if ($status.Modules -and $status.Modules.Count -gt 0) {
                Write-Host "????????? Module Status ($($status.Modules.Count))" -ForegroundColor Green
                $status.Modules | Select-Object @{Name="MODULE"; Expression={ if ($_.moduleName) { $_.moduleName } else { $_.name } }}, 
                    @{Name="VERSION"; Expression={ $_.version }}, 
                    @{Name="STATE"; Expression={ 
                        $state = if ($_.status) { $_.status } else { $_.state }
                        if ($state -eq "Downloading" -and $_.totalSize -gt 0) {
                            $pct = [int]($_.downloadProgress * 100 / $_.totalSize)
                            return "Downloading ($pct%)"
                        }
                        return $state
                    }} | Format-Table -AutoSize -Wrap
            }
        }

        "clone-state" {
            if (!$DeviceId) { 
                Write-Host "Usage: .\manage.ps1 clone-state [source-device] [target1] [target2] ... [--replace]" -ForegroundColor Cyan
                return 
            }

            $sourceId = Resolve-DeviceId $DeviceId
            $rawCloneArgs = @($Arg1, $Arg2, $Arg3, $Arg4, $Arg5) | Where-Object { $_ -ne $null }
            $hasReplaceToken = ($rawCloneArgs | Where-Object { "$_" -match '^(?i)--?replace$' }).Count -gt 0
            $replaceMode = $Replace.IsPresent -or $hasReplaceToken
            $targetTokens = $rawCloneArgs | Where-Object { "$_" -notmatch '^(?i)--?replace$' }
            $targets = @($targetTokens | ForEach-Object { Resolve-DeviceId $_ })
             
            if ($targets.Count -eq 0) {
                Write-Host "Error: At least one target device required" -ForegroundColor Red
                return
            }

            Write-Host "????????? Cloning state from '$sourceId'..." -ForegroundColor Cyan
            $source = Invoke-RestMethod -Method GET -Uri "$BaseUrl/$sourceId" -ErrorAction Stop
            
            if (!$source.desiredStateJson) {
                Write-Host "Error: Source device has no desired state configured" -ForegroundColor Red
                return
            }

            $state = $source.desiredStateJson | ConvertFrom-Json
            if (!$state.Modules -or $state.Modules.Count -eq 0) {
                Write-Host "Error: Source device has no modules in desired state" -ForegroundColor Red
                return
            }

            Write-Host "Found $($state.Modules.Count) module(s) to clone:" -ForegroundColor Gray
            $state.Modules | ForEach-Object {
                $isManaged = $true
                if ($_.PSObject.Properties.Name -contains "IsManaged") {
                    $isManaged = [bool]$_.IsManaged
                } elseif ([string]::IsNullOrWhiteSpace("$($_.DownloadUrl)")) {
                    $isManaged = $false
                }
                $kind = if ($isManaged) { "managed" } else { "adopted" }
                Write-Host "  - $($_.Name) v$($_.Version) [$kind]"
            }

            if ($replaceMode) {
                Write-Host "Replace mode enabled: target desired-state modules will be cleared before clone." -ForegroundColor DarkGray
            }
            Write-Host ""

            $totalApplied = 0
            $totalSkipped = 0
            $totalFailed = 0

            foreach ($target in $targets) {
                Write-Host "????????? Flashing to '$target'..." -ForegroundColor Yellow

                if ($replaceMode) {
                    Invoke-RestMethod -Method DELETE -Uri "$BaseUrl/$target/modules" -ErrorAction Stop | Out-Null
                }

                $targetDevice = Invoke-RestMethod -Method GET -Uri "$BaseUrl/$target" -ErrorAction Stop
                $targetProcesses = Get-DeviceProcessCatalog $targetDevice

                $applied = 0
                $skipped = 0
                $failed = 0

                foreach ($m in $state.Modules) {
                    $isManaged = $true
                    if ($m.PSObject.Properties.Name -contains "IsManaged") {
                        $isManaged = [bool]$m.IsManaged
                    } elseif ([string]::IsNullOrWhiteSpace("$($m.DownloadUrl)")) {
                        $isManaged = $false
                    }

                    $envVars = @{}
                    if ($m.PSObject.Properties.Name -contains "EnvironmentVariables" -and $m.EnvironmentVariables) {
                        $envVars = $m.EnvironmentVariables
                    }

                    $startCommand = if ($m.StartCommand) { "$($m.StartCommand)" } else { "" }

                    if (-not $isManaged) {
                        $resolved = Resolve-AdoptedCommandForTarget -module $m -targetProcesses $targetProcesses
                        if (-not $resolved.Success) {
                            $skipped++
                            Write-Host "  ! Skipped adopted module '$($m.Name)' on '$target': $($resolved.Reason)." -ForegroundColor Yellow
                            continue
                        }

                        $startCommand = $resolved.StartCommand
                        if ("$($m.StartCommand)".Trim() -ne "$startCommand".Trim()) {
                            Write-Host "  ~ Remapped '$($m.Name)' on '$target' to '$($resolved.MatchedPath)'." -ForegroundColor DarkGray
                        }
                    }

                    $modulePayload = @{
                        Name = if ($m.Name) { "$($m.Name)" } else { "" }
                        Version = if ($m.Version) { "$($m.Version)" } else { "" }
                        DownloadUrl = if ($m.DownloadUrl) { "$($m.DownloadUrl)" } else { "" }
                        StartCommand = $startCommand
                        LaunchMode = if ($m.LaunchMode) { "$($m.LaunchMode)" } else { "service" }
                        DesiredStatus = if ($m.DesiredStatus) { "$($m.DesiredStatus)" } else { "Running" }
                        IsManaged = $isManaged
                        EnvironmentVariables = $envVars
                    }

                    try {
                        Invoke-RestMethod -Method POST -Uri "$BaseUrl/$target/modules" -Body ($modulePayload | ConvertTo-Json -Depth 6) -ContentType "application/json" -ErrorAction Stop | Out-Null
                        $applied++
                    } catch {
                        $failed++
                        Write-Host "  x Failed '$($m.Name)' on '$target': $($_.Exception.Message)" -ForegroundColor Red
                    }
                }

                $totalApplied += $applied
                $totalSkipped += $skipped
                $totalFailed += $failed

                if ($failed -eq 0) {
                    Write-Host "??????? $target configured (applied: $applied, skipped: $skipped)" -ForegroundColor Green
                } else {
                    Write-Host "???????? $target completed with errors (applied: $applied, skipped: $skipped, failed: $failed)" -ForegroundColor Yellow
                }
            }
            Write-Host ""
            if ($totalFailed -eq 0) {
                Write-Host "????????? Clone complete! Targets: $($targets.Count), Applied: $totalApplied, Skipped: $totalSkipped." -ForegroundColor Green
            } else {
                Write-Host "????????? Clone complete with errors. Targets: $($targets.Count), Applied: $totalApplied, Skipped: $totalSkipped, Failed: $totalFailed." -ForegroundColor Yellow
            }
        }

        "prune-devices" {
            $maxAgeHours = 24
            $shouldApply = $Apply.IsPresent
            
            # Legacy argument parsing (in case passed as string not switch, though PS usually binds switches)
            $tokens = @($DeviceId, $Arg1, $Arg2, $Arg3, $Arg4, $Arg5) | Where-Object { $_ -ne $null -and "$_".Trim().Length -gt 0 }

            foreach ($token in $tokens) {
                $value = "$token".Trim()
                if ($value -match "^\d+$") {
                    $maxAgeHours = [int]$value
                    continue
                }

                if ($value -in @("--apply", "-a", "-Apply")) {
                    $shouldApply = $true
                    continue
                }
            }

            if (-not $shouldApply) {
                Write-Host "Usage: .\manage.ps1 prune-devices [older-than-hours] [--apply]" -ForegroundColor Cyan
                Write-Host "Example dry-run: .\manage.ps1 prune-devices 24" -ForegroundColor Gray
                Write-Host "Example delete:  .\manage.ps1 prune-devices 24 --apply" -ForegroundColor Gray
                # Don't return here, proceed to show dry run
            }

            $cutoffUtc = [DateTime]::UtcNow.AddHours(-$maxAgeHours)
            $devices = Invoke-RestMethod -Method GET -Uri $BaseUrl -ErrorAction Stop
            $stale = @()

            foreach ($d in $devices) {
                $lastSeenUtc = Parse-UtcDate $d.lastSeenAt
                if (!$lastSeenUtc) { continue }

                $age = [DateTime]::UtcNow - $lastSeenUtc
                if ($age.TotalMinutes -lt 2) { continue }
                if ($lastSeenUtc -gt $cutoffUtc) { continue }

                $stale += [PSCustomObject]@{
                    DEVICE_ID = $d.id
                    NAME = $d.name
                    LAST_SEEN_UTC = $lastSeenUtc.ToString("yyyy-MM-dd HH:mm:ss")
                    AGE_HOURS = [Math]::Round($age.TotalHours, 1)
                }
            }

            if ($stale.Count -eq 0) {
                Write-Host "No stale offline devices found older than $maxAgeHours hour(s)." -ForegroundColor Green
                return
            }

            Write-Host "Stale offline devices older than $maxAgeHours hour(s):" -ForegroundColor Yellow
            $stale | Sort-Object LAST_SEEN_UTC | Format-Table -AutoSize -Wrap

            if (-not $shouldApply) {
                Write-Host ""
                Write-Host "Dry run only. Re-run with --apply to delete these from cloud registry." -ForegroundColor Gray
                return
            }

            $deleted = 0
            $failed = 0
            foreach ($entry in $stale) {
                try {
                    Invoke-RestMethod -Method DELETE -Uri "$BaseUrl/$($entry.DEVICE_ID)" -ErrorAction Stop | Out-Null
                    $deleted++
                } catch {
                    $failed++
                    Write-Host "Failed to delete '$($entry.DEVICE_ID)': $($_.Exception.Message)" -ForegroundColor Red
                }
            }

            Write-Host "Prune complete. Deleted: $deleted, Failed: $failed" -ForegroundColor Green
        }

        "help" { Show-Help }
        Default { Show-Help }
    }
} catch {
    Write-Host "Error: Could not perform action." -ForegroundColor Red
    
    # Try to extract server error message from the response stream
    if ($_.Exception.Response) {
        try {
            $stream = $_.Exception.Response.GetResponseStream()
            if ($stream) {
                $reader = New-Object System.IO.StreamReader($stream)
                $body = $reader.ReadToEnd()
                if ($body) {
                    try {
                        $json = $body | ConvertFrom-Json
                        if ($json.error) {
                            Write-Host "Server Message: $($json.error)" -ForegroundColor Yellow
                        } else {
                            Write-Host "Server Output: $body" -ForegroundColor Yellow
                        }
                    } catch {
                        Write-Host "Server Output: $body" -ForegroundColor Yellow
                    }
                }
            }
        } catch {
            # Ignore stream reading errors
        }
    }

    if ($_) { Write-Host "Details: $($_.Exception.Message)" -ForegroundColor Gray }
}
Write-Host ""
