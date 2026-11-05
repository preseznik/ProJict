using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using ProJict.Core.Chat;
using Whisper.net;
using Whisper.net.Ggml;
using Whisper.net.LibraryLoader;

namespace ProJict.App.Services;

/// <summary>A recording of a phrase, made into text (ElevenLabs, Whisper); throws InvalidOperationException with the problem.</summary>
public interface ISpeechToText : IDisposable
{
    /// <summary>The text of <paramref name="pcm"/> (16 kHz, 16-bit mono); empty when it heard no words.</summary>
    Task<string> TranscribeAsync(byte[] pcm, CancellationToken stop);
    /// <summary>Done when it can transcribe (Whisper: its model loaded); faulted with why when it can't.</summary>
    Task Ready => Task.CompletedTask;
}

/// <summary>
/// Hears you a phrase at a time (#4980bbee): the microphone's sound is cut into phrases where you pause, and each goes to
/// a recognizer that takes a recording (ElevenLabs, Whisper), one after the other, so what you say arrives in order.
/// When the recognizer fails (no credits, no network), <see cref="ISpeechIn.Failed"/> says why.
/// </summary>
public sealed class PhraseSpeechIn(ISoundIn sound, ISpeechToText recognizer, string name = "It") : ISpeechIn
{
    /// <summary>Done when the recognizer can transcribe; phrases said before wait for it.</summary>
    public Task Ready => recognizer.Ready;
    public string? GettingReady => $"{name} is getting ready: it loads its model (the first time on a computer takes longer). What you say meanwhile waits for it.";

    private readonly PhraseSplitter _splitter = new(CallVoice.Rate * 2);
    private readonly CancellationTokenSource _stop = new();
    private Task _queue = Task.CompletedTask;
    private bool _started;
    private bool _failed;
    private bool _playing;
    /// <summary>Sound heard since the speakers stopped, in bytes (the echo's tail is measured in sound, not on the clock).</summary>
    private long _heardSincePlaying = long.MaxValue / 2;
    private bool _echo;

    /// <summary>After the speakers stop, their sound still reaches the microphone for a moment (the room).</summary>
    public static readonly TimeSpan EchoTail = TimeSpan.FromMilliseconds(300);

    /// <summary>Phrases dropped as the speakers' own sound.</summary>
    public int EchoesDropped { get; private set; }

    // ── Speaking over an answer (#4ede2c7e) ──
    /// <summary>The speakers' sound at the microphone while an answer plays (RMS), followed as it plays.</summary>
    private double _echoLevel;
    /// <summary>Sound heard since the answer started playing, in bytes: the first moment only measures the echo.</summary>
    private long _heardWhilePlaying;
    /// <summary>Frames in a row clearly louder than the echo.</summary>
    private int _louderFrames;
    /// <summary>You spoke over the answer: the phrase you're in is yours, not the echo.</summary>
    private bool _bargedIn;

    /// <summary>This much louder than the speakers' echo is you speaking over it.</summary>
    public const double OverEcho = 3;
    /// <summary>And at least this loud (RMS), so a quiet room's hum never counts.</summary>
    public const double LoudEnough = 0.024;
    /// <summary>For this long, so a click or a cough doesn't stop an answer.</summary>
    public static readonly TimeSpan Over = TimeSpan.FromMilliseconds(200);
    /// <summary>
    /// The first moment of an answer only measures the speakers' echo: speech starts after a little silence, and only
    /// then does the echo show how loud it is. Stop (or Esc) cuts an answer off from its start.
    /// </summary>
    public static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(1200);

    public event Action? Interrupted;

    public bool DropsEcho => true;

    public void SpeakersPlaying(bool playing)
    {
        lock (_splitter)
        {
            if (_playing && !playing) _heardSincePlaying = 0;
            if (!_playing && playing)
            {
                _echoLevel = Math.Max(_splitter.Hum, 0.002);
                _heardWhilePlaying = 0;
                _louderFrames = 0;
            }
            _playing = playing;
        }
    }

