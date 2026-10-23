using ClipScope.Core;
using ClipScope.Infrastructure;
using Microsoft.Data.Sqlite;

namespace ClipScope.Infrastructure.Tests;

public sealed class StorageTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ClipScope-tests", Guid.NewGuid().ToString("N"));
    private DataPaths _paths = null!;
    private SqliteClipRepository _repository = null!;
    private MediaStore _media = null!;
    private readonly TestLog _log = new();
    public async ValueTask InitializeAsync()
    {
        _paths = new(_directory); _media = new(_paths, _log); _repository = new(_paths, _log);
        await _repository.InitializeAsync(ct: TestContext.Current.CancellationToken);
    }
    public ValueTask DisposeAsync()
    { SqliteConnection.ClearAllPools(); Directory.Delete(_directory, true); return ValueTask.CompletedTask; }
    private static Clip Item(string text = "192.168.1.15", int ageDays = 0) => new()
    {
        PlainText = text, ContentHash = ContentPolicy.Hash(new() { PlainText = text }),
        CreatedUtc = DateTimeOffset.UtcNow.AddDays(-ageDays),
        Source = new() { AppName = "Visual Studio", ProcessName = "devenv", ProcessId = 12, WindowTitle = "network.cs", WindowHandle = 14 },
        Formats = ["CF_UNICODETEXT"], LocalOffset = TimeSpan.FromHours(-7)
    };
    private Task<bool> Add(Clip clip) => _repository.AddAsync(clip, TimeSpan.FromSeconds(2), ct: TestContext.Current.CancellationToken);
    [Fact]
    public async Task MigrationsAreRepeatableAndContentSurvivesReopen()
    {
        var clip = Item() with { Html = "<b>network</b>", Rtf = "{\\rtf1 network}" };
        Assert.True(await Add(clip)); await _repository.InitializeAsync(ct: TestContext.Current.CancellationToken);
        var reopened = new SqliteClipRepository(_paths, _log);
        var found = await reopened.GetAsync(clip.Id, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(found); Assert.Equal(clip.Html, found.Html); Assert.Equal(clip.Rtf, found.Rtf);
        Assert.Equal(clip.LocalOffset, found.LocalOffset); Assert.Equal(clip.Source, found.Source);
    }
    [Fact]
    public async Task DedupIsAtomicAndContextAware()
    {
        var clip = Item(); var repeat = clip with { Id = Guid.NewGuid(), CreatedUtc = clip.CreatedUtc.AddMilliseconds(100) };
        Assert.True(await Add(clip)); Assert.False(await Add(repeat));
        Assert.True(await Add(repeat with { Source = clip.Source with { WindowTitle = "different.cs" } }));
        Assert.Equal(2, (await _repository.SearchAsync(new(), ct: TestContext.Current.CancellationToken)).Count);
    }
    [Fact]
    public async Task ConcurrentRepeatedCopiesDoNotCreateGarbage()
    {
        var clip = Item();
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Add(clip with { Id = Guid.NewGuid() })));
        Assert.Single(results, x => x);
    }
    [Theory]
    [InlineData("192.168")]
    [InlineData("Visual")]
    [InlineData("devenv")]
    [InlineData("network")]
    [InlineData("ネットワーク")]
    public async Task FtsSearchesContentAndSource(string query)
    {
        await Add(Item("192.168.1.15 ネットワーク"));
        Assert.Single(await _repository.SearchAsync(new(query), ct: TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task FileNamesAreSearchableAndFiltersWork()
    {
        var clip = Item() with { Kind = ClipKind.Files, Files = [@"C:\Reports\budget.xlsx"] };
        await Add(clip); await Add(Item("image") with { Kind = ClipKind.Image });
        await _repository.SetPinnedAsync(clip.Id, true, ct: TestContext.Current.CancellationToken);
        Assert.Single(await _repository.SearchAsync(new("budget", ClipFilter.Files), ct: TestContext.Current.CancellationToken));
        Assert.Single(await _repository.SearchAsync(new(Filter: ClipFilter.Images), ct: TestContext.Current.CancellationToken));
        Assert.Single(await _repository.SearchAsync(new(Filter: ClipFilter.Pinned), ct: TestContext.Current.CancellationToken));
        Assert.Empty(await _repository.SearchAsync(new(Filter: ClipFilter.Text), ct: TestContext.Current.CancellationToken));
    }
    [Theory]
    [InlineData("\" OR * NEAR() --")]
    [InlineData("' ; DROP TABLE clips;")]
    [InlineData("*** : ()")]
    public async Task SearchSyntaxCannotBreakQuery(string input)
    {
        await Add(Item()); await _repository.SearchAsync(new(input), ct: TestContext.Current.CancellationToken);
        Assert.Single(await _repository.SearchAsync(new(), ct: TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task PaginationAndCancellationWork()
    {
        for (var i = 0; i < 5; i++) await Add(Item("item " + i));
        var first = await _repository.SearchAsync(new(Limit: 2), ct: TestContext.Current.CancellationToken);
        var second = await _repository.SearchAsync(new(Limit: 2, Offset: 2), ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, first.Count); Assert.Empty(first.Select(x => x.Id).Intersect(second.Select(x => x.Id)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _repository.SearchAsync(new(), new CancellationToken(true)));
    }
    [Fact]
    public async Task DeletionUpdatesFtsAndContextCannotResurrectDeletedClip()
    {
        var clip = Item(); await Add(clip);
        Assert.False(Assert.Single(await _repository.SearchAsync(new("192"), ct: TestContext.Current.CancellationToken)).HasContextScreenshot);
        Assert.True(await _repository.AttachContextAsync(clip.Id, "media/a.png", "thumbnails/a.png", null, ct: TestContext.Current.CancellationToken));
        Assert.True(Assert.Single(await _repository.SearchAsync(new("192"), ct: TestContext.Current.CancellationToken)).HasContextScreenshot);
        var references = await _repository.DeleteAsync(clip.Id, ct: TestContext.Current.CancellationToken);
        Assert.Contains("media/a.png", references); Assert.Empty(await _repository.SearchAsync(new("192"), ct: TestContext.Current.CancellationToken));
        Assert.False(await _repository.AttachContextAsync(clip.Id, "media/b.png", "thumbnails/b.png", null, ct: TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task ClearUsesCurrentMediaAfterLateScreenshotAttachment()
    {
        var clip = Item("late snapshot"); await Add(clip);
        var snapshot = Assert.Single(await _repository.ListForMaintenanceAsync(TestContext.Current.CancellationToken));
        Assert.Empty(snapshot.MediaPaths);
        var context = await _media.WriteAsync(clip.Id, "context", [1, 2, 3], TestContext.Current.CancellationToken);
        var thumb = await _media.WriteAsync(clip.Id, "thumb", [4, 5, 6], TestContext.Current.CancellationToken);
        await _repository.AttachContextAsync(clip.Id, context, thumb, null, TestContext.Current.CancellationToken);
        var deleted = await _repository.DeleteUnpinnedWithMediaAsync(snapshot.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(deleted);
        _media.Delete(deleted);
        Assert.False(File.Exists(_paths.ResolveMedia(context)));
        Assert.False(File.Exists(_paths.ResolveMedia(thumb)));
        Assert.Empty(await _repository.SearchAsync(new("snapshot"), TestContext.Current.CancellationToken));
        Assert.False(await _repository.AttachContextAsync(clip.Id, context, thumb, null, TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task ClearRechecksPinAndDoesNotDeleteClipsCapturedAfterItsSnapshot()
    {
        var clip = Item("protect"); await Add(clip);
        var snapshot = await _repository.ListForMaintenanceAsync(TestContext.Current.CancellationToken);
        await _repository.SetPinnedAsync(clip.Id, true, TestContext.Current.CancellationToken);
        var newer = Item("arrived during clear"); await Add(newer);
        foreach (var candidate in snapshot)
            Assert.Null(await _repository.DeleteUnpinnedWithMediaAsync(candidate.Id, TestContext.Current.CancellationToken));
        Assert.True((await _repository.GetAsync(clip.Id, TestContext.Current.CancellationToken))!.IsPinned);
        Assert.NotNull(await _repository.GetAsync(newer.Id, TestContext.Current.CancellationToken));
        await _repository.SetPinnedAsync(clip.Id, false, TestContext.Current.CancellationToken);
        Assert.NotNull(await _repository.DeleteUnpinnedWithMediaAsync(clip.Id, TestContext.Current.CancellationToken));
        Assert.Null(await _repository.DeleteUnpinnedWithMediaAsync(clip.Id, TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task RetentionRemovesOldUnpinnedMediaAndPreservesPinned()
    {
        var old = Item("old", 60); var pinned = Item("pinned", 90) with { IsPinned = true };
        var path = await _media.WriteAsync(old.Id, "content", [1,2,3], ct: TestContext.Current.CancellationToken); old = old with { PrimaryMediaPath = path };
        await Add(old); await Add(pinned); await Add(Item("new"));
        var result = await new RetentionService(_repository, _media, _log).RunAsync(new(), DateTimeOffset.UtcNow, ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, result.RemovedClips); Assert.False(File.Exists(_paths.ResolveMedia(path)));
        Assert.NotNull(await _repository.GetAsync(pinned.Id, ct: TestContext.Current.CancellationToken)); Assert.Equal(2, (await _repository.SearchAsync(new(), ct: TestContext.Current.CancellationToken)).Count);
    }
    [Fact]
    public async Task StorageBudgetEvictsOldestUnpinned()
    {
        var first = Item("first", 2); var second = Item("second", 1);
        var one = await _media.WriteAsync(first.Id, "content", new byte[12 * 1024 * 1024], ct: TestContext.Current.CancellationToken);
        var two = await _media.WriteAsync(second.Id, "content", new byte[12 * 1024 * 1024], ct: TestContext.Current.CancellationToken);
        await Add(first with { PrimaryMediaPath = one }); await Add(second with { PrimaryMediaPath = two });
        var result = await new RetentionService(_repository, _media, _log).RunAsync(new() { MaxMediaBytes = 16 * 1024 * 1024 }, DateTimeOffset.UtcNow, ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, result.RemovedClips); Assert.Null(await _repository.GetAsync(first.Id, ct: TestContext.Current.CancellationToken)); Assert.NotNull(await _repository.GetAsync(second.Id, ct: TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task PinnedMediaMayExceedBudgetWithoutBeingDeleted()
    {
        var clip = Item("pinned") with { IsPinned = true };
        var path = await _media.WriteAsync(clip.Id, "content", new byte[17 * 1024 * 1024], TestContext.Current.CancellationToken);
        await Add(clip with { PrimaryMediaPath = path });
        var result = await new RetentionService(_repository, _media, _log).RunAsync(
            new() { MaxMediaBytes = 16 * 1024 * 1024 }, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
        Assert.True(result.PinnedBudgetExceeded); Assert.Equal(0, result.RemovedClips);
        Assert.True(File.Exists(_paths.ResolveMedia(path)));
    }
    [Fact]
    public async Task SettingsRoundTripAndCorruptionFailsClosed()
    {
        var store = new SettingsStore(_paths, _log);
        var settings = new AppSettings { ScreenshotsEnabled = true, Theme = AppTheme.Dark, ExcludedApplications = ["vault.exe"], Shortcut = new(3, 0x4A) };
        await store.SaveAsync(settings, ct: TestContext.Current.CancellationToken); var restored = await store.LoadAsync(ct: TestContext.Current.CancellationToken);
        Assert.True(restored.ScreenshotsEnabled); Assert.Equal(settings.ExcludedApplications, restored.ExcludedApplications);
        Assert.Equal(settings.Shortcut, restored.Shortcut); Assert.Equal(settings.Theme, restored.Theme);
        await File.WriteAllTextAsync(_paths.Settings, "{broken", cancellationToken: TestContext.Current.CancellationToken); Assert.False((await store.LoadAsync(ct: TestContext.Current.CancellationToken)).CaptureEnabled);
    }
    [Fact]
    public async Task OrphansRespectGracePeriodAndPathTraversalIsRejected()
    {
        var old = await _media.WriteAsync(Guid.NewGuid(), "context", [1], ct: TestContext.Current.CancellationToken);
        var fresh = await _media.WriteAsync(Guid.NewGuid(), "context", [2], ct: TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(_paths.ResolveMedia(old), DateTime.UtcNow.AddHours(-2));
        _media.RemoveOrphans(new HashSet<string>(), DateTimeOffset.UtcNow.AddHours(-1));
        Assert.False(File.Exists(_paths.ResolveMedia(old))); Assert.True(File.Exists(_paths.ResolveMedia(fresh)));
        Assert.Throws<ArgumentException>(() => _paths.ResolveMedia("../private.txt"));
        Assert.Throws<ArgumentException>(() => _paths.ResolveMedia(_paths.Database));
    }
    [Fact]
    public async Task ANewlyPinnedClipCannotBeDeletedByStaleMaintenance()
    {
        var clip = Item("keep", 90); await Add(clip);
        var snapshot = await _repository.ListForMaintenanceAsync(TestContext.Current.CancellationToken);
        Assert.False(Assert.Single(snapshot).IsPinned);
        await _repository.SetPinnedAsync(clip.Id, true, TestContext.Current.CancellationToken);
        Assert.False(await _repository.DeleteIfUnpinnedAsync(clip.Id, TestContext.Current.CancellationToken));
        Assert.NotNull(await _repository.GetAsync(clip.Id, TestContext.Current.CancellationToken));
    }
    [Fact]
    public void DiagnosticLogsRotateAndNeverIncludeExceptionMessages()
    {
        var log = new LocalDiagnosticLog(_paths);
        for (var i = 0; i < 40000; i++) log.Write("capture.failed", new IOException("SECRET_CLIPBOARD_CONTENT"));
        var files = Directory.GetFiles(_paths.Logs);
        Assert.InRange(files.Length, 2, 4);
        Assert.All(files, file => {
            Assert.True(new FileInfo(file).Length < 1_049_000);
            Assert.DoesNotContain("SECRET_CLIPBOARD_CONTENT", File.ReadAllText(file));
        });
    }
    private sealed class TestLog : IDiagnosticLog { public void Write(string eventName, Exception? error = null) { } }
}
