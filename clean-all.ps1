[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
param(
    [switch]$IncludeRuntimeData,
    [switch]$IncludeLogs
)

$ErrorActionPreference = "Stop"
$solutionRoot = $PSScriptRoot

Write-Host "Cleaning build and MSI artifacts in: $solutionRoot" -ForegroundColor Cyan

$pathsToRemove = @(
    (Join-Path $solutionRoot "artifacts"),
    (Join-Path $solutionRoot "_archive"),
    (Join-Path $solutionRoot ".module-feed"),
    (Join-Path $solutionRoot "VianaRego.Installer\Release"),
    (Join-Path $solutionRoot "VianaRego.Installer\bin"),
    (Join-Path $solutionRoot "VianaRego.Installer\obj"),
    (Join-Path $solutionRoot "VianaRego.Installer\deploy"),
    (Join-Path $solutionRoot "VianaRego.Installer\deploy_service"),
    (Join-Path $solutionRoot "TestResults")
)

# Legacy project outputs in case any were produced outside ArtifactsPath.
$legacyOutputDirs = Get-ChildItem -Path (Join-Path $solutionRoot "src") -Directory -Recurse -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -in @("bin", "obj") } |
    Select-Object -ExpandProperty FullName

$pathsToRemove += $legacyOutputDirs

# Safety: remove accidental per-project artifacts folders created by custom OutputPath overrides.
$legacyArtifactsDirs = Get-ChildItem -Path (Join-Path $solutionRoot "src") -Directory -Recurse -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -eq "artifacts" } |
    Select-Object -ExpandProperty FullName

$pathsToRemove += $legacyArtifactsDirs

if ($IncludeRuntimeData) {
    $pathsToRemove += (Join-Path $solutionRoot "data")
}

if ($IncludeLogs) {
    $pathsToRemove += (Join-Path $solutionRoot "logs")
}

$pathsToRemove = $pathsToRemove | Sort-Object -Unique

$removedPaths = 0
$missingPaths = 0
$removedFiles = 0
$skippedPaths = 0
$skippedFiles = 0

function Remove-PathSafe {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [string]$Type = "path"
    )

    $maxAttempts = 3
    for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
        try {
            Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
            return $true
        } catch [System.UnauthorizedAccessException], [System.IO.IOException] {
            if ($attempt -lt $maxAttempts) {
                Start-Sleep -Milliseconds (250 * $attempt)
                continue
            }

            Write-Warning "Skipped locked ${Type}: $Path"
            Write-Host "  Reason: $($_.Exception.Message)" -ForegroundColor DarkGray
            return $false
        } catch {
            Write-Warning "Failed to remove ${Type}: $Path"
            Write-Host "  Reason: $($_.Exception.Message)" -ForegroundColor DarkGray
            return $false
        }
    }

    return $false
}

foreach ($path in $pathsToRemove) {
    if (-not (Test-Path -LiteralPath $path)) {
        $missingPaths++
        continue
    }

    if ($PSCmdlet.ShouldProcess($path, "Remove directory")) {
        if (Remove-PathSafe -Path $path -Type "directory") {
            Write-Host "Removed: $path" -ForegroundColor Green
            $removedPaths++
        } else {
            $skippedPaths++
        }
    }
}

# Remove any MSI/Wix artifacts that may exist outside the standard folders.
$artifactFiles = Get-ChildItem -Path $solutionRoot -Recurse -File -Include "*.msi", "*.wixpdb", "*.cab" -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notlike (Join-Path $solutionRoot ".git\*") }

foreach ($file in $artifactFiles) {
    if ($PSCmdlet.ShouldProcess($file.FullName, "Remove file")) {
        if (Remove-PathSafe -Path $file.FullName -Type "file") {
            Write-Host "Removed file: $($file.FullName)" -ForegroundColor Green
            $removedFiles++
        } else {
            $skippedFiles++
        }
    }
}

if ($IncludeLogs) {
    $logFiles = Get-ChildItem -Path $solutionRoot -Recurse -File -Include "*.log", "build_*.txt", "*build*.log", "build_error*.json", "build_errors*.txt", "*_build_*.log" -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notlike (Join-Path $solutionRoot ".git\*") }

    foreach ($file in $logFiles) {
        if ($PSCmdlet.ShouldProcess($file.FullName, "Remove log file")) {
            if (Remove-PathSafe -Path $file.FullName -Type "log file") {
                Write-Host "Removed log: $($file.FullName)" -ForegroundColor Yellow
                $removedFiles++
            } else {
                $skippedFiles++
            }
        }
    }
}

Write-Host ""
Write-Host "Cleanup complete." -ForegroundColor Cyan
Write-Host "Directories removed: $removedPaths"
Write-Host "Files removed: $removedFiles"
Write-Host "Paths already absent: $missingPaths"
Write-Host "Skipped locked directories: $skippedPaths"
Write-Host "Skipped locked files: $skippedFiles"

if ($skippedPaths -gt 0 -or $skippedFiles -gt 0) {
    Write-Host "Some items were in use. Stop running dev/service processes and run clean-all again for full cleanup." -ForegroundColor Yellow
}

if (-not $IncludeRuntimeData) {
    Write-Host "Tip: use -IncludeRuntimeData to also remove .\data" -ForegroundColor DarkGray
}

if (-not $IncludeLogs) {
    Write-Host "Tip: use -IncludeLogs to also remove *.log and build log files" -ForegroundColor DarkGray
}
