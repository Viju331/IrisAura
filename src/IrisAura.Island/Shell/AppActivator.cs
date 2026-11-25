using System.Diagnostics;
using System.Runtime.InteropServices;
using IrisAura.Island.Providers.Media;
using Serilog;
using static IrisAura.Island.Interop.NativeMethods;

namespace IrisAura.Island.Shell;

/// <summary>
/// Brings an activity's source app to the front (BLUEPRINT §8.6.1 "Open the source app on click"):
/// packaged apps through the documented <c>IApplicationActivationManager</c>; unpackaged apps by restoring
/// their main window and calling <c>SetForegroundWindow</c>. Called in response to the user's click on the
/// Island, which is what allows the foreground change. No input injection, no AttachThreadInput.
/// </summary>
internal static class AppActivator
{
    public static bool TryActivate(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return false;
        }

        try
        {
            return MediaAppIdentity.IsPackaged(appId) ? ActivatePackaged(appId) : ActivateProcessWindow(appId);
        }
        catch (Exception ex)
        {
            Log.Information("Could not bring the source app to the front: {Error}", ex.GetType().Name);
            return false;
        }
    }

    private static bool ActivatePackaged(string aumid)
    {
        var manager = (IApplicationActivationManager)new ApplicationActivationManager();
        try
        {
            return manager.ActivateApplication(aumid, null, 0, out _) >= 0;
        }
        finally
        {
            Marshal.ReleaseComObject(manager);
        }
    }

    private static bool ActivateProcessWindow(string appId)
    {
        var name = MediaAppIdentity.ProcessNameFor(appId);
        if (name is null)
        {
            return false;
        }

        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                var hwnd = process.MainWindowHandle;
                if (hwnd == 0 || !IsWindowVisible(hwnd))
                {
                    continue;
                }

                if (IsIconic(hwnd))
                {
                    ShowWindow(hwnd, SW_RESTORE);
                }

                if (SetForegroundWindow(hwnd))
                {
                    return true;
                }
            }
        }

        return false;
    }

    [ComImport]
    [Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string? arguments,
            int options,
            out uint processId);
    }

    [ComImport]
    [Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ApplicationActivationManager
    {
    }
}
