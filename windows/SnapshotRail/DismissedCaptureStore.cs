using System.IO;
using System.Text.Json;

namespace SnapshotRail;

// A path alone is not enough: overwriting that path with a new capture must
// allow it to hang again. Store the version seen when the card was created.
public sealed record CaptureVersion(long Length, long LastWriteTicks)
{
    public static CaptureVersion? Read(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? new(file.Length, file.LastWriteTimeUtc.Ticks) : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}

public sealed class DismissedCaptureStore
{
    private readonly string _path;
    private readonly Dictionary<string, CaptureVersion> _dismissed = new(StringComparer.OrdinalIgnoreCase);
    public string? LoadError { get; }

    public DismissedCaptureStore(string path)
    {
        _path = path;
        try
        {
            if (!File.Exists(path)) return;
            using var stream = File.OpenRead(path);
            var entries = JsonSerializer.Deserialize<Dictionary<string, CaptureVersion>>(stream);
            if (entries is not null)
                foreach (var entry in entries) _dismissed[entry.Key] = entry.Value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LoadError = ex.Message;
        }
    }

    public bool Contains(string path) =>
        _dismissed.TryGetValue(Path.GetFullPath(path), out var version) && version == CaptureVersion.Read(path);

    // Save immediately, not only on graceful exit (a forced restart should
    // not resurrect a card either). Atomic replacement avoids partial JSON.
    public void Remember(string path, CaptureVersion? version)
    {
        if (version is null) return;
        _dismissed[Path.GetFullPath(path)] = version;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            JsonSerializer.Serialize(stream, _dismissed);
        File.Move(temporary, _path, overwrite: true);
    }
}
