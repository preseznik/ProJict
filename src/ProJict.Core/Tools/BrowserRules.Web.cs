using System.Net;
using System.Net.Sockets;

namespace ProJict.Core.Tools;

/// <summary>How far agents may go in ProJict's web browser (Settings › Tools), a browser not tied to a tool.</summary>
public enum WebBrowsing
{
    /// <summary>Not at all: only tools' own websites, where you signed in.</summary>
    Off,
    /// <summary>Only this computer's own sites (localhost): agents try what they build.</summary>
    ThisComputer,
    /// <summary>The web, and this computer's own sites.</summary>
    Web,
}

public static partial class BrowserRules
{
    /// <summary>
    /// Why an agent may not open this address in ProJict's web browser, or null when it may: http(s) only; this computer's
    /// own sites (localhost, 127.0.0.1, [::1]) when browsing is on and the agent runs here; the web when that's allowed; never
    /// other devices on your network (your router's page, a printer, 192.168.…, *.local) nor files on this computer.
    /// </summary>
    public static string? WebProblem(string? address, WebBrowsing allowed, bool remoteAgent)
    {
        if (allowed == WebBrowsing.Off)
            return "The user hasn't let agents browse the web in ProJict's browser (Settings › Tools › Agents browse the web). Ask them if you need it.";
        if (!Uri.TryCreate(address, UriKind.Absolute, out var url)) return $"“{address}” isn't a web address: give it whole, e.g. https://example.com or http://localhost:5173.";
        if (url.Scheme is not ("http" or "https")) return $"Only web pages (http and https) open in ProJict's browser, not {url.Scheme}: addresses.";
        if (IsThisComputer(url.Host))
            return remoteAgent
                ? "localhost in ProJict's browser is ProJict's computer, not yours: you're connected over the API from elsewhere. Use your own browser for your own sites."
                : null;
        if (IsLocalNetwork(url.Host))
            return $"{url.Host} is on the user's own network (another device, a router or printer): agents don't go there in ProJict's browser.";
        return allowed == WebBrowsing.Web ? null
            : $"The user lets agents open only this computer's own sites (localhost) in ProJict's browser, not {url.Host}. Ask them if you need the web.";
    }

    /// <summary>localhost, 127.x.x.x, [::1] or a name under .localhost: this computer.</summary>
    public static bool IsThisComputer(string host)
    {
        host = host.Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        if (host is "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal)) return true;
        return IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
    }

    /// <summary>
    /// Another device on your network, by its address or name: private and link-local addresses (10.…, 172.16–31.…,
    /// 192.168.…, 169.254.…, 100.64–127.… as Tailscale uses, fc00::/7, fe80::/10), 0.0.0.0, names without a dot
    /// ("router") and names under .local, .lan, .home, .internal, .intranet, .corp or .home.arpa.
    /// </summary>
    public static bool IsLocalNetwork(string host)
    {
        host = host.Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        if (IPAddress.TryParse(host, out var ip)) return IsPrivate(ip);
        if (!host.Contains('.')) return true;
        string[] local = [".local", ".lan", ".home", ".internal", ".intranet", ".corp", ".home.arpa", ".localdomain"];
        return local.Any(l => host.EndsWith(l, StringComparison.Ordinal));
    }

    /// <summary>An address that isn't on the internet: private, link-local, shared (CGNAT, Tailscale), unspecified or multicast. Loopback counts too.</summary>
    public static bool IsPrivate(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] is 0 or 10 or >= 224
                   || b[0] == 172 && b[1] is >= 16 and <= 31
                   || b[0] == 192 && b[1] == 168
                   || b[0] == 169 && b[1] == 254
                   || b[0] == 100 && b[1] is >= 64 and <= 127;
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.Equals(IPAddress.IPv6Any) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast) return true;
            var first = ip.GetAddressBytes()[0];
            return (first & 0xFE) == 0xFC; // fc00::/7, unique local
        }
        return true;
    }

    /// <summary>
    /// Whether a page may fetch from this address (a picture, a script, a call to a server): never another device on your
    /// network, and this computer's own sites only from a page that is itself on this computer.
    /// </summary>
    public static bool MayFetch(string? address, string? page)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var url)) return true; // data:, blob: and the like stay in the page
        if (url.Scheme is not ("http" or "https" or "ws" or "wss")) return url.Scheme is not "file";
        if (IsThisComputer(url.Host))
            return Uri.TryCreate(page, UriKind.Absolute, out var from) && from.Scheme is "http" or "https" && IsThisComputer(from.Host);
        return !IsLocalNetwork(url.Host);
    }
}
