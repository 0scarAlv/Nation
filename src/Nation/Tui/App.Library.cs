using System.Text;
using Nation.Library;

namespace Nation.Tui;

// Left panel: the queue, the whole library and the user's playlists, with type-to-search.
// Searching only narrows what's on screen: playing a result keeps the whole list queued,
// and `x` slips a song into "a continuación" without touching the rest of the queue.
public sealed partial class App
{
    /// <summary>User playlists (.m3u8 files in %LOCALAPPDATA%\Nation\playlists).</summary>
    public PlaylistStore? Playlists { get; init; }

    private enum SideView { Cola, Biblioteca, Listas, YouTube }
    private SideView _view = SideView.Cola;

    // Cursor and scroll per list; the queue's own (_cursor, _scroll) also follows the playing song.
    private int _libCursor, _libScroll, _listsCursor, _listsScroll, _plCursor, _plScroll;
    private Playlist? _openList; // Listas: the list being browsed, null = all lists
    private int _listsVersion;   // bumped when lists are created, renamed or deleted

    // Search (`/`): rows on screen → index in the full list, best match first; null = no search.
    private string _filter = "";
    private bool _typing;
    private List<int>? _rows;
    private (string Filter, object? List, int Version, SideView View) _rowsKey;

    // Add-to-playlist picker (`a`) and the name prompt for new or renamed lists.
    private List<Track>? _pickTracks;
    private int _pickRow;
    private enum PromptKind { None, NewList, Rename }
    private PromptKind _prompt;
    private string _promptText = "";
    private string? _promptError;
    private Playlist? _renaming;
    private Playlist? _confirmDelete;
    private double _confirmAt;

    // Cursor trail, per list (it shouldn't jump between lists when switching).
    private (SideView, Playlist?) _trailKey;

    private static readonly Rgb Danger = Rgb.Hex(0xE06C75);

    /// <summary>Typing goes to a text field (search or a list name), so letters aren't shortcuts.</summary>
    private bool TextEntry => _typing || _prompt != PromptKind.None;

    private IReadOnlyList<Track>? ViewTracks => _view switch
    {
        SideView.Cola => _player.Queue,
        SideView.Biblioteca => _player.Library,
        SideView.YouTube => _ytOpen is not null ? _ytOpenTracks : _filter.Length > 0 ? _ytResults : null,
        _ => _openList?.Tracks,
    };

    private bool ShowingListNames => _view == SideView.Listas && _openList is null;
    private List<Playlist> AllLists => Playlists?.All ?? new List<Playlist>();
    private int FullCount => ShowingListNames ? AllLists.Count : ShowingYtLists ? _ytLists?.Count ?? 0 : ViewTracks?.Count ?? 0;

    /// <summary>The search's ranked rows for the list on screen, recomputed only when the text or the list changes.</summary>
    private List<int>? Rows()
    {
        // YouTube Music ranks its own search results; there's nothing to filter locally.
        if (_filter.Length == 0 || YtSearchMode) return _rows = null;
        object? list = ShowingListNames ? AllLists : ViewTracks;
        int version = _view == SideView.Cola ? _player.Version : ShowingListNames ? _listsVersion : _openList?.Version ?? 0;
        var key = (_filter, list, version, _view);
        if (_rows is not null && key == _rowsKey) return _rows;
        _rowsKey = key;
        return _rows = ShowingListNames
            ? TrackSearch.RankNames(AllLists.Select(p => p.Name).ToList(), _filter)
            : TrackSearch.Rank(ViewTracks ?? Array.Empty<Track>(), _filter);
    }

    private int RowCount => Rows()?.Count ?? FullCount;

    /// <summary>Row on screen → index in the full list (-1 when out of range).</summary>
    private int ToFull(int row)
    {
        var rows = Rows();
        int count = rows?.Count ?? FullCount;
        if (row < 0 || row >= count) return -1;
        return rows is null ? row : rows[row];
    }

    private ref int ViewCursor()
    {
        if (_view == SideView.Cola) return ref _cursor;
        if (_view == SideView.Biblioteca) return ref _libCursor;
        if (_view == SideView.YouTube) return ref _ytOpen is null ? ref _ytCursor : ref _ytListCursor;
        if (_openList is null) return ref _listsCursor;
        return ref _plCursor;
    }

    private ref int ViewScroll()
    {
        if (_view == SideView.Cola) return ref _scroll;
        if (_view == SideView.Biblioteca) return ref _libScroll;
        if (_view == SideView.YouTube) return ref _ytOpen is null ? ref _ytScroll : ref _ytListScroll;
        if (_openList is null) return ref _listsScroll;
        return ref _plScroll;
    }

    private int SelectedFull => ToFull(ViewCursor());

