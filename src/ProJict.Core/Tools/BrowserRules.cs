using System.Text.RegularExpressions;

namespace ProJict.Core.Tools;

/// <summary>
/// What agents may do in ProJict's browser for a tool (Suno, Tripo3D): stay on the tool's own website and its
/// subdomains (studio.tripo3d.ai for tripo3d.ai), never type into a password, card or other secret field, and leave
/// paying (buy, upgrade, subscribe, checkout…) to you.
/// </summary>
public static partial class BrowserRules
{
    /// <summary>Whether an address is on the tool's website: the same site, or a subdomain of it ("www." aside).</summary>
    public static bool IsOnSite(string? address, string? website)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http")) return false;
        if (!Uri.TryCreate(website, UriKind.Absolute, out var site)) return false;
        var home = Bare(site.Host);
        var host = Bare(url.Host);
        return host == home || host.EndsWith("." + home, StringComparison.Ordinal);
    }

    private static string Bare(string host)
    {
        host = host.ToLowerInvariant().TrimEnd('.');
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    [GeneratedRegex(@"\b(buy|purchase|upgrade|subscribe|subscription|checkout|check out|pay|payment|billing|top up|top-up|add credits|get credits|buy credits|get more credits|pricing|plans?\b(?=.*\b(upgrade|choose|select)\b))\b", RegexOptions.IgnoreCase)]
    private static partial Regex Spending();

    /// <summary>Whether a button or link looks like spending money (not making something with credits you have): you do those.</summary>
    public static bool LooksLikeSpending(string? text) => !string.IsNullOrWhiteSpace(text) && Spending().IsMatch(text);

    [GeneratedRegex(@"pass(word|code|phrase)?|\bpin\b|card|\bcc\b|cvc|cvv|iban|security code|one-time|otp|2fa|ssn", RegexOptions.IgnoreCase)]
    private static partial Regex SecretWords();

    /// <summary>
    /// Whether a field is a secret one agents mustn't type into: a password field, or one whose name, label or
    /// autocomplete says password, card number, security code, PIN, one-time code or the like.
    /// </summary>
    public static bool IsSecretField(string? type, string? name, string? label, string? autocomplete)
    {
        if (string.Equals(type, "password", StringComparison.OrdinalIgnoreCase)) return true;
        if (autocomplete is { } a && (a.Contains("password", StringComparison.OrdinalIgnoreCase) || a.StartsWith("cc-", StringComparison.OrdinalIgnoreCase)
                                      || a.Contains("one-time-code", StringComparison.OrdinalIgnoreCase))) return true;
        return SecretWords().IsMatch($"{name} {label}");
    }
}
