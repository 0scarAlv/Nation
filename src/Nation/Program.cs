using Nation.Audio;
using Nation.External;
using Nation.Library;
using Nation.Lyrics;
using Nation.Remote;
using Nation.Tui;

// Usage: nation [files|folders...] [--shuffle] [--volume <0-100>] [--offline] [--no-api] [--api-port <n>] [--no-media-keys] [--skin] [--no-external] [--sessions] [--snapshot <file> [--external] [--snapshot-keys <keys>]]
// With no paths, plays the user's Music folder.
var paths = new List<string>();
string? snapshotPath = null, snapshotKeys = null;
bool shuffle = false, offline = false, api = true, smtc = true, external = true, externalSnapshot = false, skin = false;
int apiPort = TauonCompatServer.DefaultPort;
int? volume = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--snapshot" && i + 1 < args.Length) snapshotPath = args[++i];
    else if (args[i] == "--snapshot-keys" && i + 1 < args.Length) snapshotKeys = args[++i];
    else if (args[i] == "--shuffle") shuffle = true;
    else if (args[i] == "--offline") offline = true;
    else if (args[i] == "--no-api") api = false;
    else if (args[i] == "--no-media-keys") smtc = false;
    else if (args[i] == "--no-external") external = false;
    else if (args[i] == "--external") externalSnapshot = true;
    else if (args[i] == "--sessions") { }
    else if (args[i] == "--skin") skin = true;
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
if (tracks.Count == 0)
{
    Console.Error.WriteLine("No se encontraron archivos de audio en: " + string.Join(", ", paths));
    return 1;
}

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

var app = new App(player, spectrum, new LyricsProvider(online: !offline)) { Remote = remote, Media = media, External = externalPlayer, StartAsSkin = skin, Sources = sources, Ui = UiSettings.Load(), PersistSession = snapshotPath is null };
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
    for (var until = DateTime.UtcNow.AddSeconds(3); DateTime.UtcNow < until; Thread.Sleep(frame))
    {
        // --snapshot-keys: press one key every 0.25 s once sessions had time to load (letters, '>' = enter).
        if (snapshotKeys is { Length: > 0 } && DateTime.UtcNow >= keysAt)
        {
            char k = snapshotKeys[0];
            app.HandleKey(k == '>' ? new ConsoleKeyInfo((char)13, ConsoleKey.Enter, false, false, false)
                : new ConsoleKeyInfo(k, (ConsoleKey)char.ToUpperInvariant(k), false, false, false));
            snapshotKeys = snapshotKeys[1..];
            keysAt = DateTime.UtcNow.AddSeconds(0.25);
        }
        canvas.Clear();
        app.Draw(canvas);
    }
    File.WriteAllLines(snapshotPath, Enumerable.Range(0, canvas.Height).Select(canvas.RowText));
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
}
return 0;