    private void MoveCursor(int delta)
    {
        ref int cursor = ref ViewCursor();
        cursor = Math.Clamp(cursor + delta, 0, Math.Max(0, RowCount - 1));
        if (_view == SideView.Cola) _lastManualMove = DateTime.UtcNow;
    }

    /// <summary>The sidebar is needed for lists and search; brings it back if hidden. False while following another app.</summary>
    private bool ShowSidebar()
    {
        if (_externalMode)
        {
            Notice($"{External?.AppName} no tiene lista local; vuelve a la biblioteca con o");
            return false;
        }
        if (!Ui.ShowQueue)
        {
            Ui = Ui with { ShowQueue = true };
            Ui.Save();
        }
        return true;
    }

    private void SwitchView(SideView view)
    {
        if (!ShowSidebar()) return;
        if (view != _view) SetFilter("");
        _view = view;
        _typing = false;
    }

    private void CycleView(int delta)
    {
        int n = Enum.GetValues<SideView>().Length - (YouTube is null ? 1 : 0);
        SwitchView((SideView)(((int)_view + delta + n) % n));
    }

    /// <summary>`c`: back to the queue, on the playing song.</summary>
    private void ShowCurrent()
    {
        if (QueueVisible) SwitchView(SideView.Cola);
        JumpToCurrent();
    }

    private void SetFilter(string text)
    {
        if (text == _filter) return;
        _filter = text;
        _rows = null;
        _filterChangedAt = Now;
        if (YtSearchMode && text.Length == 0) { _ytResults = null; _ytSearched = ""; }
        // A new search starts at its best match.
        ViewCursor() = 0;
        ViewScroll() = 0;
    }

    /// <summary>`/`: search the library (or the list being browsed); results update with every key.</summary>
    private void StartSearch()
    {
        if (!ShowSidebar()) return;
        if (_view == SideView.Cola) SwitchView(SideView.Biblioteca);
        SetFilter("");
        _typing = true;
    }

    private string ViewName => _view switch
    {
        SideView.Cola => "cola",
        SideView.Biblioteca => "biblioteca",
        SideView.YouTube => _ytOpen is null ? "youtube music" : $"youtube «{_ytOpen.Title}»",
        _ => _openList is null ? "listas" : $"lista «{_openList.Name}»",
    };

    // ── keys ───────────────────────────────────────────────────────────

    /// <summary>`/` and the view numbers work from anywhere (they bring the sidebar back).</summary>
    private bool HandleLibraryShortcut(ConsoleKeyInfo key)
    {
        switch (key.KeyChar)
        {
            case '/': StartSearch(); return true;
            case '1': SwitchView(SideView.Cola); return true;
            case '2': SwitchView(SideView.Biblioteca); return true;
            case '3': SwitchView(SideView.Listas); return true;
            case '4' when YouTube is not null: SwitchView(SideView.YouTube); return true;
        }
        return false;
    }

    private bool HandleSidebarKey(ConsoleKeyInfo key, bool shift)
    {
        if (_view == SideView.YouTube && HandleYouTubeKey(key, shift)) return true;
        int count = RowCount;
        ref int cursor = ref ViewCursor();
        cursor = Math.Clamp(cursor, 0, Math.Max(0, count - 1));
        switch (key.Key)
        {
            // Shift+j/k carry the song along instead of moving the cursor.
            case ConsoleKey.J or ConsoleKey.DownArrow when shift: Reorder(+1); return true;
            case ConsoleKey.K or ConsoleKey.UpArrow when shift: Reorder(-1); return true;
            case ConsoleKey.J or ConsoleKey.DownArrow: MoveCursor(1); return true;
            case ConsoleKey.K or ConsoleKey.UpArrow: MoveCursor(-1); return true;
            case ConsoleKey.PageDown: MoveCursor(10); return true;
            case ConsoleKey.PageUp: MoveCursor(-10); return true;
            case ConsoleKey.G when shift: MoveCursor(count); return true;
            case ConsoleKey.End: MoveCursor(count); return true;
            case ConsoleKey.G or ConsoleKey.Home: MoveCursor(-count); return true;
            case ConsoleKey.Enter: Activate(SelectedFull); return true;
            case ConsoleKey.X: QueueSelected(SelectedFull, front: shift); return true;
            case ConsoleKey.D or ConsoleKey.Delete: RemoveSelected(SelectedFull); return true;
            case ConsoleKey.A when ShowingListNames: StartPrompt(PromptKind.NewList); return true;
            case ConsoleKey.A: OpenPicker(SelectedFull); return true;
            case ConsoleKey.R when shift && ShowingListNames:
                if (SelectedFull >= 0) StartPrompt(PromptKind.Rename, AllLists[SelectedFull]);
                return true;
            case ConsoleKey.H or ConsoleKey.Backspace when _view == SideView.Listas && _openList is not null:
                CloseList();
                return true;
            case ConsoleKey.Tab: CycleView(shift ? -1 : 1); return true;
            case ConsoleKey.Escape when _filter.Length > 0: SetFilter(""); return true;
            case ConsoleKey.Escape when _view == SideView.Listas && _openList is not null: CloseList(); return true;
        }
        return false;
    }

