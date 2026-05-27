using System.Collections.ObjectModel;

namespace LiveShelf;

internal sealed record ShelfTarget(
    DisplayMonitor Monitor,
    IntPtr ShelfHwnd,
    MainWindow Window,
    ObservableCollection<ShelvedWindow> Items);
