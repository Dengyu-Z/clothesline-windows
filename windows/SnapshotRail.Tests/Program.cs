using SnapshotRail;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

var directory = Path.Combine(Path.GetTempPath(), "Clothesline-state-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var state = Path.Combine(directory, "dismissed.json");
var image = Path.Combine(directory, "photo.png");
var video = Path.Combine(directory, "capture.mp4");
var fresh = Path.Combine(directory, "new.png");
var assertions = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    assertions++;
}
try
{
    File.WriteAllText(image, "test image bytes");
    File.WriteAllText(video, "test video bytes");
    File.WriteAllText(fresh, "new capture bytes");
    var store = new DismissedCaptureStore(state);
    Check(!store.Contains(image), "New image must be eligible");
    store.Remember(image, CaptureVersion.Read(image));
    store.Remember(video, CaptureVersion.Read(video));
    Check(store.Contains(image), "Pending duplicate watcher events must be suppressed");
    // New instances read from disk, just as subsequent app launches do.
    var restarted = new DismissedCaptureStore(state);
    Check(restarted.Contains(image), "Dismissed photo reappeared after restart");
    Check(restarted.Contains(video), "Dismissed recording reappeared after restart");
    Check(restarted.Contains(image.ToUpperInvariant()), "Windows paths must be case insensitive");
    Check(!restarted.Contains(fresh), "New captures must still hang");
    File.AppendAllText(image, "replacement capture");
    Check(!restarted.Contains(image), "Replaced file at the same path must be eligible");
    var version = CaptureVersion.Read(video);
    File.Delete(video);
    restarted.Remember(video, version);
    Check(!restarted.Contains(video), "Missing files are not current dismissed versions");
    File.WriteAllText(state, "invalid JSON");
    var corrupt = new DismissedCaptureStore(state);
    Check(corrupt.LoadError is not null, "Invalid state must report a load error");
    Check(!corrupt.Contains(fresh), "Invalid state must not crash or hide new files");

    var olderVideo = Path.Combine(directory, "older.mp4");
    var closedVideo = Path.Combine(directory, "closed.mov");
    File.WriteAllText(olderVideo, "historical recording");
    File.WriteAllText(closedVideo, "recording being finalized");
    File.SetLastWriteTimeUtc(olderVideo, DateTime.UtcNow.AddYears(-1));
    var history = new DismissedCaptureStore(Path.Combine(directory, "video-dismissals.json"));
    var earlyVersion = CaptureVersion.Read(closedVideo);
    File.AppendAllText(closedVideo, " final metadata");
    history.Remember(closedVideo, CaptureVersion.Read(closedVideo));
    Check(earlyVersion != CaptureVersion.Read(closedVideo), "Video test must cover finalization after thumbnail");
    var videoHistoryRestart = new DismissedCaptureStore(Path.Combine(directory, "video-dismissals.json"));
    Check(videoHistoryRestart.Contains(closedVideo), "Finalized MOV dismissal must survive restart");
    var migration = RailSessionStore.SelectStartupPaths(null, new[] { olderVideo, closedVideo }, 1, videoHistoryRestart.Contains);
    Check(migration.Length == 0, "Migration must not backfill dismissed video with older MP4");
    var sessions = new RailSessionStore(Path.Combine(directory, "rail-session.json"));
    sessions.Save(new[] { fresh }, DateTime.UtcNow.AddMinutes(1));
    var restored = RailSessionStore.SelectStartupPaths(sessions.Load(), new[] { olderVideo, closedVideo, fresh }, 12, videoHistoryRestart.Contains);
    Check(restored.SequenceEqual(new[] { fresh }), "Restart must restore active cards without importing old recordings");
    sessions.Save(Array.Empty<string>(), DateTime.UtcNow.AddMinutes(1));
    Check(RailSessionStore.SelectStartupPaths(sessions.Load(), new[] { olderVideo, closedVideo }, 12, videoHistoryRestart.Contains).Length == 0,
        "Empty line must remain empty across restart");
    sessions.Save(Array.Empty<string>(), DateTime.UtcNow.AddYears(-2));
    Check(RailSessionStore.SelectStartupPaths(sessions.Load(), new[] { fresh }, 12, videoHistoryRestart.Contains).Contains(fresh),
        "New captures made while closed must still hang");
    sessions.Save(Array.Empty<string>(), DateTime.UtcNow.AddMinutes(1));
    File.SetLastWriteTimeUtc(olderVideo, DateTime.UtcNow.AddMinutes(2));
    Check(RailSessionStore.SelectStartupPaths(sessions.Load(), new[] { olderVideo }, 12, videoHistoryRestart.Contains).Length == 0,
        "Old recording metadata update must not count as a new capture");
    var pixelData = new byte[] { 10, 20, 30, 255, 40, 50, 60, 255 };
    var bitmap = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, pixelData, 8);
    var differentDpi = BitmapSource.Create(2, 1, 144, 144, PixelFormats.Bgra32, null, pixelData, 8);
    Check(ImageFingerprint.FromBitmap(bitmap) == ImageFingerprint.FromBitmap(differentDpi),
        "Different DPI must not create a duplicate card");
    var undefinedAlpha = new byte[] { 10, 20, 30, 0, 40, 50, 60, 0 };
    var transparentCapture = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, undefinedAlpha, 8);
    Check(ImageFingerprint.FromBitmap(bitmap) == ImageFingerprint.FromBitmap(transparentCapture),
        "Undefined screenshot alpha must normalize across the two inputs");
    var pngPath = Path.Combine(directory, "pixels.png");
    var bmpPath = Path.Combine(directory, "pixels.bmp");
    var pngEncoder = new PngBitmapEncoder();
    pngEncoder.Frames.Add(BitmapFrame.Create(bitmap));
    using (var stream = File.Create(pngPath)) pngEncoder.Save(stream);
    var bmpEncoder = new BmpBitmapEncoder();
    bmpEncoder.Frames.Add(BitmapFrame.Create(bitmap));
    using (var stream = File.Create(bmpPath)) bmpEncoder.Save(stream);
    Check(ImageFingerprint.FromFile(pngPath) == ImageFingerprint.FromFile(bmpPath),
        "Identical image pixels in different encodings must match");
    Check(ImageFingerprint.FromFile(pngPath) == ImageFingerprint.FromBitmap(bitmap),
        "Directory and clipboard inputs must produce the same fingerprint");
    var differentPixels = new byte[] { 11, 20, 30, 255, 40, 50, 60, 255 };
    var differentImage = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, differentPixels, 8);
    Check(ImageFingerprint.FromBitmap(bitmap) != ImageFingerprint.FromBitmap(differentImage),
        "Genuinely different images must not be merged");
    if (args.Length == 2)
    {
        Check(ImageFingerprint.FromFile(args[0]) == ImageFingerprint.FromFile(args[1]),
            "The supplied clipboard and screenshot files must match");
        Console.WriteLine("PASS: supplied clipboard/directory screenshot pair matches");
    }
    Console.WriteLine($"PASS: {assertions} regression assertions (state + image deduplication)");
}
finally
{
    foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
    Directory.Delete(directory);
}