    private void CloseList()
    {
        int at = _openList is null ? -1 : AllLists.IndexOf(_openList);
        _openList = null;
        SetFilter("");
        if (at >= 0) _listsCursor = at;
    }

    /// <summary>Enter: play the song (keeping its whole list queued), or open a playlist.</summary>
    private void Activate(int full)
    {
        if (full < 0) return;
        // A YouTube Music song that isn't resolved yet waits for Nation's resolver instead of
        // making mpv run a second yt-dlp in parallel (which made both take twice as long).
        if (!ShowingListNames && !ShowingYtLists && ViewTracks is { } shown && full < shown.Count && shown[full] is { IsStream: true } song
            && DeferUntilResolved(song, () =>
            {
                if (ViewTracks is { } now && full < now.Count && ReferenceEquals(now[full], song)) ActivateNow(full);
            }))
            return;
        ActivateNow(full);
    }

    private void ActivateNow(int full)
    {
        if (!ShowingListNames && ViewTracks is { } tracks && !CanPlay(new[] { tracks[full] })) return;
        switch (_view)
        {
            case SideView.Cola:
                SetSource(external: false);
                _player.PlayEntry(full);
                break;
            case SideView.YouTube when _ytOpen is null:
                SetSource(external: false);
                PlayWithRadio(_ytResults![full]);
                break;
            case SideView.YouTube:
                SetSource(external: false);
                if (_player.ContextName != $"YouTube · {_ytOpen.Title}") Notice($"YouTube · {_ytOpen.Title}");
                _player.PlayFrom(_ytOpenTracks!, $"YouTube · {_ytOpen.Title}", full);
                break;
            case SideView.Biblioteca:
                SetSource(external: false);
                if (_player.ContextName is not null) Notice("biblioteca completa");
                _player.PlayFrom(_player.Library, null, full);
                break;
            case SideView.Listas when _openList is null:
                _openList = AllLists[full];
                SetFilter("");
                _plCursor = _plScroll = 0;
                return;
            default:
                SetSource(external: false);
                if (!CanPlay(_openList!.Tracks)) return;
                if (_player.ContextName != _openList!.Name) Notice($"lista «{_openList.Name}»");
                _player.PlayFrom(_openList.Tracks, _openList.Name, full);
                break;
        }
        _lastManualMove = DateTime.MinValue;
    }

    /// <summary>`x`: after the current song (and after what's already waiting); `X`: right after the current song.</summary>
    private void QueueSelected(int full, bool front)
    {
        if (full < 0) return;
        List<Track> tracks;
        string what;
        if (ShowingListNames)
        {
            var list = AllLists[full];
            tracks = list.Tracks.ToList();
            what = $"lista «{list.Name}» ({tracks.Count})";
            if (tracks.Count == 0) { Notice("esa lista está vacía"); return; }
        }
        else
        {
            var t = ViewTracks![full];
            tracks = new List<Track> { t };
            what = t.Title;
        }
        if (!CanPlay(tracks)) return;
        if (_view == SideView.Cola) _player.QueueEntry(full, front);
        else _player.AddToUpNext(tracks, front);
        Notice(front ? $"sigue: {what}" : $"a continuación (#{_player.UpNextCount}): {what}");
    }

    private void RemoveSelected(int full)
    {
        if (full < 0) return;
        switch (_view)
        {
            case SideView.Cola:
                Notice(_player.RemoveEntry(full) ? "quitada de la cola" : "es la que está sonando");
                break;
            case SideView.Biblioteca:
                Notice("la biblioteca es tu carpeta: quita canciones de la cola o de una lista");
                break;
            case SideView.Listas when _openList is null:
                var list = AllLists[full];
                if (ReferenceEquals(_confirmDelete, list) && Now - _confirmAt < 3)
                {
                    Playlists?.Delete(list);
                    _listsVersion++;
                    _confirmDelete = null;
                    Notice($"lista «{list.Name}» borrada");
                }
                else
                {
                    _confirmDelete = list;
                    _confirmAt = Now;
                    Notice($"d otra vez para borrar «{list.Name}»");
                }
                break;
            default:
                Playlists?.RemoveAt(_openList!, full);
                Notice($"quitada de «{_openList!.Name}»");
                break;
        }
    }

