using System.Collections.Concurrent;
using Nation.Library;
using Nation.Streaming;

namespace Nation.Tui;

// YouTube Music tab (`4`): the user's library and lists, and search over the whole catalog.
// Its songs are links that mpv plays through yt-dlp, so once they're in the queue they
// behave like local files: x, J/K, the equalizer, the visualizers, lyrics from LRCLIB.
public sealed partial class App
{
    /// <summary>YouTube Music client (search, radio, library); null to leave the tab out.</summary>
    public YouTubeMusic? YouTube { get; init; }

    // Network calls finish on other threads; their results are applied here on the UI thread.
    private readonly ConcurrentQueue<Action> _ytDone = new();

    private List<YtPlaylist>? _ytLists;
    private YtPlaylist? _ytOpen;
    private List<Track>? _ytOpenTracks;
    private readonly Dictionary<string, List<Track>> _ytLoaded = new();
    private List<Track>? _ytResults;
    private string _ytSearched = "";
    private CancellationTokenSource? _ytSearchCts;
    private double _filterChangedAt;
    private bool _ytListsLoading, _ytOpenLoading, _ytSearching, _ytDownloading, _ytSigningIn;
    private string? _ytError;
    private int _ytCursor, _ytScroll, _ytListCursor, _ytListScroll;
    // Enter on a song still being resolved: play it the moment its audio URL is ready.
    private (Track Track, Action Play, double Since)? _deferred;

    /// <summary>The YouTube tab's top level with nothing typed: the user's lists (or how to sign in).</summary>
    private bool ShowingYtLists => _view == SideView.YouTube && _ytOpen is null && _filter.Length == 0;

    /// <summary>Typing at the YouTube tab's top level searches YouTube Music itself, not the rows on screen.</summary>
    private bool YtSearchMode => _view == SideView.YouTube && _ytOpen is null;

    private bool YtBusy => _ytListsLoading || _ytOpenLoading || _ytSearching || _ytDownloading || _ytSigningIn;

    private string YtSpinner => Spinner[(int)(Now * 12) % Spinner.Length].ToString();

    /// <summary>Once per frame: apply finished requests, run the debounced search, load the library when needed.</summary>
    private void UpdateYouTube()
    {
        while (_ytDone.TryDequeue(out var done)) done();
        if (_deferred is { } wait)
        {
            _animSlow = true;
            // Resolved, failed (then mpv's own ytdl hook tries), or taking too long.
            if (_player.IsResolved(wait.Track) || !_player.IsResolving(wait.Track) || Now - wait.Since > 25)
            {
                _deferred = null;
                wait.Play();
            }
        }
        if (YouTube is not { } yt) return;
        // Search once typing pauses, not on every key.
        if (YtSearchMode && _filter.Length > 0 && _filter != _ytSearched && Now - _filterChangedAt > 0.35)
            StartYtSearch(_filter);
        if (_view == SideView.YouTube && yt.SignedIn && _ytLists is null && !_ytListsLoading && _ytError is null)
            LoadYtLists();
        if (YtBusy) _animSlow = true;
        // A YouTube result the cursor rests on starts resolving, so enter plays it without the ~5 s wait.
        if (_view == SideView.YouTube && QueueVisible && ViewTracks is { } tracks && SelectedFull is int sel and >= 0
            && sel < tracks.Count && tracks[sel].IsStream && Now - _cursorMovedAt > 0.4)
            _player.Prefetch(tracks[sel]);
    }

