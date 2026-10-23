using ClipScope.Core;

namespace ClipScope.Core.Tests;

public class HistorySessionTests
{
    [Fact]
    public void SearchMustCompleteBeforeAnyCommandCanUseOldRows()
    {
        var session = Ready(out _);
        session.BeginSearch(); // typing, changing filter or page uses the same fence
        Assert.Null(session.Target);
    }

    [Fact]
    public void LateOrCancelledSearchCannotPublishOrEnableCommands()
    {
        var session = Ready(out _);
        var old = session.BeginSearch();
        var current = session.BeginSearch();
        Assert.False(session.CompleteSearch(old));
        Assert.Null(session.Target); // also the state after a failed current search
        Assert.True(session.CompleteSearch(current));
    }

    [Fact]
    public async Task ChangingSelectionDuringDetailLoadInvalidatesOriginalTarget()
    {
        var session = Ready(out var id);
        var target = session.Target!;
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> Resolve() { await loaded.Task; return session.IsCurrent(target); }
        var command = Resolve();
        session.Select(Guid.NewGuid());
        session.Select(id); // even selecting away and back invalidates the old action
        loaded.SetResult();
        Assert.False(await command);
    }

    [Fact]
    public void NewCaptureRefreshOrDeletionInvalidatesAnAwaitingCommand()
    {
        var session = Ready(out _);
        var target = session.Target!;
        var next = session.BeginSearch();
        session.CompleteSearch(next);
        Assert.False(session.IsCurrent(target));
        session.Select(null);
        Assert.Null(session.Target);
    }

    private static HistorySession Ready(out Guid id)
    {
        var session = new HistorySession();
        session.CompleteSearch(session.BeginSearch());
        id = Guid.NewGuid(); session.Select(id);
        return session;
    }
}
