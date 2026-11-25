using System.Buffers.Binary;
using System.Text;
using IrisAura.Contracts;

namespace IrisAura.Island.Providers.Sdk;

/// <summary>Text rules for SDK content (BLUEPRINT §8.6.14 "text is sanitised; no markup or scripts").</summary>
public static class SdkText
{
    public const int MaxAppName = 40;
    public const int MaxLeadingText = 40;
    public const int MaxTrailingText = 24;
    public const int MaxTitle = 80;
    public const int MaxSubtitle = 120;
    public const int MaxButtonLabel = 24;
    public const int MaxFinalText = 40;

    /// <summary>
    /// Plain text only: removes control characters, bidirectional overrides and zero-width characters (which can
    /// disguise text), collapses whitespace, trims and shortens with an ellipsis. The Island renders text as text, so
    /// markup is never interpreted; this keeps what is shown honest and bounded. Empty → <see langword="null"/>.
    /// </summary>
    public static string? Clean(string? value, int maxLength)
    {
        if (value is null)
        {
            return null;
        }

        var sb = new StringBuilder(Math.Min(value.Length, maxLength + 8));
        var lastWasSpace = true;
        foreach (var c in value)
        {
            if (IsInvisibleOrControl(c))
            {
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            sb.Append(c);
            lastWasSpace = false;
        }

        var text = sb.ToString().Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (text.Length > maxLength)
        {
            var cut = maxLength - 1;
            if (char.IsHighSurrogate(text[cut - 1]))
            {
                cut--; // do not split a surrogate pair
            }

            text = text[..cut].TrimEnd() + "…";
        }

        return text;
    }

    /// <summary>Activity and button ids: 1–<paramref name="maxLength"/> of A–Z a–z 0–9 . _ -</summary>
    public static bool IsValidId(string? id, int maxLength) =>
        id is { Length: > 0 } && id.Length <= maxLength && id.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '_' or '-');

    private static bool IsInvisibleOrControl(char c) =>
        (char.IsControl(c) && c is not ' ' and not '\t' and not '\n' and not '\r')
        || c is '​' or '‌' or '‍' or '‎' or '‏' or '⁠' or '﻿' or '؜'
        || c is >= '‪' and <= '‮'
        || c is >= '⁦' and <= '⁩';
}

/// <summary>
/// Which links an SDK activity may open when the user clicks it. http/https and an app's own scheme (myapp://) are
/// allowed; schemes that run code, open local files or trigger known-dangerous handlers are refused (the user clicked
/// the Island, not a link they could read).
/// </summary>
public static class SdkUriPolicy
{
    public const int MaxLength = 2048;

    private static readonly HashSet<string> Denied = new(StringComparer.OrdinalIgnoreCase)
    {
        "file", "javascript", "vbscript", "data", "about", "res", "mk", "its", "ms-its", "ldap",
        "shell", "search", "search-ms", "ms-search", "ms-msdt", "ms-officecmd", "ms-appinstaller", "ms-cxh", "ms-cxh-full",
        "ms-word", "ms-excel", "ms-powerpoint", "ms-access", "ms-publisher", "ms-visio", "ms-project", "ms-spd",
        "ms-infopath", "ms-settings", "microsoft-edge", "cmd", "powershell", "vscode", "hcp", "telnet", "ftp",
    };

    public static bool IsAllowed(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri) || uri.Length > MaxLength || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        var scheme = parsed.Scheme;
        if (Denied.Contains(scheme) || scheme.Length < 2 || parsed.IsUnc || parsed.IsFile)
        {
            return false;
        }

        return scheme.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '+' or '.' or '-');
    }
}

/// <summary>Icon rules: a data URI with a PNG or JPEG of at most 128 × 128 (read from the header, no decoding).</summary>
public static class SdkImage
{
    /// <summary>Decoded image bytes, or an error code text.</summary>
    public static (byte[]? Bytes, string? Error) FromDataUri(string? dataUri)
    {
        if (dataUri is null)
        {
            return (null, null);
        }

        const string pngPrefix = "data:image/png;base64,";
        const string jpegPrefix = "data:image/jpeg;base64,";
        var prefix = dataUri.StartsWith(pngPrefix, StringComparison.OrdinalIgnoreCase) ? pngPrefix
            : dataUri.StartsWith(jpegPrefix, StringComparison.OrdinalIgnoreCase) ? jpegPrefix
            : null;
        if (prefix is null)
        {
            return (null, "leadingIcon must be a data:image/png or data:image/jpeg base64 URI");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(dataUri[prefix.Length..]);
        }
        catch (FormatException)
        {
            return (null, "leadingIcon is not valid base64");
        }

        var size = Size(bytes);
        if (size is null)
        {
            return (null, "leadingIcon is not a readable PNG or JPEG");
        }

        return size.Value.Width > IslandSdkLimits.MaxIconPixels || size.Value.Height > IslandSdkLimits.MaxIconPixels
            ? (null, $"leadingIcon is larger than {IslandSdkLimits.MaxIconPixels} × {IslandSdkLimits.MaxIconPixels}")
            : (bytes, null);
    }

    /// <summary>Pixel size from a PNG IHDR or a JPEG SOF header, or <see langword="null"/>.</summary>
    public static (int Width, int Height)? Size(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
        if (data.Length >= 24 && data[..8].SequenceEqual(png) && data.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            var w = BinaryPrimitives.ReadInt32BigEndian(data.Slice(16, 4));
            var h = BinaryPrimitives.ReadInt32BigEndian(data.Slice(20, 4));
            return w > 0 && h > 0 ? (w, h) : null;
        }

        if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8)
        {
            var i = 2;
            while (i + 9 < data.Length)
            {
                if (data[i] != 0xFF)
                {
                    return null;
                }

                var marker = data[i + 1];
                var length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(i + 2, 2));
                if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                {
                    var h = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(i + 5, 2));
                    var w = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(i + 7, 2));
                    return w > 0 && h > 0 ? (w, h) : null;
                }

                if (length < 2)
                {
                    return null;
                }

                i += 2 + length;
            }
        }

        return null;
    }
}

/// <summary>Token bucket: <see cref="IslandSdkLimits.MaxUpdatesPerSecond"/> per key, bursts up to the same number.</summary>
public sealed class SdkRateLimiter(double perSecond = IslandSdkLimits.MaxUpdatesPerSecond)
{
    private readonly Dictionary<string, (double Tokens, DateTimeOffset At)> _buckets = new(StringComparer.Ordinal);

    public bool TryTake(string key, DateTimeOffset now)
    {
        var (tokens, at) = _buckets.TryGetValue(key, out var b) ? b : (perSecond, now);
        tokens = Math.Min(perSecond, tokens + (Math.Max(0, (now - at).TotalSeconds) * perSecond));
        if (tokens < 1)
        {
            _buckets[key] = (tokens, now);
            return false;
        }

        _buckets[key] = (tokens - 1, now);
        return true;
    }

    public void Forget(string key) => _buckets.Remove(key);
}
