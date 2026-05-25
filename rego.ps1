# rego.ps1 - PowerShell wrapper for manage.ps1
# This wrapper properly preserves quoted arguments with special characters

$scriptPath = Join-Path $PSScriptRoot "manage.ps1"

# Pass all arguments directly to manage.ps1
& $scriptPath @args
