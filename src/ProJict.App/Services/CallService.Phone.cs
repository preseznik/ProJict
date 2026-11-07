using ProJict.Core.Api;
using ProJict.Core.Chat;

namespace ProJict.App.Services;

/// <summary>
/// A call from your phone (#d6b701ce): the same call with an agent, in your private conversation with it, except that
/// it hears the phone's microphone instead of this computer's and the agent's voice plays on the phone instead of the
/// speakers here. Hearing and voices follow Settings › Chat; Windows' online dictation can only hear this computer, so
/// the offline recognizer hears the phone then. The agent hears you as text, as in any call.
/// </summary>
public sealed partial class CallService
{
    /// <summary>The phone's audio while the call is from your phone; null for a call from this computer.</summary>
    private PhoneCallMedia? _phone;

    /// <summary>The call going on is from your phone.</summary>
    public bool FromPhone => _phone != null;

    /// <summary>Calls the agent from your phone; the problem when it can't, or null.</summary>
    public string? StartFromPhone(ChatChannel direct, string agent, PhoneCallMedia phone, string? language)
    {
        if (IsOn) return "ProJict is in a call already: hang up there first.";
        _phone = phone;
        var problem = Start(direct, [agent], language);
        if (problem != null || Current is not { } call)
        {
            _phone = null;
            return problem ?? "The call couldn't start.";
        }
        phone.CallId = call.Id;
        return null;
    }

    /// <summary>Who hears the phone: "elevenlabs", "whisper" or "windows" (Settings › Chat, as far as a phone can use it).</summary>
    public string PhoneHearing => state.AppSettings.CallRecognizer switch
    {
        CallRecognizer.ElevenLabs when ElevenLabsKey() != null => "elevenlabs",
        CallRecognizer.Whisper when WhisperModels.Has(WhisperModel.For(state.AppSettings.WhisperModel)) => "whisper",
        _ => "windows",
    };

    /// <summary>Whose voices the phone hears: "elevenlabs" or "windows".</summary>
    public string PhoneVoices => state.AppSettings.CallVoices == CallVoices.ElevenLabs && ElevenLabsKey() != null ? "elevenlabs" : "windows";

    private ISpeechIn NewPhoneSpeechIn(PhoneCallMedia phone)
    {
        switch (PhoneHearing)
        {
            case "elevenlabs" when ElevenLabsKey() is { } key:
                var scribe = new ElevenLabsSpeechToText(key, api: ElevenLabsApi);
                scribe.UseLanguage(Language);
                return new PhraseSpeechIn(new PhoneSoundIn(phone), scribe, "ElevenLabs");
            case "whisper":
                return new PhraseSpeechIn(new PhoneSoundIn(phone), new WhisperSpeechToText(WhisperModels.PathOf(WhisperModel.For(state.AppSettings.WhisperModel)),
                    [.. Current?.Agents.Select(a => a.Name) ?? [], .. main.AgentTools.ConnectedAgentNames(), "ProJict"], Language), "Whisper");
        }
        return new WindowsSpeechIn(new PhoneSoundIn(phone), Language);
    }

    /// <summary>The agent's voice for the phone: ElevenLabs' as 16 kHz PCM when it's picked and has its key, else Windows' made into PCM.</summary>
    private ISpeechOut NewPhoneSpeechOut(PhoneCallMedia phone) =>
        PhoneVoices == "elevenlabs" && ElevenLabsKey() is { } key
            ? new ElevenLabsSpeechOut(key, api: ElevenLabsApi) { OutputFormat = "pcm_16000", Play = phone.PlayAsync }
            : new PhoneWindowsSpeechOut(phone);
}
