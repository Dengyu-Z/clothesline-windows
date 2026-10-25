using System.IO;
using System.Text.Json;

namespace SnapshotRail;

public sealed record RailSession(string[] ActivePaths, DateTime ScanStartedUtc);

public sealed class RailSessionStore(string path)
{
    public RailSession? Load()
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            var session = JsonSerializer.Deserialize<RailSession>(stream);
            return session is { ActivePaths: not null } ? session : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Save(IEnumerable<string> activePaths, DateTime scanStartedUtc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            JsonSerializer.Serialize(stream, new RailSession(activePaths.ToArray(), scanStartedUtc));
        File.Move(temporary, path, overwrite: true);
    }

    public static string[] SelectStartupPaths(RailSession? session, IEnumerable<string> candidates,
        int maximumItems, Func<string, bool> dismissed)
    {
        var files = candidates.Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists).ToArray();
        if (session is null)
        {
            // Migration/first launch: choose the recent window BEFORE filtering
            // dismissals. Never backfill it with progressively older videos.
            return files.OrderBy(File.GetLastWriteTimeUtc).TakeLast(maximumItems)
                .Where(p => !dismissed(p)).ToArray();
        }
        var active = session.ActivePaths.Where(File.Exists).Where(p => !dismissed(p));
        // While closed, only newly created files are arrivals. Editing metadata
        // on an old recording must not turn it into a new capture.
        var arrivals = files.Where(p => File.GetCreationTimeUtc(p) > session.ScanStartedUtc)
            .Where(p => !dismissed(p)).OrderBy(File.GetCreationTimeUtc);
        return active.Concat(arrivals).Distinct(StringComparer.OrdinalIgnoreCase)
            .TakeLast(maximumItems).ToArray();
    }
}
