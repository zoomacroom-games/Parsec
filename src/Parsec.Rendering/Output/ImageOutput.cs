using SkiaSharp;

namespace Parsec.Rendering.Output;

public static class ImageOutput
{
    /// <summary>
    /// Encodes <paramref name="bitmap"/> as PNG and writes to <paramref name="path"/>.
    /// Creates the directory if needed.
    /// </summary>
    public static void SavePng(SKBitmap bitmap, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        // File.Create truncates an existing file; OpenWrite does not, which
        // left stale trailing bytes when re-rendering to a smaller PNG.
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }
}
