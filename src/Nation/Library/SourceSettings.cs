using System.Text.Json;

namespace Nation.Library;

/// <summary>
/// Where the player takes its music from (the sources panel, `o`): whether it follows
/// other apps on its own, an app to always follow instead of picking automatically, and
/// whether Nation's own library is the local one or YouTube Music.
/// Saved to %LOCALAPPDATA%\Nation\sources.json.
/// </summary>
public sealed record SourceSettings(bool AutoFollow = true, string? PinnedAppId = null, string? PinnedName = null, bool YouTubeMode = false)
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nation", "sources.json");

    public static SourceSettings Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<SourceSettings>(File.ReadAllText(FilePath)) ?? new() : new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The panel still works for this run.
        }
    }
}
