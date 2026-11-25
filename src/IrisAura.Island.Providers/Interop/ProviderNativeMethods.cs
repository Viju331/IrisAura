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

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>Process id of the client connected to a named pipe (server side).</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryFullProcessImageName(nint process, uint flags, [Out] char[] name, ref uint size);

    /// <summary>Full image path of a process (limited query rights), or <see langword="null"/>.</summary>
    public static string? ProcessImagePath(uint processId)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle == 0)
        {
            return null;
        }

        try
        {
            var buffer = new char[1024];
            var size = (uint)buffer.Length;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

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
