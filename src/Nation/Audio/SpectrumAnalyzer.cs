using NAudio.Dsp;
using NAudio.Wave;

namespace Nation.Audio;

/// <summary>
/// Ported from Shade.Agent: WASAPI loopback capture of the mixed system output,
/// turned into log-scaled magnitude bands. libmpv exposes no PCM tap, so the
/// loopback is the simplest way to see what is actually coming out of the speakers.
/// </summary>
public sealed class SpectrumAnalyzer : IDisposable
{
    public const int BandCount = 32;
    private const int FftLength = 2048;
    private const int FftExponent = 11; // 2^11 = FftLength
    // 50% overlap: a new frame every 1024 samples (~47 fps at 48 kHz).
    private const int HopLength = FftLength / 2;

    private readonly float[] _sampleBuffer = new float[FftLength];
    private readonly Complex[] _fftBuffer = new Complex[FftLength];
    private readonly float[] _bandGain = BuildBandGain();
    private int _sampleCount;
    private WasapiLoopbackCapture? _capture;

    /// <summary>Most recent bands in [0,1]; replaced atomically by the capture thread.</summary>
    public float[] Latest { get; private set; } = new float[BandCount];

    /// <summary>When the last frame arrived; loopback delivers nothing during silence.</summary>
    public DateTime LastFrameUtc { get; private set; }

    public bool Available => _capture is not null;

    // Real music has roughly a -6dB/octave tilt, so higher bands get up to +18dB
    // to reach the same visual range as bass without inventing movement from silence.
    private static float[] BuildBandGain()
    {
        const float maxBoostDb = 18f;
        var gains = new float[BandCount];
        for (var band = 0; band < BandCount; band++)
        {
            var db = band / (float)(BandCount - 1) * maxBoostDb;
            gains[band] = MathF.Pow(10f, db / 20f);
        }
        return gains;
    }

    public void Start()
    {
        if (_capture is not null) return;
        try
        {
            var capture = new WasapiLoopbackCapture();
            capture.DataAvailable += OnDataAvailable;
            capture.StartRecording();
            _capture = capture;
        }
        catch (Exception)
        {
            // No output device (or audio service down): the visualizer just stays flat.
            _capture = null;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var format = _capture!.WaveFormat;
        var bytesPerFrame = format.BitsPerSample / 8 * format.Channels;
        var channels = format.Channels;

        for (var offset = 0; offset + bytesPerFrame <= e.BytesRecorded; offset += bytesPerFrame)
        {
            // Mono downmix of the first two channels.
            var sample = BitConverter.ToSingle(e.Buffer, offset);
            if (channels > 1) sample = (sample + BitConverter.ToSingle(e.Buffer, offset + 4)) * 0.5f;
            _sampleBuffer[_sampleCount++] = sample;

            if (_sampleCount < FftLength) continue;

            ProcessFft();
            Array.Copy(_sampleBuffer, HopLength, _sampleBuffer, 0, FftLength - HopLength);
            _sampleCount = FftLength - HopLength;
        }
    }

    private void ProcessFft()
    {
        for (var i = 0; i < FftLength; i++)
        {
            // Hann window to keep the FFT from smearing energy across bins.
            var window = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (FftLength - 1)));
            _fftBuffer[i].X = (float)(_sampleBuffer[i] * window);
            _fftBuffer[i].Y = 0;
        }

        FastFourierTransform.FFT(true, FftExponent, _fftBuffer);

        var bands = new float[BandCount];
        var usableBins = FftLength / 2;
        for (var band = 0; band < BandCount; band++)
        {
            // Squared spacing: narrow bass bands, wide treble bands.
            var lowBin = Math.Max(1, (int)(Math.Pow((double)band / BandCount, 2) * usableBins));
            var highBin = Math.Max(lowBin + 1, (int)(Math.Pow((double)(band + 1) / BandCount, 2) * usableBins));

            float sum = 0;
            var count = 0;
            for (var bin = lowBin; bin < highBin && bin < usableBins; bin++)
            {
                sum += MathF.Sqrt(_fftBuffer[bin].X * _fftBuffer[bin].X + _fftBuffer[bin].Y * _fftBuffer[bin].Y);
                count++;
            }

            var magnitude = count > 0 ? sum / count : 0f;
            bands[band] = Math.Clamp(MathF.Log10(1 + magnitude * _bandGain[band] * 200) / 1.4f, 0f, 1f);
        }

        Latest = bands;
        LastFrameUtc = DateTime.UtcNow;
    }

    /// <summary>Stops capturing (idempotent); the UI stops it while Nation isn't playing.</summary>
    public void Stop()
    {
        if (_capture is null) return;
        _capture.DataAvailable -= OnDataAvailable;
        _capture.StopRecording();
        _capture.Dispose();
        _capture = null;
        _sampleCount = 0;
        Latest = new float[BandCount];
    }

    public void Dispose() => Stop();
}
