using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using ProJict.Core.Storage;

namespace ProJict.Core.Sharing;

/// <summary>Someone allowed to connect to this ProJict (Settings › Sharing).</summary>
public sealed class GuestAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public GuestRole Role { get; set; }
    /// <summary>The groups whose projects the guest sees; null for all.</summary>
    public List<string>? Groups { get; set; }
    /// <summary>SHA-256 of the guest's secret token (the token itself is only on the guest's computer).</summary>
    public string TokenHash { get; set; } = "";
    public bool Blocked { get; set; }
    /// <summary>The guest's computer name, as it said when joining.</summary>
    public string? Device { get; set; }
    /// <summary>
    /// You, on another computer: confirmed with a 6-digit code typed there. It takes part under your name, its changes
    /// are signed with it, and its agents count as yours (no "via"). Private projects stay on this computer still.
    /// </summary>
    public bool Self { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.Now;
    public DateTime? LastSeen { get; set; }

    public bool Sees(string group) => Groups == null || Groups.Contains(group, StringComparer.OrdinalIgnoreCase);
}

/// <summary>A one-time code to give someone; whoever joins with it needs your approval, unless you let them in with it.</summary>
public sealed class Invite
{
    public string Code { get; set; } = "";
    public GuestRole Role { get; set; }
    public List<string>? Groups { get; set; }
    /// <summary>Whoever joins with it is in at once, with its role and groups, without asking you (you chose so when making it).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool LetsIn { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime ExpiresAt { get; set; }

    public bool IsValid(DateTime now) => now < ExpiresAt;
}

/// <summary>A ProJict this computer connected to as a guest.</summary>
public sealed class KnownHost
{
    /// <summary>host:port as typed.</summary>
    public string Address { get; set; } = "";
    /// <summary>SHA-256 of the host's certificate, trusted on the first connection.</summary>
    public string Fingerprint { get; set; } = "";
    public string? HostName { get; set; }
    public Guid GuestId { get; set; }
    /// <summary>The secret token, protected for this Windows user (DPAPI).</summary>
    public string? ProtectedToken { get; set; }
    public bool AutoConnect { get; set; } = true;
    /// <summary>
    /// Your Open channels go to that ProJict too, over the same connection, so its owner reads and writes in them (a
    /// ProJict linked as you shares all of yours anyway).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool ShareChatBack { get; set; }
}

/// <summary>%LocalAppData%\ProJict\sharing.json: this computer's side of sharing, as host and as guest.</summary>
public sealed class SharingData
{
    public const int DefaultPort = 47613;

    /// <summary>Let other ProJict apps on the network connect.</summary>
    public bool Enabled { get; set; }
    public int Port { get; set; } = DefaultPort;
    /// <summary>The host certificate (PKCS#12), protected for this Windows user; made the first time sharing is on.</summary>
    public string? ProtectedCertificate { get; set; }
    public List<GuestAccount> Guests { get; set; } = [];
    /// <summary>Guests' AI agents may work in what you share, as far as the guest's role allows; signed "Ada (via Alice)".</summary>
    public bool GuestAgents { get; set; } = true;
    public List<Invite> Invites { get; set; } = [];
    public List<KnownHost> Hosts { get; set; } = [];

    public static string DefaultPath => Path.Combine(Path.GetDirectoryName(AppSettings.DefaultPath)!, "sharing.json");

    public static SharingData Load(string path)
    {
        try { return File.Exists(path) ? Json.Deserialize<SharingData>(File.ReadAllText(path)) ?? new() : new(); }
        catch (Exception) { return new(); }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllText(path, Json.Serialize(this));
    }

    /// <summary>Easy to read out: no 0/O or 1/I.</summary>
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>"K7QF-2MPD": 40 random bits, good for one use within its lifetime and only with your approval.</summary>
    public static string NewInviteCode()
    {
        var chars = new char[9];
        for (var i = 0; i < chars.Length; i++) chars[i] = i == 4 ? '-' : CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        return new string(chars);
    }

    public static string NormalizeCode(string code) =>
        new string(code.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray()) is { Length: 8 } c ? $"{c[..4]}-{c[4..]}" : code.Trim().ToUpperInvariant();

    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>"3F:A2:…" — SHA-256 of a certificate, in pairs, for people to compare.</summary>
    public static string Fingerprint(byte[] certificate) => string.Join(":", SHA256.HashData(certificate).Select(b => b.ToString("X2")));

    /// <summary>The first few pairs, enough to compare by eye.</summary>
    public static string ShortFingerprint(string fingerprint) => string.Join(":", fingerprint.Split(':').Take(8));
}
