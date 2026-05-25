[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [string]$EnvPath = ".env",
    [int]$KeySize = 2048,
    [switch]$BackupEnv,
    [string]$PrivatePemOut,
    [string]$PublicPemOut
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $EnvPath)) {
    throw "Env file not found: $EnvPath"
}

if ($KeySize -lt 2048) {
    throw "KeySize must be at least 2048."
}

function New-PemKeysWithDotnet {
    param(
        [Parameter(Mandatory = $true)]
        [int]$Bits
    )

    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("vianarego-keygen-" + [Guid]::NewGuid().ToString("N"))
    New-Item -Path $tempDir -ItemType Directory -Force | Out-Null

    try {
        $projPath = Join-Path $tempDir "KeyGen.csproj"
        $progPath = Join-Path $tempDir "Program.cs"

        @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
'@ | Set-Content -LiteralPath $projPath

        @'
using System.Security.Cryptography;
using System.Text;

var keySize = int.Parse(args[0]);
using var rsa = RSA.Create(keySize);

var privatePem = rsa.ExportRSAPrivateKeyPem();
var publicPem = rsa.ExportRSAPublicKeyPem();

Console.WriteLine("PRIVATE_BASE64:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(privatePem)));
Console.WriteLine("PUBLIC_BASE64:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(publicPem)));
'@ | Set-Content -LiteralPath $progPath

        $output = & dotnet run --project $projPath -c Release -v q -- $Bits 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet key generation failed: $($output -join [Environment]::NewLine)"
        }

        $privateLine = $output | Where-Object { $_ -like "PRIVATE_BASE64:*" } | Select-Object -Last 1
        $publicLine = $output | Where-Object { $_ -like "PUBLIC_BASE64:*" } | Select-Object -Last 1

        if (-not $privateLine -or -not $publicLine) {
            throw "Could not parse key output from dotnet helper."
        }

        $privatePemValue = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($privateLine.Substring("PRIVATE_BASE64:".Length)))
        $publicPemValue = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($publicLine.Substring("PUBLIC_BASE64:".Length)))

        return @{
            PrivatePem = $privatePemValue
            PublicPem = $publicPemValue
        }
    }
    finally {
        if (Test-Path -LiteralPath $tempDir) {
            Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

Write-Host "Generating new RSA signing keys..." -ForegroundColor Cyan

$privatePem = $null
$publicPem = $null

if ($PSBoundParameters.ContainsKey("WhatIf") -and $PSBoundParameters["WhatIf"]) {
    Write-Host "WhatIf mode detected. Skipping real key generation." -ForegroundColor DarkYellow
    $privatePem = "-----BEGIN RSA PRIVATE KEY-----`nWHATIF`n-----END RSA PRIVATE KEY-----"
    $publicPem = "-----BEGIN RSA PUBLIC KEY-----`nWHATIF`n-----END RSA PUBLIC KEY-----"
} else {
    $rsa = [System.Security.Cryptography.RSA]::Create($KeySize)
    $hasPemApi = ($rsa | Get-Member -Name "ExportRSAPrivateKeyPem" -MemberType Method -ErrorAction SilentlyContinue) -ne $null
    if ($hasPemApi) {
        $privatePem = $rsa.ExportRSAPrivateKeyPem()
        $publicPem = $rsa.ExportRSAPublicKeyPem()
    } else {
        Write-Host "Native PEM export unavailable in this PowerShell runtime. Using dotnet fallback..." -ForegroundColor DarkYellow
        $fallback = New-PemKeysWithDotnet -Bits $KeySize
        $privatePem = $fallback.PrivatePem
        $publicPem = $fallback.PublicPem
    }
}

$privatePem = $privatePem.TrimEnd("`r", "`n")
$publicPem = $publicPem.TrimEnd("`r", "`n")

# Store PEM safely in .env as escaped single-line values.
$privateEscaped = $privatePem -replace "`r?`n", "\n"
$publicEscaped = $publicPem -replace "`r?`n", "\n"

if ($BackupEnv) {
    $timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
    $backupPath = "$EnvPath.bak.$timestamp"
    if ($PSCmdlet.ShouldProcess($backupPath, "Create .env backup")) {
        Copy-Item -LiteralPath $EnvPath -Destination $backupPath -Force
        Write-Host "Backup created: $backupPath" -ForegroundColor DarkGray
    }
}

$lines = [System.Collections.Generic.List[string]](Get-Content -LiteralPath $EnvPath)
$privateSet = $false
$publicSet = $false

for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^\s*SIGNING_PRIVATE_KEY\s*=') {
        $lines[$i] = 'SIGNING_PRIVATE_KEY="' + $privateEscaped + '"'
        $privateSet = $true
        continue
    }

    if ($lines[$i] -match '^\s*SIGNING_PUBLIC_KEY\s*=') {
        $lines[$i] = 'SIGNING_PUBLIC_KEY="' + $publicEscaped + '"'
        $publicSet = $true
        continue
    }
}

if (-not $privateSet) {
    $lines.Add('SIGNING_PRIVATE_KEY="' + $privateEscaped + '"')
}

if (-not $publicSet) {
    $lines.Add('SIGNING_PUBLIC_KEY="' + $publicEscaped + '"')
}

if ($PSCmdlet.ShouldProcess($EnvPath, "Update SIGNING_PRIVATE_KEY and SIGNING_PUBLIC_KEY")) {
    Set-Content -LiteralPath $EnvPath -Value $lines
    Write-Host "Updated: $EnvPath" -ForegroundColor Green
}

if ($PrivatePemOut) {
    if ($PSCmdlet.ShouldProcess($PrivatePemOut, "Write private PEM")) {
        Set-Content -LiteralPath $PrivatePemOut -Value $privatePem -NoNewline
        Write-Host "Private key written: $PrivatePemOut" -ForegroundColor Green
    }
}

if ($PublicPemOut) {
    if ($PSCmdlet.ShouldProcess($PublicPemOut, "Write public PEM")) {
        Set-Content -LiteralPath $PublicPemOut -Value $publicPem -NoNewline
        Write-Host "Public key written: $PublicPemOut" -ForegroundColor Green
    }
}

Write-Host ""
Write-Host "Signing keys rotated successfully." -ForegroundColor Cyan
Write-Host "Cloud uses SIGNING_PRIVATE_KEY from $EnvPath."
Write-Host "Edge can use SIGNING_PUBLIC_KEY from registry or environment."
