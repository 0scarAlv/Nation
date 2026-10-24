using System.Collections.Concurrent;
using Nation.Library;
using Nation.Streaming;

namespace Nation.Tui;

// YouTube Music mode (chosen in the sources panel, `o`): the sidebar's library becomes
// YouTube Music's. "Principal" is its home page, "Listas" the user's YouTube lists, and
// `/` searches all of it (songs, artists, albums, lists). Albums, lists and artists open
// as pages on top (h goes back), like the app. Songs are links mpv plays through yt-dlp,
// so in the queue they behave like files: x, J/K, the equalizer, lyrics from LRCLIB.
public sealed partial class App
{
    /// <summary>YouTube Music client (home, search, radio, library); null to leave the mode out.</summary>
    public YouTubeMusic? YouTube { get; init; }

    /// <summary>The sidebar shows YouTube Music instead of the local library.</summary>
    private bool YtMode => YouTube is not null && Sources.YouTubeMode && !_externalMode;

    /// <summary>A YouTube Music page is on screen (every tab but the queue, in YouTube mode).</summary>
    private bool InYt => YtMode && _view != SideView.Cola;

    // Network calls finish on other threads; their results are applied here on the UI thread.
    private readonly ConcurrentQueue<Action> _ytDone = new();

    /// <summary>One screen of YouTube Music: shelves of items (home, search, an artist, your lists) or a song list (album, playlist).</summary>
    private sealed class YtPage
    {
        public string Title = "";
        public List<YtShelf>? Shelves;
        public List<Track>? Tracks;
        public bool Loading;
        public string? Error;
        /// <summary>Your lists without a session: the page explains how to sign in.</summary>
        public bool NeedsSignIn;
        public int Cursor, Scroll;
        private List<(string? Header, YtItem? Item)>? _flat;

        /// <summary>The shelves as rows: a header row per titled shelf, then its items.</summary>
        public List<(string? Header, YtItem? Item)> Flat
        {
            get
            {
                if (_flat is not null || Shelves is null) return _flat ?? new();
                _flat = new();
                foreach (var shelf in Shelves)
                {
                    if (shelf.Title.Length > 0) _flat.Add((shelf.Title, null));
                    foreach (var item in shelf.Items) _flat.Add((null, item));
                }
                return _flat;
            }
        }
    }

    // Each tab keeps its own trail of opened pages; [0] is the tab's own page.
    private readonly List<YtPage> _ytHome = new(), _ytLibrary = new();
    private YtPage? _ytSearch;            // results of `/` on the home tab
    private string _ytSearched = "";
    private string _ytHomeQuery = "";     // the search left behind when a result was opened
    private CancellationTokenSource? _ytSearchCts;
    private readonly Dictionary<string, YtCollection> _ytOpened = new();
    private double _filterChangedAt;
    private bool _ytDownloading, _ytSigningIn;
    // Enter on a song still being resolved: play it the moment its audio URL is ready.
    private (Track Track, Action Play, double Since)? _deferred;

    private List<YtPage> YtStack => _view == SideView.Listas ? _ytLibrary : _ytHome;

    /// <summary>The page on screen in YouTube mode, or null.</summary>
    private YtPage? YtTop =>
        !InYt ? null
        : YtStack.Count > 1 ? YtStack[^1]
        : _view == SideView.Biblioteca && _filter.Length > 0 ? _ytSearch
        : YtStack.Count == 1 ? YtStack[0] : null;

    /// <summary>Typing on the home tab searches YouTube Music itself, not the rows on screen.</summary>
    private bool YtSearchMode => InYt && _view == SideView.Biblioteca && _ytHome.Count <= 1;

    /// <summary>The page on screen is made of shelves (not a plain song list).</summary>
    private bool ShowingYtRows => YtTop is { Tracks: null };

    private bool YtBusy => _ytDownloading || _ytSigningIn || _ytHome.Concat(_ytLibrary).Append(_ytSearch).Any(p => p?.Loading == true);

    private string YtSpinner => Spinner[(int)(Now * 12) % Spinner.Length].ToString();

    private string YtContext(YtPage page) => $"YouTube · {page.Title}";

