namespace ClipScope.Core;

// A UI-thread-owned generation fence. A command keeps its original target across
// awaits; a newer search or selection invalidates it instead of retargeting it.
public sealed class HistorySession
{
    public long Version { get; private set; }
    public bool IsReady { get; private set; }
    private long _selection;
    private Guid? _id;

    public long BeginSearch()
    {
        IsReady = false;
        return ++Version;
    }
    public bool CompleteSearch(long version)
    {
        if (Version != version) return false;
        IsReady = true;
        return true;
    }
    public void Select(Guid? id) { _id = id; _selection++; }
    public HistoryTarget? Target => IsReady && _id is { } id ? new(Version, _selection, id) : null;
    public bool IsCurrent(HistoryTarget target) => Target == target;
}

public sealed record HistoryTarget(long SearchVersion, long SelectionVersion, Guid Id);
