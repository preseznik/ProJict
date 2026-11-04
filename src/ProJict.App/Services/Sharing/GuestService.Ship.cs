using System.Text.Json.Nodes;
using System.Windows;
using ProJict.Core.Features;
using ProJict.Core.Model;
using ProJict.Core.Sharing;

namespace ProJict.App.Services.Sharing;

/// <summary>
/// Shipping a project shared by your own ProJict on another computer ("this is me", #41f21359): asking to ship it,
/// approving or declining the plan, withdrawing the request and closing its card go to that ProJict, which does it as
/// you would there. The request then comes back with the project. Guests of someone else can't.
/// </summary>
public sealed partial class GuestService
{
    /// <summary>How long the host has to answer before it's taken not to know how (a ProJict before 0.1.43).</summary>
    private static readonly TimeSpan ShipAnswer = TimeSpan.FromSeconds(20);

    /// <summary>Projects whose ship request was changed here and hasn't come back yet.</summary>
    private readonly Dictionary<Guid, object> _shipAsked = [];

    /// <summary>
    /// Asks the host to change the project's ship request: op is request (with <paramref name="steps"/>), approve,
    /// decline, withdraw or dismiss (the <paramref name="request"/> it's about). The problem if it can't, or null.
    /// </summary>
    public string? Ship(Project project, string op, ShipRequest? request = null, IEnumerable<ShipStep>? steps = null)
    {
        if (project.Remote is not { } remote) return "It isn't shared from another ProJict.";
        if (!remote.Self) return $"Only {remote.HostName} ships {project.Name}, in their ProJict.";
        var connection = Connections.FirstOrDefault(c => c.Address == remote.HostKey && c.State == ConnectionState.Connected);
        if (connection == null) return $"Not connected to {remote.HostName}'s ProJict now: you can ship from here once it reconnects.";
        var message = Wire.Message("shipOp", ("projectId", project.Id.ToString()), ("op", op), ("shipId", request?.Id.ToString()));
        if (steps != null) message["steps"] = new JsonArray(steps.Select(s => (JsonNode)ShipFlow.Name(s)).ToArray());
        connection.Send(message);
        // Closing a card that's closed there already changes nothing, so nothing comes back.
        if (op == "dismiss") return null;
        var asked = new object();
        _shipAsked[project.Id] = asked;
        _ = Task.Delay(ShipAnswer).ContinueWith(_ => Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            // Neither the project nor a refusal came back: a host before 0.1.43 doesn't know the question.
            if (!_shipAsked.TryGetValue(project.Id, out var now) || now != asked) return;
            _shipAsked.Remove(project.Id);
            _state.StatusText = $"{remote.HostName}'s ProJict didn't answer about shipping {project.Name}: it may need ProJict 0.1.43 or newer.";
        }));
        return null;
    }

    /// <summary>The host refused a ship change from here: why.</summary>
    private void ShipFailed(HostConnection connection, JsonObject message)
    {
        if (Guid.TryParse((string?)message["projectId"], out var id)) _shipAsked.Remove(id);
        _state.StatusText = $"{connection.HostName}'s ProJict: {(string?)message["message"] ?? "it couldn't change the ship request."}";
    }
}
