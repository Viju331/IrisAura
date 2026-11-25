using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace IrisAura.Island.Providers.Interop;

/// <summary>Documented Win32 functions used by providers (hand-written LibraryImport; no undocumented APIs).</summary>
internal static partial class ProviderNativeMethods
{
    public const int REG_NOTIFY_CHANGE_NAME = 0x1;
    public const int REG_NOTIFY_CHANGE_LAST_SET = 0x4;

    /// <summary>FOLDERID_Downloads.</summary>
    public static readonly Guid FolderIdDownloads = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>Signals <paramref name="hEvent"/> once when the key (or a subkey) changes; must be re-armed after each signal.</summary>
    [LibraryImport("advapi32.dll")]
    public static partial int RegNotifyChangeKeyValue(
        SafeRegistryHandle hKey,
        [MarshalAs(UnmanagedType.Bool)] bool bWatchSubtree,
        int dwNotifyFilter,
        SafeWaitHandle hEvent,
        [MarshalAs(UnmanagedType.Bool)] bool fAsynchronous);

    [LibraryImport("shell32.dll")]
    public static partial int SHGetKnownFolderPath(in Guid rfid, uint dwFlags, nint hToken, out nint ppszPath);

    [LibraryImport("ole32.dll")]
    public static partial void CoTaskMemFree(nint pv);

    /// <summary>The user's Downloads folder (it can be moved), or <see langword="null"/>.</summary>
    public static string? DownloadsFolder()
    {
        if (SHGetKnownFolderPath(FolderIdDownloads, 0, 0, out var path) != 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(path);
        }
        finally
        {
            CoTaskMemFree(path);
        }
    }
}
