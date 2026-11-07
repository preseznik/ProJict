using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ProJict.Core.Updates;

/// <summary>ProJict for Android as published on the latest GitHub release: projict-android-x.y.z.apk.</summary>
public sealed record PhoneAppInfo(Version Version, string ReleaseTag, string ReleaseUrl, string AssetName, string AssetUrl, long Size, string? Sha256)
{
    public string VersionText => Version.ToString(3);
}

/// <summary>
/// App updates for the phone through this PC (#06baf4c6): the PC finds ProJict for Android on its own release, fetches
/// it once (size and SHA-256 checked) and hands it to a paired phone over /me, so the phone never contacts the internet.
/// The phone checks the APK's signing certificate against the installed app before Android installs it.
/// </summary>
public static partial class PhoneAppRelease
{
    public const int ApiVersion = 1;
    /// <summary>No phone APK is anywhere near this big; a bigger asset isn't fetched.</summary>
    public const long MaxBytes = 200L * 1024 * 1024;

    [GeneratedRegex(@"^projict-android-(\d+)\.(\d+)\.(\d+)\.apk$", RegexOptions.IgnoreCase)]
    private static partial Regex AssetName();

    /// <summary>The phone app on a GitHub "release" JSON object; null when it has none (or it's a draft/prerelease).</summary>
    public static PhoneAppInfo? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (Bool(root, "draft") || Bool(root, "prerelease")) return null;
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        PhoneAppInfo? best = null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = Str(asset, "name") ?? "";
            var match = AssetName().Match(name);
            if (!match.Success || Str(asset, "browser_download_url") is not { } url) continue;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) continue;
            var version = new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value));
            var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
            var digest = Str(asset, "digest");
            var sha = digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && digest.Length == 71 ? digest[7..].ToUpperInvariant() : null;
            if (best == null || version > best.Version)
                best = new PhoneAppInfo(version, Str(root, "tag_name") ?? "", Str(root, "html_url") ?? UpdateChecker.ReleasesPage, name, url, size, sha);
        }
        return best;
    }

    /// <summary>GET /me/app-update: the newest phone app this PC knows of (the phone compares it with its own version).</summary>
    public static JsonObject Describe(PhoneAppInfo? info, string? problem = null) => new()
    {
        ["apiVersion"] = ApiVersion,
        ["available"] = info != null,
        ["version"] = info?.VersionText,
        ["size"] = info?.Size,
        ["sha256"] = info?.Sha256,
        ["releaseUrl"] = info?.ReleaseUrl,
        ["problem"] = info == null ? problem : null,
    };

    /// <summary>Fetches the APK into <paramref name="path"/> and returns its SHA-256; throws when it doesn't match what GitHub published.</summary>
    public static async Task<string> DownloadAsync(HttpClient http, PhoneAppInfo info, string path, CancellationToken ct = default)
    {
        if (info.Size > MaxBytes) throw new InvalidDataException("The phone app on the release is too big to fetch.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var partial = path + ".part";
        using var response = await http.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long written = 0;
        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var target = File.Create(partial))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                written += read;
                if (written > MaxBytes) break;
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
            }
        }
        var actual = Convert.ToHexString(hash.GetHashAndReset());
        var ok = written <= MaxBytes && (info.Size <= 0 || written == info.Size) &&
                 (info.Sha256 == null || string.Equals(actual, info.Sha256, StringComparison.OrdinalIgnoreCase));
        if (!ok)
        {
            File.Delete(partial);
            throw new InvalidDataException("The phone app fetched from the release didn't match the published file.");
        }
        File.Move(partial, path, overwrite: true);
        return actual;
    }

    /// <summary>The SHA-256 of a file already fetched (hex, upper case).</summary>
    public static string HashOf(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
