using NAudio.Wave;

namespace ProJict.App.Services;

/// <summary>Your voice for a call with a person: the microphone's sound, in short frames.</summary>
public interface ISoundIn : IDisposable
{
    /// <summary>A frame of sound (16 kHz, 16-bit, mono PCM), on any thread.</summary>
    event Action<byte[]>? Captured;
    /// <summary>Starts the microphone; the problem when there's none, or null.</summary>
    string? Start();
}

/// <summary>The other person's voice: plays frames as they come.</summary>
public interface ISoundOut : IDisposable
{
    /// <summary>Starts the speakers; the problem when there are none, or null.</summary>
    string? Start();
    void Play(byte[] pcm);
}

/// <summary>Voice as it goes between ProJicts: 16 kHz, 16-bit mono, in frames of a tenth of a second.</summary>
public static class CallVoice
{
    public const int Rate = 16000;
    public const int FrameMs = 100;
    public static WaveFormat Format => new(Rate, 16, 1);

    /// <summary>How loud a frame is, 0 to 1 (for whose tile lights up).</summary>
    public static double Level(byte[] pcm)
    {
        if (pcm.Length < 2) return 0;
        double sum = 0;
        var samples = pcm.Length / 2;
        for (var i = 0; i < samples; i++)
        {
            var sample = BitConverter.ToInt16(pcm, i * 2) / 32768.0;
            sum += sample * sample;
        }
        return Math.Sqrt(sum / samples);
    }

    /// <summary>A frame this loud counts as speaking.</summary>
    public const double Speaking = 0.02;

    /// <summary>How loud a frame is for the bar on your tile, 0 to 1 (a voice's level is small: scaled so speaking fills most of it).</summary>
    public static double Loudness(byte[] pcm) => Math.Min(1, Level(pcm) * 8);
}

/// <summary>The microphones Windows has, by name, and the one calls use (Settings › Chat, #b39e8f23).</summary>
public static class Microphones
{
    /// <summary>Windows' default, as the choice in Settings says it.</summary>
    public const string DefaultName = "Windows' default microphone";

    /// <summary>Their names, as Windows lists them (off-screen tests put in their own).</summary>
    public static Func<IReadOnlyList<string>> List { get; set; } = () =>
    {
        var names = new List<string>();
        try
        {
            for (var i = 0; i < WaveIn.DeviceCount; i++) names.Add(WaveIn.GetCapabilities(i).ProductName);
        }
        catch (NAudio.MmException) { }
        return names;
    };

    /// <summary>The device the named microphone is; -1 (Windows' default) for none, or one that isn't there now.</summary>
    public static int DeviceOf(string? name) =>
        name == null ? -1 : List().ToList().FindIndex(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>What a call listens to, for the hint when it hears nothing.</summary>
    public static string Describe(string? name) => DeviceOf(name) >= 0 ? name! : DefaultName;
}

/// <summary>A microphone (Windows' wave input): Windows' default (-1), or the device you picked.</summary>
public sealed class MicrophoneIn(int device = -1) : ISoundIn
{
    private WaveIn? _wave;
    public event Action<byte[]>? Captured;

    public string? Start()
    {
        if (_wave != null) return null;
        try
        {
            var wave = new WaveIn { DeviceNumber = device, WaveFormat = CallVoice.Format, BufferMilliseconds = CallVoice.FrameMs };
            wave.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded > 0) Captured?.Invoke(e.Buffer.AsSpan(0, e.BytesRecorded).ToArray());
            };
            wave.StartRecording();
            _wave = wave;
            return null;
        }
        catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException)
        {
            return "No microphone to use: " + ex.Message;
        }
    }

    public void Dispose()
    {
        if (_wave is not { } wave) return;
        _wave = null;
        try { wave.StopRecording(); }
        catch (NAudio.MmException) { }
        wave.Dispose();
    }
}

/// <summary>The default speakers (Windows' wave output), with a little buffer so frames play smoothly.</summary>
public sealed class SpeakersOut : ISoundOut
{
    private BufferedWaveProvider? _buffer;
    private WaveOut? _wave;

    public string? Start()
    {
        if (_wave != null) return null;
        try
        {
            var buffer = new BufferedWaveProvider(CallVoice.Format, TimeSpan.FromSeconds(2)) { DiscardOnBufferOverflow = true };
            var wave = new WaveOut { BufferMilliseconds = 100 };
            wave.Init(buffer);
            wave.Play();
            _buffer = buffer;
            _wave = wave;
            return null;
        }
        catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException)
        {
            return "No speakers to use: " + ex.Message;
        }
    }

    public void Play(byte[] pcm) => _buffer?.AddSamples(pcm, 0, pcm.Length);

    public void Dispose()
    {
        if (_wave is not { } wave) return;
        _wave = null;
        _buffer = null;
        wave.Stop();
        wave.Dispose();
    }
}
