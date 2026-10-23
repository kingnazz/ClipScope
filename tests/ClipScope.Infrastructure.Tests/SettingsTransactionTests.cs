using ClipScope.Core;
using ClipScope.Infrastructure;

namespace ClipScope.Infrastructure.Tests;

public class SettingsTransactionTests
{
    [Theory]
    [InlineData("hotkey")]
    [InlineData("startup")]
    [InlineData("file")]
    public async Task FailedSaveRestoresEveryPreparedChange(string failingStage)
    {
        var hotkey = new Change(); var startup = new Change(); var persisted = false;
        await Assert.ThrowsAsync<IOException>(() => SettingsTransaction.SaveAsync(new(),
            () => failingStage == "hotkey" ? throw new IOException() : Task.FromResult<ISettingsChange?>(hotkey),
            () => failingStage == "startup" ? throw new IOException() : startup,
            _ => { if (failingStage == "file") throw new IOException(); persisted = true; return Task.CompletedTask; }));
        Assert.False(persisted);
        Assert.False(hotkey.Committed); Assert.False(startup.Committed);
        Assert.Equal(failingStage != "hotkey", hotkey.Disposed);
        Assert.Equal(failingStage == "file", startup.Disposed);
    }
    [Fact]
    public async Task SuccessfulSaveCommitsOnlyAfterPersistenceThenReleasesSnapshots()
    {
        var hotkey = new Change(); var startup = new Change();
        await SettingsTransaction.SaveAsync(new(), () => Task.FromResult<ISettingsChange?>(hotkey), () => startup,
            _ => { Assert.False(hotkey.Committed); Assert.False(startup.Committed); return Task.CompletedTask; });
        Assert.True(hotkey.Committed && startup.Committed && hotkey.Disposed && startup.Disposed);
    }
    [Fact]
    public async Task RollbackFailureIsReportedAndDoesNotPreventOtherRollback()
    {
        var hotkey = new Change(); var startup = new Change { FailRollback = true };
        await Assert.ThrowsAsync<SettingsRollbackException>(() => SettingsTransaction.SaveAsync(new(),
            () => Task.FromResult<ISettingsChange?>(hotkey), () => startup, _ => throw new IOException()));
        Assert.True(hotkey.Disposed); Assert.False(hotkey.Committed);
    }
    private sealed class Change : ISettingsChange
    {
        public bool Committed, Disposed, FailRollback;
        public Task CommitAsync() { Committed = true; return Task.CompletedTask; }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            if (!Committed && FailRollback) throw new IOException();
            return ValueTask.CompletedTask;
        }
    }
}
