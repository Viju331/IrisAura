using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IrisAura.Island.Providers.Media;
using static IrisAura.Island.Interop.NativeMethods;

namespace IrisAura.Island.Views;

/// <summary>An app's display name and icon.</summary>
public sealed record AppVisuals(string Name, ImageSource? Icon);

/// <summary>
/// Resolves a media app's name and icon (BLUEPRINT §8.6.1 "Source app name and icon"): packaged apps through
/// <c>AppInfo.GetFromAppUserModelId</c>; unpackaged apps from the running process's executable (file
/// description + shell icon). Results are cached per app id and frozen, so they can cross threads.
/// </summary>
public sealed class AppVisualsResolver
{
    private readonly ConcurrentDictionary<string, Task<AppVisuals>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public Task<AppVisuals> ResolveAsync(string appId) =>
        _cache.GetOrAdd(appId, id => Task.Run(() => ResolveCoreAsync(id)));

    private static async Task<AppVisuals> ResolveCoreAsync(string appId)
    {
        var name = MediaAppIdentity.DisplayNameFor(appId);

        if (MediaAppIdentity.IsPackaged(appId))
        {
            try
            {
                var info = Windows.ApplicationModel.AppInfo.GetFromAppUserModelId(appId);
                name = info.DisplayInfo.DisplayName;
                var logo = info.DisplayInfo.GetLogo(new Windows.Foundation.Size(32, 32));
                using var stream = await logo.OpenReadAsync();
                using var read = stream.AsStreamForRead();
                using var buffer = new MemoryStream();
                await read.CopyToAsync(buffer).ConfigureAwait(false);
                return new AppVisuals(name, Decode(buffer.ToArray(), 32));
            }
            catch (Exception)
            {
                return new AppVisuals(name, null);
            }
        }

        var processName = MediaAppIdentity.ProcessNameFor(appId);
        if (processName is null)
        {
            return new AppVisuals(name, null);
        }

        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                string? path;
                try
                {
                    path = process.MainModule?.FileName;
                }
                catch (Exception)
                {
                    continue; // protected or exited process
                }

                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.IsNullOrWhiteSpace(description))
                {
                    name = description.Trim();
                }

                return new AppVisuals(name, ShellIcon(path));
            }
        }

        return new AppVisuals(name, null);
    }

    /// <summary>Decodes encoded image bytes to a frozen bitmap (any thread).</summary>
    internal static BitmapSource? Decode(byte[] bytes, int decodeWidth)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = decodeWidth;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null; // corrupt or unsupported image
        }
    }

    private static unsafe ImageSource? ShellIcon(string path)
    {
        var info = default(SHFILEINFOW);
        if (SHGetFileInfo(path, 0, &info, (uint)sizeof(SHFILEINFOW), SHGFI_ICON | SHGFI_LARGEICON) == 0 || info.hIcon == 0)
        {
            return null;
        }

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }
}