    /// <summary>Runs a request in the background; `then` runs on the UI thread with the result, or the error is shown.</summary>
    private void YtRun<T>(Func<CancellationToken, Task<T>> request, Action<T> then, Action? always = null, CancellationToken ct = default)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await request(ct);
                _ytDone.Enqueue(() => { always?.Invoke(); if (!ct.IsCancellationRequested) then(result); });
            }
            catch (YouTubeMusicException e)
            {
                _ytDone.Enqueue(() => { always?.Invoke(); _ytError = e.Message; Notice(e.Message); });
            }
            catch (OperationCanceledException)
            {
                _ytDone.Enqueue(() => always?.Invoke());
            }
            catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException)
            {
                _ytDone.Enqueue(() => { always?.Invoke(); _ytError = "respuesta inesperada de YouTube Music"; Notice(_ytError); });
            }
        });
    }

    private void StartYtSearch(string query)
    {
        if (YouTube is not { } yt) return;
        _ytSearched = query;
        _ytSearchCts?.Cancel();
        var cts = _ytSearchCts = new CancellationTokenSource();
        _ytSearching = true;
        _ytError = null;
        YtRun(ct => yt.SearchAsync(query, ct), results =>
        {
            // A newer search may have started meanwhile.
            if (_filter != query) return;
            _ytResults = results;
            ViewCursor() = 0;
            ViewScroll() = 0;
        }, () => { if (ReferenceEquals(_ytSearchCts, cts)) _ytSearching = false; }, cts.Token);
    }

    private void LoadYtLists()
    {
        if (YouTube is not { } yt) return;
        _ytListsLoading = true;
        _ytError = null;
        YtRun(yt.LibraryAsync, lists => _ytLists = lists, () => _ytListsLoading = false);
    }

    /// <summary>A list's songs, from memory or fetched; `then` runs on the UI thread.</summary>
    private void WithYtList(YtPlaylist list, Action<List<Track>> then)
    {
        if (_ytLoaded.TryGetValue(list.BrowseId, out var cached)) { then(cached); return; }
        if (YouTube is not { } yt) return;
        _ytOpenLoading = true;
        YtRun(ct => yt.PlaylistAsync(list.BrowseId, ct), tracks =>
        {
            _ytLoaded[list.BrowseId] = tracks;
            then(tracks);
        }, () => _ytOpenLoading = false);
    }

    private void OpenYtList(YtPlaylist list)
    {
        _ytOpen = list;
        _ytOpenTracks = null;
        _ytListCursor = _ytListScroll = 0;
        WithYtList(list, tracks => { if (ReferenceEquals(_ytOpen, list)) _ytOpenTracks = tracks; });
    }

    private void CloseYtList()
    {
        int at = _ytOpen is null || _ytLists is null ? -1 : _ytLists.IndexOf(_ytOpen);
        _ytOpen = null;
        _ytOpenTracks = null;
        SetFilter("");
        if (at >= 0) _ytCursor = at;
    }

    /// <summary>`i`: signs in with the cookie the user copied from the browser.</summary>
    private void SignInFromClipboard()
    {
        if (YouTube is not { } yt || _ytSigningIn) return;
        if (Clipboard.ReadText() is not { Length: > 0 } text)
        {
            Notice("el portapapeles está vacío: copia primero el valor de la cookie");
            return;
        }
        _ytSigningIn = true;
        _ytError = null;
        YtRun(ct => yt.SignInAsync(text, ct), error =>
        {
            if (error is not null) { _ytError = error; Notice(error); return; }
            _ytLists = null;
            _ytLoaded.Clear();
            Notice("sesión de YouTube Music iniciada");
        }, () => _ytSigningIn = false);
    }

    private void SignOutYouTube()
    {
        YouTube?.SignOut();
        _ytLists = null;
        _ytOpen = null;
        _ytLoaded.Clear();
        _ytError = null;
        Notice("sesión de YouTube Music cerrada (la cookie guardada se borró)");
    }

    /// <summary>`y`: fetches yt-dlp, which mpv needs to play YouTube Music links.</summary>
    private void DownloadYtDlp()
    {
        if (_ytDownloading) return;
        if (YtDlp.Available) { Notice("yt-dlp ya está instalado"); return; }
        _ytDownloading = true;
        YtRun(YtDlp.DownloadAsync, error => Notice(error ?? "yt-dlp listo: ya puedes reproducir YouTube Music"), () => _ytDownloading = false);
    }

    /// <summary>True when the song's start was put on hold until its audio URL is resolved.</summary>
    private bool DeferUntilResolved(Track song, Action play)
    {
        if (!song.IsStream || !YtDlp.Available || _player.IsResolved(song)) return false;
        _player.Prefetch(song);
        if (!_player.IsResolving(song)) return false; // failed a moment ago: let mpv's hook try
        _deferred = (song, play, Now);
        return true;
    }

    /// <summary>Streamed songs need yt-dlp; says how to get it instead of letting mpv skip through them.</summary>
    private bool CanPlay(IEnumerable<Track> tracks)
    {
        if (YtDlp.Available || !tracks.Any(t => t.IsStream)) return true;
        Notice("falta yt-dlp para sonar YouTube Music · y en la pestaña YouTube (4) lo descarga");
        return false;
    }

    /// <summary>Enter on a search result: that song, then its YouTube Music radio (the search never becomes the queue).</summary>
    private void PlayWithRadio(Track seed)
    {
        string context = $"YouTube · radio de {seed.Title}";
        _player.PlayFrom(new List<Track> { seed }, context, 0);
        if (YouTube is not { } yt || StreamTracks.VideoIdOf(seed.Path) is not { } id) return;
        YtRun(ct => yt.RadioAsync(id, ct), radio =>
        {
            if (_player.ContextName == context) _player.AppendToContext(radio);
        });
    }

    /// <summary>Keys only the YouTube tab has; false to fall through to the list keys.</summary>
    private bool HandleYouTubeKey(ConsoleKeyInfo key, bool shift)
    {
        switch (key.Key)
        {
            case ConsoleKey.I: SignInFromClipboard(); return true;
            case ConsoleKey.Y: DownloadYtDlp(); return true;
            case ConsoleKey.R when shift: ReloadYt(); return true;
            case ConsoleKey.D or ConsoleKey.Delete:
                Notice("las listas de YouTube Music se editan en su app · a copia canciones a una lista tuya");
                return true;
            case ConsoleKey.H or ConsoleKey.Backspace when _ytOpen is not null: CloseYtList(); return true;
            case ConsoleKey.Escape when _ytOpen is not null && _filter.Length == 0: CloseYtList(); return true;
        }
        if (!ShowingYtLists) return false;
        switch (key.Key)
        {
            case ConsoleKey.Enter: OpenSelectedYt(); return true;
            case ConsoleKey.X: QueueSelectedYt(front: shift); return true;
            case ConsoleKey.A: CopySelectedYt(); return true;
        }
        return false;
    }

    private YtPlaylist? SelectedYtList => _ytLists is { } all && _ytCursor >= 0 && _ytCursor < all.Count ? all[_ytCursor] : null;

    private void OpenSelectedYt()
    {
        if (SelectedYtList is { } list) OpenYtList(list);
    }

    private void QueueSelectedYt(bool front)
    {
        if (SelectedYtList is not { } list) return;
        WithYtList(list, tracks =>
        {
            if (tracks.Count == 0) { Notice("esa lista está vacía"); return; }
            if (!CanPlay(tracks)) return;
            _player.AddToUpNext(tracks, front);
            Notice($"lista «{list.Title}» a continuación ({tracks.Count})");
        });
    }

    /// <summary>`a` on a list: copies all of it into one of the user's own lists.</summary>
    private void CopySelectedYt()
    {
        if (SelectedYtList is not { } list || Playlists is null) return;
        WithYtList(list, tracks =>
        {
            if (tracks.Count == 0) { Notice("esa lista está vacía"); return; }
            _pickTracks = tracks.ToList();
            _pickRow = 0;
        });
    }

    private void ReloadYt()
    {
        _ytLists = null;
        _ytLoaded.Clear();
        _ytError = null;
        if (YouTube?.SignedIn != true) Notice("inicia sesión con i para ver tu biblioteca");
    }

    private IEnumerable<MenuItem> YouTubeMenuItems()
    {
        const string s = "YouTube Music";
        bool signedIn = YouTube?.SignedIn == true;
        if (ShowingYtLists)
        {
            yield return new(s, "enter", "abrir la lista", OpenSelectedYt);
            yield return new(s, "x", "toda la lista a continuación", () => QueueSelectedYt(false));
            yield return new(s, "a", "copiar la lista a una lista tuya", CopySelectedYt);
            yield return new(s, "R", "recargar tu biblioteca", ReloadYt);
        }
        else if (_ytOpen is not null)
            yield return new(s, "h", "volver a tus listas", CloseYtList);
        yield return new(s, "/", "buscar en todo YouTube Music", StartSearch);
        yield return new(s, "i", signedIn ? "cambiar de cuenta (cookie del portapapeles)" : "iniciar sesión (cookie del portapapeles)", SignInFromClipboard, signedIn ? "con sesión" : "sin sesión");
        if (signedIn) yield return new(s, "", "cerrar sesión y borrar la cookie", SignOutYouTube);
        yield return new(s, "y", "descargar yt-dlp (necesario para sonar)", DownloadYtDlp, YtDlp.Available ? "listo" : "falta");
    }

    // ── drawing ────────────────────────────────────────────────────────

    private void DrawYtLists(Canvas c, int x, int top, int inner, int rows)
    {
        int r = 0;
        void Line(string text, Rgb color)
        {
            if (r < rows) c.Text(x + 2, top + r, text, color, maxWidth: inner - 3);
            r++;
        }

        if (!YtDlp.Available)
        {
            Line(_ytDownloading ? $"{YtSpinner} descargando yt-dlp…" : "⚠ falta yt-dlp para sonar · y lo descarga (18 MB)", _ytDownloading ? Muted : Danger);
            r++;
        }
        if (YouTube?.SignedIn != true)
        {
            Line("/ busca en YouTube Music (sin cuenta)", Theme.Text);
            r++;
            Line(_ytSigningIn ? $"{YtSpinner} comprobando la sesión…" : "Tu biblioteca: inicia sesión", _ytSigningIn ? Muted : Accent);
            Line("1. music.youtube.com con tu cuenta", Muted);
            Line("2. F12 → Red → filtra «browse» → F5", Muted);
            Line("3. clic en una → Encabezados de", Muted);
            Line("   solicitud → copia el valor «cookie»", Muted);
            Line("4. aquí, pulsa i (lee el portapapeles)", Muted);
            r++;
            Line("se guarda cifrada para tu usuario", Muted);
            if (_ytError is { } error) { r++; Line(error, Danger); }
            return;
        }
        if (_ytLists is null)
        {
            Line(_ytListsLoading ? $"{YtSpinner} cargando tu biblioteca…" : _ytError ?? "", _ytError is null ? Muted : Danger);
            return;
        }

        int listRows = rows - r;
        if (listRows <= 0) return;
        _ytCursor = Math.Clamp(_ytCursor, 0, Math.Max(0, _ytLists.Count - 1));
        if (_ytCursor < _ytScroll) _ytScroll = _ytCursor;
        if (_ytCursor >= _ytScroll + listRows) _ytScroll = _ytCursor - listRows + 1;
        _ytScroll = Math.Clamp(_ytScroll, 0, Math.Max(0, _ytLists.Count - listRows));
        var selection = Rgb.Lerp(_palette.Backdrop, Theme.Selection, 0.25);
        for (int row = 0; row < listRows && _ytScroll + row < _ytLists.Count; row++)
        {
            int i = _ytScroll + row, ry = top + r + row;
            var list = _ytLists[i];
            bool selected = i == _ytCursor, playing = _player.ContextName == $"YouTube · {list.Title}" && !_externalMode;
            if (selected) c.FillRow(x, ry, inner, selection);
            if (selected && _fx) c.Set(x, ry, '▌', _second, selection);
            if (playing) DrawMiniEq(c, x + 1, ry);
            string detail = _ytLoaded.TryGetValue(list.BrowseId, out var loaded) ? $"{loaded.Count}" : "";
            int nameMax = inner - 6 - TextWidth.Of(detail) - 2;
            int used = c.Text(x + 5, ry, list.Title, playing ? Accent : Theme.Text, bold: playing || selected, maxWidth: nameMax);
            if (used + 3 < nameMax && list.Detail.Length > 0)
                c.Text(x + 5 + used, ry, " · " + list.Detail, _fx ? Rgb.Lerp(Muted, _third, 0.5) : Muted, maxWidth: nameMax - used);
            if (detail.Length > 0) c.Text(x + inner - detail.Length - 1, ry, detail, Muted);
        }
    }

    /// <summary>What to show in the YouTube tab while there are no song rows yet (searching, loading a list).</summary>
    private string? YtPendingText() =>
        _view != SideView.YouTube ? null
        : _ytOpen is not null ? (_ytOpenLoading ? $"{YtSpinner} cargando «{_ytOpen.Title}»…" : _ytError)
        : _filter.Length == 0 ? null
        : _ytSearching || _filter != _ytSearched ? $"{YtSpinner} buscando en YouTube Music…"
        : _ytError ?? $"YouTube Music no encontró «{_filter}»";
}
