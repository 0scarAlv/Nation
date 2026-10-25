namespace Nation.Library;

public static class TrackScanner
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".opus", ".ogg", ".mp3", ".m4a", ".mp4", ".aac", ".wav", ".wv", ".ape",
    };

    /// <summary>Expands files/folders into tracks, folders recursively, sorted by path.</summary>
    public static List<Track> Scan(IEnumerable<string> inputs)
    {
        var paths = new List<string>();
        foreach (var input in inputs)
        {
            if (Directory.Exists(input))
                paths.AddRange(Directory.EnumerateFiles(Path.GetFullPath(input), "*", SearchOption.AllDirectories)
                    .Where(IsAudio)
                    .Order(StringComparer.OrdinalIgnoreCase));
            else if (File.Exists(input) && IsAudio(input))
                paths.Add(Path.GetFullPath(input));
        }

        var cache = LibraryCache.Load();
        var tracks = new Track[paths.Count];
        var misses = new List<(int Index, long Size, long MTime)>();
        for (int i = 0; i < paths.Count; i++)
        {
            var info = new FileInfo(paths[i]);
            long size = info.Length, mtime = info.LastWriteTimeUtc.Ticks;
            if (cache.TryGetValue(paths[i], out var hit) && hit.Size == size && hit.MTime == mtime)
                tracks[i] = hit.ToTrack();
            else
                misses.Add((i, size, mtime));
        }

        if (misses.Count > 0)
        {
            // Parse only new/changed files. Capped parallelism: each parse holds the
            // file's cover block in memory, so more threads mostly means a higher peak.
            Parallel.ForEach(misses, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount) },
                m => tracks[m.Index] = Read(paths[m.Index]));
            foreach (var m in misses)
                cache[paths[m.Index]] = LibraryCache.Entry.From(tracks[m.Index], m.Size, m.MTime);
            LibraryCache.Save(cache);
        }

        return tracks.Select((t, i) => t with { Id = i }).ToList();
    }

    private static bool IsAudio(string path) => Extensions.Contains(Path.GetExtension(path));

    private static Track Read(string path)
    {
        var fallbackTitle = Path.GetFileNameWithoutExtension(path);
        var codec = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        try
        {
            using var file = TagLib.File.Create(path, TagLib.ReadStyle.Average | TagLib.ReadStyle.PictureLazy);
            var tag = file.Tag;
            var props = file.Properties;
            return new Track(
                path,
                string.IsNullOrWhiteSpace(tag.Title) ? fallbackTitle : tag.Title,
                tag.FirstPerformer ?? tag.FirstAlbumArtist ?? "Desconocido",
                tag.Album ?? "",
                tag.Year,
                props.Duration,
                codec,
                props.AudioSampleRate,
                props.BitsPerSample,
                props.AudioBitrate)
            {
                Genre = string.IsNullOrWhiteSpace(tag.JoinedGenres) ? null : tag.JoinedGenres,
                Isrc = string.IsNullOrWhiteSpace(tag.ISRC) ? null : tag.ISRC.Trim().ToUpperInvariant(),
            };
        }
        catch (Exception)
        {
            // Unreadable/unsupported tags must not block playback; mpv may still decode it.
            return new Track(path, fallbackTitle, "Desconocido", "", 0, TimeSpan.Zero, codec, 0, 0, 0);
        }
    }
}
