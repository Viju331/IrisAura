using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace IrisAura.Island.Update;

/// <summary>What an update manifest describes (BLUEPRINT §12.5). JSON, camelCase; signed as raw bytes.</summary>
/// <param name="Product">Must be <see cref="UpdateVerifier.ProductName"/>.</param>
/// <param name="Version">Version of the MSI (major.minor.build).</param>
/// <param name="File">MSI file name.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of the MSI.</param>
/// <param name="Size">MSI size in bytes.</param>
public sealed record UpdateManifest(string Product, string Version, string File, string Sha256, long Size);

/// <summary>Outcome of <see cref="UpdateVerifier.Verify"/>.</summary>
public enum UpdateVerdict
{
    Valid,
    BadManifestSignature,
    BadManifest,
    WrongProduct,
    NotNewer,
    HashMismatch,
    UntrustedInstaller,
}

/// <summary>Result with a human-readable reason.</summary>
public sealed record UpdateCheck(UpdateVerdict Verdict, string Detail, UpdateManifest? Manifest = null)
{
    public bool IsValid => Verdict == UpdateVerdict.Valid;
}

/// <summary>
/// Update verification (BLUEPRINT §12.5 steps 1–2, Release 1): an update is accepted only if
/// <list type="number">
/// <item>the manifest is signed (ECDSA P-256 / SHA-256) by a trusted update key;</item>
/// <item>it is for this product and newer than the installed version;</item>
/// <item>the MSI's SHA-256 and size match the manifest;</item>
/// <item>the MSI's Authenticode signature is intact and its signer is a known IrisAura publisher or a pinned thumbprint.</item>
/// </list>
/// Offline: this class never downloads anything. Fetching manifests over HTTPS needs a release server and the owner's
/// approval of the network call (CLAUDE.md), so it is not built yet (KNOWN_ISSUES KI-043).
/// </summary>
public static class UpdateVerifier
{
    public const string ProductName = "IrisAura.Island";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    /// <param name="manifestBytes">The manifest file, byte for byte as signed.</param>
    /// <param name="signature">ECDSA P-256 signature (IEEE P1363) of <paramref name="manifestBytes"/>.</param>
    /// <param name="msiPath">The downloaded MSI.</param>
    /// <param name="installed">Installed version.</param>
    /// <param name="trustedKeys">Update public keys (SubjectPublicKeyInfo DER).</param>
    /// <param name="authenticode">Checks the MSI's signature; returns <see langword="null"/> when trusted, else the reason.</param>
    public static UpdateCheck Verify(
        byte[] manifestBytes,
        byte[] signature,
        string msiPath,
        Version installed,
        IEnumerable<byte[]> trustedKeys,
        Func<string, string?> authenticode)
    {
        if (!trustedKeys.Any(key => VerifySignature(key, manifestBytes, signature)))
        {
            return new UpdateCheck(UpdateVerdict.BadManifestSignature, "The manifest is not signed by a trusted IrisAura update key.");
        }

        UpdateManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<UpdateManifest>(manifestBytes, Json);
        }
        catch (JsonException)
        {
            manifest = null;
        }

        if (manifest is null || !System.Version.TryParse(manifest.Version, out var offered) || manifest.Sha256 is not { Length: 64 })
        {
            return new UpdateCheck(UpdateVerdict.BadManifest, "The manifest is incomplete.");
        }

        if (manifest.Product != ProductName)
        {
            return new UpdateCheck(UpdateVerdict.WrongProduct, $"The manifest is for '{manifest.Product}'.", manifest);
        }

        if (offered <= installed)
        {
            return new UpdateCheck(UpdateVerdict.NotNewer, $"Version {offered} is not newer than the installed {installed}.", manifest);
        }

        var info = new FileInfo(msiPath);
        if (!info.Exists || info.Length != manifest.Size)
        {
            return new UpdateCheck(UpdateVerdict.HashMismatch, "The installer's size does not match the manifest.", manifest);
        }

        using (var stream = info.OpenRead())
        {
            var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
            if (!string.Equals(hash, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return new UpdateCheck(UpdateVerdict.HashMismatch, "The installer's SHA-256 does not match the manifest.", manifest);
            }
        }

        return authenticode(msiPath) is { } problem
            ? new UpdateCheck(UpdateVerdict.UntrustedInstaller, problem, manifest)
            : new UpdateCheck(UpdateVerdict.Valid, $"IrisAura Island {offered} verified.", manifest);
    }

