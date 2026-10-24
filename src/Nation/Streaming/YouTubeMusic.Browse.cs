using System.Text.Json.Nodes;
using Nation.Library;

namespace Nation.Streaming;

public enum YtKind { Song, Album, Playlist, Artist }

/// <summary>A row of YouTube Music: a song, or something that opens (album, playlist, artist).</summary>
public sealed record YtItem(YtKind Kind, string Title, string Subtitle, Track? Song = null, string? BrowseId = null);

/// <summary>A titled group of items, like the rows of the home page or the sections of a search.</summary>
public sealed record YtShelf(string Title, List<YtItem> Items);

/// <summary>What opening an item shows: a song list (album, playlist) or more shelves (artist).</summary>
public sealed record YtCollection(string Title, List<Track>? Tracks, List<YtShelf>? Shelves);

// Home, full search, albums and artists: the browsing half of the client.
public sealed partial class YouTubeMusic
{
    // ytmusicapi's search filters.
    private const string AlbumsFilter = "EgWKAQIYAWoMEA4QChADEAQQCRAF";
    private const string ArtistsFilter = "EgWKAQIgAWoMEA4QChADEAQQCRAF";
    private const string FeaturedListsFilter = "EgeKAQQoADgBagwQDhAKEAMQBBAJEAU=";
    private const string CommunityListsFilter = "EgeKAQQoAEABagwQDhAKEAMQBBAJEAU=";

    /// <summary>The home page ("Principal"): personal with a session, generic without. First few pages of shelves.</summary>
    public async Task<List<YtShelf>> HomeAsync(CancellationToken ct)
    {
        var shelves = new List<YtShelf>();
        int pages = 0;
        await foreach (var page in PagesAsync(new JsonObject { ["browseId"] = "FEmusic_home" }, legacy: true, ct))
        {
            shelves.AddRange(Shelves(page));
            if (++pages >= 3) break;
        }
        return shelves;
    }

    /// <summary>
    /// Search over songs, artists, albums and playlists at once. The unfiltered search
    /// returns one untitled mixed list, so each kind is asked for separately, in parallel.
    /// </summary>
    public async Task<List<YtShelf>> SearchAllAsync(string query, CancellationToken ct)
    {
        Task<JsonNode> Ask(string filter) => PostAsync("search", new JsonObject { ["query"] = query, ["params"] = filter }, ct);
        var songs = Ask(SongsFilter);
        var artists = Ask(ArtistsFilter);
        var albums = Ask(AlbumsFilter);
        var featured = Ask(FeaturedListsFilter);
        var community = Ask(CommunityListsFilter);
        await Task.WhenAll(songs, artists, albums, featured, community);

        List<YtItem> Items(JsonNode root, int max) =>
            Find(root, "musicResponsiveListItemRenderer").Select(ListItem).OfType<YtItem>().Take(max).ToList();
        var shelves = new List<YtShelf>
        {
            new("Canciones", Items(songs.Result, 10)),
            new("Artistas", Items(artists.Result, 4)),
            new("Álbumes", Items(albums.Result, 6)),
            new("Listas", Items(featured.Result, 3).Concat(Items(community.Result, 5)).ToList()),
        };
        return shelves.Where(s => s.Items.Count > 0).ToList();
    }

    /// <summary>Opens an album, playlist or artist.</summary>
    public async Task<YtCollection> OpenAsync(YtItem item, CancellationToken ct)
    {
        if (item.BrowseId is not { } id) return new YtCollection(item.Title, new List<Track>(), null);
        switch (item.Kind)
        {
            case YtKind.Playlist:
                return new YtCollection(item.Title, await PlaylistAsync(id, ct), null);
            case YtKind.Album:
                return await AlbumAsync(id, item.Title, ct);
            default:
                var page = await PostAsync("browse", new JsonObject { ["browseId"] = id }, ct);
                return new YtCollection(item.Title, null, Shelves(page));
        }
    }

    /// <summary>An album's songs. Its rows carry neither cover nor (usually) artist: those come from the header.</summary>
    private async Task<YtCollection> AlbumAsync(string browseId, string fallbackTitle, CancellationToken ct)
    {
        var page = await PostAsync("browse", new JsonObject { ["browseId"] = browseId }, ct);
        var header = Find(page, "musicResponsiveHeaderRenderer").FirstOrDefault();
        string title = Runs(header?["title"]) is { Length: > 0 } t ? t : fallbackTitle;
        string artist = Runs(header?["straplineTextOne"]);
        string? cover = Cover(header?["thumbnail"]?["musicThumbnailRenderer"]?["thumbnail"]);
        var tracks = new List<Track>();
        foreach (var shelf in Find(page, "musicShelfRenderer").Take(1))
            foreach (var row in Find(shelf, "musicResponsiveListItemRenderer"))
                if (ListSong(row, artist, title, cover) is { } song) tracks.Add(song);
        return new YtCollection(title, tracks, null);
    }

