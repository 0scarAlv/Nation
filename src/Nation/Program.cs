using Nation.Audio;
using Nation.External;
using Nation.Library;
using Nation.Lyrics;
using Nation.Remote;
using Nation.Streaming;
using Nation.Tui;

// Usage: nation [files|folders...] [--shuffle] [--volume <0-100>] [--offline] [--no-api] [--api-port <n>] [--no-media-keys] [--skin] [--no-external] [--listens-db <file>] [--sessions] [--snapshot <file> [--external] [--snapshot-keys <keys>]]
// With no paths, plays the user's Music folder.
var paths = new List<string>();
string? snapshotPath = null, snapshotKeys = null, listensDb = null;
bool shuffle = false, offline = false, api = true, smtc = true, external = true, externalSnapshot = false, skin = false, demoSpectrum = false;
int apiPort = TauonCompatServer.DefaultPort;
int? volume = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--snapshot" && i + 1 < args.Length) snapshotPath = args[++i];
    else if (args[i] == "--snapshot-keys" && i + 1 < args.Length) snapshotKeys = args[++i];
    else if (args[i] == "--listens-db" && i + 1 < args.Length) listensDb = args[++i];
    else if (args[i] == "--shuffle") shuffle = true;
    else if (args[i] == "--offline") offline = true;
    else if (args[i] == "--no-api") api = false;
    else if (args[i] == "--no-media-keys") smtc = false;
    else if (args[i] == "--no-external") external = false;
    else if (args[i] == "--external") externalSnapshot = true;
    else if (args[i] == "--sessions") { }
    else if (args[i] == "--skin") skin = true;
    else if (args[i] == "--demo-spectrum") demoSpectrum = true;
    else if (args[i] == "--volume" && i + 1 < args.Length && int.TryParse(args[i + 1], out var vol)) { volume = Math.Clamp(vol, 0, 100); i++; }
    else if (args[i] == "--api-port" && i + 1 < args.Length && int.TryParse(args[i + 1], out var port)) { apiPort = port; i++; }
    else paths.Add(args[i]);
}
// Diagnostic: list the media sessions skin mode can follow, then exit.
if (args.Contains("--sessions"))
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    Console.WriteLine(await ExternalPlayer.DescribeSessionsAsync());
    return 0;
}
if (paths.Count == 0) paths.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));

var tracks = TrackScanner.Scan(paths);
// No local music is fine: Nation can still skin Spotify or a browser. Without that either, there's nothing to do.
if (tracks.Count == 0 && (!external || snapshotPath is not null && !externalSnapshot))
{
    Console.Error.WriteLine("No se encontraron archivos de audio en: " + string.Join(", ", paths));
    return 1;
}

// mpv's ytdl hook finds yt-dlp on PATH, including Nation's own copy (downloaded on request).
YtDlp.PrepareEnvironment();
using var player = new Player();
player.Enqueue(tracks);
if (shuffle) player.ToggleShuffle();
if (volume is { } startVolume) player.SetVolume(startVolume);

using var spectrum = new SpectrumAnalyzer();
spectrum.Start();
// Tauon-compatible API so Shade can show and control Nation (snapshots stay offline).
using var remote = api && snapshotPath is null ? new TauonCompatServer(apiPort) : null;
remote?.Start();
// Windows media session: keyboard media keys + volume flyout (snapshots stay out of it).
using var media = smtc && snapshotPath is null ? MediaSession.TryCreate() : null;
// Other apps' sessions, so Nation can skin Spotify/YouTube Music (snapshots only with --external).
var sources = SourceSettings.Load();
using var externalPlayer = external && (snapshotPath is null || externalSnapshot) ? ExternalPlayer.TryStart(sources.PinnedAppId) : null;

// Listening journal for stats; snapshots only open one passed explicitly (their few muted seconds stay under the 5 s minimum).
using var listens = snapshotPath is null || listensDb is not null ? TryOpenListens(listensDb) : null;
// Shade's phone player syncs its journal with this one through the API.
if (remote is not null && listens is not null) remote.Sync = new Nation.Stats.ListenSync(listens);