    /// <summary>Shift+j/k: moves the song under the cursor, in the queue or in a playlist.</summary>
    private void Reorder(int delta)
    {
        if (_filter.Length > 0) { Notice("quita la búsqueda (esc) para reordenar"); return; }
        if (_view == SideView.Cola)
        {
            int to = _player.MoveEntry(_cursor, delta);
            if (to == _cursor) Notice(_cursor <= _player.CurrentIndex ? "solo se mueven las que vienen después de la actual" : "ya está en el borde");
            _cursor = to;
            _lastManualMove = DateTime.UtcNow;
        }
        else if (_view == SideView.Listas && _openList is not null && Playlists is { } store)
            _plCursor = store.Move(_openList, _plCursor, delta);
        else
            Notice("se reordena en la cola (1) o dentro de una lista (3)");
    }

    private void HandleSearchKey(ConsoleKeyInfo key)
    {
        bool ctrl = key.Modifiers.HasFlag(ConsoleModifiers.Control);
        switch (key.Key)
        {
            case ConsoleKey.Escape: _typing = false; SetFilter(""); return;
            // Enter keeps the results and hands the keys back to the list (enter plays, x queues).
            case ConsoleKey.Enter:
                _typing = false;
                if (YtSearchMode && _filter.Length > 0 && _filter != _ytSearched) StartYtSearch(_filter);
                return;
            case ConsoleKey.DownArrow: MoveCursor(1); return;
            case ConsoleKey.UpArrow: MoveCursor(-1); return;
            case ConsoleKey.PageDown: MoveCursor(10); return;
            case ConsoleKey.PageUp: MoveCursor(-10); return;
        }
        if (EditText(key, ctrl, _filter, 80) is { } text) SetFilter(text);
    }

    /// <summary>Backspace, Ctrl+Backspace/Ctrl+W (word), Ctrl+U (all) and printable characters; null when the key isn't editing.</summary>
    private static string? EditText(ConsoleKeyInfo key, bool ctrl, string text, int max)
    {
        if (key.Key == ConsoleKey.Backspace && ctrl || key.Key == ConsoleKey.W && ctrl)
        {
            string trimmed = text.TrimEnd();
            int cut = trimmed.LastIndexOf(' ');
            return cut < 0 ? "" : trimmed[..(cut + 1)];
        }
        if (key.Key == ConsoleKey.U && ctrl) return "";
        if (key.Key == ConsoleKey.Backspace)
        {
            if (text.Length == 0) return text;
            int drop = text.Length >= 2 && char.IsLowSurrogate(text[^1]) ? 2 : 1;
            return text[..^drop];
        }
        char ch = key.KeyChar;
        if (ch < ' ' || char.IsControl(ch) || text.Length >= max) return null;
        return text + ch;
    }

    // ── add to playlist / name prompt ──────────────────────────────────

    private void OpenPicker(int full)
    {
        if (full < 0 || ViewTracks is not { } list) return;
        if (Playlists is null) return;
        _pickTracks = new List<Track> { list[full] };
        _pickRow = 0;
    }

    private void HandlePickerKey(ConsoleKeyInfo key)
    {
        int rows = 1 + AllLists.Count;
        switch (key.Key)
        {
            case ConsoleKey.Escape: _pickTracks = null; return;
            case ConsoleKey.J or ConsoleKey.DownArrow: _pickRow = (_pickRow + 1) % rows; return;
            case ConsoleKey.K or ConsoleKey.UpArrow: _pickRow = (_pickRow - 1 + rows) % rows; return;
            case ConsoleKey.Enter:
                if (_pickRow == 0) { StartPrompt(PromptKind.NewList); return; }
                AddPicked(AllLists[_pickRow - 1]);
                return;
        }
    }

    private void AddPicked(Playlist list)
    {
        if (_pickTracks is not { } tracks || Playlists is not { } store) return;
        bool already = tracks.Count == 1 && list.Tracks.Any(t => ReferenceEquals(t, tracks[0]));
        store.Add(list, tracks);
        Notice(already ? $"agregada otra vez a «{list.Name}»" : $"agregada a «{list.Name}» ({list.Tracks.Count})");
        _pickTracks = null;
    }

    private void StartPrompt(PromptKind kind, Playlist? renaming = null)
    {
        if (Playlists is null) return;
        _prompt = kind;
        _renaming = renaming;
        _promptText = renaming?.Name ?? "";
        _promptError = null;
    }

    private void HandlePromptKey(ConsoleKeyInfo key)
    {
        bool ctrl = key.Modifiers.HasFlag(ConsoleModifiers.Control);
        switch (key.Key)
        {
            case ConsoleKey.Escape:
                _prompt = PromptKind.None;
                _pickTracks = null;
                return;
            case ConsoleKey.Enter:
                SubmitPrompt();
                return;
        }
        if (EditText(key, ctrl, _promptText, 60) is { } text)
        {
            _promptText = text;
            _promptError = null;
        }
    }

