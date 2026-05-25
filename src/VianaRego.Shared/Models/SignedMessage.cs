namespace VianaRego.Shared.Models;

/// <summary>
/// Wrapper for signed MQTT messages.
/// Cloud API wraps payloads with signature; Edge Agent verifies before processing.
/// </summary>
public class SignedMessage
{
    /// <summary>
    /// The original JSON payload (desired state, command, etc.)
    /// </summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>
    /// Base64-encoded RSA signature of the Payload.
    /// Null if signing is disabled.
    /// </summary>
    public string? Signature { get; set; }
}
