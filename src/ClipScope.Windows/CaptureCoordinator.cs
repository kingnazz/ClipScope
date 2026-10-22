using System.Threading.Channels;
using ClipScope.Core;
using ClipScope.Infrastructure;

namespace ClipScope.Windows;

public sealed class CaptureCoordinator : IAsyncDisposable
{
    private readonly Channel<(CapturedClip Clip, long Bytes)> _queue = Channel.CreateBounded<(CapturedClip, long)>(
        new BoundedChannelOptions(16) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Func<AppSettings> _settings;
    private readonly IClipRepository _repository;
    private readonly MediaStore _media;
    private readonly IDiagnosticLog _log;
    private readonly WindowContext _context;
    private readonly RetentionService _retention;
    private readonly Task _worker;
    private long _queuedBytes;
    private int _screenshotBusy, _savedCount;
    public event Action? HistoryChanged;
    public CaptureCoordinator(Func<AppSettings> settings, IClipRepository repository, MediaStore media, WindowContext context, IDiagnosticLog log)
    {
        _settings = settings; _repository = repository; _media = media; _context = context; _log = log;
        _retention = new(repository, media, log); _worker = Task.Run(ProcessAsync);
    }
    public void Enqueue(CapturedClip clip)
    {
        var p = clip.Payload;
        var bytes = (long)(p.Dib?.Length ?? 0) + 2L * ((p.PlainText?.Length ?? 0) + (p.Html?.Length ?? 0) + (p.Rtf?.Length ?? 0)
            + p.Files.Sum(x => x.Length) + p.Formats.Sum(x => x.Length));
        if (Interlocked.Add(ref _queuedBytes, bytes) > 256L * 1024 * 1024)
        { Interlocked.Add(ref _queuedBytes, -bytes); _log.Write("capture.queue_byte_limit"); return; }
        if (!_queue.Writer.TryWrite((clip, bytes))) { Interlocked.Add(ref _queuedBytes, -bytes); _log.Write("capture.queue_full"); }
    }
    private async Task ProcessAsync()
    {
        await foreach (var item in _queue.Reader.ReadAllAsync())
        {
            List<string> written = []; var persisted = false;
            try
            {
                var incoming = item.Clip; var payload = incoming.Payload; var settings = _settings();
                if (!settings.CaptureEnabled || PrivacyPolicy.IsExcluded(incoming.Source, settings)) continue;
                var clip = new Clip { CreatedUtc = incoming.CapturedAt.ToUniversalTime(), LocalOffset = incoming.CapturedAt.Offset,
                    SequenceNumber = incoming.Sequence, Kind = payload.Kind, PlainText = payload.PlainText,
                    Html = payload.Html, Rtf = payload.Rtf, Files = payload.Files, Formats = payload.Formats,
                    Source = incoming.Source, ContentHash = ContentPolicy.Hash(payload) };
                if (clip.PlainText is null && clip.Html is not null) clip = clip with { PlainText = ContentPolicy.HtmlText(clip.Html) };
                if (payload.Dib is not null)
                {
                    try
                    {
                        var image = WindowImages.DecodeDib(payload.Dib);
                        var original = await _media.WriteAsync(clip.Id, "content", image.Original); written.Add(original);
                        var thumbnail = await _media.WriteAsync(clip.Id, "thumb", image.Thumbnail); written.Add(thumbnail);
                        clip = clip with { PrimaryMediaPath = original, ThumbnailPath = thumbnail };
                    }
                    catch (Exception e) { _log.Write("image.decode_failed", e); if (clip.Kind == ClipKind.Image && clip.PlainText is null) continue; }
                }
                try
                {
                    if (WindowImages.ApplicationIcon(incoming.Source.ExecutablePath) is { } icon)
                    {
                        var path = await _media.WriteAsync(clip.Id, "icon", icon); written.Add(path);
                        clip = clip with { Source = clip.Source with { IconPath = path } };
                    }
                }
                catch (Exception e) { _log.Write("icon.capture_failed", e); }
                if (!_settings().CaptureEnabled || PrivacyPolicy.IsExcluded(incoming.Source, _settings())) continue;
                persisted = await _repository.AddAsync(clip, TimeSpan.FromSeconds(2));
                if (!persisted) continue;
                HistoryChanged?.Invoke();
                ScheduleScreenshot(clip);
                if (++_savedCount % 50 == 0) await CleanupAsync();
            }
            catch (Exception e) { _log.Write("capture.persist_failed", e); }
            finally { if (!persisted) _media.Delete(written); Interlocked.Add(ref _queuedBytes, -item.Bytes); }
        }
    }
    private void ScheduleScreenshot(Clip clip)
    {
        if (!_settings().ScreenshotsEnabled || DateTimeOffset.UtcNow - clip.CreatedUtc > TimeSpan.FromSeconds(1)
            || Interlocked.CompareExchange(ref _screenshotBusy, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            List<string> written = []; var attached = false;
            try
            {
                bool Allowed()
                {
                    var settings = _settings(); var foreground = _context.Foreground();
                    return settings.CaptureEnabled && settings.ScreenshotsEnabled
                        && DateTimeOffset.UtcNow - clip.CreatedUtc <= TimeSpan.FromSeconds(2)
                        && !PrivacyPolicy.IsExcluded(clip.Source, settings) && !PrivacyPolicy.IsExcluded(foreground, settings)
                        && foreground.ProcessId == clip.Source.ProcessId && foreground.WindowHandle == clip.Source.WindowHandle
                        && foreground.WindowTitle == clip.Source.WindowTitle;
                }
                if (!Allowed()) return;
                var result = WindowImages.CaptureWindow(clip.Source);
                if (result is null || !Allowed()) { _log.Write("screenshot.unavailable"); return; }
                var path = await _media.WriteAsync(clip.Id, "context", result.Value.Original); written.Add(path);
                // An image clip already has a content thumbnail. Do not overwrite it.
                var thumb = clip.ThumbnailPath;
                if (thumb is null) { thumb = await _media.WriteAsync(clip.Id, "thumb", result.Value.Thumbnail); written.Add(thumb); }
                if (!Allowed()) return;
                attached = await _repository.AttachContextAsync(clip.Id, path, thumb, null);
                if (attached) HistoryChanged?.Invoke();
            }
            catch (Exception e) { _log.Write("screenshot.capture_failed", e); }
            finally { if (!attached) _media.Delete(written); Interlocked.Exchange(ref _screenshotBusy, 0); }
        });
    }
    public async Task<RetentionResult?> CleanupAsync()
    {
        try
        {
            var result = await _retention.RunAsync(_settings(), DateTimeOffset.UtcNow);
            if (_repository is SqliteClipRepository sqlite) sqlite.Checkpoint();
            if (result.RemovedClips > 0) HistoryChanged?.Invoke();
            return result;
        }
        catch (Exception e) { _log.Write("storage.cleanup_failed", e); return null; }
    }
    public async ValueTask DisposeAsync()
    { _queue.Writer.TryComplete(); await _worker.WaitAsync(TimeSpan.FromSeconds(3)); }
}
