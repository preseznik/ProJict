using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace ProJict.Core.Api;

/// <summary>
/// Pairing ProJict on your phone with this computer (#1bbc8a8d): what the QR code in Settings › API holds, so nothing is
/// typed on the phone and it trusts only this computer. It holds the addresses the phone may try (a private VPN's
/// such as Tailscale's first, which work away from home, then the local network's), the "You, on your phone" token, and
/// the HTTPS certificate's SHA-256 fingerprint, which the phone pins instead of checking a name.
/// </summary>
public static class ApiPairing
{
    public const string Kind = "projict-pair";
    public const int Version = 1;

    public static string Payload(string name, string secret, IEnumerable<IPAddress> addresses, int port, string fingerprint) => new JsonObject
    {
        ["kind"] = Kind,
        ["v"] = Version,
        ["name"] = name,
        ["token"] = secret,
        ["urls"] = new JsonArray(addresses.Select(a => (JsonNode)Url(a, port)).ToArray()),
        ["fingerprint"] = fingerprint,
    }.ToJsonString();

    public static string Url(IPAddress address, int port) => $"https://{address}:{port}";

    /// <summary>This computer's addresses a phone could reach, best first (see <see cref="Order"/>).</summary>
    public static IReadOnlyList<IPAddress> Addresses()
    {
        try
        {
            return Order(NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(u => u.Address)));
        }
        catch (NetworkInformationException) { return []; }
    }

    /// <summary>
    /// IPv4 addresses a phone could reach, best first: a private VPN's (Tailscale hands out 100.64.0.0/10, reachable
    /// away from home), then the local network's (192.168/16, 10/8, 172.16/12), then any other. Loopback and link-local
    /// ones (169.254/16, no network behind them) are left out, and each comes once.
    /// </summary>
    public static IReadOnlyList<IPAddress> Order(IEnumerable<IPAddress> addresses) =>
        addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && !IsLinkLocal(a))
            .Distinct()
            .OrderBy(Rank)
            .ThenBy(a => a.ToString(), StringComparer.Ordinal)
            .ToList();

    /// <summary>Tailscale's (and other carrier-grade NAT VPNs') range, 100.64.0.0/10.</summary>
    public static bool IsPrivateVpn(IPAddress address) => address.GetAddressBytes() is [100, var b, _, _] && b is >= 64 and <= 127;

    private static bool IsLinkLocal(IPAddress address) => address.GetAddressBytes() is [169, 254, _, _];

    private static int Rank(IPAddress address) => address.GetAddressBytes() switch
    {
        _ when IsPrivateVpn(address) => 0,
        [192, 168, _, _] => 1,
        [10, _, _, _] => 2,
        [172, var b, _, _] when b is >= 16 and <= 31 => 3,
        _ => 4,
    };
}
