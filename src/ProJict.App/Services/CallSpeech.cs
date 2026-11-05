using System.IO;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using System.Speech.Synthesis;

namespace ProJict.App.Services;

/// <summary>What you say in a call, as text: a phrase at a time once you pause, and when it hears you speaking.</summary>
public interface ISpeechIn : IDisposable
{
    /// <summary>A phrase it understood.</summary>
    event Action<string>? Heard;
    /// <summary>It hears you speaking (true), or stopped (false).</summary>
    event Action<bool>? Hearing;
    /// <summary>How loud the microphone is now, 0 to 1, several times a second (any thread).</summary>
    event Action<double>? Level;
    /// <summary>It can't go on (dictation off, no credits, no network): why, in words (any thread).</summary>
    event Action<string>? Failed;
    /// <summary>Done when it understands you (Whisper loads its model first); what you say meanwhile waits for it.</summary>
    Task Ready => Task.CompletedTask;
    /// <summary>What the call says while it isn't ready yet.</summary>
    string? GettingReady => null;
    /// <summary>
    /// The speakers play an answer (true) or stopped (false). A recognizer that takes phrases drops the ones that start
    /// meanwhile (the speakers' own sound); the others are told nothing, and the call doesn't use what they hear then.
    /// </summary>
    void SpeakersPlaying(bool playing) { }
    /// <summary>It drops what starts while the speakers play itself, so what it hears may be posted any time.</summary>
    bool DropsEcho => false;
    /// <summary>You speak over an answer, clearly louder than the speakers' own sound: the answer should stop (any thread).</summary>
    event Action? Interrupted { add { } remove { } }
    /// <summary>Starts listening; the problem when it can't (no microphone, no speech recognition), or null.</summary>
    string? Start();
    void Stop();
}

/// <summary>Reading an agent's answer aloud, in a voice of its own.</summary>
public interface ISpeechOut : IDisposable
{
    /// <summary>The voices there are to give agents (asked for once); throws InvalidOperationException with the problem.</summary>
    Task<IReadOnlyList<string>> VoicesAsync(CancellationToken stop);
    /// <summary>Says it, in the voice named (or the first); done when it's said or stopped.</summary>
    Task SpeakAsync(string voice, string text, CancellationToken stop);
}

/// <summary>
/// Windows speech recognition (offline): dictation from Windows' default microphone, or from the one you picked
/// (<paramref name="sound"/>: its sound goes to the recognizer as it comes, #b39e8f23).
/// </summary>
public sealed class WindowsSpeechIn(ISoundIn? sound = null) : ISpeechIn
{
    /// <summary>Below this confidence a phrase is taken as noise.</summary>
    private const float Sure = 0.35f;
    private SpeechRecognitionEngine? _engine;
    private LiveAudioStream? _stream;

    public event Action<string>? Heard;
    public event Action<bool>? Hearing;
    public event Action<double>? Level;
    /// <summary>Windows' own recognizer says what's wrong when it starts, or not at all.</summary>
    public event Action<string>? Failed { add { } remove { } }

