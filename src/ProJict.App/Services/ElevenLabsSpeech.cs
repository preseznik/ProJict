using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;

namespace ProJict.App.Services;

/// <summary>
/// ElevenLabs' voices for calls (#acb5d54c), through its API with your key from Settings › Tools: your voices are listed
/// once, each agent gets one of its own, and each answer is made into speech and played. The key goes only in the
/// request to ElevenLabs; it's never shown or written down.
/// </summary>
public sealed class ElevenLabsSpeechOut(string key, HttpClient? http = null, string api = "https://api.elevenlabs.io") : ISpeechOut
{
    /// <summary>ElevenLabs' fast model, for answers read as they come.</summary>
    public const string Model = "eleven_flash_v2_5";

    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    private readonly bool _ownsHttp = http == null;
    private Dictionary<string, string>? _voices;

    /// <summary>How speech is played: through the speakers (off-screen tests put in their own, so nothing sounds).</summary>
    public static Func<byte[], CancellationToken, Task> Player { get; set; } = PlayMp3Async;

    /// <summary>Plays the speech it made (an MP3).</summary>
    public Func<byte[], CancellationToken, Task> Play { get; set; } = Player;

    /// <summary>The speech is fetched and starts playing now (until then nothing is heard from the speakers).</summary>
    public event Action? Playing;

    /// <summary>The model that reads the answers: Flash, or one that speaks the call's language (Eleven v3 for Slovenian, #f021ff25).</summary>
    public string ModelId { get; set; } = Model;

    public async Task<IReadOnlyList<string>> VoicesAsync(CancellationToken stop)
    {
        if (_voices == null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{api}/v1/voices");
            request.Headers.Add("xi-api-key", key);
            var json = await SendAsync(request, stop);
            var list = JsonNode.Parse(json)?["voices"]?.AsArray() ?? [];
            _voices = list.Where(v => v?["voice_id"] != null && v["name"] != null)
                .GroupBy(v => (string)v!["name"]!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => (string)g.First()!["voice_id"]!, StringComparer.OrdinalIgnoreCase);
            if (_voices.Count == 0) throw new InvalidOperationException("ElevenLabs has no voices for your account.");
        }
        return [.. _voices.Keys.Order(StringComparer.OrdinalIgnoreCase)];
    }

    public async Task SpeakAsync(string voice, string text, CancellationToken stop)
    {
        await VoicesAsync(stop);
        var id = _voices!.TryGetValue(voice, out var known) ? known : _voices.Values.First();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{api}/v1/text-to-speech/{Uri.EscapeDataString(id)}?output_format=mp3_44100_128")
        {
            Content = JsonContent.Create(new { text, model_id = ModelId }),
        };
        request.Headers.Add("xi-api-key", key);
        request.Headers.Add("Accept", "audio/mpeg");
        using var response = await SendRawAsync(request, stop);
        var mp3 = await response.Content.ReadAsByteArrayAsync(stop);
        Playing?.Invoke();
        await Play(mp3, stop);
    }

    private async Task<string> SendAsync(HttpRequestMessage request, CancellationToken stop)
    {
        using var response = await SendRawAsync(request, stop);
        return await response.Content.ReadAsStringAsync(stop);
    }

    /// <summary>The answer when it's OK; otherwise throws with what ElevenLabs said, in words.</summary>
    private async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request, CancellationToken stop)
    {
        HttpResponseMessage response;
        try { response = await _http.SendAsync(request, stop); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !stop.IsCancellationRequested)
        {
            throw new InvalidOperationException("ElevenLabs couldn't be reached: " + ex.Message);
        }
        if (response.IsSuccessStatusCode) return response;
        var status = (int)response.StatusCode;
        response.Dispose();
        throw new InvalidOperationException(status switch
        {
            401 or 403 => "ElevenLabs refused the key in Settings › Tools (wrong or expired).",
            429 => "ElevenLabs says you're out of credits, or asked too often.",
            _ => $"ElevenLabs answered {status}.",
        });
    }

    /// <summary>Plays an MP3 through the speakers (a file of its own for the time it plays).</summary>
    private static async Task PlayMp3Async(byte[] mp3, CancellationToken stop)
    {
        var file = Path.Combine(Path.GetTempPath(), $"ProJict call {Guid.NewGuid():N}.mp3");
        await File.WriteAllBytesAsync(file, mp3, stop);
        var done = new TaskCompletionSource();
        var player = new MediaPlayer();
        player.MediaEnded += (_, _) => done.TrySetResult();
        player.MediaFailed += (_, _) => done.TrySetResult();
        using (stop.Register(() => done.TrySetResult()))
        {
            player.Open(new Uri(file));
            player.Play();
            await done.Task;
        }
        player.Stop();
        player.Close();
        try { File.Delete(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
