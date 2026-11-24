using static IrisAura.Island.Interop.NativeMethods;

namespace IrisAura.Island.Shell;

/// <summary>An item of a native popup menu.</summary>
public sealed record MenuItemSpec(
    int Id,
    string Text,
    bool Checked = false,
    bool Enabled = true,
    IReadOnlyList<MenuItemSpec>? Children = null)
{
    public static MenuItemSpec Separator { get; } = new(0, string.Empty);

    public bool IsSeparator => Id == 0 && Text.Length == 0;
}

/// <summary>
/// Native Win32 popup menu (documented <c>TrackPopupMenuEx</c>). Used for the Island's right-click
/// menu and the tray icon. The owner window is brought to the foreground for the menu's lifetime, as
/// Windows requires for correct dismissal, and focus is handed back to the previous window afterwards
/// unless the chosen command opens a window of its own.
/// </summary>
internal static class NativeMenu
{
    /// <summary>Shows the menu and returns the chosen item id (0 = dismissed) and the window that had focus before.</summary>
    public static (int Command, nint PreviousForeground) Show(nint owner, int x, int y, IReadOnlyList<MenuItemSpec> items, bool openUpwards)
    {
        var previous = GetForegroundWindow();
        var menu = Build(items);
        try
        {
            SetForegroundWindow(owner);
            var flags = TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY | (openUpwards ? TPM_BOTTOMALIGN : 0);
            var command = TrackPopupMenuEx(menu, flags, x, y, owner, 0);
            PostMessage(owner, WM_NULL, 0, 0); // documented: lets the menu close cleanly
            return (command, previous);
        }
        finally
        {
            DestroyMenu(menu); // also destroys submenus
        }
    }

    /// <summary>Gives focus back to <paramref name="window"/> if it still exists.</summary>
    public static void RestoreForeground(nint window)
    {
        if (window != 0 && IsWindow(window))
        {
            SetForegroundWindow(window);
        }
    }

    private static nint Build(IReadOnlyList<MenuItemSpec> items)
    {
        var menu = CreatePopupMenu();
        foreach (var item in items)
        {
            if (item.IsSeparator)
            {
                AppendMenu(menu, MF_SEPARATOR, 0, null);
            }
            else if (item.Children is { Count: > 0 } children)
            {
                AppendMenu(menu, MF_POPUP | (item.Enabled ? 0 : MF_GRAYED), (nuint)Build(children), item.Text);
            }
            else
            {
                var flags = MF_STRING | (item.Checked ? MF_CHECKED : 0) | (item.Enabled ? 0 : MF_GRAYED);
                AppendMenu(menu, flags, (nuint)item.Id, item.Text);
            }
        }

        return menu;
    }
}
