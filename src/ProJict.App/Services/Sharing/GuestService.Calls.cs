using System.Text.Json.Nodes;

namespace ProJict.App.Services.Sharing;

/// <summary>Calls with people (#2356ddca), from a guest's side: the host's call messages, and how to reach the host's owner to call them.</summary>
public sealed partial class GuestService
{
    /// <summary>The way to the host's owner, while connected.</summary>
    internal PersonLink? LinkTo(HostConnection connection) =>
        connection.State == ConnectionState.Connected ? new PersonLink(connection.HostName, connection.Send, OnHost: false) : null;

    /// <summary>callRing, callAnswer, callHangUp or callAudio from the host.</summary>
    private void HandleCall(HostConnection connection, JsonObject message) =>
        _main.Calls.OnPersonMessage(new PersonLink(connection.HostName, connection.Send, OnHost: false), message);
}
