using System.Security.Cryptography;
using System.Text;

namespace ProJict.Core.Agents;

/// <summary>
/// The PIN you approve with what can't be taken back (#8793323e): an agent asks with ask_approval pin=true (publishing a
/// release, say), and Approve then asks "Are you sure?" and this PIN. It's kept per computer (settings.json), never as
/// itself: a salted PBKDF2 hash of it.
/// </summary>
public sealed class ApprovalPin
{
    public const int MinDigits = 4;
    public const int MaxDigits = 8;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int DefaultIterations = 200_000;

    public string Salt { get; set; } = "";
    public string Hash { get; set; } = "";
    public int Iterations { get; set; } = DefaultIterations;
    public DateTime SetAt { get; set; } = DateTime.Now;

    /// <summary>Why it can't be a PIN, or null when it can: 4 to 8 digits.</summary>
    public static string? Problem(string? pin) =>
        pin == null || pin.Length < MinDigits || pin.Length > MaxDigits || !pin.All(char.IsAsciiDigit)
            ? $"A PIN is {MinDigits} to {MaxDigits} digits." : null;

    public static ApprovalPin Create(string pin, DateTime? now = null)
    {
        if (Problem(pin) is { } why) throw new ArgumentException(why, nameof(pin));
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        return new ApprovalPin
        {
            Salt = Convert.ToBase64String(salt),
            Hash = Convert.ToBase64String(Derive(pin, salt, DefaultIterations)),
            Iterations = DefaultIterations,
            SetAt = now ?? DateTime.Now,
        };
    }

    public bool Matches(string? pin)
    {
        if (pin == null || Problem(pin) != null || Iterations <= 0) return false;
        try
        {
            var expected = Convert.FromBase64String(Hash);
            return CryptographicOperations.FixedTimeEquals(Derive(pin, Convert.FromBase64String(Salt), Iterations), expected);
        }
        catch (FormatException) { return false; }
    }

    private static byte[] Derive(string pin, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, iterations, HashAlgorithmName.SHA256, HashBytes);
}

/// <summary>
/// Wrong PINs in a row (#8793323e): after <see cref="Allowed"/> of them, no PIN is taken for <see cref="Pause"/>, so
/// it can't be guessed (from a phone's token, say). The right one starts the count again.
/// </summary>
public sealed class PinTries(Func<DateTime>? clock = null)
{
    public const int Allowed = 5;
    public static readonly TimeSpan Pause = TimeSpan.FromMinutes(5);
    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.Now);
    private int _wrong;
    private DateTime? _until;

    /// <summary>Wrong ones left before the pause.</summary>
    public int Left => Allowed - _wrong;

    /// <summary>Checks a PIN against the one set: null when it's right, else why not (in words for the person).</summary>
    public string? Check(ApprovalPin? set, string? typed)
    {
        if (set == null) return "There's no PIN on this computer yet: set one in ProJict (Settings › AI agents › Approvals).";
        var now = _clock();
        if (_until is { } until && now < until)
            return $"Too many wrong PINs: try again in {Math.Max(1, (int)Math.Ceiling((until - now).TotalMinutes))} min.";
        if (_until != null)
        {
            _until = null;
            _wrong = 0;
        }
        if (set.Matches(typed))
        {
            _wrong = 0;
            return null;
        }
        if (++_wrong >= Allowed)
        {
            _until = now + Pause;
            return $"That isn't the PIN. Too many wrong ones: try again in {(int)Pause.TotalMinutes} min.";
        }
        return Left == 1 ? $"That isn't the PIN. One more wrong one and it waits {(int)Pause.TotalMinutes} minutes." : "That isn't the PIN.";
    }
}
