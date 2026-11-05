using Windows.Media.SpeechRecognition;

namespace ProJict.App.Services;

/// <summary>
/// Windows' online dictation (#4980bbee): the speech recognition Windows 10 and 11 apps use, much better than the old
/// offline one. Your voice goes to Microsoft for it, so Windows asks for "Online speech recognition" (Settings › Privacy
/// &amp; security › Speech). It listens to Windows' default microphone; a microphone you picked only moves the level bar
/// (<paramref name="levels"/>). It starts in the background: when it can't, <see cref="ISpeechIn.Failed"/> says why.
/// </summary>
public sealed class WindowsOnlineSpeechIn(ISoundIn? levels = null, string? language = null) : ISpeechIn
{
    /// <summary>Windows' answer when online speech recognition is off.</summary>
    private const int PrivacyNotAccepted = unchecked((int)0x80045509);

    private SpeechRecognizer? _recognizer;
    private bool _stopping;

    public event Action<string>? Heard;
    public event Action<bool>? Hearing;
    public event Action<double>? Level;
    public event Action<string>? Failed;

    public string? Start()
    {
        if (_recognizer != null) return null;
        if (levels != null)
        {
            levels.Captured += frame => Level?.Invoke(CallVoice.Loudness(frame));
            levels.Start();
        }
        _ = StartAsync();
        return null;
    }

    private async Task StartAsync()
    {
        try
        {
            // The call's language, when Windows' dictation speaks it; else it says so, and the old recognizer hears you.
            var recognizer = new SpeechRecognizer();
            if (ProJict.Core.Chat.CallLanguage.For(language) is { } wanted)
            {
                var tag = SpeechRecognizer.SupportedTopicLanguages.FirstOrDefault(l =>
                    l.LanguageTag.StartsWith(wanted.Code + "-", StringComparison.OrdinalIgnoreCase) || l.LanguageTag.Equals(wanted.Code, StringComparison.OrdinalIgnoreCase));
                if (tag == null)
                {
                    recognizer.Dispose();
                    Failed?.Invoke($"Windows' online dictation doesn't speak {wanted.Name}.");
                    return;
                }
                recognizer.Dispose();
                recognizer = new SpeechRecognizer(tag);
            }
            _recognizer = recognizer;
            recognizer.Constraints.Add(new SpeechRecognitionTopicConstraint(SpeechRecognitionScenario.Dictation, "dictation"));
            var compiled = await recognizer.CompileConstraintsAsync();
            if (compiled.Status != SpeechRecognitionResultStatus.Success)
            {
                Fail(compiled.Status);
                return;
            }
            recognizer.HypothesisGenerated += (_, _) => Hearing?.Invoke(true);
            var session = recognizer.ContinuousRecognitionSession;
            session.ResultGenerated += (_, e) =>
            {
                Hearing?.Invoke(false);
                if (e.Result.Status == SpeechRecognitionResultStatus.Success && e.Result.Confidence != SpeechRecognitionConfidence.Rejected &&
                    e.Result.Text.Trim() is { Length: > 0 } text)
                    Heard?.Invoke(text);
            };
            // A while without speech ends Windows' session: it starts again until the call ends.
            session.Completed += async (_, e) =>
            {
                if (_stopping) return;
                if (e.Status is SpeechRecognitionResultStatus.TimeoutExceeded or SpeechRecognitionResultStatus.Success or SpeechRecognitionResultStatus.UserCanceled)
                {
                    try { await session.StartAsync(); }
                    catch (Exception ex) when (!_stopping) { Failed?.Invoke("Windows' online dictation stopped: " + ex.Message); }
                }
                else Fail(e.Status);
            };
            await session.StartAsync();
        }
        catch (Exception ex) when (ex.HResult == PrivacyNotAccepted)
        {
            Failed?.Invoke(PrivacyOff);
        }
        catch (Exception ex) when (!_stopping)
        {
            Failed?.Invoke("Windows' online dictation couldn't start: " + ex.Message);
        }
    }

    private const string PrivacyOff =
        "Windows' online dictation is off: turn on Online speech recognition in Windows Settings › Privacy & security › Speech.";

    private void Fail(SpeechRecognitionResultStatus status)
    {
        if (_stopping) return;
        Failed?.Invoke(status switch
        {
            SpeechRecognitionResultStatus.NetworkFailure => "Windows' online dictation can't reach Microsoft (no network).",
            SpeechRecognitionResultStatus.TopicLanguageNotSupported or SpeechRecognitionResultStatus.GrammarLanguageMismatch =>
                "Windows' online dictation doesn't speak your Windows speech language (Windows Settings › Time & language › Speech).",
            SpeechRecognitionResultStatus.MicrophoneUnavailable => "Windows' online dictation has no microphone to use.",
            SpeechRecognitionResultStatus.AudioQualityFailure => "Windows' online dictation can't hear the microphone well enough.",
            _ => $"Windows' online dictation stopped ({status}).",
        });
    }

    public void Stop()
    {
        _stopping = true;
        levels?.Dispose();
        if (_recognizer is not { } recognizer) return;
        _recognizer = null;
        _ = Task.Run(async () =>
        {
            try { await recognizer.ContinuousRecognitionSession.CancelAsync(); }
            catch (Exception) { }
            recognizer.Dispose();
        });
    }

    public void Dispose() => Stop();
}
