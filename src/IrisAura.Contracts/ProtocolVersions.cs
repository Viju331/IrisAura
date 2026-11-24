namespace IrisAura.Contracts;

/// <summary>
/// Wire-protocol versions. Every message carries its protocol version (<c>"v"</c>);
/// a receiver rejects versions it does not understand instead of guessing (BLUEPRINT §8.6.14).
/// </summary>
public static class ProtocolVersions
{
    /// <summary>Island Activity SDK pipe protocol (newline-delimited JSON).</summary>
    public const int IslandSdk = 1;

    /// <summary>Face pipes protocol (4-byte little-endian length prefix + UTF-8 JSON).</summary>
    public const int Face = 1;
}