    public static bool VerifySignature(byte[] publicKeySpki, byte[] data, byte[] signature)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKeySpki, out _);
            return key.KeySize == 256 && key.VerifyData(data, signature, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}

/// <summary>
/// Authenticode check of a file (EXE or MSI): intact signature whose signer is a known publisher or a pinned
/// thumbprint; a pinned certificate may chain to an untrusted root (development certificate). No network.
/// </summary>
public static partial class Authenticode
{
    /// <summary>Production publisher subjects; empty until the certificate exists (Phase 9, KI-040).</summary>
    public static readonly string[] ProductionPublisherSubjects = [];

    private const int CertEUntrustedRoot = unchecked((int)0x800B0109);

    /// <returns><see langword="null"/> when trusted, otherwise the reason.</returns>
    public static string? Check(string path, IEnumerable<string> pinnedThumbprints)
    {
        X509Certificate2 signer;
        try
        {
            // CreateFromSignedFile is the only .NET API that returns an Authenticode signer; X509CertificateLoader has
            // no equivalent, so the obsolete warning is suppressed here only.
#pragma warning disable SYSLIB0057
            signer = X509CertificateLoader.LoadCertificate(X509Certificate.CreateFromSignedFile(path).GetRawCertData());
#pragma warning restore SYSLIB0057
        }
        catch (CryptographicException)
        {
            return "The installer is not signed.";
        }

        using (signer)
        {
            var result = WinVerifyTrust(path);
            var pinned = pinnedThumbprints.Any(t => string.Equals(new string(t.Where(Uri.IsHexDigit).ToArray()), signer.Thumbprint, StringComparison.OrdinalIgnoreCase));
            var known = ProductionPublisherSubjects.Contains(signer.Subject, StringComparer.OrdinalIgnoreCase);
            if ((result == 0 && (pinned || known)) || (result == CertEUntrustedRoot && pinned))
            {
                return null;
            }

            return result == 0
                ? $"The installer is signed by '{signer.Subject}', which is not a trusted IrisAura publisher."
                : $"The installer's signature check failed (0x{result:X8}).";
        }
    }

    private static int WinVerifyTrust(string path)
    {
        var pathPtr = Marshal.StringToHGlobalUni(path);
        var file = new WinTrustFileInfo { cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(), pcwszFilePath = pathPtr };
        var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(file, filePtr, false);
            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = 2, // WTD_UI_NONE
                fdwRevocationChecks = 0, // WTD_REVOKE_NONE
                dwUnionChoice = 1, // WTD_CHOICE_FILE
                pFile = filePtr,
                dwProvFlags = 0x00001000, // WTD_CACHE_ONLY_URL_RETRIEVAL: no network
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            return WinVerifyTrustNative(-1, ref action, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(filePtr);
            Marshal.FreeHGlobal(pathPtr);
        }
    }

    [LibraryImport("wintrust.dll", EntryPoint = "WinVerifyTrust")]
    private static partial int WinVerifyTrustNative(nint hwnd, ref Guid action, ref WinTrustData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        public nint pcwszFilePath;
        public nint hFile;
        public nint pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public nint pPolicyCallbackData;
        public nint pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public nint pFile;
        public uint dwStateAction;
        public nint hWVTStateData;
        public nint pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public nint pSignatureSettings;
    }
}

/// <summary>Trusted update-manifest public keys (SubjectPublicKeyInfo, base64). Private keys never enter the repository.</summary>
public static class UpdateKeys
{
    /// <summary>
    /// Development key ("IrisAura Update Signing (Development)", CNG key in the developer's user profile, created by
    /// <c>IrisAura.Tools update-key create</c>). Test builds only; replace with the production key before release.
    /// </summary>
    public const string Development = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEeSoWeyn9VaiHt6WnHr8KNUcj4FkJp/+KbS15quaTZ+eRBUCSRcaQkRk+TuDY6drEheUifiF22JnJ9c+lHydfyg==";

    /// <summary>Production keys (none yet; Phase 9).</summary>
    public static readonly string[] Production = [];

    /// <summary>Production keys, plus the development key only in builds made with <c>IRISAURA_DEV_TRUST</c> (dev releases).</summary>
    public static IEnumerable<byte[]> All =>
#if IRISAURA_DEV_TRUST
        Production.Append(Development).Select(Convert.FromBase64String);
#else
        Production.Select(Convert.FromBase64String);
#endif
}
