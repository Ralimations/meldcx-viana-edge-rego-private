using System.Security.Cryptography;
using System.Text;

namespace VianaRego.Cloud.Api.Services;

/// <summary>
/// Signs MQTT messages with RSA private key for secure command delivery.
/// </summary>
public class SigningService
{
    private readonly RSA? _rsa;
    private readonly ILogger<SigningService> _logger;
    private readonly bool _signingEnabled;

    public SigningService(IConfiguration config, ILogger<SigningService> logger)
    {
        _logger = logger;

        var privateKeyPem = config["SIGNING_PRIVATE_KEY"] ?? Environment.GetEnvironmentVariable("SIGNING_PRIVATE_KEY");

        if (string.IsNullOrWhiteSpace(privateKeyPem))
        {
            _logger.LogWarning("SIGNING_PRIVATE_KEY not configured. Message signing disabled.");
            _signingEnabled = false;
            return;
        }

        privateKeyPem = NormalizePem(privateKeyPem);

        try
        {
            _rsa = RSA.Create();
            _rsa.ImportFromPem(privateKeyPem);
            _signingEnabled = true;
            _logger.LogInformation("Message signing enabled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load signing key. Message signing disabled.");
            _signingEnabled = false;
        }
    }

    /// <summary>
    /// Signs a payload and returns the Base64-encoded signature.
    /// Returns null if signing is disabled.
    /// </summary>
    public string? Sign(string payload)
    {
        if (!_signingEnabled || _rsa == null)
            return null;

        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var signatureBytes = _rsa.SignData(payloadBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return Convert.ToBase64String(signatureBytes);
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

    public bool IsEnabled => _signingEnabled;
}
