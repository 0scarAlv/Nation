using System.Text.Json;

namespace Nation.Audio;

/// <summary>
/// 10-band graphic equalizer state: gains, presets and persistence in
/// %LOCALAPPDATA%\Nation\eq.json. Player turns it into mpv audio filters.
/// </summary>
public sealed class Equalizer
{
    public static readonly int[] Frequencies = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
    public static readonly string[] Labels = { "31", "62", "125", "250", "500", "1k", "2k", "4k", "8k", "16k" };
    public const double MaxGain = 12;

    public static readonly (string Name, double[] Gains)[] Presets =
    {
        ("Plano",       new double[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }),
        ("Bajos",       new double[] { 6, 5, 4, 2, 0, 0, 0, 0, 0, 0 }),
        ("Vocal",       new double[] { -2, -2, -1, 1, 3, 4, 3, 1, 0, -1 }),
        ("Rock",        new double[] { 4, 3, 1, -1, -2, -1, 1, 3, 4, 4 }),
        ("Electrónica", new double[] { 5, 4, 1, 0, -2, 0, 1, 2, 4, 5 }),
        ("Acústico",    new double[] { 3, 2, 1, 1, 2, 2, 3, 3, 2, 1 }),
        ("Agudos",      new double[] { 0, 0, 0, 0, 0, 1, 2, 4, 5, 6 }),
    };
    public const string CustomName = "Personalizado";

    public double[] Gains { get; private set; } = new double[Frequencies.Length];
    public bool Enabled { get; private set; }
    public string PresetName { get; private set; } = "Plano";

    // The user's own curve survives cycling through presets.
    private double[] _custom = new double[Frequencies.Length];

    /// <summary>Headroom so boosted bands don't clip: the largest boost, applied as negative gain up front.</summary>
    public double Preamp => Enabled ? -Math.Max(0, Gains.Max()) : 0;

    public bool IsFlat => !Enabled || Gains.All(g => g == 0);

    public void SetGain(int band, double gain)
    {
        Gains[band] = Math.Clamp(Math.Round(gain), -MaxGain, MaxGain);
        Gains.CopyTo(_custom, 0);
        PresetName = CustomName;
        Enabled = true;
        Save();
    }

    public void ToggleEnabled()
    {
        Enabled = !Enabled;
        Save();
    }

    /// <summary>Steps through the presets, with the custom curve as the last stop.</summary>
    public void CyclePreset(int direction)
    {
        int count = Presets.Length + 1;
        int at = PresetName == CustomName ? Presets.Length : Array.FindIndex(Presets, p => p.Name == PresetName);
        at = ((at < 0 ? 0 : at) + direction + count) % count;
        if (at == Presets.Length)
        {
            PresetName = CustomName;
            Gains = (double[])_custom.Clone();
        }
        else
        {
            PresetName = Presets[at].Name;
            Gains = (double[])Presets[at].Gains.Clone();
        }
        Enabled = true;
        Save();
    }

    // ── persistence ────────────────────────────────────────────────────

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nation", "eq.json");

    private sealed record EqFile(bool Enabled, string Preset, double[] Gains, double[] Custom);

    public static Equalizer Load()
    {
        var eq = new Equalizer();
        try
        {
            if (!File.Exists(FilePath)) return eq;
            var file = JsonSerializer.Deserialize<EqFile>(File.ReadAllText(FilePath));
            if (file is null || file.Gains.Length != Frequencies.Length || file.Custom.Length != Frequencies.Length) return eq;
            eq.Enabled = file.Enabled;
            eq.PresetName = file.Preset;
            eq.Gains = file.Gains.Select(g => Math.Clamp(g, -MaxGain, MaxGain)).ToArray();
            eq._custom = file.Custom.Select(g => Math.Clamp(g, -MaxGain, MaxGain)).ToArray();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // A broken file just means starting flat.
        }
        return eq;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new EqFile(Enabled, PresetName, Gains, _custom)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not worth interrupting playback over; the curve still applies this session.
        }
    }
}
