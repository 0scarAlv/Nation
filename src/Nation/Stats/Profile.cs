using System.Text.Json;

namespace Nation.Stats;

/// <summary>
/// Who is listening and on which device. ListenerId is one person across all their
/// devices (this PC is its home; phones get paired to it); DeviceId is this PC only.
/// Stored in %LOCALAPPDATA%\Nation\profile.json; Name can be edited there by hand.
/// </summary>
public sealed record Profile(string ListenerId, string Name, string DeviceId, string DeviceName, string MachineName)
{
    public static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nation", "profile.json");

    /// <summary>
    /// Reads the profile, or makes one. A missing or unreadable file keeps the journal's
    /// owner (knownListener) and this PC's id (knownDevice) rather than inventing new ones,
    /// which would orphan the history. A profile copied from another PC keeps the person
    /// but takes this PC's own device id (or a new one).
    /// </summary>
    public static Profile LoadOrCreate(string? knownListener, string? knownDevice, string? path = null)
    {
        path ??= DefaultPath;
        string machine = Environment.MachineName;
        Profile? profile = null;
        try
        {
            if (File.Exists(path)) profile = JsonSerializer.Deserialize<Profile>(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // Keep the broken file around instead of silently overwriting it.
            try { File.Copy(path, path + ".broken", overwrite: true); } catch (IOException) { }
        }

        var fixedUp = profile is { ListenerId.Length: > 0, DeviceId.Length: > 0 }
            ? string.Equals(profile.MachineName, machine, StringComparison.OrdinalIgnoreCase)
                ? profile
                : profile with { DeviceId = knownDevice ?? NewId(), DeviceName = machine, MachineName = machine }
            : new Profile(knownListener ?? NewId(), Environment.UserName, knownDevice ?? NewId(), machine, machine);

        if (fixedUp != profile)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(fixedUp, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Still usable for this run; the journal's meta table remembers the owner.
            }
        }
        return fixedUp;
    }

    /// <summary>Writes the profile (best effort: the journal's meta table remembers the owner anyway).</summary>
    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static string NewId() => Guid.NewGuid().ToString("N");
}
