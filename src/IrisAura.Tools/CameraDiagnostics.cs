using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using IrisAura.Face.Camera;
using Microsoft.Win32;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Security.Credentials;

namespace IrisAura.Tools;

/// <summary>
/// Phase 0B camera feasibility diagnostics (BLUEPRINT §3.6, §10 Phase 0B): which cameras exist, what kinds of stream
/// each exposes, and whether frames actually arrive in the current context (user, elevated, locked, camera in use).
/// Records device model, Windows build, VID/PID, driver version, Windows Hello / ESS state and camera privacy settings.
/// Never stores frames; never records serial numbers or user names.
/// </summary>
internal static partial class CameraDiagnostics
{
    private static readonly string[] DeviceProperties =
    [
        "System.Devices.HardwareIds",
        "{a8b865dd-2e3d-4094-ad97-e593a70c75d6} 3", // DEVPKEY_Device_DriverVersion
        "{a8b865dd-2e3d-4094-ad97-e593a70c75d6} 2", // DEVPKEY_Device_DriverDate
    ];

    /// <summary><c>cameras [--probe] [--seconds N] [--delay N] [--with-holder] [--label TEXT] [--out FILE]</c></summary>
    public static async Task<int> RunAsync(string[] args)
    {
        string? Option(string name)
        {
            var i = Array.IndexOf(args, "--" + name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        bool Flag(string name) => args.Contains("--" + name);

        if (args.ElementAtOrDefault(1) == "hold")
        {
            return await HoldAsync(TimeSpan.FromSeconds(double.Parse(Option("seconds") ?? "15", System.Globalization.CultureInfo.InvariantCulture)));
        }

        var seconds = double.Parse(Option("seconds") ?? "3", System.Globalization.CultureInfo.InvariantCulture);
        if (Option("delay") is { } delay)
        {
            // Lets the tester lock the workstation (Win+L) or close the lid before the probe starts.
            await Task.Delay(TimeSpan.FromSeconds(double.Parse(delay, System.Globalization.CultureInfo.InvariantCulture)));
        }

        Process? holder = null;
        if (Flag("with-holder"))
        {
            // Another process holds the first color camera exclusively during the probe ("camera in use" condition).
            holder = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"cameras hold --seconds {seconds * 8 + 10}") { UseShellExecute = false, CreateNoWindow = true });
            await Task.Delay(TimeSpan.FromSeconds(3));
        }

        try
        {
            var cameras = await new CameraDiscovery().EnumerateAsync();
            var results = new List<(CameraInfo Camera, IReadOnlyList<ProbeResult> Probes)>();
            foreach (var camera in cameras)
            {
                var probes = new List<ProbeResult>();
                if (Flag("probe"))
                {
                    foreach (var kind in camera.Sources.Select(s => s.Kind).Distinct())
                    {
                        foreach (var mode in new[] { MediaCaptureSharingMode.ExclusiveControl, MediaCaptureSharingMode.SharedReadOnly })
                        {
                            probes.Add(await CameraProbe.ProbeAsync(camera.GroupId, kind, mode, TimeSpan.FromSeconds(seconds)));
                        }
                    }
                }

                results.Add((camera, probes));
            }

            var helloFace = await HelloFaceSensorsAsync();
            var report = new
            {
                tool = "IrisAura.Tools cameras",
                label = Option("label"),
                timestamp = DateTimeOffset.Now.ToString("O"),
                context = Context(),
                device = Device(),
                windowsHello = new
                {
                    keyCredentialSupported = await KeyCredentialManager.IsSupportedAsync(),
                    faceSensors = helloFace,
                    enhancedSignInSecurity = Ess(),
                },
                cameraPrivacy = Privacy(),
                cameraInUseByAnotherProcess = holder is not null,
                drivers = await CameraDriversAsync(),
                cameras = results.Select(r => new
                {
                    r.Camera.Name,
                    r.Camera.VidPid,
                    r.Camera.Panel,
                    r.Camera.IsBuiltIn,
                    r.Camera.IsVirtual,
                    sources = r.Camera.Sources.Select(s => new { kind = s.Kind.ToString(), s.StreamType }),
                    probes = r.Probes,
                }),
                tier = Flag("probe") ? CameraClassifier.Tier(helloFace.Count > 0, results).ToString() : null,
            };

            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            if (Option("out") is { } path)
            {
                await File.WriteAllTextAsync(path, json);
                Console.WriteLine($"Wrote {path}");
            }
            else
            {
                Console.WriteLine(json);
            }

            return 0;
        }
        finally
        {
            if (holder is { HasExited: false })
            {
                holder.Kill();
            }

            holder?.Dispose();
        }
    }