    private void SubmitPrompt()
    {
        if (Playlists is not { } store) return;
        if (store.Validate(_promptText, _renaming) is { } error) { _promptError = error; return; }
        if (_prompt == PromptKind.Rename && _renaming is { } list)
        {
            string old = list.Name;
            store.Rename(list, _promptText);
            _player.RenameContext(old, list.Name);
            Notice($"renombrada a «{list.Name}»");
        }
        else
        {
            var created = store.Create(_promptText);
            if (_pickTracks is not null) AddPicked(created);
            else Notice($"lista «{created.Name}» creada · a en cualquier canción la agrega");
            _listsCursor = store.All.IndexOf(created);
        }
        _listsVersion++;
        _prompt = PromptKind.None;
        _renaming = null;
    }

    // ── menu ───────────────────────────────────────────────────────────

    private IEnumerable<MenuItem> SidebarMenuItems()
    {
        const string s = "Lista";
        yield return new(s, "/", "buscar: escribe y se filtra al momento", StartSearch, _filter.Length > 0 ? _filter : null);
        yield return new(s, YouTube is null ? "1 2 3" : "1 2 3 4", YouTube is null ? "cola · biblioteca · listas (tab cambia)" : "cola · biblioteca · listas · youtube (tab)", () => CycleView(1), ViewName);
        yield return new(s, "j k", "mover · g/G inicio/final · enter reproducir", () => { });
        if (ShowingListNames)
        {
            yield return new(s, "enter", "abrir la lista", () => Activate(SelectedFull));
            yield return new(s, "a", "nueva lista", () => StartPrompt(PromptKind.NewList));
            yield return new(s, "R", "renombrar lista", () => { if (SelectedFull >= 0) StartPrompt(PromptKind.Rename, AllLists[SelectedFull]); });
            yield return new(s, "d", "borrar lista (d dos veces)", () => RemoveSelected(SelectedFull));
            yield return new(s, "x", "toda la lista a continuación", () => QueueSelected(SelectedFull, false));
        }
        else
        {
            yield return new(s, "x", "a continuación (después de las que esperan)", () => QueueSelected(SelectedFull, false), _player.UpNextCount > 0 ? $"{_player.UpNextCount} en espera" : null);
            yield return new(s, "X", "a continuación, primera", () => QueueSelected(SelectedFull, true));
            yield return new(s, "J K", "subir / bajar la canción (cola y listas)", () => { });
            if (_view is SideView.Cola or SideView.Listas) yield return new(s, "d", _view == SideView.Cola ? "quitar de la cola" : "quitar de la lista", () => RemoveSelected(SelectedFull));
            yield return new(s, "a", "agregar a una lista", () => OpenPicker(SelectedFull));
            if (_openList is not null) yield return new(s, "h", "volver a las listas", CloseList);
        }
        yield return new(s, "c", "ir a la canción actual", ShowCurrent);
        if (_view == SideView.YouTube)
            foreach (var item in YouTubeMenuItems()) yield return item;
    }

    // ── drawing ────────────────────────────────────────────────────────

    private void DrawSidebar(Canvas c, int x, int y, int w, int h, int current)
    {
        Panel(c, x, y, w, h, "", Accent, Accent);
        DrawTabs(c, x, y, w);
        int top = y + 1, rows = h - 2, inner = w - 2;
        if (_typing || _filter.Length > 0)
        {
            DrawSearchBar(c, x + 1, top, inner);
            top++;
            rows--;
        }
        if (rows <= 0) return;
        if (ShowingListNames) DrawListNames(c, x + 1, top, inner, rows);
        else if (ShowingYtLists) DrawYtLists(c, x + 1, top, inner, rows);
        else if (ViewTracks is null || _view == SideView.YouTube && ViewTracks.Count == 0)
            c.Text(x + 3, top, YtPendingText() ?? "", _ytError is not null && !YtBusy ? Danger : Muted, maxWidth: inner - 3);
        else DrawTracks(c, x + 1, top, inner, rows, current);
    }

    /// <summary>The three lists as tabs in the panel's top border; the active one lit.</summary>
    private void DrawTabs(Canvas c, int x, int y, int w)
    {
        int up = _player.UpNextCount;
        string cola = "Cola" + (_player.Shuffled ? " ⤮" : "") + (up > 0 ? $" +{up}" : "");
        string listas = _openList is null ? "Listas" : $"Listas › {_openList.Name}";
        string youtube = _ytOpen is null ? "YouTube" : $"YouTube › {_ytOpen.Title}";
        var tabs = new[] { (SideView.Cola, cola), (SideView.Biblioteca, "Biblioteca"), (SideView.Listas, listas), (SideView.YouTube, youtube) };
        int cx = x + 2, end = x + w - 3;
        foreach (var (view, label) in tabs)
        {
            if (cx >= end) break;
            if (view == SideView.YouTube && YouTube is null) continue;
            bool active = view == _view;
            c.Set(cx++, y, ' ');
            cx += c.Text(cx, y, label, active ? Accent : Muted, bold: active, maxWidth: end - cx);
            c.Set(cx++, y, ' ');
        }
    }

