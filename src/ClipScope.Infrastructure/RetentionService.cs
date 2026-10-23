using ClipScope.Core;

namespace ClipScope.Infrastructure;

public sealed class RetentionService(IClipRepository repository, MediaStore media, IDiagnosticLog log)
{
    private readonly SemaphoreSlim _gate = new(1);
    public async Task<RetentionResult> RunAsync(AppSettings settings, DateTimeOffset now, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var clips = (await repository.ListForMaintenanceAsync(ct)).ToList();
            var sizes = clips.ToDictionary(x => x.Id, x => x.MediaPaths.Distinct().Sum(media.Size));
            var total = sizes.Values.Sum(); var removed = 0;
            foreach (var clip in clips.Where(x => !x.IsPinned).ToArray())
            {
                if (clip.CreatedUtc >= now.AddDays(-settings.RetentionDays) && total <= settings.MaxMediaBytes) continue;
                if (await repository.DeleteUnpinnedWithMediaAsync(clip.Id, ct) is not { } references) continue;
                media.Delete(references);
                total -= sizes[clip.Id]; clips.Remove(clip); removed++;
            }
            // Capture/context attachment may have changed references during cleanup.
            var retained = await repository.ListForMaintenanceAsync(ct);
            media.RemoveOrphans(retained.SelectMany(x => x.MediaPaths).ToHashSet(), now.AddHours(-1));
            log.Write("storage.cleanup");
            return new(removed, total, total > settings.MaxMediaBytes);
        }
        finally { _gate.Release(); }
    }
}
