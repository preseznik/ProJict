using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using ProJict.Core.Storage;

namespace ProJict.Core.Api;

public enum ApiAccess { ReadWrite, ReadOnly }

/// <summary>
/// The tools a token's agent is offered, and may use: all an agent needs over the network (not the ones that need this
/// computer itself), Chat only, or the ones you ticked. Or no agent at all: <see cref="You"/> is ProJict on your phone, acting as you.
/// </summary>
public enum ApiToolSet
{
    Agent,
    ChatOnly,
    Custom,
    /// <summary>You, on your phone (#1bbc8a8d): it signs as you, at /api/v1/me only, and decides what only you can.</summary>
    You,
}

/// <summary>
/// A key for ProJict's API (Settings › API). Its secret is shown once when it's made; only a hash of it is kept, plus
/// its last characters so you can tell your tokens apart.
/// </summary>
public sealed class ApiToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Signs everything done with the token, e.g. "n8n", until its agent has a name of its own.</summary>
    public string Name { get; set; } = "";
    /// <summary>
    /// The name its agent picked (set_name) or you gave it (Agents › Rename), e.g. "Hermes": it signs with that instead,
    /// also the next time it connects with the token. Null while it goes by the token's name.
    /// </summary>
    public string? AgentName { get; set; }
    public ApiAccess Access { get; set; }
    /// <summary>The tools its agent is offered (Agent unless you chose otherwise); it applies from its next request.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ApiToolSet ToolSet { get; set; }
    /// <summary>For <see cref="ApiToolSet.Custom"/>: the tools you ticked, by name.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? CustomTools { get; set; }
    /// <summary>Paused by the user: it still connects (and can ask who it is) but can't do anything, not even read, until unfrozen.</summary>
    public bool Frozen { get; set; }
    /// <summary>The projects the token reaches; null for all of yours.</summary>
    public List<Guid>? Projects { get; set; }
    /// <summary>How long it lasts, in days; renewing it starts that again.</summary>
    public int Days { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? RenewedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    /// <summary>SHA-256 of the secret, hex.</summary>
    public string Hash { get; set; } = "";
    /// <summary>The secret's last four characters.</summary>
    public string Hint { get; set; } = "";
    public DateTime? LastUsedAt { get; set; }
    /// <summary>The address it was last used from.</summary>
    public string? LastUsedFrom { get; set; }

    public bool IsExpired(DateTime now) => now >= ExpiresAt;
    public bool ReadOnly => Access == ApiAccess.ReadOnly;
    /// <summary>ProJict on your phone: it acts as you, not as an agent.</summary>
    public bool ActsAsYou => ToolSet == ApiToolSet.You;
    public string Masked => $"{ApiData.SecretPrefix}…{Hint}";
}

/// <summary>
/// This computer's API settings and tokens, kept next to ProJict's own settings (not in the data folder).
/// </summary>
public sealed class ApiData
{
    public const int DefaultPort = 47614;
    public const string SecretPrefix = "pjt_";
    /// <summary>The expiry choices, in days; there is no "never".</summary>
    public static readonly int[] ExpiryChoices = [7, 30, 90, 365];
    public const int DefaultExpiryDays = 90;
    /// <summary>How long before a token expires the Feed starts saying so.</summary>
    public static readonly TimeSpan ExpiryNotice = TimeSpan.FromDays(7);
    public const int MaxNameLength = 40;

    public bool Enabled { get; set; }
    public int Port { get; set; } = DefaultPort;
    /// <summary>Also serve other devices on the network (over HTTPS); otherwise only this computer (127.0.0.1).</summary>
    public bool AllowNetwork { get; set; }
    /// <summary>Built-in Tailscale (#a0d11f05): this computer joins your tailnet itself, and the API is reachable there.</summary>
    public bool Tailscale { get; set; }
    public List<ApiToken> Tokens { get; set; } = [];
    /// <summary>The HTTPS certificate for network access (PKCS#12, protected for the Windows user).</summary>
    public string? ProtectedCertificate { get; set; }

    public static string DefaultPath => Path.Combine(Path.GetDirectoryName(AppSettings.DefaultPath)!, "api.json");

    public static ApiData Load(string path)
    {
        try { return File.Exists(path) ? Json.Deserialize<ApiData>(File.ReadAllText(path)) ?? new() : new(); }
        catch (Exception) { return new(); }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllText(path, Json.Serialize(this));
    }

    /// <summary>Makes a token and returns its secret, the only time it's seen. Throws ArgumentException for a bad name or expiry.</summary>
    public string Create(string name, ApiAccess access, IReadOnlyCollection<Guid>? projects, int days, DateTime now, out ApiToken token)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("Give the token a name, e.g. the tool that will use it.");
        if (name.Length > MaxNameLength) throw new ArgumentException($"Keep the name short ({MaxNameLength} characters at most).");
        if (Tokens.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"There's already a token named “{name}”: renew it, or choose another name.");
        if (!ExpiryChoices.Contains(days)) throw new ArgumentException("Choose when the token expires.");
        if (projects is { Count: 0 }) throw new ArgumentException("Choose at least one project, or all of them.");
        token = new ApiToken
        {
            Name = name,
            Access = access,
            Projects = projects?.Distinct().ToList(),
            Days = days,
            CreatedAt = now,
        };
        Tokens.Add(token);
        return SetSecret(token, now);
    }

    /// <summary>A new secret for the token, valid for its number of days from now; the old secret stops working.</summary>
    public string Renew(ApiToken token, DateTime now)
    {
        token.RenewedAt = now;
        return SetSecret(token, now);
    }

    public bool Revoke(ApiToken token) => Tokens.Remove(token);

    /// <summary>The token a secret belongs to (expired or not), comparing hashes in constant time.</summary>
    public ApiToken? Find(string? secret)
    {
        secret = secret?.Trim();
        if (string.IsNullOrEmpty(secret) || !secret.StartsWith(SecretPrefix, StringComparison.Ordinal)) return null;
        var presented = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        ApiToken? found = null;
        foreach (var token in Tokens)
        {
            byte[] stored;
            try { stored = Convert.FromHexString(token.Hash); }
            catch (FormatException) { continue; }
            if (CryptographicOperations.FixedTimeEquals(presented, stored)) found = token;
        }
        return found;
    }

    /// <summary>"pjt_" and 43 characters: 256 random bits, URL-safe.</summary>
    public static string NewSecret() =>
        SecretPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string HashSecret(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static string SetSecret(ApiToken token, DateTime now)
    {
        var secret = NewSecret();
        token.Hash = HashSecret(secret);
        token.Hint = secret[^4..];
        token.ExpiresAt = now.AddDays(token.Days);
        return secret;
    }

    public static string ExpiryText(int days) => days switch
    {
        365 => "1 year",
        1 => "1 day",
        _ => $"{days} days",
    };
}
