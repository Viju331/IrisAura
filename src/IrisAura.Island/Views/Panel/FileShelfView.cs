using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Media;
using IrisAura.Island.Engine;
using Serilog;

namespace IrisAura.Island.Views.Panel;

/// <summary>A file or folder in the tray.</summary>
public sealed class ShelfItemView : ObservableObject
{
    private ImageSource? _icon;
    private bool _iconRequested;

    public ShelfItemView(string path, Action<ShelfItemView> remove)
    {
        Path = path;
        var name = System.IO.Path.GetFileName(path);
        Name = string.IsNullOrEmpty(name) ? path : name;
        OpenCommand = new RelayCommand(Open);
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public string Path { get; }

    public string Name { get; }

    /// <summary>Shell icon, loaded on first use.</summary>
    public ImageSource? Icon
    {
        get
        {
            if (!_iconRequested)
            {
                // SHGetFileInfo needs a COM apartment: load on the UI thread at low priority (a few ms per file).
                _iconRequested = true;
                System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    () =>
                    {
                        _icon = AppVisualsResolver.ShellIcon(Path);
                        OnPropertyChanged(nameof(Icon));
                    });
            }

            return _icon;
        }
    }

    public RelayCommand OpenCommand { get; }

    public RelayCommand RemoveCommand { get; }

    /// <summary>Opens the file or folder with its default app (the user's click).</summary>
    public void Open()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Information("File tray: could not open an item ({Error})", ex.GetType().Name);
        }
    }
}

/// <summary>
/// View model of the panel's Tray tab (BLUEPRINT §8.6.12, §8.13A): drop files on the Island to keep shortcuts, drag
/// them out to any app, click to open, remove one or clear all. Persisted with <see cref="FileShelfStore"/>.
/// Paths are never logged.
/// </summary>
public sealed class FileShelfView : ObservableObject
{
    private readonly FileShelf _shelf;
    private readonly FileShelfStore? _store;
    private readonly Func<string, bool> _exists;

    public FileShelfView(FileShelfStore? store, Func<string, bool>? exists = null)
    {
        _store = store;
        _exists = exists ?? FileShelf.Exists;
        _shelf = new FileShelf(store?.Load());
        if (_shelf.PruneMissing(_exists))
        {
            Save();
        }

        ClearCommand = new RelayCommand(Clear);
        Rebuild();
    }

    public ObservableCollection<ShelfItemView> Items { get; } = [];

    public bool HasItems => Items.Count > 0;

    public RelayCommand ClearCommand { get; }

    public string Summary => Items.Count switch
    {
        0 => "Tray empty",
        1 => "1 item",
        var n => $"{n} items",
    };

    /// <summary>Adds dropped paths (files or folders that exist).</summary>
    public void Add(IEnumerable<string> paths)
    {
        if (_shelf.Add(paths, _exists))
        {
            Save();
            Rebuild();
        }
    }

    public void Remove(ShelfItemView item)
    {
        if (_shelf.Remove(item.Path))
        {
            Save();
            Rebuild();
        }
    }

    public void Clear()
    {
        if (_shelf.Clear())
        {
            Save();
            Rebuild();
        }
    }

    private void Save() => _store?.Save(_shelf.Items);

    private void Rebuild()
    {
        var existing = Items.ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);
        Items.Clear();
        foreach (var path in _shelf.Items)
        {
            Items.Add(existing.TryGetValue(path, out var item) ? item : new ShelfItemView(path, Remove));
        }

        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(Summary));
    }
}
