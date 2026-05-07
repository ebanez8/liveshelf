namespace LiveShelf;

internal readonly record struct AgentBadge(
    ShelfBadgeKind Kind,
    string Label,
    string Detail,
    bool Notify);

