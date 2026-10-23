using ClipScope.Core;

namespace ClipScope.Infrastructure;

// Prepared OS changes retain the previous state until the atomic file replace.
public interface ISettingsChange : IAsyncDisposable
{
    Task CommitAsync();
}

public static class SettingsTransaction
{
    public static async Task SaveAsync(AppSettings settings,
        Func<Task<ISettingsChange?>> prepareHotkey, Func<ISettingsChange?> prepareStartup,
        Func<AppSettings, Task> persist)
    {
        ISettingsChange? hotkey = null, startup = null;
        Exception? failure = null;
        try
        {
            settings.Validate();
            hotkey = await prepareHotkey();
            startup = prepareStartup();
            await persist(settings);
            // Commit only releases the old hotkey / registry snapshot; it performs
            // no new registration or disk write that could invalidate the save.
            if (hotkey is not null) await hotkey.CommitAsync();
            if (startup is not null) await startup.CommitAsync();
        }
        catch (Exception e) { failure = e; }
        List<Exception> rollbackErrors = [];
        foreach (var change in new[] { startup, hotkey })
            if (change is not null)
                try { await change.DisposeAsync(); }
                catch (Exception e) { rollbackErrors.Add(e); }
        if (rollbackErrors.Count > 0)
            throw new SettingsRollbackException(new AggregateException(new[] { failure }.OfType<Exception>().Concat(rollbackErrors)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

public sealed class SettingsRollbackException(Exception inner) : Exception("An operating system settings change could not be rolled back.", inner);