using var youtube = new YouTubeMusic();
if (snapshotPath is null) YtDlp.UpdateInBackground();
var app = new App(player, spectrum, new LyricsProvider(online: !offline)) { Playlists = PlaylistStore.Load(player.Library), YouTube = youtube, Remote = remote, Media = media, External = externalPlayer, StartAsSkin = skin || tracks.Count == 0, Sources = sources, Ui = UiSettings.Load(), DemoSpectrum = demoSpectrum && snapshotPath is not null, PersistSession = snapshotPath is null, Listens = listens };
// Remote and media-key commands act on whatever the player is showing (Nation or the followed app).
if (remote is not null) remote.OnCommand = app.RunCommand;
if (media is not null) media.OnCommand = app.RunCommand;
// Pick up where the last run left off (an explicit --volume wins over the saved one).
if (snapshotPath is null) app.RestoreSession(volume: volume is null);

var frame = TimeSpan.FromMilliseconds(20);

if (snapshotPath is not null)
{
    // Debug aid: run the animation loop off-screen for a few seconds, then dump one frame.
    // Muted so a snapshot never talks over whatever is already playing.
    player.ToggleMute();
    var canvas = new Canvas(110, 28);
    var keysAt = DateTime.UtcNow.AddSeconds(1.5);
    // Long key sequences get the time they need, plus a second to settle.
    double seconds = Math.Max(3, 1.5 + (snapshotKeys?.Length ?? 0) * 0.25 + 1);
    for (var until = DateTime.UtcNow.AddSeconds(seconds); DateTime.UtcNow < until; Thread.Sleep(frame))
    {
        // --snapshot-keys: press one key every 0.25 s once sessions had time to load (letters, '>' = enter).
        if (snapshotKeys is { Length: > 0 } && DateTime.UtcNow >= keysAt)
        {
            char k = snapshotKeys[0];
            // '>' = enter, '<' = esc, capitals = with shift.
            app.HandleKey(k switch
            {
                '>' => new ConsoleKeyInfo((char)13, ConsoleKey.Enter, false, false, false),
                '<' => new ConsoleKeyInfo((char)27, ConsoleKey.Escape, false, false, false),
                _ => new ConsoleKeyInfo(k, (ConsoleKey)char.ToUpperInvariant(k), char.IsUpper(k), false, false),
            });
            snapshotKeys = snapshotKeys[1..];
            keysAt = DateTime.UtcNow.AddSeconds(0.25);
        }
        canvas.Clear();
        app.Draw(canvas);
    }
    File.WriteAllLines(snapshotPath, Enumerable.Range(0, canvas.Height).Select(canvas.RowText));
    Console.Error.WriteLine($"queue: {player.Queue.Count} entries, {player.UpNextCount} up next, mirror mismatches: {player.MirrorMismatches()}");
    return 0;
}

using (var terminal = new Terminal())
{
    // Sleep until a key, a remote command, or the next animation frame, whichever comes first.
    var wakeups = new List<WaitHandle> { terminal.InputHandle };
    if (remote is not null) wakeups.Add(remote.Wake);
    if (media is not null) wakeups.Add(media.Wake);
    if (externalPlayer is not null) wakeups.Add(externalPlayer.Wake);
    var wakeupArray = wakeups.ToArray();
    bool running = true;
    while (running)
    {
        while (remote is not null && remote.Commands.TryDequeue(out var command))
            command();
        while (media is not null && media.Commands.TryDequeue(out var command))
            command();
        foreach (var input in terminal.ReadInput())
        {
            running = input.ShiftTap ? app.ToggleMenu() : app.HandleKey(input.Key);
            if (!running) break;
        }
        if (!running) break;

        var canvas = terminal.NewFrame();
        app.Draw(canvas);
        terminal.Present(canvas);
        WaitHandle.WaitAny(wakeupArray, app.NextFrameDelay);
    }
    app.SaveSession();
    app.CloseListens();
}
return 0;

// A broken stats database must not keep the player from starting.
static Nation.Stats.ListenLog? TryOpenListens(string? path)
{
    // Tests pass their own journal; its profile lives next to it so the real one is never touched.
    string? profile = path is null ? null : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "profile.json");
    try { return new Nation.Stats.ListenLog(path, profile); }
    catch (Exception e) { Console.Error.WriteLine("Estadísticas desactivadas: " + e.Message); return null; }
}
