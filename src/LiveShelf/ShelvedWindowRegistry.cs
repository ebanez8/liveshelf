using System.IO;
using System.Text.Json;

namespace LiveShelf;

internal static class ShelvedWindowRegistry
{
    private static readonly object SyncRoot = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static void AddOrUpdate(ShelvedWindow item)
    {
        lock (SyncRoot)
        {
            var entries = LoadEntries();
            entries.RemoveAll(entry => entry.Hwnd == item.SourceHwnd.ToInt64());
            entries.Add(ShelvedWindowEntry.From(item));
            SaveEntries(entries);
        }
    }

    public static void Remove(ShelvedWindow item)
    {
        Remove(item.SourceHwnd);
    }

    public static void Remove(IntPtr hwnd)
    {
        lock (SyncRoot)
        {
            var entries = LoadEntries();
            if (entries.RemoveAll(entry => entry.Hwnd == hwnd.ToInt64()) > 0)
            {
                SaveEntries(entries);
            }
        }
    }

    public static void RestoreRegisteredWindows()
    {
        lock (SyncRoot)
        {
            var entries = LoadEntries();
            if (entries.Count == 0)
            {
                return;
            }

            var remaining = new List<ShelvedWindowEntry>();
            foreach (var entry in entries)
            {
                var hwnd = new IntPtr(entry.Hwnd);
                if (!NativeMethods.IsWindow(hwnd))
                {
                    continue;
                }

                if (!TryRestore(hwnd, entry))
                {
                    remaining.Add(entry);
                }
            }

            SaveEntries(remaining);
        }
    }

    private static bool TryRestore(IntPtr hwnd, ShelvedWindowEntry entry)
    {
        try
        {
            var placement = entry.ToWindowPlacement();
            var placementApplied = NativeMethods.SetWindowPlacement(hwnd, ref placement);
            NativeMethods.ShowWindow(
                hwnd,
                placementApplied
                    ? GetRestoreShowCommand(placement.ShowCmd)
                    : NativeMethods.SW_RESTORE);

            return placementApplied;
        }
        catch
        {
            return false;
        }
    }

    private static List<ShelvedWindowEntry> LoadEntries()
    {
        var path = GetRegistryPath();
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<ShelvedWindowEntry>>(
                       File.ReadAllText(path),
                       JsonOptions) ??
                   [];
        }
        catch
        {
            return [];
        }
    }

    private static void SaveEntries(List<ShelvedWindowEntry> entries)
    {
        var path = GetRegistryPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (entries.Count == 0)
        {
            File.Delete(path);
            return;
        }

        File.WriteAllText(path, JsonSerializer.Serialize(entries, JsonOptions));
    }

    private static string GetRegistryPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiveShelf",
            "shelved-windows.json");
    }

    private static int GetRestoreShowCommand(int originalShowCommand)
    {
        return originalShowCommand switch
        {
            NativeMethods.SW_SHOWMAXIMIZED => NativeMethods.SW_SHOWMAXIMIZED,
            NativeMethods.SW_SHOWMINIMIZED or NativeMethods.SW_MINIMIZE or NativeMethods.SW_SHOWMINNOACTIVE => NativeMethods.SW_RESTORE,
            NativeMethods.SW_SHOWNORMAL or NativeMethods.SW_SHOW or NativeMethods.SW_SHOWNA or NativeMethods.SW_SHOWNOACTIVATE => originalShowCommand,
            _ => NativeMethods.SW_RESTORE
        };
    }

    private sealed class ShelvedWindowEntry
    {
        public long Hwnd { get; init; }

        public int ProcessId { get; init; }

        public string ProcessName { get; init; } = string.Empty;

        public string Title { get; init; } = string.Empty;

        public int Flags { get; init; }

        public int ShowCmd { get; init; }

        public int MinX { get; init; }

        public int MinY { get; init; }

        public int MaxX { get; init; }

        public int MaxY { get; init; }

        public int Left { get; init; }

        public int Top { get; init; }

        public int Right { get; init; }

        public int Bottom { get; init; }

        public static ShelvedWindowEntry From(ShelvedWindow item)
        {
            var placement = item.OriginalPlacement;
            return new ShelvedWindowEntry
            {
                Hwnd = item.SourceHwnd.ToInt64(),
                ProcessId = item.SourceProcessId,
                ProcessName = item.ProcessName,
                Title = item.Title,
                Flags = placement.Flags,
                ShowCmd = placement.ShowCmd,
                MinX = placement.MinPosition.X,
                MinY = placement.MinPosition.Y,
                MaxX = placement.MaxPosition.X,
                MaxY = placement.MaxPosition.Y,
                Left = placement.NormalPosition.Left,
                Top = placement.NormalPosition.Top,
                Right = placement.NormalPosition.Right,
                Bottom = placement.NormalPosition.Bottom
            };
        }

        public NativeMethods.WINDOWPLACEMENT ToWindowPlacement()
        {
            return new NativeMethods.WINDOWPLACEMENT
            {
                Length = NativeMethods.WINDOWPLACEMENT.Create().Length,
                Flags = Flags,
                ShowCmd = ShowCmd,
                MinPosition = new NativeMethods.POINT
                {
                    X = MinX,
                    Y = MinY
                },
                MaxPosition = new NativeMethods.POINT
                {
                    X = MaxX,
                    Y = MaxY
                },
                NormalPosition = new NativeMethods.RECT(Left, Top, Right, Bottom)
            };
        }
    }
}
