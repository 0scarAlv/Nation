namespace Nation.Library;

public sealed record Track(
    string Path,
    string Title,
    string Artist,
    string Album,
    uint Year,
    TimeSpan Duration,
    string Codec,
    int SampleRate,
    int BitsPerSample,
    int Bitrate)
{
    /// <summary>Stable per-session id (scan order); exposed as the remote API track id.</summary>
    public int Id { get; init; }

    /// <summary>Short quality tag like "FLAC 24/96" or "OPUS 160k".</summary>
    public string QualityTag =>
        BitsPerSample > 0 && SampleRate > 0
            ? $"{Codec} {BitsPerSample}/{SampleRate / 1000.0:0.#}"
            : Bitrate > 0 ? $"{Codec} {Bitrate}k" : Codec;
}