    public event Action<string>? Heard;
    public event Action<bool>? Hearing;
    public event Action<double>? Level;
    public event Action<string>? Failed;

    public string? Start()
    {
        if (_started) return null;
        _started = true;
        // A phrase that starts while the speakers play (or just after) is their own sound: dropped, not even transcribed.
        // One you started first goes on as said, even when an answer starts playing over its end.
        _splitter.Started += () =>
        {
            _echo = !_bargedIn && (_playing || _heardSincePlaying < (long)(EchoTail.TotalSeconds * CallVoice.Rate * 2));
            if (!_echo) Hearing?.Invoke(true);
        };
        _splitter.Ended += phrase =>
        {
            _bargedIn = false;
            if (_echo)
            {
                _echo = false;
                if (phrase != null) EchoesDropped++;
                return;
            }
            Hearing?.Invoke(false);
            if (phrase == null || _failed) return;
            _queue = _queue.ContinueWith(_ => Transcribe(phrase), TaskScheduler.Default).Unwrap();
        };
        sound.Captured += frame =>
        {
            Level?.Invoke(CallVoice.Loudness(frame));
            var interrupted = false;
            lock (_splitter)
            {
                if (!_playing) _heardSincePlaying += frame.Length;
                else interrupted = SpeakingOver(frame);
                _splitter.Add(frame);
            }
            if (interrupted)
            {
                Interrupted?.Invoke();
                Hearing?.Invoke(true); // the phrase you're in is yours now
            }
        };
        return sound.Start();
    }

    /// <summary>
    /// While an answer plays: follows how loud the speakers' echo is at the microphone, and says when you speak over it,
    /// clearly louder, for a moment. The phrase you're in is then yours, not the echo (with a headset the echo is next
    /// to nothing, so speaking at all interrupts; with loud speakers you have to be clearly louder than them).
    /// </summary>
    private bool SpeakingOver(byte[] frame)
    {
        var level = PhraseSplitter.Level(frame);
        _heardWhilePlaying += frame.Length;
        var settling = _heardWhilePlaying < (long)(Settle.TotalSeconds * CallVoice.Rate * 2);
        var louder = !settling && !_bargedIn && level > Math.Max(LoudEnough, _echoLevel * OverEcho);
        if (!louder)
        {
            // The echo's loud moments, followed: up fast, down slowly (speech goes up and down from syllable to syllable,
            // and the loud ones mustn't count as you speaking over it).
            _echoLevel = level > _echoLevel ? _echoLevel * 0.5 + level * 0.5 : _echoLevel * 0.98 + level * 0.02;
            _louderFrames = 0;
            return false;
        }
        if (++_louderFrames * frame.Length < (long)(Over.TotalSeconds * CallVoice.Rate * 2)) return false;
        _bargedIn = true;
        _echo = false; // the phrase going on is yours
        return true;
    }

    private async Task Transcribe(byte[] phrase)
    {
        if (_stop.IsCancellationRequested || _failed) return;
        try
        {
            var text = (await recognizer.TranscribeAsync(phrase, _stop.Token)).Trim();
            if (!WhisperModel.IsNoise(text)) Heard?.Invoke(text);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException)
        {
            _failed = true;
            Failed?.Invoke(ex.Message);
        }
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        _stop.Cancel();
        sound.Dispose();
        recognizer.Dispose();
    }

    public void Dispose() => Stop();
}

/// <summary>
/// ElevenLabs' speech to text (Scribe), with your key from Settings › Tools: each phrase goes as a WAV file, and costs
/// credits for the time spoken. The key goes only in the request to ElevenLabs.
/// </summary>
public sealed class ElevenLabsSpeechToText(string key, HttpClient? http = null, string api = "https://api.elevenlabs.io") : ISpeechToText
{
    /// <summary>ElevenLabs' speech-to-text model.</summary>
    public const string Model = "scribe_v1";

    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    private readonly bool _ownsHttp = http == null;

