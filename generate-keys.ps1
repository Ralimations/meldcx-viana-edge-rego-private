# Key Generation Script for Message Signing
# Generates RSA 2048-bit key pair for signing MQTT messages

param(
    [string]$OutputDir = "."
)

Write-Host "🔐 Generating RSA 2048-bit Key Pair..." -ForegroundColor Cyan

$rsa = [System.Security.Cryptography.RSA]::Create(2048)

# Export Private Key (PEM format)
$privateKeyPem = $rsa.ExportRSAPrivateKeyPem()
$privateKeyPath = Join-Path $OutputDir "private.pem"
Set-Content -Path $privateKeyPath -Value $privateKeyPem -NoNewline
Write-Host "✅ Private Key saved to: $privateKeyPath" -ForegroundColor Green

# Export Public Key (PEM format)
$publicKeyPem = $rsa.ExportRSAPublicKeyPem()
$publicKeyPath = Join-Path $OutputDir "public.pem"
Set-Content -Path $publicKeyPath -Value $publicKeyPem -NoNewline
Write-Host "✅ Public Key saved to: $publicKeyPath" -ForegroundColor Green

Write-Host ""
Write-Host "📋 Next Steps:" -ForegroundColor Yellow
Write-Host "  1. Set SIGNING_PRIVATE_KEY env var on Cloud API server:"
Write-Host "     `$env:SIGNING_PRIVATE_KEY = (Get-Content private.pem -Raw)"
Write-Host ""
Write-Host "  2. Add public key to Edge device Registry:"
Write-Host "     Set-ItemProperty -Path 'HKLM:\SOFTWARE\MeldCX\VianaRego' -Name 'SIGNING_PUBLIC_KEY' -Value (Get-Content public.pem -Raw)"
Write-Host ""
Write-Host "  3. Or include in your MSI provisioning workflow for automated setup"
Write-Host ""
Write-Host "⚠️  IMPORTANT: Keep private.pem SECRET! Never commit to git." -ForegroundColor Red