    /// <summary>Holds the first color camera exclusively (frames discarded) for the "camera in use" test.</summary>
    private static async Task<int> HoldAsync(TimeSpan duration)
    {
        var camera = (await new CameraDiscovery().EnumerateAsync()).FirstOrDefault(c => c.Has(CameraKind.Color) && !c.IsVirtual);
        if (camera is null)
        {
            return 1;
        }

        await CameraProbe.ProbeAsync(camera.GroupId, CameraKind.Color, MediaCaptureSharingMode.ExclusiveControl, duration, maxFrames: int.MaxValue);
        return 0;
    }

    private static object Context()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new
        {
            elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator),
            localSystem = identity.IsSystem,
            sessionId = Process.GetCurrentProcess().SessionId,
            inputDesktop = InputDesktopName(), // "Default" when unlocked; "Winlogon" (or unreadable) when locked / secure desktop
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
        };
    }

    private static object Device()
    {
        using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        using var nt = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        return new
        {
            manufacturer = bios?.GetValue("SystemManufacturer") as string,
            model = bios?.GetValue("SystemProductName") as string,
            windowsBuild = $"{Environment.OSVersion.Version.Build}.{nt?.GetValue("UBR")}",
            windowsVersion = nt?.GetValue("DisplayVersion") as string,
            edition = nt?.GetValue("EditionID") as string,
            architecture = RuntimeInformation.OSArchitecture.ToString(),
        };
    }

    private static object Ess()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\SecureBiometrics");
        return new { secureBiometricsEnabled = key?.GetValue("Enabled") is int v ? v == 1 : (bool?)null };
    }

    private static object Privacy()
    {
        static string? Value(RegistryKey root, string path)
        {
            using var key = root.OpenSubKey(path);
            return key?.GetValue("Value") as string;
        }

        const string store = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";
        using var policy = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy");
        return new
        {
            deviceWide = Value(Registry.LocalMachine, store),
            user = Value(Registry.CurrentUser, store),
            desktopApps = Value(Registry.CurrentUser, store + @"\NonPackaged"),
            policyLetAppsAccessCamera = policy?.GetValue("LetAppsAccessCamera") as int?,
        };
    }

    private static async Task<List<string>> HelloFaceSensorsAsync()
    {
        // Biometric device class; Windows Hello face sensors appear here (names are generic, e.g. "Windows Hello Face Software Device").
        var devices = await DeviceInformation.FindAllAsync("System.Devices.ClassGuid:=\"{53D29EF7-377C-4D14-864B-EB3A85769359}\"", [], DeviceInformationKind.Device);
        return devices.Where(d => d.Name.Contains("face", StringComparison.OrdinalIgnoreCase)).Select(d => d.Name).Distinct().ToList();
    }

    private static async Task<object> CameraDriversAsync()
    {
        // Camera and Image device classes.
        var aqs = "System.Devices.ClassGuid:=\"{ca3e7ab9-b4c3-4ae6-8251-579ef933890f}\" OR System.Devices.ClassGuid:=\"{6bdd1fc6-810f-11d0-bec7-08002be2092f}\"";
        var devices = await DeviceInformation.FindAllAsync(aqs, DeviceProperties, DeviceInformationKind.Device);
        return devices.Select(d => new
        {
            d.Name,
            vidPid = d.Properties.TryGetValue("System.Devices.HardwareIds", out var ids) && ids is string[] list
                ? list.Select(CameraClassifier.VidPid).FirstOrDefault(v => v is not null)
                : null,
            driverVersion = d.Properties.TryGetValue(DeviceProperties[1], out var version) ? version as string : null,
            driverDate = d.Properties.TryGetValue(DeviceProperties[2], out var date) && date is DateTimeOffset dt ? dt.ToString("yyyy-MM-dd") : null,
        }).ToList();
    }

    private static string? InputDesktopName()
    {
        var desktop = OpenInputDesktop(0, false, 0x0001 /* DESKTOP_READOBJECTS */);
        if (desktop == 0)
        {
            return null; // not accessible: typically the secure desktop (locked / UAC)
        }

        try
        {
            var buffer = new char[256];
            return GetUserObjectInformation(desktop, 2 /* UOI_NAME */, buffer, buffer.Length * 2, out var needed)
                ? new string(buffer, 0, Math.Max(0, (needed / 2) - 1))
                : null;
        }
        finally
        {
            CloseDesktop(desktop);
        }
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);

    [LibraryImport("user32.dll", EntryPoint = "GetUserObjectInformationW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetUserObjectInformation(nint handle, int index, [Out] char[] info, int length, out int needed);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseDesktop(nint desktop);
}
