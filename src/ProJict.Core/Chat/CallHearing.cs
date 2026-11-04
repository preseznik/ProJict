namespace ProJict.Core.Chat;

/// <summary>
/// What understands you in calls (Settings › Chat, #4980bbee): Windows' old offline recognizer, Windows' online dictation,
/// ElevenLabs' speech to text, or Whisper on this computer. Whichever can't (no key, no model, dictation off), the old
/// recognizer hears you instead, and the call says why.
/// </summary>
public enum CallRecognizer { Windows, WindowsOnline, ElevenLabs, Whisper }

/// <summary>
/// Cuts the microphone's sound into phrases, for recognizers that take a recording at a time (ElevenLabs, Whisper): a
/// phrase starts when it gets loud and ends after a pause; a moment of sound from before the start goes with it, so the
/// first syllable isn't lost. Frames are 16-bit mono PCM.
/// </summary>
public sealed class PhraseSplitter(int bytesPerSecond = 32000)
{
    /// <summary>Louder than this (RMS, 0 to 1), over the room's own hum, is someone speaking.</summary>
    public const double Speaking = 0.02;
    /// <summary>This much quiet ends a phrase.</summary>
    public static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(800);
    /// <summary>Sound from just before the start that goes with the phrase.</summary>
    public static readonly TimeSpan Before = TimeSpan.FromMilliseconds(300);
    /// <summary>Shorter than this is a cough or a click, not said.</summary>
    public static readonly TimeSpan Shortest = TimeSpan.FromMilliseconds(400);
    /// <summary>A phrase this long goes as it is, and the next one starts.</summary>
    public static readonly TimeSpan Longest = TimeSpan.FromSeconds(20);

    private readonly List<byte[]> _before = [];
    private readonly List<byte[]> _phrase = [];
    private int _beforeBytes;
    private int _phraseBytes;
    private int _quietBytes;
    private int _loudBytes;
    private double _hum = Speaking / 2;

    /// <summary>In a phrase now.</summary>
    public bool InPhrase { get; private set; }

    /// <summary>Someone started speaking.</summary>
    public event Action? Started;
    /// <summary>A phrase, all of it (with the sound from just before); or nothing, when it was too short to be one.</summary>
    public event Action<byte[]?>? Ended;

    private int Bytes(TimeSpan time) => (int)(time.TotalSeconds * bytesPerSecond) & ~1;

    /// <summary>How loud a frame is: its RMS, 0 to 1.</summary>
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

    /// <summary>The next frame from the microphone.</summary>
    public void Add(byte[] frame)
    {
        var level = Level(frame);
        // The room's hum, followed slowly while nobody speaks: speaking is clearly louder than it.
        var loud = level > Math.Max(Speaking, _hum * 3);
        if (!InPhrase && !loud) _hum = _hum * 0.95 + level * 0.05;
        if (!InPhrase)
        {
            if (!loud)
            {
                // The last moment of quiet, kept to go before a phrase.
                _before.Add(frame);
                _beforeBytes += frame.Length;
                while (_before.Count > 1 && _beforeBytes - _before[0].Length >= Bytes(Before))
                {
                    _beforeBytes -= _before[0].Length;
                    _before.RemoveAt(0);
                }
                return;
            }
            InPhrase = true;
            _phrase.AddRange(_before);
            _phrase.Add(frame);
            _phraseBytes = _beforeBytes + frame.Length;
            _loudBytes = frame.Length;
            _quietBytes = 0;
            _before.Clear();
            _beforeBytes = 0;
            Started?.Invoke();
            return;
        }
        _phrase.Add(frame);
        _phraseBytes += frame.Length;
        if (loud)
        {
            _loudBytes += frame.Length;
            _quietBytes = 0;
        }
        else _quietBytes += frame.Length;
        if (_quietBytes >= Bytes(Pause) || _phraseBytes >= Bytes(Longest)) End();
    }

    /// <summary>The microphone stopped: a phrase going on ends here.</summary>
    public void Flush()
    {
        if (InPhrase) End();
    }

    private void End()
    {
        InPhrase = false;
        var phrase = _loudBytes >= Bytes(Shortest) ? _phrase.SelectMany(f => f).ToArray() : null;
        _phrase.Clear();
        _phraseBytes = _quietBytes = _loudBytes = 0;
        Ended?.Invoke(phrase);
    }
}

/// <summary>Sound as a WAV file: 16-bit PCM with its header, for recognizers that take a file.</summary>
public static class Wav
{
    public static byte[] Of(byte[] pcm, int rate = 16000, short channels = 1)
    {
        var wav = new byte[44 + pcm.Length];
        void Text(int at, string s) { for (var i = 0; i < 4; i++) wav[at + i] = (byte)s[i]; }
        void Int(int at, int v) => BitConverter.GetBytes(v).CopyTo(wav, at);
        void Short(int at, short v) => BitConverter.GetBytes(v).CopyTo(wav, at);
        Text(0, "RIFF");
        Int(4, 36 + pcm.Length);
        Text(8, "WAVE");
        Text(12, "fmt ");
        Int(16, 16);
        Short(20, 1);
        Short(22, channels);
        Int(24, rate);
        Int(28, rate * channels * 2);
        Short(32, (short)(channels * 2));
        Short(34, 16);
        Text(36, "data");
        Int(40, pcm.Length);
        pcm.CopyTo(wav, 44);
        return wav;
    }

    /// <summary>16-bit PCM as samples from -1 to 1 (what Whisper takes).</summary>
    public static float[] Samples(byte[] pcm)
    {
        var samples = new float[pcm.Length / 2];
        for (var i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(pcm, i * 2) / 32768f;
        return samples;
    }
}

/// <summary>A Whisper model to download for calls: its name in Settings, its size, and its file.</summary>
public sealed record WhisperModel(string Id, string Name, int SizeMb, string Note)
{
    /// <summary>The ones to pick from, smallest first; Small unless you pick another.</summary>
    public static IReadOnlyList<WhisperModel> All { get; } =
    [
        new("base", "Base", 142, "fastest, fine for clear speech"),
        new("small", "Small", 466, "good, and quick on most computers"),
        new("large-v3-turbo", "Large v3 Turbo", 1550, "the best, quick on a strong graphics card"),
    ];

    public const string DefaultId = "small";

    public static WhisperModel For(string? id) => All.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)) ?? All.First(m => m.Id == DefaultId);

    /// <summary>Its file's name in the models folder.</summary>
    public string FileName => $"ggml-{Id}.bin";

    public string Describe => $"{Name} ({(SizeMb >= 1000 ? $"{SizeMb / 1000.0:0.0} GB" : $"{SizeMb} MB")}): {Note}";

    /// <summary>What Whisper writes for sound that isn't words ("[BLANK_AUDIO]", "(music)", "..."), not anything said.</summary>
    public static bool IsNoise(string text)
    {
        var t = text.Trim();
        return t.Trim('.', ' ').Length == 0 || (t.StartsWith('[') && t.EndsWith(']')) || (t.StartsWith('(') && t.EndsWith(')'));
    }
}
