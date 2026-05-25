using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace VianaRego.Edge.Agent.Services;

/// <summary>
/// Verifies RSA signatures on incoming MQTT messages.
/// Ensures commands come from the trusted Cloud API.
/// </summary>
public class SignatureVerifier
{
    private readonly RSA? _rsa;
    private readonly ILogger<SignatureVerifier> _logger;
    private readonly bool _verificationEnabled;

    public SignatureVerifier(ILogger<SignatureVerifier> logger)
    {
        _logger = logger;

        // Try to load public key from Registry
        string? publicKeyPem = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\MeldCX\VianaRego");
            publicKeyPem = key?.GetValue("SIGNING_PUBLIC_KEY")?.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read public key from Registry.");
        }

        // Fallback: environment variable
        if (string.IsNullOrWhiteSpace(publicKeyPem))
        {
            publicKeyPem = Environment.GetEnvironmentVariable("SIGNING_PUBLIC_KEY");
        }

        if (string.IsNullOrWhiteSpace(publicKeyPem))
        {
            _logger.LogWarning("SIGNING_PUBLIC_KEY not configured. Signature verification disabled.");
            _verificationEnabled = false;
            return;
        }

        publicKeyPem = NormalizePem(publicKeyPem);

        try
        {
            _rsa = RSA.Create();
            _rsa.ImportFromPem(publicKeyPem);
            _verificationEnabled = true;
            _logger.LogInformation("Signature verification enabled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load public key. Signature verification disabled.");
            _verificationEnabled = false;
        }
    }

    /// <summary>
    /// Verifies the signature of a payload.
    /// Returns true if signature is valid OR if verification is disabled.
    /// </summary>
    public bool Verify(string payload, string? signature)
    {
        if (!_verificationEnabled || _rsa == null)
        {
            // If verification is disabled, allow message through (backward compatibility)
            return true;
        }

        if (string.IsNullOrEmpty(signature))
        {
            _logger.LogWarning("Received unsigned message - rejecting.");
            return false;
        }

        try
        {
            var payloadBytes = Encoding.UTF8.GetBytes(payload);
            var signatureBytes = Convert.FromBase64String(signature);
            var isValid = _rsa.VerifyData(payloadBytes, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            if (!isValid)
            {
                _logger.LogWarning("Signature verification failed - rejecting command.");
            }

            return isValid;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Signature verification error - rejecting command.");
            return false;
        }
    }

    private static string NormalizePem(string pem)
    {
        var normalized = pem.Trim();
        if (normalized.StartsWith('"') && normalized.EndsWith('"') && normalized.Length >= 2)
        {
            normalized = normalized[1..^1];
        }

        return normalized.Replace("\\n", "\n").Replace("\\r", "\r");
    }

    public bool IsEnabled => _verificationEnabled;
}
