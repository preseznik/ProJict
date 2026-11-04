using System.Text.Json.Nodes;

namespace ProJict.App.Services.Sharing;

/// <summary>Calls with people (#2356ddca), from the host's side: a guest's call messages, and how to reach a guest to call it.</summary>
public sealed partial class HostService
{
    /// <summary>The way to a guest connected now, by the name it goes by in your Chat; null while it isn't (or it's you, from another computer).</summary>
    internal PersonLink? LinkTo(string person)
    {
        var session = _sessions.FirstOrDefault(s => !s.Account.Self && string.Equals(NameOf(s.Account), person, StringComparison.OrdinalIgnoreCase));
        return session == null ? null : new PersonLink(NameOf(session.Account), session.Send, OnHost: true);
    }

    /// <summary>callRing, callAnswer, callHangUp or callAudio from a guest.</summary>
    private void HandleCall(GuestSession session, JsonObject message) =>
        _main.Calls.OnPersonMessage(new PersonLink(NameOf(session.Account), session.Send, OnHost: true), message);
}