    /// <summary>Every shelf on a page: lists of rows (top songs) and carousels of cards.</summary>
    private static List<YtShelf> Shelves(JsonNode page)
    {
        var shelves = new List<YtShelf>();
        foreach (var shelf in FindAny(page, "musicShelfRenderer", "musicCarouselShelfRenderer", "musicImmersiveCarouselShelfRenderer"))
            {
                string title = Runs(shelf["title"]) is { Length: > 0 } t ? t
                    : Runs(shelf["header"]?["musicCarouselShelfBasicHeaderRenderer"]?["title"]) is { Length: > 0 } c ? c
                    : Runs(shelf["header"]?["musicImmersiveCarouselShelfBasicHeaderRenderer"]?["title"]);
                var items = new List<YtItem>();
                if (shelf["contents"] is JsonArray contents)
                    foreach (var entry in contents)
                    {
                        var item = entry?["musicResponsiveListItemRenderer"] is { } row ? ListItem(row)
                            : entry?["musicTwoRowItemRenderer"] is { } card ? CardItem(card) : null;
                        if (item is not null) items.Add(item);
                    }
                if (items.Count > 0) shelves.Add(new YtShelf(title, items));
            }
        return shelves;
    }

    /// <summary>Like Find, for several keys at once, keeping the page's order (so shelves show as on the site).</summary>
    private static List<JsonNode> FindAny(JsonNode? node, params string[] keys)
    {
        var found = new List<JsonNode>();
        void Walk(JsonNode? n)
        {
            switch (n)
            {
                case JsonObject o:
                    foreach (var (k, v) in o)
                        if (!keys.Contains(k)) Walk(v);
                        else if (v is not null) found.Add(v);
                    break;
                case JsonArray a:
                    foreach (var child in a) Walk(child);
                    break;
            }
        }
        Walk(node);
        return found;
    }

    private static YtKind? KindOf(string? pageType) => pageType switch
    {
        "MUSIC_PAGE_TYPE_ALBUM" => YtKind.Album,
        "MUSIC_PAGE_TYPE_PLAYLIST" => YtKind.Playlist,
        "MUSIC_PAGE_TYPE_ARTIST" or "MUSIC_PAGE_TYPE_USER_CHANNEL" => YtKind.Artist,
        _ => null,
    };

    private static string? PageType(JsonNode? browseEndpoint) =>
        Str(browseEndpoint, "browseEndpointContextSupportedConfigs", "browseEndpointContextMusicConfig", "pageType");

    /// <summary>A list row: an album/artist/playlist when the row itself links to one, else a song.</summary>
    private static YtItem? ListItem(JsonNode row)
    {
        var browse = row["navigationEndpoint"]?["browseEndpoint"];
        if (KindOf(PageType(browse)) is { } kind && Str(browse, "browseId") is { } id)
        {
            var columns = row["flexColumns"] as JsonArray;
            string title = Runs(columns?[0]?["musicResponsiveListItemFlexColumnRenderer"]?["text"]);
            string subtitle = columns is { Count: > 1 } ? Runs(columns[1]?["musicResponsiveListItemFlexColumnRenderer"]?["text"]) : "";
            return new YtItem(kind, title, subtitle, BrowseId: id);
        }
        return ListSong(row) is { } song ? new YtItem(YtKind.Song, song.Title, song.Artist, song) : null;
    }

    /// <summary>A card (carousels): links to an album/playlist/artist, or plays a song.</summary>
    private static YtItem? CardItem(JsonNode card)
    {
        string title = Runs(card["title"]), subtitle = Runs(card["subtitle"]);
        var nav = card["navigationEndpoint"] ?? card["title"]?["runs"]?[0]?["navigationEndpoint"];
        if (nav?["browseEndpoint"] is { } browse && KindOf(PageType(browse)) is { } kind && Str(browse, "browseId") is { } id)
            return new YtItem(kind, title, subtitle, BrowseId: id);
        if (Str(nav, "watchPlaylistEndpoint", "playlistId") is { } list)
            return new YtItem(YtKind.Playlist, title, subtitle, BrowseId: "VL" + list);
        if (Str(nav, "watchEndpoint", "videoId") is { } videoId)
        {
            var artists = new List<string>();
            if (card["subtitle"]?["runs"] is JsonArray runs)
                foreach (var run in runs)
                    if (KindOf(PageType(run?["navigationEndpoint"]?["browseEndpoint"])) == YtKind.Artist) artists.Add(Str(run, "text") ?? "");
            string artist = artists.Count > 0 ? string.Join(", ", artists) : subtitle.Split(" • ")[0];
            var song = StreamTracks.Get(videoId, title, artist, "", TimeSpan.Zero,
                Cover(card["thumbnailRenderer"]?["musicThumbnailRenderer"]?["thumbnail"]));
            return new YtItem(YtKind.Song, title, subtitle, song);
        }
        return null;
    }
}