    public string? Start()
    {
        if (_engine != null) return null;
        try
        {
            var engine = new SpeechRecognitionEngine();
            engine.LoadGrammar(new DictationGrammar());
            if (sound == null) engine.SetInputToDefaultAudioDevice();
            else
            {
                var stream = new LiveAudioStream();
                sound.Captured += stream.Add;
                // The recognizer says how loud only for Windows' default microphone: for this one, the sound itself does.
                sound.Captured += pcm => Level?.Invoke(CallVoice.Loudness(pcm));
                engine.SetInputToAudioStream(stream, new SpeechAudioFormatInfo(CallVoice.Rate, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
                _stream = stream;
                if (sound.Start() is { } problem)
                {
                    engine.Dispose();
                    return problem;
                }
            }
            engine.AudioLevelUpdated += (_, e) => Level?.Invoke(e.AudioLevel / 100.0);
            engine.SpeechDetected += (_, _) => Hearing?.Invoke(true);
            engine.SpeechRecognitionRejected += (_, _) => Hearing?.Invoke(false);
            engine.SpeechRecognized += (_, e) =>
            {
                Hearing?.Invoke(false);
                if (e.Result.Confidence >= Sure && e.Result.Text.Trim() is { Length: > 0 } text) Heard?.Invoke(text);
            };
            engine.RecognizeAsync(RecognizeMode.Multiple);
            _engine = engine;
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or PlatformNotSupportedException or System.Runtime.InteropServices.COMException)
        {
            return "Windows speech recognition isn't available here (a microphone, and speech recognition in Windows' language settings): " + ex.Message;
        }
    }

    public void Stop()
    {
        sound?.Dispose();
        _stream?.Close();
        _stream = null;
        if (_engine is not { } engine) return;
        _engine = null;
        try { engine.RecognizeAsyncCancel(); }
        catch (InvalidOperationException) { }
        engine.Dispose();
    }

    public void Dispose() => Stop();
}

/// <summary>
/// Sound as it comes from a microphone, read by the speech recognizer: a read waits for more until the stream is
/// closed, then it ends.
/// </summary>
public sealed class LiveAudioStream : Stream
{
    private readonly Queue<byte[]> _frames = new();
    private byte[]? _current;
    private int _offset;
    private bool _closed;

    /// <summary>A frame of sound (any thread).</summary>
    public void Add(byte[] pcm)
    {
        lock (_frames)
        {
            if (_closed) return;
            _frames.Enqueue(pcm);
            Monitor.PulseAll(_frames);
        }
    }

    /// <summary>
    /// Fills all it's asked for, waiting for the microphone as long as it takes: Windows' recognizer takes a read that
    /// comes back short for the end of the sound. Only once the stream is closed does it come back short (or empty).
    /// </summary>
    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = 0;
        lock (_frames)
        {
            while (read < count)
            {
                if (_current == null || _offset >= _current.Length)
                {
                    while (_frames.Count == 0 && !_closed) Monitor.Wait(_frames);
                    if (_frames.Count == 0) break; // closed
                    _current = _frames.Dequeue();
                    _offset = 0;
                }
                var n = Math.Min(count - read, _current.Length - _offset);
                Buffer.BlockCopy(_current, _offset, buffer, offset + read, n);
                _offset += n;
                read += n;
            }
        }
        return read;
    }

    protected override void Dispose(bool disposing)
    {
        lock (_frames)
        {
            _closed = true;
            Monitor.PulseAll(_frames);
        }
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    // The recognizer asks how long it is and where it's at when it starts: a live stream has no end, and is always at its start.
    public override long Length => -1;
    public override long Position { get => 0; set { } }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => 0;
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Windows' voices (offline), each agent with one of its own.</summary>
public sealed class WindowsSpeechOut : ISpeechOut
{
    private readonly SpeechSynthesizer _synth = new();

    public WindowsSpeechOut() => _synth.SetOutputToDefaultAudioDevice();

    public IReadOnlyList<string> Voices => [.. _synth.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo.Name)];

    public Task<IReadOnlyList<string>> VoicesAsync(CancellationToken stop) => Task.FromResult(Voices);

    public Task SpeakAsync(string voice, string text, CancellationToken stop)
    {
        var done = new TaskCompletionSource();
        try { _synth.SelectVoice(voice); }
        catch (ArgumentException) { }
        Prompt? prompt = null;
        void Completed(object? sender, SpeakCompletedEventArgs e)
        {
            if (e.Prompt != prompt) return;
            _synth.SpeakCompleted -= Completed;
            done.TrySetResult();
        }
        _synth.SpeakCompleted += Completed;
        prompt = _synth.SpeakAsync(text);
        stop.Register(() =>
        {
            try { _synth.SpeakAsyncCancel(prompt); }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
            done.TrySetResult();
        });
        return done.Task;
    }

    public void Dispose()
    {
        _synth.SpeakAsyncCancelAll();
        _synth.Dispose();
    }
}