    private void DrawSearchBar(Canvas c, int x, int y, int w)
    {
        string count = !YtSearchMode ? $"{RowCount}/{FullCount}"
            : _ytSearching || _filter != _ytSearched ? YtSpinner : _ytResults is { } found ? $"{found.Count}" : "";
        c.Text(x + 1, y, "/", Accent, bold: true);
        int textW = w - 4 - count.Length - 2;
        // Show the end of a long query, where the typing happens.
        string shown = _filter;
        while (TextWidth.Of(shown) > textW - 1 && shown.Length > 0) shown = shown[1..];
        int used = c.Text(x + 3, y, shown, _typing ? Theme.Text : Rgb.Lerp(Theme.Text, Muted, 0.4), maxWidth: textW);
        if (_typing) c.Set(x + 3 + used, y, '▏', Accent);
        else if (_filter.Length > 0 && used + 16 < textW) c.Text(x + 3 + used + 2, y, "esc quita", Muted);
        c.Text(x + w - 1 - count.Length, y, count, RowCount == 0 ? Danger : Muted);
    }

    private void DrawTracks(Canvas c, int x, int top, int inner, int rows, int current)
    {
        var list = ViewTracks;
        if (list is null) return;
        var map = Rows();
        int count = map?.Count ?? list.Count;
        ref int cursor = ref ViewCursor();
        ref int scroll = ref ViewScroll();
        cursor = Math.Clamp(cursor, 0, Math.Max(0, count - 1));
        if (cursor < scroll) scroll = cursor;
        if (cursor >= scroll + rows) scroll = cursor - rows + 1;
        scroll = Math.Clamp(scroll, 0, Math.Max(0, count - rows));

        if (count == 0)
        {
            string empty = _filter.Length > 0 ? $"nada coincide con «{_filter}»"
                : _openList is not null ? "lista vacía · a en una canción de la biblioteca la agrega aquí"
                : "";
            c.Text(x + 2, top, empty, Muted, maxWidth: inner - 3);
            return;
        }

        // Songs waiting in "a continuación" are marked in the other lists too.
        HashSet<Track>? waiting = null;
        if (_view != SideView.Cola && _player.UpNextCount > 0)
            waiting = new HashSet<Track>(_player.UpNext, ReferenceEqualityComparer.Instance);

        var selection = Rgb.Lerp(_palette.Backdrop, Theme.Selection, 0.25);
        var trailKey = (_view, _openList);
        if (cursor != _lastCursor || trailKey != _trailKey)
        {
            if (_lastCursor >= 0 && trailKey == _trailKey) { _prevCursor = _lastCursor; _cursorMovedAt = Now; }
            else _prevCursor = -1;
            _lastCursor = cursor;
            _trailKey = trailKey;
        }
        double trail = (Now - _cursorMovedAt) / 0.3;
        if (_fx && trail < 1) _animFast = true;
        int numWidth = Math.Max(2, list.Count.ToString().Length);
        var hit = _fx ? Rgb.Lerp(_second, Fx.White, 0.35) : Fx.White;
        for (int row = 0; row < rows; row++)
        {
            int r = scroll + row;
            if (r >= count) break;
            int i = map is null ? r : map[r];
            var t = list[i];
            int ry = top + row, rx = x;
            bool playing = _view == SideView.Cola ? i == current : current >= 0 && ReferenceEquals(t, _player.Current);
            bool selected = r == cursor;
            bool queued = _view == SideView.Cola ? i > current && _player.IsQueued(i) : waiting?.Contains(t) == true;

            // Playing row: a band of the cover's backdrop that brightens with the bass.
            if (playing && !selected && _fx)
                c.FillRow(rx, ry, inner, Rgb.Lerp(_palette.Backdrop, Accent, _player.Playing ? BassStep * 0.22 : 0));
            if (selected) c.FillRow(rx, ry, inner, selection);
            if (selected && _fx) c.Set(rx, ry, '▌', _second, selection);
            else if (_fx && r == _prevCursor && trail < 1) c.Set(rx, ry, '▌', Rgb.Lerp(_second, _palette.Backdrop, trail));
            if (playing) DrawMiniEq(c, rx + 1, ry);
            else if (queued) c.Set(rx + 3, ry, '+', _fx ? _third : Accent, bold: true);
            c.Text(rx + 5, ry, (i + 1).ToString().PadLeft(numWidth), queued ? (_fx ? _third : Accent) : _fx ? Rgb.Lerp(Muted, _second, 0.35) : Muted);

            string dur = FormatTime(t.Duration.TotalSeconds);
            int textX = rx + 5 + numWidth + 2;
            int textMax = inner - (textX - rx) - dur.Length - 2;
            int used = c.Text(textX, ry, t.Title, playing ? Accent : Theme.Text, bold: playing, maxWidth: textMax);
            if (_filter.Length > 0) Highlight(c, textX, ry, t.Title, used, hit);
            if (used + 3 < textMax)
            {
                int artistX = textX + used + 3;
                c.Text(textX + used, ry, " · " + t.Artist, _fx ? Rgb.Lerp(Muted, _third, 0.5) : Muted, maxWidth: textMax - used);
                if (_filter.Length > 0) Highlight(c, artistX, ry, t.Artist, textMax - used - 3, hit);
            }
            if (playing && _fx && _player.Playing)
            {
                // A light band sweeps across the playing title every few seconds.
                double sweep = Now % 4.0 / 0.9 * (used + 6) - 3;
                for (int col = Math.Max(0, (int)sweep - 2); col <= sweep + 2 && col < used; col++)
                    c.Recolor(textX + col, ry, Rgb.Lerp(Accent, Fx.White, 1 - Math.Abs(col - sweep) / 3));
            }
            c.Text(rx + inner - dur.Length - 1, ry, dur, _fx ? Rgb.Lerp(Muted, _second, 0.25) : Muted);
        }
    }

