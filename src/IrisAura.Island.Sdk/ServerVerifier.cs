using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;

namespace IrisAura.Island.Sdk;

/// <summary>How strictly the client checks the pipe server before sending anything (THREAT_MODEL T-02, pipe squatting).</summary>
public enum IslandServerVerification
{
    /// <summary>
    /// Default. The server must be <c>IrisAura.Island.exe</c> in the caller's session with a valid Authenticode
    /// signature whose signer is a known IrisAura publisher or a pinned thumbprint
    /// (<see cref="IslandClientOptions.TrustedThumbprints"/>). A pinned thumbprint is accepted even when the
    /// certificate does not chain to a trusted root (development certificate), as long as the file's signature is intact.
    /// </summary>
    SignedIsland,

    /// <summary>Development builds only: <c>IrisAura.Island.exe</c> in the caller's session, signature not checked.</summary>
    UnsignedDevelopmentBuild,

    /// <summary>Tests only: no check at all.</summary>
    None,
}

/// <summary>Raised when the pipe server is not the IrisAura Island the client expects.</summary>
public sealed class IslandServerVerificationException(string message) : Exception(message);

/// <summary>Checks who is serving the SDK pipe. Uses only documented Win32 calls; makes no network calls.</summary>
internal static class ServerVerifier
{
    public const string IslandExeName = "IrisAura.Island.exe";

    /// <summary>
    /// Subjects of the production code-signing certificate. Empty until the certificate is bought (BLUEPRINT §12.4,
    /// Phase 9); until then only pinned thumbprints are accepted in <see cref="IslandServerVerification.SignedIsland"/>.
    /// </summary>
    public static readonly string[] ProductionPublisherSubjects = [];

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int CertEUntrustedRoot = unchecked((int)0x800B0109);

    public static void Verify(SafePipeHandle pipe, IslandServerVerification mode, ICollection<string> trustedThumbprints)
    {
        if (mode == IslandServerVerification.None)
        {
            return;
        }

        if (!GetNamedPipeServerProcessId(pipe, out var pid))
        {
            throw new IslandServerVerificationException("Could not read the pipe server's process id.");
        }

        if (!ProcessIdToSessionId(pid, out var serverSession)
            || !ProcessIdToSessionId((uint)System.Diagnostics.Process.GetCurrentProcess().Id, out var ownSession)
            || serverSession != ownSession)
        {
            throw new IslandServerVerificationException("The pipe server is not in this Windows session.");
        }

        var path = ImagePath(pid) ?? throw new IslandServerVerificationException("Could not read the pipe server's program path.");
        if (!string.Equals(Path.GetFileName(path), IslandExeName, StringComparison.OrdinalIgnoreCase))
        {
            throw new IslandServerVerificationException($"The pipe server is '{Path.GetFileName(path)}', not {IslandExeName}.");
        }

        if (mode == IslandServerVerification.UnsignedDevelopmentBuild)
        {
            return;
        }

        VerifySignature(path, trustedThumbprints);
    }

    /// <summary>Authenticode check of <paramref name="path"/> against the allowed publishers.</summary>
    public static void VerifySignature(string path, ICollection<string> trustedThumbprints)
    {
        X509Certificate2 signer;
        try
        {
            signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
        }
        catch (Exception)
        {
            throw new IslandServerVerificationException($"{IslandExeName} is not signed.");
        }

        using (signer)
        {
            var result = WinVerifyTrust(path);
            var pinned = trustedThumbprints.Any(t => string.Equals(Normalize(t), signer.Thumbprint, StringComparison.OrdinalIgnoreCase));
            var knownPublisher = ProductionPublisherSubjects.Any(s => string.Equals(s, signer.Subject, StringComparison.OrdinalIgnoreCase));

            if (result == 0 && (pinned || knownPublisher))
            {
                return;
            }

            if (result == CertEUntrustedRoot && pinned)
            {
                return; // pinned development certificate: file integrity verified, root not trusted (by design)
            }

            throw new IslandServerVerificationException(result == 0
                ? $"{IslandExeName} is signed by '{signer.Subject}', which is not a trusted IrisAura publisher."
                : $"{IslandExeName} signature check failed (0x{result:X8}).");
        }
    }

    private static string Normalize(string thumbprint) => new(thumbprint.Where(Uri.IsHexDigit).ToArray());

    private static string? ImagePath(uint pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new char[1024];
            var size = buffer.Length;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>WinVerifyTrust for an embedded Authenticode signature; revocation not checked and no network retrieval.</summary>
    private static int WinVerifyTrust(string path)
    {
        var file = new WinTrustFileInfo { cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(), pcwszFilePath = path };
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
                dwStateAction = 0, // WTD_STATEACTION_IGNORE
                dwProvFlags = 0x00001000, // WTD_CACHE_ONLY_URL_RETRIEVAL: never goes to the network
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            return WinVerifyTrustNative(new IntPtr(-1), ref action, ref data);
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(filePtr);
            Marshal.FreeHGlobal(filePtr);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, [Out] char[] name, ref int size);

    [DllImport("wintrust.dll", EntryPoint = "WinVerifyTrust", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrustNative(IntPtr hwnd, ref Guid action, ref WinTrustData data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
