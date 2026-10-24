using System.Text;

namespace Nation.Library;

/// <summary>A named, user-made list of songs, in the user's order.</summary>
public sealed class Playlist
{
    public string Name { get; internal set; } = "";
    public List<Track> Tracks { get; } = new();
    internal string FilePath { get; set; } = "";
    /// <summary>Bumped on every edit (for UI caches).</summary>
    public int Version { get; internal set; }
    public TimeSpan Duration => TimeSpan.FromTicks(Tracks.Sum(t => t.Duration.Ticks));
}

/// <summary>
/// Playlists as plain .m3u8 files in %LOCALAPPDATA%\Nation\playlists (one per list, the
/// display name in a #PLAYLIST line), so they can be opened or copied by other players.
/// Songs resolve against the scanned library by path; songs outside it are read on the
/// spot, and missing files are kept (shown as missing) so a list never loses entries
/// just because a drive or folder wasn't scanned this run.
/// </summary>
public sealed class PlaylistStore
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nation", "playlists");

    private readonly Dictionary<string, Track> _byPath;
    private int _nextId = 500_000; // outside library ids (scan order) and other apps' (1,000,000+)

    public List<Playlist> All { get; } = new();

    private PlaylistStore(IReadOnlyList<Track> library)
    {
        _byPath = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in library) _byPath.TryAdd(t.Path, t);
    }

    public static PlaylistStore Load(IReadOnlyList<Track> library)
    {
        var store = new PlaylistStore(library);
        try
        {
            if (!Directory.Exists(Dir)) return store;
            var files = Directory.EnumerateFiles(Dir, "*.m3u8").ToList();
            var parsed = files.Select(f => (File: f, Lines: File.ReadAllLines(f, Encoding.UTF8))).ToList();
            // One scan for every song that isn't in the library, instead of one per file.
            var outside = parsed.SelectMany(p => p.Lines).Where(IsPath).Select(l => l.Trim())
                .Where(l => !store._byPath.ContainsKey(l) && File.Exists(l)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (outside.Count > 0)
                foreach (var t in TrackScanner.Scan(outside)) store._byPath.TryAdd(t.Path, t with { Id = store._nextId++ });

            foreach (var (file, lines) in parsed)
            {
                var list = new Playlist { FilePath = file, Name = Path.GetFileNameWithoutExtension(file) };
                foreach (var line in lines)
                {
                    if (line.StartsWith("#PLAYLIST:", StringComparison.Ordinal)) list.Name = line[10..].Trim();
                    else if (IsPath(line)) list.Tracks.Add(store.Resolve(line.Trim()));
                }
                store.All.Add(list);
            }
            store.Sort();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Unreadable folder: start without lists rather than not at all.
        }
        return store;
    }

    private static bool IsPath(string line) => line.Length > 0 && !line.StartsWith('#') && line.Trim().Length > 0;

    /// <summary>The library's track for a path, or a placeholder for a file that isn't there.</summary>
    public Track Resolve(string path)
    {
        if (_byPath.TryGetValue(path, out var t)) return t;
        var missing = new Track(path, Path.GetFileNameWithoutExtension(path), "archivo no encontrado", "", 0, TimeSpan.Zero,
            Path.GetExtension(path).TrimStart('.').ToUpperInvariant(), 0, 0, 0) { Id = _nextId++ };
        _byPath[path] = missing;
        return missing;
    }

    public Playlist? Find(string name) => All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Why a name can't be used, or null when it's fine.</summary>
    public string? Validate(string name, Playlist? renaming = null)
    {
        name = name.Trim();
        if (name.Length == 0) return "escribe un nombre";
        if (Find(name) is { } other && !ReferenceEquals(other, renaming)) return "ya existe una lista con ese nombre";
        return null;
    }

    public Playlist Create(string name)
    {
        var list = new Playlist { Name = name.Trim() };
        list.FilePath = FreeFile(list.Name);
        All.Add(list);
        Sort();
        Save(list);
        return list;
    }

    public void Rename(Playlist list, string name)
    {
        string old = list.FilePath;
        list.Name = name.Trim();
        list.FilePath = FreeFile(list.Name, old);
        Save(list);
        if (!string.Equals(old, list.FilePath, StringComparison.OrdinalIgnoreCase))
            try { File.Delete(old); } catch (IOException) { }
        Sort();
    }

    public void Delete(Playlist list)
    {
        All.Remove(list);
        try { File.Delete(list.FilePath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public void Add(Playlist list, IEnumerable<Track> tracks)
    {
        list.Tracks.AddRange(tracks);
        Save(list);
    }

    public void RemoveAt(Playlist list, int index)
    {
        if (index < 0 || index >= list.Tracks.Count) return;
        list.Tracks.RemoveAt(index);
        Save(list);
    }

    /// <summary>Swaps a song with its neighbor; returns its new index.</summary>
    public int Move(Playlist list, int index, int delta)
    {
        int to = index + delta;
        if (index < 0 || index >= list.Tracks.Count || to < 0 || to >= list.Tracks.Count) return index;
        (list.Tracks[index], list.Tracks[to]) = (list.Tracks[to], list.Tracks[index]);
        Save(list);
        return to;
    }

    public void Save(Playlist list)
    {
        list.Version++;
        try
        {
            Directory.CreateDirectory(Dir);
            var sb = new StringBuilder("#EXTM3U\n").Append("#PLAYLIST:").Append(list.Name).Append('\n');
            foreach (var t in list.Tracks)
                sb.Append("#EXTINF:").Append((int)t.Duration.TotalSeconds).Append(',').Append(t.Artist).Append(" - ").Append(t.Title).Append('\n')
                    .Append(t.Path).Append('\n');
            // Write-then-rename so a crash mid-save never leaves a half-written list.
            string tmp = list.FilePath + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            File.Move(tmp, list.FilePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The list stays edited in memory for this run.
        }
    }

    private void Sort() => All.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

    /// <summary>A file name for the list that no other list uses (names may hold characters files can't).</summary>
    private string FreeFile(string name, string? keep = null)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string safe = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim().TrimEnd('.');
        if (safe.Length == 0) safe = "lista";
        for (int n = 1; ; n++)
        {
            string path = Path.Combine(Dir, (n == 1 ? safe : $"{safe} ({n})") + ".m3u8");
            bool taken = All.Any(p => string.Equals(p.FilePath, path, StringComparison.OrdinalIgnoreCase)) || File.Exists(path);
            if (!taken || string.Equals(path, keep, StringComparison.OrdinalIgnoreCase)) return path;
        }
    }
}