    /// <summary>Lights the characters that matched the search, within the first `width` columns of `text` drawn at x.</summary>
    private void Highlight(Canvas c, int x, int y, string text, int width, Rgb color)
    {
        var mask = TrackSearch.Matches(text, _filter);
        int col = 0;
        for (int i = 0; i < text.Length;)
        {
            if (Rune.DecodeFromUtf16(text.AsSpan(i), out var rune, out int used) != System.Buffers.OperationStatus.Done) { i++; continue; }
            int rw = TextWidth.Of(rune);
            if (col + rw > width) break;
            if (mask[i])
                for (int k = 0; k < rw; k++) c.Recolor(x + col + k, y, color);
            col += rw;
            i += used;
        }
    }

    private void DrawListNames(Canvas c, int x, int top, int inner, int rows)
    {
        var lists = AllLists;
        var map = Rows();
        int count = map?.Count ?? lists.Count;
        ref int cursor = ref ViewCursor();
        ref int scroll = ref ViewScroll();
        cursor = Math.Clamp(cursor, 0, Math.Max(0, count - 1));
        if (cursor < scroll) scroll = cursor;
        if (cursor >= scroll + rows) scroll = cursor - rows + 1;
        scroll = Math.Clamp(scroll, 0, Math.Max(0, count - rows));

        if (count == 0)
        {
            string empty = _filter.Length > 0 ? $"ninguna lista coincide con «{_filter}»" : "todavía no hay listas · a crea una";
            c.Text(x + 2, top, empty, Muted, maxWidth: inner - 3);
            if (_filter.Length == 0 && rows > 2)
                c.Text(x + 2, top + 2, "en cualquier canción, a la agrega a una lista", Muted, maxWidth: inner - 3);
            return;
        }

        var selection = Rgb.Lerp(_palette.Backdrop, Theme.Selection, 0.25);
        bool confirming = _confirmDelete is not null && Now - _confirmAt < 3;
        if (confirming) _animSlow = true;
        for (int row = 0; row < rows; row++)
        {
            int r = scroll + row;
            if (r >= count) break;
            var list = lists[map is null ? r : map[r]];
            int ry = top + row;
            bool selected = r == cursor, playing = _player.ContextName == list.Name && !_externalMode;
            if (selected) c.FillRow(x, ry, inner, selection);
            if (selected && _fx) c.Set(x, ry, '▌', _second, selection);
            if (playing) DrawMiniEq(c, x + 1, ry);
            string detail = confirming && ReferenceEquals(list, _confirmDelete) ? "d otra vez borra"
                : $"{list.Tracks.Count} · {FormatTime(list.Duration.TotalSeconds)}";
            var detailColor = confirming && ReferenceEquals(list, _confirmDelete) ? Danger : Muted;
            int nameMax = inner - 6 - TextWidth.Of(detail) - 2;
            int used = c.Text(x + 5, ry, list.Name, playing ? Accent : Theme.Text, bold: playing || selected, maxWidth: nameMax);
            if (_filter.Length > 0) Highlight(c, x + 5, ry, list.Name, used, _fx ? Rgb.Lerp(_second, Fx.White, 0.35) : Fx.White);
            c.Text(x + inner - TextWidth.Of(detail) - 1, ry, detail, detailColor);
        }
    }

