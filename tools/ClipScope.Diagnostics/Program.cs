// Developer-only, never included in the installer. Requires an explicitly owned
// synthetic profile. Outputs hashes/counts, never clipboard payloads or titles.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClipScope.Core;
using ClipScope.Infrastructure;
using Microsoft.Data.Sqlite;

if (args.Length < 2) throw new ArgumentException("Use snapshot|probe|benchmark <owned-profile> [count|needle].");
var command = args[0];
var root = Path.GetFullPath(args[1]);
if (!File.Exists(Path.Combine(root, ".clipscope-test-owner"))) throw new InvalidOperationException("Synthetic test ownership marker is required.");
for (var parent = new DirectoryInfo(root); parent is not null; parent = parent.Parent)
    if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Reparse points are not accepted.");
foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Reparse points are not accepted.");
var paths = new DataPaths(root);
var repository = new SqliteClipRepository(paths, new SilentLog());
if (command == "benchmark")
{
    var count = args.Length > 2 ? int.Parse(args[2]) : 100;
    if (count is < 1 or > 10000 || File.Exists(paths.Database)) throw new InvalidOperationException("Use a fresh profile and 1..10000 clips.");
    await repository.InitializeAsync();
    for (var i = 0; i < count; i++)
        await repository.AddAsync(new() { PlainText = $"RC synthetic searchable item {i:D5}", ContentHash = i.ToString(),
            Source = new() { AppName = "Synthetic", ProcessName = "synthetic", ProcessId = 1, WindowHandle = 1 } }, TimeSpan.Zero);
    // Warm once, then measure the actual FTS repository query, including SQLite open/materialization.
    await repository.SearchAsync(new("searchable"));
    var times = new List<double>();
    for (var i = 0; i < 100; i++)
    {
        var watch = Stopwatch.StartNew();
        var found = await repository.SearchAsync(new("searchable"));
        watch.Stop();
        if (found.Count != Math.Min(count, 100)) throw new InvalidOperationException("Unexpected FTS result count.");
        times.Add(watch.Elapsed.TotalMilliseconds);
    }
    times.Sort();
    Console.WriteLine(JsonSerializer.Serialize(new { clips = count, samples = times.Count, medianMs = times[49], p95Ms = times[94], maxMs = times[^1], scope = "repository FTS query; excludes UI debounce/render" }));
}
else
{
    if (!File.Exists(paths.Database)) throw new InvalidOperationException("An existing database is required.");
    using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.Database, Mode = SqliteOpenMode.ReadWrite }.ToString());
    db.Open();
    object? Scalar(string sql, string? value = null)
    {
        using var query = db.CreateCommand(); query.CommandText = sql;
        if (value is not null) query.Parameters.AddWithValue("$value", value);
        return query.ExecuteScalar();
    }
    if ((string?)Scalar("PRAGMA integrity_check;") != "ok") throw new InvalidOperationException("SQLite integrity check failed.");
    Scalar("INSERT INTO clips_fts(clips_fts,rank) VALUES('integrity-check',1);");
    if (command == "probe")
    {
        if (args.Length != 3) throw new ArgumentException("Probe requires a synthetic needle.");
        var needle = args[2];
        Clip? latest = null;
        if (Scalar("SELECT payload FROM clips WHERE instr(payload,$value)>0 ORDER BY created DESC LIMIT 1;", needle) is string payload)
            latest = JsonSerializer.Deserialize<Clip>(payload);
        PngPreview? Preview(string? relative) => relative is null ? null : PngPreview.Read(File.ReadAllBytes(paths.ResolveMedia(relative)), 320);
        Console.WriteLine(JsonSerializer.Serialize(new { clips = Scalar("SELECT COUNT(*) FROM clips;"),
            kindCounts = new { text = Scalar("SELECT COUNT(*) FROM clips WHERE kind=0;"), images = Scalar("SELECT COUNT(*) FROM clips WHERE kind=1;"), files = Scalar("SELECT COUNT(*) FROM clips WHERE kind=2;") },
            payloadHits = Scalar("SELECT COUNT(*) FROM clips WHERE instr(payload,$value)>0;", needle),
            metadataHits = Scalar("SELECT COUNT(*) FROM clips WHERE instr(title,$value)>0 OR instr(app,$value)>0;", needle),
            ftsHits = Scalar("SELECT COUNT(*) FROM clips_fts WHERE clips_fts MATCH $value;", "\"" + needle.Replace("\"", "\"\"") + "\""),
            mediaFiles = Directory.GetFiles(paths.Media).Length + Directory.GetFiles(paths.Thumbnails).Length, integrity = "ok",
            latest = latest is null ? null : new { kind = latest.Kind.ToString(), source = latest.Source.ProcessName,
                hasHtml = latest.Html is not null, hasRtf = latest.Rtf is not null, formats = latest.Formats,
                fileCount = latest.Files.Length, context = Preview(latest.ContextScreenshotPath), thumbnail = Preview(latest.ThumbnailPath) } }));
    }
    else if (command == "snapshot")
    {
        var rows = new List<string>();
        using (var query = db.CreateCommand())
        {
            query.CommandText = "SELECT id,pinned,payload FROM clips ORDER BY id;";
            using var reader = query.ExecuteReader();
            while (reader.Read()) rows.Add($"{reader.GetString(0)}|{reader.GetInt32(1)}|{reader.GetString(2)}");
        }
        var media = Directory.GetFiles(paths.Media).Concat(Directory.GetFiles(paths.Thumbnails)).Order(StringComparer.Ordinal)
            .Select(path => new { path = Path.GetRelativePath(root, path), sha256 = Hash(File.ReadAllBytes(path)) }).ToArray();
        // Ensure every persisted media reference is safe, present, and byte-readable.
        foreach (var clip in await repository.ListForMaintenanceAsync())
            foreach (var relative in clip.MediaPaths) _ = File.ReadAllBytes(paths.ResolveMedia(relative));
        Console.WriteLine(JsonSerializer.Serialize(new { clips = rows.Count, pins = Scalar("SELECT COUNT(*) FROM clips WHERE pinned=1;"),
            recordsSha256 = Hash(Encoding.UTF8.GetBytes(string.Join('\n', rows))),
            settingsSha256 = File.Exists(paths.Settings) ? Hash(File.ReadAllBytes(paths.Settings)) : null,
            media, searchHits = (await repository.SearchAsync(new("pinned sentinel"))).Count, integrity = "ok" }));
    }
    else throw new ArgumentException("Unknown diagnostic command.");
}
SqliteConnection.ClearAllPools();
static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
sealed class SilentLog : IDiagnosticLog { public void Write(string eventName, Exception? error = null) { } }
