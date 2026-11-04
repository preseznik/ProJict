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
    /// <summary>Starts listening; the problem when it can't (no microphone, no speech recognition), or null.</summary>
    string? Start();
    void Stop();
}

/// <summary>Reading an agent's answer aloud, in a voice of its own.</summary>
public interface ISpeechOut : IDisposable
{
    /// <summary>The voices there are to give agents.</summary>
    IReadOnlyList<string> Voices { get; }
    /// <summary>Says it, in the voice named (or the first); done when it's said or stopped.</summary>
    Task SpeakAsync(string voice, string text, CancellationToken stop);
}

/// <summary>Windows speech recognition (offline): dictation from the default microphone.</summary>
public sealed class WindowsSpeechIn : ISpeechIn
{
    /// <summary>Below this confidence a phrase is taken as noise.</summary>
    private const float Sure = 0.35f;
    private SpeechRecognitionEngine? _engine;

    public event Action<string>? Heard;
    public event Action<bool>? Hearing;

    public string? Start()
    {
        if (_engine != null) return null;
        try
        {
            var engine = new SpeechRecognitionEngine();
            engine.LoadGrammar(new DictationGrammar());
            engine.SetInputToDefaultAudioDevice();
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
        if (_engine is not { } engine) return;
        _engine = null;
        try { engine.RecognizeAsyncCancel(); }
        catch (InvalidOperationException) { }
        engine.Dispose();
    }

    public void Dispose() => Stop();
}

/// <summary>Windows' voices (offline), each agent with one of its own.</summary>
public sealed class WindowsSpeechOut : ISpeechOut
{
    private readonly SpeechSynthesizer _synth = new();

    public WindowsSpeechOut() => _synth.SetOutputToDefaultAudioDevice();

    public IReadOnlyList<string> Voices => [.. _synth.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo.Name)];

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
