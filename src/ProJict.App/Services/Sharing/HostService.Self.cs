using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ProJict.Core.Sharing;

namespace ProJict.App.Services.Sharing;

/// <summary>
/// You, on another computer: a guest you say is you ("It's me" when it asks to join, or This is me… in Sharing) types a
/// 6-digit code this ProJict shows, within a few minutes. From then on it takes part under your name: in Chat (your
/// read marks and mentions are the same on both), in what it changes (signed with your name, as an editor), and its
/// agents count as yours. Private projects stay on this computer all the same, and moderating Chat stays here.
/// </summary>
public sealed partial class HostService
{
    public const int SelfCodeMinutes = 5;
    private const int SelfCodeTries = 5;

    private sealed class SelfCode(string code, DateTime until)
    {
        public string Code { get; } = code;
        public DateTime Until { get; } = until;
        public int Wrong { get; set; }
    }

    private readonly Dictionary<Guid, SelfCode> _selfCodes = [];

    /// <summary>The name a guest goes by here: yours when it's you.</summary>
    private string NameOf(GuestAccount account) => account.Self ? _state.UserName : account.Name;

    /// <summary>You on another computer change what an editor may.</summary>
    private static GuestRole RoleOf(GuestAccount account) => account.Self ? GuestRole.Editor : account.Role;

    /// <summary>The code waiting to be typed on that computer, while it's valid.</summary>
    public string? SelfCodeFor(GuestAccount account) =>
        _selfCodes.TryGetValue(account.Id, out var pending) && pending.Until > DateTime.Now ? pending.Code : null;

    /// <summary>"Type 123456 in iztok4090's ProJict (IZTOK-4090) to confirm it's you", for each code waiting.</summary>
    public IReadOnlyList<string> SelfCodeLines =>
        Data.Guests.Where(g => SelfCodeFor(g) != null).Select(g => $"Type {SelfCodeFor(g)} in the ProJict on {g.Device ?? g.Name} to confirm it's you " +
                                                                   $"(within {SelfCodeMinutes} minutes).").ToList();

    /// <summary>
    /// "This is me, on another computer": makes a code to type there, and asks it for the code at once. Null when it isn't
    /// connected now (or already is you).
    /// </summary>
    public string? StartSelf(GuestAccount account)
    {
        if (account.Self || account.Blocked || _sessions.FirstOrDefault(s => s.Account == account) is not { } session) return null;
        var code = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6");
        _selfCodes[account.Id] = new SelfCode(code, DateTime.Now.AddMinutes(SelfCodeMinutes));
        session.Send(Wire.Message("confirmSelf", ("host", _state.UserName), ("minutes", SelfCodeMinutes)));
        _state.StatusText = $"Type {code} in the ProJict on {account.Device ?? account.Name} to confirm it's you.";
        Changed?.Invoke();
        return code;
    }

    /// <summary>No code after all: the other computer stops asking.</summary>
    public void CancelSelf(GuestAccount account)
    {
        if (!_selfCodes.Remove(account.Id)) return;
        foreach (var session in _sessions.Where(s => s.Account == account))
            session.Send(SelfResult(false, $"{_state.UserName} cancelled confirming this computer.", done: true));
        Changed?.Invoke();
    }

    /// <summary>Not you any more: a guest again, under its own name (it reconnects as one).</summary>
    public void EndSelf(GuestAccount account)
    {
        if (!account.Self) return;
        account.Self = false;
        _store.Save();
        _state.StatusText = $"{account.Name} ({account.Device}) is a guest again, under its own name.";
        Disconnect(account);
        Changed?.Invoke();
    }

    /// <summary>The code typed on the other computer (or "it isn't me"): right, it's you from its next connection.</summary>
    private JsonObject ConfirmSelf(GuestSession session, JsonObject message)
    {
        var account = session.Account;
        if (!_selfCodes.TryGetValue(account.Id, out var pending) || pending.Until <= DateTime.Now)
        {
            _selfCodes.Remove(account.Id);
            Changed?.Invoke();
            return SelfResult(false, $"There's no code to type now, or it ran out: ask again in {_state.UserName}'s ProJict (Sharing).", done: true);
        }
        if ((bool?)message["decline"] == true)
        {
            _selfCodes.Remove(account.Id);
            _state.StatusText = $"The ProJict on {account.Device ?? account.Name} says it isn't you.";
            Changed?.Invoke();
            return SelfResult(false, "All right: it stays a guest.", done: true);
        }
        var typed = new string(((string?)message["code"] ?? "").Where(char.IsAsciiDigit).ToArray());
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(typed), Encoding.ASCII.GetBytes(pending.Code)))
        {
            if (++pending.Wrong < SelfCodeTries)
                return SelfResult(false, $"That isn't the code. {SelfCodeTries - pending.Wrong} tries left.", done: false);
            _selfCodes.Remove(account.Id);
            Changed?.Invoke();
            return SelfResult(false, "Wrong code too often: ask again in Sharing on the other computer.", done: true);
        }
        _selfCodes.Remove(account.Id);
        account.Self = true;
        _store.Save();
        _state.StatusText = $"The ProJict on {account.Device ?? account.Name} is you now: it takes part as {_state.UserName}.";
        Changed?.Invoke();
        // It comes back under your name: reconnecting gives it the new welcome (after this answer has gone out).
        _ = Task.Delay(TimeSpan.FromSeconds(1)).ContinueWith(_ => _dispatcher.BeginInvoke(() => Disconnect(account)), TaskScheduler.Default);
        return SelfResult(true, $"Confirmed: this computer is {_state.UserName} there. Reconnecting…", done: true);
    }

    private static JsonObject SelfResult(bool ok, string text, bool done) => Wire.Message("selfResult", ("ok", ok), ("message", text), ("done", done));
}
