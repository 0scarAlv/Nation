using Nation.Audio;
using Nation.Library;
using Nation.Lyrics;
using Nation.Remote;
using Nation.Tui;

// Usage: nation [files|folders...] [--shuffle] [--volume <0-100>] [--offline] [--no-api] [--api-port <n>] [--no-media-keys] [--snapshot <file>]
// With no paths, plays the user's Music folder.
var paths = new List<string>();
string? snapshotPath = null;
bool shuffle = false, offline = false, api = true, smtc = true;
int apiPort = TauonCompatServer.DefaultPort;
int? volume = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--snapshot" && i + 1 < args.Length) snapshotPath = args[++i];
    else if (args[i] == "--shuffle") shuffle = true;
    else if (args[i] == "--offline") offline = true;
    else if (args[i] == "--no-api") api = false;
    else if (args[i] == "--no-media-keys") smtc = false;
    else if (args[i] == "--volume" && i + 1 < args.Length && int.TryParse(args[i + 1], out var vol)) { volume = Math.Clamp(vol, 0, 100); i++; }
    else if (args[i] == "--api-port" && i + 1 < args.Length && int.TryParse(args[i + 1], out var port)) { apiPort = port; i++; }
    else paths.Add(args[i]);
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
void RunCommand(string command, long arg)
{
    switch (command)
    {
        case "play": player.SetPaused(false); break;
        case "pause" or "stop": player.SetPaused(true); break;
        case "next": player.Next(); break;
        case "back": player.Previous(); break;
        case "seek": player.SeekAbsolute(arg / 1000.0); break;
        case "shuffle": player.ToggleShuffle(); break;
        case "repeat": player.ToggleRepeat(); break;
    }
}
using var remote = api && snapshotPath is null ? new TauonCompatServer(apiPort) : null;
if (remote is not null)
{
    remote.OnCommand = RunCommand;
    remote.Start();
}
// Windows media session: keyboard media keys + volume flyout (snapshots stay out of it).
using var media = smtc && snapshotPath is null ? MediaSession.TryCreate() : null;
if (media is not null) media.OnCommand = RunCommand;

var app = new App(player, spectrum, new LyricsProvider(online: !offline)) { Remote = remote, Media = media };

var frame = TimeSpan.FromMilliseconds(20);

if (snapshotPath is not null)
{
    // Debug aid: run the animation loop off-screen for a few seconds, then dump one frame.
    // Muted so a snapshot never talks over whatever is already playing.
    player.ToggleMute();
    var canvas = new Canvas(110, 28);
    for (var until = DateTime.UtcNow.AddSeconds(3); DateTime.UtcNow < until; Thread.Sleep(frame))
    {
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
    var wakeupArray = wakeups.ToArray();
    bool running = true;
    while (running)
    {
        while (remote is not null && remote.Commands.TryDequeue(out var command))
            command();
        while (media is not null && media.Commands.TryDequeue(out var command))
            command();
        while (running && Console.KeyAvailable)
            running = app.HandleKey(Console.ReadKey(intercept: true));
        if (!running) break;

        var canvas = terminal.NewFrame();
        app.Draw(canvas);
        terminal.Present(canvas);
        WaitHandle.WaitAny(wakeupArray, app.NextFrameDelay);
    }
}
return 0;
