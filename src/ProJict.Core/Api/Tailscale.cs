using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ProJict.Core.Api;

/// <summary>
/// Where built-in Tailscale (#a0d11f05) is, as its bridge says: its state ("Running", "NeedsLogin", "Starting",
/// "Stopped"...), the page to sign in at while it needs a sign-in, and its addresses and name on your tailnet.
/// </summary>
public sealed record TailscaleStatus(string State, string? SignInUrl, IReadOnlyList<IPAddress> Addresses, string? Name, string? Error = null)
{
    public static readonly TailscaleStatus Stopped = new("Stopped", null, [], null);

    public bool IsRunning => State == "Running";
    /// <summary>It waits for you to sign in, at <see cref="SignInUrl"/>.</summary>
    public bool NeedsSignIn => State == "NeedsLogin" && SignInUrl != null;
    /// <summary>Its IPv4 address on the tailnet (100.x.y.z), which the phone uses.</summary>
    public IPAddress? IPv4 => IsRunning ? Addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) : null;

    /// <summary>The bridge's JSON; anything it can't read is an error status, never an exception.</summary>
    public static TailscaleStatus Parse(string? json)
    {
        try
        {
            using var document = JsonDocument.Parse(json ?? "");
            var root = document.RootElement;
            string? Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
            if (Text("error") is { } error) return new TailscaleStatus("Failed", null, [], null, error);
            var addresses = root.TryGetProperty("ips", out var ips) && ips.ValueKind == JsonValueKind.Array
                ? ips.EnumerateArray().Select(i => i.ValueKind == JsonValueKind.String && IPAddress.TryParse(i.GetString(), out var ip) ? ip : null).OfType<IPAddress>().ToList()
                : [];
            return new TailscaleStatus(Text("state") ?? "Stopped", Tailscale.IsSignInUrl(Text("authUrl")) ? Text("authUrl") : null, addresses,
                Text("dnsName")?.TrimEnd('.'));
        }
        catch (JsonException ex)
        {
            return new TailscaleStatus("Failed", null, [], null, ex.Message);
        }
    }
}

/// <summary>Built-in Tailscale's names and rules (#a0d11f05).</summary>
public static class Tailscale
{
    /// <summary>The node's name on your tailnet: "projict-" and this computer's name, as Tailscale allows names (a-z, 0-9, -).</summary>
    public static string HostName(string machine)
    {
        var name = new StringBuilder();
        foreach (var c in machine.ToLowerInvariant())
            if (char.IsAsciiLetterOrDigit(c)) name.Append(c);
            else if (name.Length > 0 && name[^1] != '-') name.Append('-');
        var part = name.ToString().Trim('-');
        var host = part.Length == 0 ? "projict" : "projict-" + part;
        return host.Length <= 63 ? host : host[..63].TrimEnd('-');
    }

    /// <summary>Only Tailscale's own sign-in page is ever opened (an https address on login.tailscale.com).</summary>
    public static bool IsSignInUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        string.Equals(uri.Host, "login.tailscale.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>"Running" and the like in words, for Settings › API.</summary>
    public static string Describe(TailscaleStatus status, bool turnedOn, bool available, bool apiServesNetwork, int port) =>
        !available ? "Tailscale isn't included in this build of ProJict."
        : !turnedOn ? "Off. Turn it on, and your phone reaches ProJict through Tailscale away from home, without the Tailscale app."
        : !apiServesNetwork ? "Waiting for the API: turn it on and tick “Also let other devices on my network connect” above (your phone talks to ProJict over HTTPS)."
        : status.Error is { } error ? "Tailscale stopped: " + error
        : status.NeedsSignIn ? "Sign in to Tailscale to connect this computer: Sign in opens Tailscale's page in your browser, once."
        : status.IPv4 is { } ip ? $"On your Tailscale as {status.Name?.Split('.')[0] ?? "this computer"}: your phone reaches ProJict at https://{ip}:{port}, at home or away."
        : "Connecting to Tailscale…";
}