    public async Task<string> TranscribeAsync(byte[] pcm, CancellationToken stop)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(Model), "model_id");
        form.Add(new StringContent("false"), "tag_audio_events");
        var file = new ByteArrayContent(Wav.Of(pcm, CallVoice.Rate));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "phrase.wav");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{api}/v1/speech-to-text") { Content = form };
        request.Headers.Add("xi-api-key", key);
        HttpResponseMessage response;
        try { response = await _http.SendAsync(request, stop); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !stop.IsCancellationRequested)
        {
            throw new InvalidOperationException("ElevenLabs couldn't be reached: " + ex.Message);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException((int)response.StatusCode switch
                {
                    401 or 403 => "ElevenLabs refused the key in Settings › Tools (wrong or expired).",
                    429 => "ElevenLabs says you're out of credits, or asked too often.",
                    var status => $"ElevenLabs answered {status}.",
                });
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(stop));
            return (string?)json?["text"] ?? "";
        }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}

/// <summary>
/// Whisper (whisper.cpp) on this computer, offline: the model you downloaded in Settings › Chat, on the graphics card
/// (Vulkan) when it can, else the processor. The model loads once (when a call first rings) and warms up on a moment of
/// silence, then stays loaded while ProJict runs, so a call doesn't wait for it; each call gets its own processor.
/// <paramref name="names"/> (the agents in the call and the ones connected) are how it spells names it hears: "Wren", not "Ren".
/// </summary>
public sealed class WhisperSpeechToText : ISpeechToText
{
    private static readonly object Gate = new();
    private static (string Path, Task<WhisperFactory> Factory)? _loaded;

    private readonly Task<WhisperProcessor> _loading;
    private bool _disposed;

    public WhisperSpeechToText(string modelPath, IEnumerable<string>? names = null)
    {
        var prompt = names?.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() is { Count: > 0 } list
            ? string.Join(", ", list) + "." : null;
        _loading = Task.Run(async () =>
        {
            var factory = await Load(modelPath);
            var builder = factory.CreateBuilder().WithLanguage("auto");
            if (prompt != null) builder = builder.WithPrompt(prompt);
            return builder.Build();
        });
    }

    /// <summary>
    /// Loads the model (once; another model replaces it), and runs it on half a second of silence: the first pass is
    /// slow while the graphics card gets ready. Calls start it when they ring, so it's ready when you speak.
    /// </summary>
    public static Task<WhisperFactory> Load(string modelPath)
    {
        lock (Gate)
        {
            if (_loaded is { } loaded && loaded.Path == modelPath && !loaded.Factory.IsFaulted) return loaded.Factory;
            Unload();
            var factory = Task.Run(async () =>
            {
                RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];
                try
                {
                    var made = WhisperFactory.FromPath(modelPath);
                    using var warm = made.CreateBuilder().WithLanguage("en").Build();
                    await foreach (var _ in warm.ProcessAsync(new float[CallVoice.Rate / 2])) { }
                    return made;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    throw new InvalidOperationException("Whisper couldn't load its model: " + ex.Message);
                }
            });
            _loaded = (modelPath, factory);
            return factory;
        }
    }

    /// <summary>Lets go of the loaded model (its memory back), e.g. when it's deleted or another is picked.</summary>
    public static void Unload()
    {
        lock (Gate)
        {
            if (_loaded is not { } loaded) return;
            _loaded = null;
            _ = loaded.Factory.ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully) t.Result.Dispose();
            }, TaskScheduler.Default);
        }
    }

    /// <summary>Where Whisper runs, once loaded: the graphics card or the processor.</summary>
    public static string? RunsOn => RuntimeOptions.LoadedLibrary?.ToString();

    /// <summary>Done when the model is loaded and warmed up, and this call's processor made.</summary>
    public Task Ready => _loading;

    public async Task<string> TranscribeAsync(byte[] pcm, CancellationToken stop)
    {
        var processor = await _loading.WaitAsync(stop);
        var text = new System.Text.StringBuilder();
        await foreach (var segment in processor.ProcessAsync(Wav.Samples(pcm), stop))
            if (!WhisperModel.IsNoise(segment.Text)) text.Append(segment.Text);
        return text.ToString().Trim();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ = _loading.ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully) t.Result.Dispose();
        }, TaskScheduler.Default);
    }
}

