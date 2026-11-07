using System.IO;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using ProJict.Core.Api;

namespace ProJict.App.Services;

/// <summary>
/// Your voice in a call from your phone (#d6b701ce): the phone's microphone, as it arrives over the call's media
/// connection, instead of this computer's. Nothing comes while the call is muted.
/// </summary>
public sealed class PhoneSoundIn(PhoneCallMedia phone) : ISoundIn
{
    private IDisposable? _listening;

    public event Action<byte[]>? Captured;

    public string? Start()
    {
        _listening ??= phone.Listen(pcm => Captured?.Invoke(pcm));
        return null;
    }

    public void Dispose()
    {
        _listening?.Dispose();
        _listening = null;
    }
}

/// <summary>
/// Windows' voices (offline) for a call from your phone: each answer is made into 16 kHz PCM and played on the phone,
/// never on this computer's speakers.
/// </summary>
public sealed class PhoneWindowsSpeechOut(PhoneCallMedia phone) : ISpeechOut
{
    private readonly SpeechSynthesizer _synth = new();
    private static readonly SpeechAudioFormatInfo Format = new(PhoneCallMedia.SampleRate, AudioBitsPerSample.Sixteen, AudioChannel.Mono);

    public Task<IReadOnlyList<string>> VoicesAsync(CancellationToken stop) => Task.FromResult(WindowsSpeechOut.VoicesOf(_synth, null));

    public Task<IReadOnlyList<string>> VoicesAsync(string? language, CancellationToken stop) => Task.FromResult(WindowsSpeechOut.VoicesOf(_synth, language));

    public async Task SpeakAsync(string voice, string text, CancellationToken stop)
    {
        var pcm = await Task.Run(() => Say(voice, text), stop);
        stop.ThrowIfCancellationRequested();
        await phone.PlayAsync(pcm, stop);
    }

    private byte[] Say(string voice, string text)
    {
        lock (_synth)
        {
            try { _synth.SelectVoice(voice); }
            catch (ArgumentException) { }
            using var pcm = new MemoryStream();
            _synth.SetOutputToAudioStream(pcm, Format);
            try { _synth.Speak(text); }
            finally { _synth.SetOutputToNull(); }
            return pcm.ToArray();
        }
    }

    public void Dispose()
    {
        lock (_synth) _synth.Dispose();
    }
}
