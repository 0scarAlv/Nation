using System.Text.Json;

namespace Nation.Library;

/// <summary>
/// Last track, position and volume, so the next start picks up where playback left
/// off. Saved to %LOCALAPPDATA%\Nation\session.json every few seconds and on exit
/// (closing the terminal window skips the exit path, hence the periodic saves).
/// </summary>
public sealed record Session(string Path, double Position, double Volume)
{
    private static readonly string FilePath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nation", "session.json");

    public static Session? Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<Session>(File.ReadAllText(FilePath)) : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Losing the resume point isn't worth an error.
        }
    }
}