/// <summary>
/// Whisper's models, on this computer (not in the data folder, so they aren't backed up or mirrored): downloaded once
/// from Settings › Chat, then used offline. One download at a time; it goes on while Settings is closed.
/// </summary>
public static class WhisperModels
{
    /// <summary>Where they're kept (tests put in their own).</summary>
    public static string Folder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProJict", "Whisper");

    /// <summary>The model's file, downloaded or not.</summary>
    public static string PathOf(WhisperModel model) => Path.Combine(Folder, model.FileName);

    public static bool Has(WhisperModel model) => File.Exists(PathOf(model));

    /// <summary>Gets the model's file from the network (tests put in their own, so nothing is downloaded).</summary>
    public static Func<WhisperModel, CancellationToken, Task<Stream>> Source { get; set; } = (model, stop) =>
        WhisperGgmlDownloader.Default.GetGgmlModelAsync(TypeOf(model), QuantizationType.NoQuantization, stop);

    private static GgmlType TypeOf(WhisperModel model) => model.Id switch
    {
        "base" => GgmlType.Base,
        "large-v3-turbo" => GgmlType.LargeV3Turbo,
        _ => GgmlType.Small,
    };

    /// <summary>The download going on: which model, and how far (0 to 1).</summary>
    public static (WhisperModel Model, double Done)? Downloading { get; private set; }

    /// <summary>A download moved on, finished or failed (any thread).</summary>
    public static event Action? Changed;

    /// <summary>Why the last download failed, if it did.</summary>
    public static string? Problem { get; private set; }

    private static CancellationTokenSource? _cancel;

    /// <summary>Downloads the model (it's written under another name until it's all there); nothing when one is downloading.</summary>
    public static async Task DownloadAsync(WhisperModel model)
    {
        if (Downloading != null) return;
        Downloading = (model, 0);
        Problem = null;
        _cancel = new CancellationTokenSource();
        Changed?.Invoke();
        var path = PathOf(model);
        var part = path + ".part";
        try
        {
            Directory.CreateDirectory(Folder);
            await using (var source = await Source(model, _cancel.Token))
            await using (var file = File.Create(part))
            {
                var buffer = new byte[1 << 20];
                long got = 0;
                var expected = (long)model.SizeMb * 1024 * 1024;
                var lastShown = DateTime.MinValue;
                int read;
                while ((read = await source.ReadAsync(buffer, _cancel.Token)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), _cancel.Token);
                    got += read;
                    if (DateTime.Now - lastShown < TimeSpan.FromMilliseconds(250)) continue;
                    lastShown = DateTime.Now;
                    Downloading = (model, Math.Min(0.99, (double)got / expected));
                    Changed?.Invoke();
                }
            }
            File.Move(part, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException or UnauthorizedAccessException or InvalidOperationException)
        {
            Problem = ex is OperationCanceledException ? null : $"{model.Name} didn't download: {ex.Message}";
            try { File.Delete(part); }
            catch (Exception) { }
        }
        finally
        {
            Downloading = null;
            _cancel = null;
            Changed?.Invoke();
        }
    }

    /// <summary>Stops the download going on.</summary>
    public static void Cancel() => _cancel?.Cancel();

    /// <summary>Deletes a downloaded model (its space back).</summary>
    public static void Delete(WhisperModel model)
    {
        WhisperSpeechToText.Unload();
        try { File.Delete(PathOf(model)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Problem = ex.Message; }
        Changed?.Invoke();
    }
}
