using Nation.Library;

namespace Nation.Audio;

/// <summary>
/// What the UI shows and controls as "now playing": Nation's own mpv player, or
/// another app (Spotify, YouTube Music…) followed through Windows' media session.
/// </summary>
public interface IPlayback
{
    /// <summary>Re-reads state; call once at the start of each frame.</summary>
    void Refresh();

    Track? Current { get; }
    bool Paused { get; }
    bool Idle { get; }
    bool Playing { get; }
    bool Muted { get; }
    bool Shuffled { get; }
    bool Repeat { get; }
    double Position { get; }
    double Duration { get; }
    /// <summary>0–100, or NaN when the volume can't be read.</summary>
    double Volume { get; }

    void TogglePause();
    void SetPaused(bool paused);
    void Next();
    void Previous();
    void SeekRelative(double seconds);
    void SeekAbsolute(double seconds);
    void ToggleShuffle();
    void ToggleRepeat();
    void ToggleMute();
    void AddVolume(int delta);
}
