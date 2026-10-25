using System.IO;
using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SnapshotRail;

// Compare decoded pixels, not PNG bytes: encoders, metadata and DPI may
// differ between the clipboard snapshot and the system screenshot file.
public static class ImageFingerprint
{
    public static string FromFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return FromBitmap(decoder.Frames[0]);
    }

    public static string FromBitmap(BitmapSource source)
    {
        var bitmap = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = checked(bitmap.PixelWidth * 4);
        var pixels = new byte[checked(stride * bitmap.PixelHeight)];
        bitmap.CopyPixels(pixels, stride, 0);
        // Some screenshot producers supply undefined all-zero alpha. Treat
        // those as opaque, as the clipboard saving pipeline does.
        var allTransparent = true;
        for (var i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0) { allTransparent = false; break; }
        if (allTransparent)
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        Span<byte> dimensions = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(dimensions, bitmap.PixelWidth);
        BinaryPrimitives.WriteInt32LittleEndian(dimensions[4..], bitmap.PixelHeight);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(dimensions);
        hash.AppendData(pixels);
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