    /// <summary>Dark centered box shared by the overlays; returns its inner left/top and background.</summary>
    private (int X, int Y, Rgb Bg) Overlay(Canvas c, int w, int h, string title)
    {
        w = Math.Min(c.Width - 4, w);
        h = Math.Min(c.Height - 2, h);
        int x = (c.Width - w) / 2, y = Math.Max(1, (c.Height - h) / 2);
        var bg = Rgb.Lerp(_palette.Backdrop, Rgb.Hex(0x0A0A0C), 0.55);
        for (int row = y; row < y + h; row++)
            for (int col = x; col < x + w; col++) c.Set(col, row, ' ', null, bg);
        c.Box(x, y, w, h, title, _fx ? Rgb.Lerp(Accent, Fx.White, 0.2) : Accent, Accent);
        return (x, y, bg);
    }

    private void DrawPicker(Canvas c)
    {
        var lists = AllLists;
        int w = 48, h = Math.Min(lists.Count + 1, 14) + 4;
        string song = _pickTracks is [var one] ? one.Title : $"{_pickTracks?.Count} canciones";
        var (x, y, bg) = Overlay(c, w, h, $"Agregar «{song}» a…");
        w = Math.Min(c.Width - 4, w);
        h = Math.Min(c.Height - 2, h);
        int rows = h - 4, scroll = Math.Clamp(_pickRow - rows / 2, 0, Math.Max(0, lists.Count + 1 - rows));
        var selection = Rgb.Lerp(bg, Theme.Selection, 0.8);
        for (int r = 0; r < rows && scroll + r <= lists.Count; r++)
        {
            int index = scroll + r, ry = y + 1 + r;
            bool selected = index == _pickRow;
            var rowBg = selected ? selection : bg;
            if (selected) for (int col = x + 1; col < x + w - 1; col++) c.Set(col, ry, ' ', null, rowBg);
            if (selected) c.Set(x + 1, ry, '▌', _second, rowBg);
            if (index == 0)
            {
                c.Text(x + 3, ry, "+ nueva lista…", selected ? Fx.White : Accent, rowBg, bold: true);
                continue;
            }
            var list = lists[index - 1];
            string n = list.Tracks.Count.ToString();
            c.Text(x + 3, ry, list.Name, selected ? Theme.Text : Rgb.Lerp(Theme.Text, Muted, 0.35), rowBg, maxWidth: w - 8 - n.Length);
            c.Text(x + w - 2 - n.Length, ry, n, _second, rowBg);
        }
        c.Text(x + 2, y + h - 2, "j/k elegir · enter agregar · esc cancelar", Muted, bg, maxWidth: w - 4);
    }

    private void DrawPrompt(Canvas c)
    {
        string title = _prompt == PromptKind.Rename ? $"Renombrar «{_renaming?.Name}»" : "Nueva lista";
        int w = 50;
        var (x, y, bg) = Overlay(c, w, 6, title);
        w = Math.Min(c.Width - 4, w);
        int fieldW = w - 6;
        string shown = _promptText;
        while (TextWidth.Of(shown) > fieldW - 1 && shown.Length > 0) shown = shown[1..];
        c.Text(x + 2, y + 2, "›", Accent, bg, bold: true);
        int used = c.Text(x + 4, y + 2, shown, Theme.Text, bg, maxWidth: fieldW);
        c.Set(x + 4 + used, y + 2, '▏', Accent, bg);
        if (_promptError is { } error) c.Text(x + 2, y + 4, error, Danger, bg, maxWidth: w - 4);
        else c.Text(x + 2, y + 4, "enter guardar · esc cancelar", Muted, bg, maxWidth: w - 4);
    }

    /// <summary>Footer hint for whatever is taking the keys right now, or null.</summary>
    private string? SidebarHint() =>
        _typing && YtSearchMode ? "escribe para buscar en YouTube Music · ↑↓ elegir · enter listo · esc borra"
        : _filter.Length > 0 && YtSearchMode && QueueVisible ? "enter la reproduce y sigue con su radio · x a continuación · a a una lista tuya"
        : _prompt != PromptKind.None ? "escribe el nombre · enter guardar · esc cancelar"
        : _pickTracks is not null ? "enter agrega a la lista · esc cancelar"
        : _typing ? "escribe para buscar · ↑↓ elegir · enter listo · esc borra"
        : _filter.Length > 0 && QueueVisible ? "enter reproduce (sigue toda la lista) · x a continuación · esc quita la búsqueda"
        : null;
}
