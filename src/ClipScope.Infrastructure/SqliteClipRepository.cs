using System.Text.Json;
using System.Text.RegularExpressions;
using ClipScope.Core;
using Microsoft.Data.Sqlite;

namespace ClipScope.Infrastructure;

public sealed partial class SqliteClipRepository(DataPaths paths, IDiagnosticLog log) : IClipRepository
{
    private readonly SemaphoreSlim _writes = new(1);
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = paths.Database, Mode = SqliteOpenMode.ReadWriteCreate, DefaultTimeout = 5,
            Pooling = true
        }.ToString());
        connection.Open();
        return connection;
    }
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _writes.WaitAsync(ct);
        try
        {
            using var db = Open();
            using var pragma = db.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
            pragma.ExecuteNonQuery();
            using var version = db.CreateCommand();
            version.CommandText = "PRAGMA user_version;";
            var current = Convert.ToInt32(version.ExecuteScalar());
            if (current > 1) throw new InvalidOperationException("Database was created by a newer ClipScope version.");
            if (current == 1) return;
            using var tx = db.BeginTransaction();
            using var migration = db.CreateCommand();
            migration.Transaction = tx;
            migration.CommandText = """
                PRAGMA auto_vacuum=INCREMENTAL;
                CREATE TABLE clips (
                    id TEXT PRIMARY KEY, created INTEGER NOT NULL, sequence INTEGER NOT NULL,
                    kind INTEGER NOT NULL, hash TEXT NOT NULL, pinned INTEGER NOT NULL DEFAULT 0,
                    source_key TEXT NOT NULL, app TEXT NOT NULL, title TEXT NOT NULL,
                    preview TEXT NOT NULL, thumbnail TEXT, icon TEXT, payload TEXT NOT NULL,
                    search_text TEXT NOT NULL);
                CREATE INDEX ix_clips_created ON clips(created DESC);
                CREATE INDEX ix_clips_pinned ON clips(pinned, created DESC);
                CREATE VIRTUAL TABLE clips_fts USING fts5(search_text, content='clips', content_rowid='rowid', tokenize='unicode61');
                CREATE TRIGGER clips_insert AFTER INSERT ON clips BEGIN
                    INSERT INTO clips_fts(rowid, search_text) VALUES(new.rowid, new.search_text);
                END;
                CREATE TRIGGER clips_delete AFTER DELETE ON clips BEGIN
                    INSERT INTO clips_fts(clips_fts,rowid,search_text) VALUES('delete',old.rowid,old.search_text);
                END;
                CREATE TRIGGER clips_update AFTER UPDATE OF search_text ON clips BEGIN
                    INSERT INTO clips_fts(clips_fts,rowid,search_text) VALUES('delete',old.rowid,old.search_text);
                    INSERT INTO clips_fts(rowid,search_text) VALUES(new.rowid,new.search_text);
                END;
                PRAGMA user_version=1;
                """;
            migration.ExecuteNonQuery();
            tx.Commit();
            log.Write("database.migrated_v1");
        }
        finally { _writes.Release(); }
    }
    public async Task<bool> AddAsync(Clip clip, TimeSpan duplicateWindow, CancellationToken ct = default)
    {
        await _writes.WaitAsync(ct);
        try
        {
            using var db = Open();
            using var tx = db.BeginTransaction();
            using var previous = db.CreateCommand();
            previous.Transaction = tx;
            previous.CommandText = "SELECT payload FROM clips ORDER BY created DESC,rowid DESC LIMIT 1;";
            if (previous.ExecuteScalar() is string json && JsonSerializer.Deserialize<Clip>(json) is { } last
                && ContentPolicy.IsDuplicate(last, clip, duplicateWindow)) return false;
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO clips(id,created,sequence,kind,hash,pinned,source_key,app,title,preview,thumbnail,icon,payload,search_text)
                VALUES($id,$created,$sequence,$kind,$hash,$pinned,$source,$app,$title,$preview,$thumbnail,$icon,$payload,$search);
                """;
            Add(cmd, "$id", clip.Id.ToString()); Add(cmd, "$created", clip.CreatedUtc.UtcTicks);
            Add(cmd, "$sequence", (long)clip.SequenceNumber); Add(cmd, "$kind", (int)clip.Kind);
            Add(cmd, "$hash", clip.ContentHash); Add(cmd, "$pinned", clip.IsPinned);
            Add(cmd, "$source", clip.Source.Identity); Add(cmd, "$app", clip.Source.AppName);
            Add(cmd, "$title", clip.Source.WindowTitle);
            var preview = clip.Kind == ClipKind.Files ? string.Join(" · ", clip.Files.Select(Path.GetFileName))
                : clip.PlainText ?? (clip.Kind == ClipKind.Image ? "Copied image" : "Rich text");
            Add(cmd, "$preview", preview[..Math.Min(preview.Length, 240)]);
            Add(cmd, "$thumbnail", clip.ThumbnailPath); Add(cmd, "$icon", clip.Source.IconPath);
            Add(cmd, "$payload", JsonSerializer.Serialize(clip)); Add(cmd, "$search", clip.SearchText);
            cmd.ExecuteNonQuery();
            tx.Commit();
            return true;
        }
        finally { _writes.Release(); }
    }
    public Task<Clip?> GetAsync(Guid id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var db = Open();
        return Task.FromResult(Read(db, id));
    }
    private static Clip? Read(SqliteConnection db, Guid id)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT payload,pinned FROM clips WHERE id=$id;";
        Add(cmd, "$id", id.ToString());
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? JsonSerializer.Deserialize<Clip>(reader.GetString(0))! with { IsPinned = reader.GetBoolean(1) } : null;
    }
    public Task<IReadOnlyList<ClipSummary>> SearchAsync(SearchRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var db = Open();
        using var cmd = db.CreateCommand();
        using var cancel = ct.Register(cmd.Cancel);
        var terms = Words().Matches(request.Text[..Math.Min(request.Text.Length, 4096)]).Take(32)
            .Select(x => "\"" + x.Value.Replace("\"", "\"\"") + "\"*").ToArray();
        var search = terms.Length == 0 ? "" : "AND c.rowid IN (SELECT rowid FROM clips_fts WHERE clips_fts MATCH $query)";
        var filter = request.Filter switch
        {
            ClipFilter.Text => "AND c.kind=0", ClipFilter.Images => "AND c.kind=1",
            ClipFilter.Files => "AND c.kind=2", ClipFilter.Pinned => "AND c.pinned=1", _ => ""
        };
        cmd.CommandText = $"""
            SELECT c.id,c.kind,c.preview,c.created,c.app,c.title,c.pinned,c.thumbnail,c.icon,
                   json_extract(c.payload,'$.ContextScreenshotPath') IS NOT NULL
            FROM clips c WHERE 1=1 {search} {filter} ORDER BY c.created DESC,c.rowid DESC LIMIT $limit OFFSET $offset;
            """;
        if (terms.Length > 0) Add(cmd, "$query", string.Join(" AND ", terms));
        Add(cmd, "$limit", Math.Clamp(request.Limit, 1, 200)); Add(cmd, "$offset", Math.Max(0, request.Offset));
        using var reader = cmd.ExecuteReader();
        List<ClipSummary> results = [];
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            results.Add(new(Guid.Parse(reader.GetString(0)), (ClipKind)reader.GetInt32(1), reader.GetString(2),
                new DateTimeOffset(reader.GetInt64(3), TimeSpan.Zero), reader.GetString(4), reader.GetString(5),
                reader.GetBoolean(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetBoolean(9)));
        }
        return Task.FromResult<IReadOnlyList<ClipSummary>>(results);
    }
    public async Task SetPinnedAsync(Guid id, bool pinned, CancellationToken ct = default)
    {
        await _writes.WaitAsync(ct);
        try
        {
            using var db = Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE clips SET pinned=$pinned WHERE id=$id;";
            Add(cmd, "$id", id.ToString()); Add(cmd, "$pinned", pinned); cmd.ExecuteNonQuery();
        }
        finally { _writes.Release(); }
    }
    public async Task<bool> AttachContextAsync(Guid id, string screenshot, string thumbnail, string? icon, CancellationToken ct = default)
    {
        await _writes.WaitAsync(ct);
        try
        {
            using var db = Open();
            if (Read(db, id) is not { } clip) return false;
            clip = clip with { ContextScreenshotPath = screenshot, ThumbnailPath = clip.Kind == ClipKind.Image ? clip.ThumbnailPath : thumbnail,
                Source = clip.Source with { IconPath = icon ?? clip.Source.IconPath } };
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE clips SET payload=$payload,thumbnail=$thumbnail,icon=$icon WHERE id=$id;";
            Add(cmd, "$id", id.ToString()); Add(cmd, "$payload", JsonSerializer.Serialize(clip));
            Add(cmd, "$thumbnail", clip.ThumbnailPath); Add(cmd, "$icon", clip.Source.IconPath);
            return cmd.ExecuteNonQuery() == 1;
        }
        finally { _writes.Release(); }
    }
    public async Task<IReadOnlyList<string>> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await _writes.WaitAsync(ct);
        try
        {
            using var db = Open(); var clip = Read(db, id);
            using var cmd = db.CreateCommand(); cmd.CommandText = "DELETE FROM clips WHERE id=$id;";
            Add(cmd, "$id", id.ToString()); cmd.ExecuteNonQuery();
            return clip?.MediaPaths.ToArray() ?? [];
        }
        finally { _writes.Release(); }
    }
    public async Task<bool> DeleteIfUnpinnedAsync(Guid id, CancellationToken ct = default)
    {
        await _writes.WaitAsync(ct);
        try
        {
            using var db = Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "DELETE FROM clips WHERE id=$id AND pinned=0;";
            Add(cmd, "$id", id.ToString()); return cmd.ExecuteNonQuery() == 1;
        }
        finally { _writes.Release(); }
    }
    public Task<IReadOnlyList<Clip>> ListForMaintenanceAsync(CancellationToken ct = default)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        // Maintenance never loads large text/rich payloads into managed memory.
        cmd.CommandText = """
            SELECT id,created,pinned,json_extract(payload,'$.PrimaryMediaPath'),
              json_extract(payload,'$.ThumbnailPath'),json_extract(payload,'$.ContextScreenshotPath'),icon
            FROM clips ORDER BY created ASC;
            """;
        using var reader = cmd.ExecuteReader(); List<Clip> clips = [];
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            string? Value(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
            clips.Add(new() { Id = Guid.Parse(reader.GetString(0)), CreatedUtc = new(reader.GetInt64(1), TimeSpan.Zero),
                IsPinned = reader.GetBoolean(2), PrimaryMediaPath = Value(3), ThumbnailPath = Value(4),
                ContextScreenshotPath = Value(5), Source = new() { IconPath = Value(6) } });
        }
        return Task.FromResult<IReadOnlyList<Clip>>(clips);
    }
    public void Checkpoint()
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(PASSIVE); PRAGMA incremental_vacuum(128); PRAGMA optimize;";
        cmd.ExecuteNonQuery();
    }
    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    [GeneratedRegex("[\\p{L}\\p{N}_]+")]
    private static partial Regex Words();
}
