using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Serilog;
using static IrisAura.Island.Interop.NativeMethods;

namespace IrisAura.Island.Shell;

/// <summary>
/// Notification-area icon (BLUEPRINT §0.6 tray icon) using the documented <c>Shell_NotifyIcon</c> API.
/// Gives access to the Island menu when the pill is idle (click-through) or hidden. The icon is the
/// monochrome bracket-person glyph rendered at the system small-icon size, light or dark to match
/// the taskbar, and re-added if Explorer restarts.
/// </summary>
internal sealed unsafe class TrayIcon : IDisposable
{
    private const uint IconId = 1;
    private const int CallbackMessage = WM_APP + 1;
    private const string Tooltip = "IrisAura Island";

    private readonly HwndSource _owner;
    private readonly uint _taskbarCreatedMessage;
    private nint _icon;
    private bool _added;
    private bool _disposed;

    public TrayIcon()
    {
        // Hidden top-level owner window: receives tray callbacks and owns popup menus.
        var parameters = new HwndSourceParameters("IrisAura.Island.Tray")
        {
            Width = 0,
            Height = 0,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP, never shown
            ExtendedWindowStyle = (int)WS_EX_TOOLWINDOW,
        };
        _owner = new HwndSource(parameters);
        _owner.AddHook(WndProc);
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        Add();
    }

    /// <summary>Left or right click (or keyboard select) on the icon, with the anchor point in screen pixels.</summary>
    public event EventHandler<(int X, int Y)>? MenuRequested;

    /// <summary>Window that owns tray menus.</summary>
    public nint OwnerHandle => _owner.Handle;

    public bool IsAdded => _added;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_added)
        {
            var data = NewData(0);
            Shell_NotifyIcon(NIM_DELETE, &data);
            _added = false;
        }

        if (_icon != 0)
        {
            DestroyIcon(_icon);
            _icon = 0;
        }

        _owner.RemoveHook(WndProc);
        _owner.Dispose();
    }

    private void Add()
    {
        ReplaceIcon();
        var data = NewData(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        _added = Shell_NotifyIcon(NIM_ADD, &data);
        if (_added)
        {
            data.uVersion = NOTIFYICON_VERSION_4;
            Shell_NotifyIcon(NIM_SETVERSION, &data);
        }
        else
        {
            Log.Warning("IrisAura Island tray icon could not be added");
        }
    }

    private void UpdateIcon()
    {
        if (!_added)
        {
            return;
        }

        ReplaceIcon();
        var data = NewData(NIF_ICON);
        Shell_NotifyIcon(NIM_MODIFY, &data);
    }

    private NOTIFYICONDATAW NewData(uint flags)
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = _owner.Handle,
            uID = IconId,
            uFlags = flags,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
        };

        var tip = Tooltip.AsSpan();
        for (var i = 0; i < tip.Length && i < 127; i++)
        {
            data.szTip[i] = tip[i];
        }

        return data;
    }

    private void ReplaceIcon()
    {
        var old = _icon;
        _icon = CreateGlyphIcon();
        if (old != 0)
        {
            DestroyIcon(old);
        }
    }

    private nint CreateGlyphIcon()
    {
        var dpi = GetDpiForWindow(_owner.Handle);
        var size = GetSystemMetricsForDpi(SM_CXSMICON, dpi == 0 ? 96 : dpi);
        if (size <= 0)
        {
            size = 16;
        }

        var png = RenderGlyphPng(size, IsTaskbarLight());
        fixed (byte* bytes = png)
        {
            // PNG-compressed icon images are supported by CreateIconFromResourceEx (Windows Vista+).
            return CreateIconFromResourceEx(bytes, (uint)png.Length, true, 0x00030000, size, size, 0);
        }
    }

    /// <summary>Monochrome bracket-person glyph (BLUEPRINT §0.6 tray icon), stroked, transparent background.</summary>
    internal static byte[] RenderGlyphPng(int size, bool lightTaskbar)
    {
        var resources = Application.Current;
        var brackets = (Geometry)resources.FindResource("Aura.Glyph.FaceBrackets");
        var person = (Geometry)resources.FindResource("Aura.Glyph.FacePerson");
        var brush = (Brush)resources.FindResource(lightTaskbar ? "Aura.Void" : "Aura.Frost");

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / 24.0, size / 24.0));
            var pen = new Pen(brush, 2.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            dc.DrawGeometry(null, pen, brackets);
            dc.DrawGeometry(null, pen, person);
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static bool IsTaskbarLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            var notification = (int)(lParam & 0xFFFF);
            if (notification is WM_CONTEXTMENU or NIN_SELECT or NIN_KEYSELECT)
            {
                handled = true;
                var x = (short)(wParam & 0xFFFF);
                var y = (short)((wParam >> 16) & 0xFFFF);
                MenuRequested?.Invoke(this, (x, y));
            }
        }
        else if (msg == (int)_taskbarCreatedMessage)
        {
            _added = false; // Explorer restarted
            Add();
        }
        else if (msg == WM_SETTINGCHANGE && lParam != 0
                 && new string((char*)lParam) == "ImmersiveColorSet")
        {
            UpdateIcon(); // light/dark taskbar changed
        }

        return 0;
    }
}