    /// <summary>Once per frame: apply finished requests, load the tab's page, run the debounced search, prefetch.</summary>
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
        if (YouTube is not { } yt || !InYt) return;
        if (_view == SideView.Biblioteca && _ytHome.Count == 0) LoadHome(yt);
        if (_view == SideView.Listas && _ytLibrary.Count == 0) LoadLibrary(yt);
        // Search once typing pauses, not on every key.
        if (YtSearchMode && _filter.Length > 0 && _filter != _ytSearched && Now - _filterChangedAt > 0.35)
            StartYtSearch(_filter);
        if (YtBusy) _animSlow = true;
        // The song under a cursor that rests starts resolving, so enter plays it without the ~5 s wait.
        if (QueueVisible && SelectedYtSong() is { } song && Now - _cursorMovedAt > 0.4) _player.Prefetch(song);
    }

    private Track? SelectedYtSong()
    {
        if (YtTop is not { } page) return null;
        if (page.Tracks is { } tracks) return SelectedFull is int i and >= 0 && i < tracks.Count ? tracks[i] : null;
        var flat = page.Flat;
        return page.Cursor >= 0 && page.Cursor < flat.Count ? flat[page.Cursor].Item?.Song : null;
    }

    /// <summary>Runs a request in the background; `then` runs on the UI thread with the result, `failed` with the error.</summary>
    private void YtRun<T>(Func<CancellationToken, Task<T>> request, Action<T> then, Action<string> failed, CancellationToken ct = default)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await request(ct);
                _ytDone.Enqueue(() => { if (!ct.IsCancellationRequested) then(result); });
            }
            catch (YouTubeMusicException e) { _ytDone.Enqueue(() => failed(e.Message)); }
            catch (OperationCanceledException) { }
            catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or NullReferenceException)
            {
                _ytDone.Enqueue(() => failed("respuesta inesperada de YouTube Music"));
            }
        });
    }

    /// <summary>Fills a page from a request, showing a spinner meanwhile and the error if it fails.</summary>
    private void Fill(YtPage page, Func<CancellationToken, Task<(List<YtShelf>? Shelves, List<Track>? Tracks)>> request, CancellationToken ct = default)
    {
        page.Loading = true;
        page.Error = null;
        YtRun(request, result =>
        {
            page.Loading = false;
            page.Shelves = result.Shelves;
            page.Tracks = result.Tracks;
            page.Cursor = FirstItemRow(page, 0, +1);
        }, error => { page.Loading = false; page.Error = error; Notice(error); }, ct);
    }

    private void LoadHome(YouTubeMusic yt)
    {
        var page = new YtPage { Title = "Principal" };
        _ytHome.Add(page);
        Fill(page, async ct => ((List<YtShelf>?)await yt.HomeAsync(ct), null));
    }

    private void LoadLibrary(YouTubeMusic yt)
    {
        var page = new YtPage { Title = "Tus listas", NeedsSignIn = !yt.SignedIn };
        _ytLibrary.Add(page);
        if (!yt.SignedIn) return;
        Fill(page, async ct =>
        {
            var lists = await yt.LibraryAsync(ct);
            var items = lists.Select(l => new YtItem(YtKind.Playlist, l.Title, l.Detail, BrowseId: l.BrowseId)).ToList();
            return (new List<YtShelf> { new("", items) }, (List<Track>?)null);
        });
    }

    private void StartYtSearch(string query)
    {
        if (YouTube is not { } yt) return;
        _ytSearched = query;
        _ytSearchCts?.Cancel();
        var cts = _ytSearchCts = new CancellationTokenSource();
        var page = _ytSearch = new YtPage { Title = $"«{query}»" };
        Fill(page, async ct => ((List<YtShelf>?)await yt.SearchAllAsync(query, ct), null), cts.Token);
    }

    /// <summary>Opens an album, list or artist as a new page on this tab.</summary>
    private void OpenYt(YtItem item)
    {
        if (YouTube is not { } yt) return;
        // Leaving the search results: keep the query to bring them back with h.
        if (YtSearchMode && _filter.Length > 0)
        {
            _ytHomeQuery = _filter;
            _filter = "";
            _rows = null;
        }
        var page = new YtPage { Title = item.Title };
        YtStack.Add(page);
        _typing = false;
        Fill(page, async ct =>
        {
            var opened = await OpenedAsync(yt, item, ct);
            return (opened.Shelves, opened.Tracks);
        });
    }

    /// <summary>An item's contents, fetched once per session.</summary>
    private async Task<YtCollection> OpenedAsync(YouTubeMusic yt, YtItem item, CancellationToken ct)
    {
        string key = item.BrowseId ?? item.Title;
        lock (_ytOpened)
            if (_ytOpened.TryGetValue(key, out var cached)) return cached;
        var opened = await yt.OpenAsync(item, ct);
        lock (_ytOpened) _ytOpened[key] = opened;
        return opened;
    }

    /// <summary>The songs an item stands for: itself, an album's or list's songs, an artist's top songs.</summary>
    private void WithSongs(YtItem item, Action<List<Track>> then)
    {
        if (item.Song is { } song) { then(new List<Track> { song }); return; }
        if (YouTube is not { } yt) return;
        Notice($"{YtSpinner} cargando «{item.Title}»…");
        YtRun(ct => OpenedAsync(yt, item, ct), opened =>
        {
            var tracks = opened.Tracks ?? opened.Shelves?.SelectMany(s => s.Items).Select(i => i.Song).OfType<Track>().Take(20).ToList() ?? new();
            if (tracks.Count == 0) Notice($"«{item.Title}» no tiene canciones");
            else then(tracks);
        }, error => Notice(error));
    }

    private void CloseYtPage()
    {
        if (YtStack.Count <= 1) return;
        YtStack.RemoveAt(YtStack.Count - 1);
        _rows = null;
        _filter = YtStack.Count == 1 && _view == SideView.Biblioteca ? _ytHomeQuery : "";
        if (YtStack.Count == 1) _ytHomeQuery = "";
    }

    /// <summary>`R`: reloads this tab from YouTube Music.</summary>
    private void ReloadYt()
    {
        YtStack.Clear();
        lock (_ytOpened) _ytOpened.Clear();
        _ytSearch = null;
        _ytSearched = "";
        SetFilter("");
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
        YtRun(ct => yt.SignInAsync(text, ct), error =>
        {
            _ytSigningIn = false;
            if (error is not null) { Notice(error); return; }
            // Home and lists become the account's own.
            _ytHome.Clear();
            _ytLibrary.Clear();
            lock (_ytOpened) _ytOpened.Clear();
            Notice("sesión de YouTube Music iniciada");
        }, error => { _ytSigningIn = false; Notice(error); });
    }

    private void SignOutYouTube()
    {
        YouTube?.SignOut();
        _ytHome.Clear();
        _ytLibrary.Clear();
        lock (_ytOpened) _ytOpened.Clear();
        Notice("sesión de YouTube Music cerrada (la cookie guardada se borró)");
    }

    /// <summary>`y`: fetches yt-dlp, which mpv needs to play YouTube Music links.</summary>
    private void DownloadYtDlp()
    {
        if (_ytDownloading) return;
        if (YtDlp.Available) { Notice("yt-dlp ya está instalado"); return; }
        _ytDownloading = true;
        YtRun(YtDlp.DownloadAsync,
            error => { _ytDownloading = false; Notice(error ?? "yt-dlp listo: ya puedes reproducir YouTube Music"); },
            error => { _ytDownloading = false; Notice(error); });
    }

    /// <summary>Switches the sidebar between the local library and YouTube Music (sources panel).</summary>
    private void SetYouTubeMode(bool on)
    {
        if (Sources.YouTubeMode != on)
        {
            Sources = Sources with { YouTubeMode = on };
            Sources.Save();
        }
        SetSource(external: false);
        Notice(on ? "YouTube Music" : "biblioteca local");
        SetFilter("");
        _typing = false;
        if (on && ShowSidebar()) _view = SideView.Biblioteca;
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
        Notice("falta yt-dlp para sonar YouTube Music · y lo descarga (modo YouTube)");
        return false;
    }

    /// <summary>A song on its own (home, search, an artist): it plays, then its YouTube Music radio, like the app.</summary>
    private void PlayWithRadio(Track seed)
    {
        if (!CanPlay(new[] { seed })) return;
        void Start()
        {
            SetSource(external: false);
            string context = $"YouTube · radio de {seed.Title}";
            _player.PlayFrom(new List<Track> { seed }, context, 0);
            _lastManualMove = DateTime.MinValue;
            if (YouTube is not { } yt || StreamTracks.VideoIdOf(seed.Path) is not { } id) return;
            YtRun(ct => yt.RadioAsync(id, ct), radio =>
            {
                if (_player.ContextName == context) _player.AppendToContext(radio);
            }, error => Notice(error));
        }
        if (!DeferUntilResolved(seed, Start)) Start();
    }

    /// <summary>Enter on a song of an album or list page: the page's songs become the queue from there.</summary>
    private void PlayYtPage(YtPage page, int index)
    {
        if (page.Tracks is not { } tracks || index < 0 || index >= tracks.Count) return;
        SetSource(external: false);
        if (_player.ContextName != YtContext(page)) Notice(YtContext(page));
        _player.PlayFrom(tracks, YtContext(page), index);
    }

    // ── keys ───────────────────────────────────────────────────────────

    /// <summary>Keys of the YouTube pages; false to fall through to the song-list keys (album and list pages).</summary>
    private bool HandleYouTubeKey(ConsoleKeyInfo key, bool shift)
    {
        switch (key.Key)
        {
            case ConsoleKey.I: SignInFromClipboard(); return true;
            case ConsoleKey.Y: DownloadYtDlp(); return true;
            case ConsoleKey.R when shift: ReloadYt(); return true;
            case ConsoleKey.D or ConsoleKey.Delete:
                Notice("YouTube Music se edita en su app · a copia canciones a una lista tuya (modo local)");
                return true;
            case ConsoleKey.H or ConsoleKey.Backspace when YtStack.Count > 1: CloseYtPage(); return true;
            case ConsoleKey.Escape when YtStack.Count > 1 && _filter.Length == 0: CloseYtPage(); return true;
        }
        if (!ShowingYtRows || YtTop is not { } page) return false;
        var flat = page.Flat;
        var item = page.Cursor >= 0 && page.Cursor < flat.Count ? flat[page.Cursor].Item : null;
        switch (key.Key)
        {
            case ConsoleKey.J or ConsoleKey.DownArrow when shift: Notice("se reordena en la cola (1)"); return true;
            case ConsoleKey.K or ConsoleKey.UpArrow when shift: Notice("se reordena en la cola (1)"); return true;
            case ConsoleKey.J or ConsoleKey.DownArrow: MoveYtCursor(page, +1); return true;
            case ConsoleKey.K or ConsoleKey.UpArrow: MoveYtCursor(page, -1); return true;
            case ConsoleKey.PageDown: MoveYtCursor(page, +10); return true;
            case ConsoleKey.PageUp: MoveYtCursor(page, -10); return true;
            case ConsoleKey.G when shift: page.Cursor = FirstItemRow(page, flat.Count - 1, -1); return true;
            case ConsoleKey.End: page.Cursor = FirstItemRow(page, flat.Count - 1, -1); return true;
            case ConsoleKey.G or ConsoleKey.Home: page.Cursor = FirstItemRow(page, 0, +1); return true;
            case ConsoleKey.Enter:
                if (item?.Song is { } song) PlayWithRadio(song);
                else if (item is not null) OpenYt(item);
                return true;
            case ConsoleKey.X:
                if (item is null) return true;
                bool front = shift;
                WithSongs(item, tracks =>
                {
                    if (!CanPlay(tracks)) return;
                    _player.AddToUpNext(tracks, front);
                    string what = tracks.Count == 1 ? tracks[0].Title : $"«{item.Title}» ({tracks.Count})";
                    Notice(front ? $"sigue: {what}" : $"a continuación (#{_player.UpNextCount}): {what}");
                });
                return true;
            case ConsoleKey.A:
                if (item is null || Playlists is null) return true;
                WithSongs(item, tracks => { _pickTracks = tracks; _pickRow = 0; });
                return true;
            case ConsoleKey.Tab: CycleView(shift ? -1 : 1); return true;
        }
        return false;
    }

    /// <summary>Moves the cursor by `delta` item rows, stepping over shelf headers.</summary>
    private void MoveYtCursor(YtPage page, int delta)
    {
        var flat = page.Flat;
        if (flat.Count == 0) return;
        int step = Math.Sign(delta), at = page.Cursor;
        for (int moved = 0; moved < Math.Abs(delta); moved++)
        {
            int next = at + step;
            while (next >= 0 && next < flat.Count && flat[next].Item is null) next += step;
            if (next < 0 || next >= flat.Count) break;
            at = next;
        }
        page.Cursor = at;
    }

    /// <summary>The nearest item row from `from` going `step` (headers can't hold the cursor).</summary>
    private static int FirstItemRow(YtPage page, int from, int step)
    {
        var flat = page.Flat;
        for (int i = Math.Clamp(from, 0, Math.Max(0, flat.Count - 1)); i >= 0 && i < flat.Count; i += step)
            if (flat[i].Item is not null) return i;
        return 0;
    }

    private IEnumerable<MenuItem> YouTubeMenuItems()
    {
        const string s = "YouTube Music";
        bool signedIn = YouTube?.SignedIn == true;
        yield return new(s, "/", "buscar: canciones, artistas, álbumes y listas", StartSearch, _filter.Length > 0 ? _filter : null);
        yield return new(s, "1 2 3", "cola · principal · tus listas (tab cambia)", () => CycleView(1), ViewName);
        yield return new(s, "j k", "mover · enter reproducir o abrir · g/G inicio/final", () => { });
        yield return new(s, "x", "a continuación (canción, álbum, lista o artista)", () => { }, _player.UpNextCount > 0 ? $"{_player.UpNextCount} en espera" : null);
        yield return new(s, "a", "agregar a una lista tuya", () => { });
        if (YtStack.Count > 1) yield return new(s, "h", "volver", CloseYtPage);
        yield return new(s, "R", "recargar desde YouTube Music", ReloadYt);
        yield return new(s, "i", signedIn ? "cambiar de cuenta (cookie del portapapeles)" : "iniciar sesión (cookie del portapapeles)", SignInFromClipboard, signedIn ? "con sesión" : "sin sesión");
        if (signedIn) yield return new(s, "", "cerrar sesión y borrar la cookie", SignOutYouTube);
        yield return new(s, "y", "descargar yt-dlp (necesario para sonar)", DownloadYtDlp, YtDlp.Available ? "listo" : "falta");
        yield return new(s, "c", "ir a la canción actual", ShowCurrent);
    }

    // ── drawing ────────────────────────────────────────────────────────

    private void DrawYouTube(Canvas c, int x, int top, int inner, int rows, int current)
    {
        int r = 0;
        void Line(string text, Rgb color)
        {
            if (r < rows) c.Text(x + 2, top + r, text, color, maxWidth: inner - 3);
            r++;
        }
        if (!YtDlp.Available)
        {
            Line(_ytDownloading ? $"{YtSpinner} descargando yt-dlp…" : "⚠ falta yt-dlp para sonar · y lo descarga", _ytDownloading ? Muted : Danger);
            r++;
        }
        if (YtTop is not { } page) return;
        if (page.NeedsSignIn || page.Error is not null && YouTube?.SignedIn != true)
        {
            DrawSignInHelp(Line);
            return;
        }
        // The session turned out expired while loading: same help as signed out.
        if (_view == SideView.Listas && YouTube?.SessionExpired == true && !page.Loading)
        {
            DrawSignInHelp(Line);
            return;
        }
        if (page.Loading) { Line($"{YtSpinner} cargando {page.Title}…", Muted); return; }
        if (page.Error is { } error)
        {
            Line(error, Danger);
            if (YouTube?.SignedIn == true && _view == SideView.Listas) { r++; DrawSignInHelp(Line); }
            return;
        }
        if (page.Tracks is not null)
        {
            DrawTracks(c, x, top + r, inner, rows - r, current);
            return;
        }
        if (page.Flat.Count == 0)
        {
            Line(_view == SideView.Listas ? "no tienes listas guardadas en YouTube Music" : $"nada en {page.Title}", Muted);
            return;
        }
        DrawYtRows(c, x, top + r, inner, rows - r, page);
    }

    private void DrawSignInHelp(Action<string, Rgb> line)
    {
        if (YouTube?.SessionExpired == true && !_ytSigningIn) line("tu sesión caducó: vuelve a pegar la cookie", Danger);
        line(_ytSigningIn ? $"{YtSpinner} comprobando la sesión…" : "Tu biblioteca: inicia sesión (i)", _ytSigningIn ? Muted : Accent);
        line("1. ventana privada → music.youtube.com", Muted);
        line("   e inicia sesión con tu cuenta", Muted);
        line("2. F12 → Red → filtra «browse» → F5", Muted);
        line("3. clic en una → Encabezados de", Muted);
        line("   solicitud → copia el valor «cookie»", Muted);
        line("4. cierra esa ventana SIN cerrar sesión", Muted);
        line("5. aquí, pulsa i (lee el portapapeles)", Muted);
        line("", Muted);
        line("se guarda cifrada para tu usuario", Muted);
        line("sin sesión: la Principal y / funcionan", Muted);
    }

    private static string KindLabel(YtKind kind) => kind switch
    {
        YtKind.Album => "álbum",
        YtKind.Playlist => "lista",
        YtKind.Artist => "artista",
        _ => "",
    };

    /// <summary>YouTube's subtitles start with the kind ("Álbum • Arctic Monkeys • 2013"); the row already says it on the right.</summary>
    private static string WithoutKind(YtItem item)
    {
        if (item.Kind == YtKind.Song) return item.Subtitle;
        int dot = item.Subtitle.IndexOf(" • ", StringComparison.Ordinal);
        if (dot < 0) return item.Subtitle;
        string first = item.Subtitle[..dot].Trim().ToLowerInvariant();
        bool isKind = first is "artista" or "álbum" or "album" or "lista" or "lista de reproducción" or "single" or "ep" or "sencillo";
        return isKind ? item.Subtitle[(dot + 3)..] : item.Subtitle;
    }

    private static char KindGlyph(YtKind kind) => kind switch
    {
        YtKind.Album => '◎',
        YtKind.Playlist => '≡',
        YtKind.Artist => '◆',
        _ => '♪',
    };

    /// <summary>Shelves as rows: headers, then songs (title · artist, length) and things that open (title · subtitle, kind).</summary>
    private void DrawYtRows(Canvas c, int x, int top, int inner, int rows, YtPage page)
    {
        var flat = page.Flat;
        page.Cursor = Math.Clamp(page.Cursor, 0, Math.Max(0, flat.Count - 1));
        if (flat.Count > 0 && flat[page.Cursor].Item is null) page.Cursor = FirstItemRow(page, page.Cursor, +1);
        // Keep the header of the cursor's shelf in view when moving up into it.
        int want = page.Cursor > 0 && flat[page.Cursor - 1].Item is null ? page.Cursor - 1 : page.Cursor;
        if (want < page.Scroll) page.Scroll = want;
        if (page.Cursor >= page.Scroll + rows) page.Scroll = page.Cursor - rows + 1;
        page.Scroll = Math.Clamp(page.Scroll, 0, Math.Max(0, flat.Count - rows));

        HashSet<Track>? waiting = _player.UpNextCount > 0 ? new HashSet<Track>(_player.UpNext, ReferenceEqualityComparer.Instance) : null;
        var selection = Rgb.Lerp(_palette.Backdrop, Theme.Selection, 0.25);
        var hit = _fx ? Rgb.Lerp(_second, Fx.White, 0.35) : Fx.White;
        bool searching = ReferenceEquals(page, _ytSearch) && _filter.Length > 0;
        for (int row = 0; row < rows && page.Scroll + row < flat.Count; row++)
        {
            int i = page.Scroll + row, ry = top + row;
            var (header, item) = flat[i];
            if (item is null)
            {
                int used = c.Text(x + 2, ry, header ?? "", _fx ? _third : Accent, bold: true, maxWidth: inner - 4);
                for (int col = x + 3 + used; col < x + inner - 1; col++) c.Set(col, ry, '─', Rgb.Lerp(Theme.Border, _palette.Backdrop, 0.3));
                continue;
            }
            bool selected = i == page.Cursor, playing = item.Song is { } s && ReferenceEquals(s, _player.Current);
            if (selected) c.FillRow(x, ry, inner, selection);
            if (selected && _fx) c.Set(x, ry, '▌', _second, selection);
            if (playing) DrawMiniEq(c, x + 1, ry);
            else if (item.Song is { } w && waiting?.Contains(w) == true) c.Set(x + 3, ry, '+', _fx ? _third : Accent, bold: true);
            c.Set(x + 4, ry, KindGlyph(item.Kind), item.Kind == YtKind.Song ? Muted : _fx ? _second : Accent);

            string right = item.Song is { Duration.Ticks: > 0 } song ? FormatTime(song.Duration.TotalSeconds) : KindLabel(item.Kind);
            int textX = x + 6, textMax = inner - 6 - TextWidth.Of(right) - 2;
            int usedTitle = c.Text(textX, ry, item.Title, playing ? Accent : Theme.Text, bold: playing || selected && item.Kind != YtKind.Song, maxWidth: textMax);
            if (searching) Highlight(c, textX, ry, item.Title, usedTitle, hit);
            string subtitle = WithoutKind(item);
            if (usedTitle + 3 < textMax && subtitle.Length > 0)
                c.Text(textX + usedTitle, ry, " · " + subtitle, _fx ? Rgb.Lerp(Muted, _third, 0.5) : Muted, maxWidth: textMax - usedTitle);
            c.Text(x + inner - TextWidth.Of(right) - 1, ry, right, _fx ? Rgb.Lerp(Muted, _second, 0.25) : Muted);
        }
    }
}
