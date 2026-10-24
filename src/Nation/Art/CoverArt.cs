using System.Collections.Concurrent;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Nation.Art;

/// <summary>Finds a track's cover (embedded, else folder image) and derives its palette off the UI thread.</summary>
public static class CoverArt
{
    private const uint SampleSize = 64;
    private static readonly string[] FolderNames = { "cover", "folder", "front", "album" };
    private static readonly string[] FolderExts = { ".jpg", ".jpeg", ".png" };

    // Albums share covers, so cache by the image source rather than by track.
    private static readonly ConcurrentDictionary<string, ArtPalette> Cache = new();

    public static async Task<ArtPalette> PaletteForAsync(Library.Track track)
    {
        try
        {
            var (key, bytes) = FindCover(track);
            if (bytes is null) return ArtPalette.Default;
            if (Cache.TryGetValue(key, out var cached)) return cached;

            var pixels = await DecodeAsync(bytes);
            var palette = ArtPalette.Extract(pixels, (int)(SampleSize * SampleSize));
            Cache[key] = palette;
            return palette;
        }
        catch (Exception)
        {
            // Corrupt/unsupported image: keep the UI on the default accent.
            return ArtPalette.Default;
        }
    }

    /// <summary>Cover bytes (embedded front cover, else folder image) plus a cache key; Bytes null when none.</summary>
    public static (string Key, byte[]? Bytes) FindCover(Library.Track track)
    {
        // Another app's track: the cover came with its media session.
        if (track.IsExternal) return (track.CoverKey ?? "", track.Cover);
        if (track.IsStream) return track.CoverUrl is { } url ? (url, Download(url)) : ("", null);
        return FindCover(track.Path);
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    // Thumbnails are asked for by the palette, the media flyout and Shade: download once.
    private static readonly ConcurrentDictionary<string, byte[]?> Downloads = new();

    /// <summary>Callers are already off the UI thread (palette task, media session, API server).</summary>
    private static byte[]? Download(string url)
    {
        if (Downloads.TryGetValue(url, out var cached)) return cached;
        byte[]? bytes;
        try { bytes = Http.GetByteArrayAsync(url).GetAwaiter().GetResult(); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return null; } // retry next time
        if (Downloads.Count > 64) Downloads.Clear();
        Downloads[url] = bytes;
        return bytes;
    }

    private static (string Key, byte[]? Bytes) FindCover(string trackPath)
    {
        using (var file = TagLib.File.Create(trackPath))
        {
            var pictures = file.Tag.Pictures;
            var pic = pictures.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover) ?? pictures.FirstOrDefault();
            if (pic is not null)
            {
                var data = pic.Data.Data;
                // Embedded art is usually identical across an album: key by size + album.
                return ($"emb:{file.Tag.Album}:{data.Length}", data);
            }
        }

        var dir = Path.GetDirectoryName(trackPath)!;
        foreach (var name in FolderNames)
            foreach (var ext in FolderExts)
            {
                var candidate = Path.Combine(dir, name + ext);
                if (File.Exists(candidate)) return (candidate, File.ReadAllBytes(candidate));
            }
        return ("", null);
    }

    /// <summary>Decodes with the built-in WinRT imaging codecs, downscaled to a 64x64 BGRA buffer.</summary>
    private static async Task<byte[]> DecodeAsync(byte[] image)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(image);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        stream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(stream);
        var transform = new BitmapTransform
        {
            ScaledWidth = SampleSize,
            ScaledHeight = SampleSize,
            InterpolationMode = BitmapInterpolationMode.Linear,
        };
        var data = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        return data.DetachPixelData();
    }
}
