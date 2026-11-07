using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProJict.Core.Updates;

namespace ProJict.App.Services;

/// <summary>
/// ProJict for Android's updates, through this PC (#06baf4c6): what the latest release offers (looked up at most every
/// few hours), and its APK fetched once into ProJict's local folder and handed to a paired phone. See PhoneAppRelease.
/// </summary>
public sealed class PhoneAppUpdates
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    public static readonly TimeSpan LookEvery = TimeSpan.FromHours(4);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProJict", "PhoneApp");
    private PhoneAppInfo? _info;
    private string? _problem;
    private DateTime _lookedAt = DateTime.MinValue;

    /// <summary>GET /me/app-update's answer.</summary>
    public async Task<JsonObject> InfoAsync(CancellationToken stop)
    {
        await _gate.WaitAsync(stop);
        try
        {
            await LookAsync(stop);
            return PhoneAppRelease.Describe(_info, _problem);
        }
        finally { _gate.Release(); }
    }

    /// <summary>The APK to hand to the phone, fetched now if it isn't here yet; null when the release has none.</summary>
    public async Task<(string Path, string Sha256, PhoneAppInfo Info)?> ApkAsync(CancellationToken stop)
    {
        await _gate.WaitAsync(stop);
        try
        {
            await LookAsync(stop);
            if (_info is not { } info) return null;
            var path = Path.Combine(_folder, info.AssetName);
            // Fetched once; a file that doesn't match what was published (cut short, changed) is fetched again.
            var sha = File.Exists(path) && (info.Size <= 0 || new FileInfo(path).Length == info.Size) ? PhoneAppRelease.HashOf(path) : null;
            if (sha == null || (info.Sha256 != null && !string.Equals(sha, info.Sha256, StringComparison.OrdinalIgnoreCase)))
                sha = await PhoneAppRelease.DownloadAsync(Http, info, path, stop);
            // Older versions aren't needed any more.
            foreach (var old in Directory.EnumerateFiles(_folder, "projict-android-*.apk").Where(f => !string.Equals(f, path, StringComparison.OrdinalIgnoreCase)))
            {
                try { File.Delete(old); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return (path, sha, info);
        }
        finally { _gate.Release(); }
    }

    private async Task LookAsync(CancellationToken stop)
    {
        if (DateTime.Now - _lookedAt < LookEvery) return;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UpdateChecker.LatestReleaseApi);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ProJict", UpdateService.CurrentVersionText));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await Http.SendAsync(request, stop);
            if (response.StatusCode == HttpStatusCode.NotFound) { _info = null; _problem = null; }
            else
            {
                response.EnsureSuccessStatusCode();
                _info = PhoneAppRelease.Parse(await response.Content.ReadAsStringAsync(stop));
                _problem = null;
            }
            _lookedAt = DateTime.Now;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !stop.IsCancellationRequested)
        {
            // What was known stays; the phone hears why there's nothing new, and the next ask looks again.
            _problem = "Your PC couldn't reach GitHub to look for a newer phone app.";
        }
    }
}
