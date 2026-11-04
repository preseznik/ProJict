using System.Text.Json.Nodes;
using ProJict.Core.Features;
using ProJict.Core.Model;
using ProJict.Core.Sharing;

namespace ProJict.App.Services.Sharing;

/// <summary>
/// Shipping from your own ProJict on another computer (#41f21359): a ProJict linked as you ("this is me") asks to ship
/// a project shared with it, approves or declines the plan, withdraws the request or closes its card, as you would
/// here, signed with your name. Other guests can't: only you ship your projects.
/// </summary>
public sealed partial class HostService
{
    private JsonObject? ShipOp(GuestSession session, JsonObject message)
    {
        var account = session.Account;
        var id = Guid.TryParse((string?)message["projectId"], out var g) ? g : Guid.Empty;
        JsonObject Refuse(string why) => Wire.Message("shipError", ("projectId", id.ToString()), ("message", why));
        if (!account.Self) return Refuse($"Only {_state.UserName} ships the projects here.");
        if (Visible(account).FirstOrDefault(p => p.Id == id) is not { } project) return Refuse("That project isn't shared with you any more.");
        var request = Guid.TryParse((string?)message["shipId"], out var shipId) ? project.Ships?.FirstOrDefault(s => s.Id == shipId) : null;
        var me = _state.UserName;
        var from = account.Device ?? account.Name;
        var board = _main.GetProjectViewModel(project);
        try
        {
            switch ((string?)message["op"])
            {
                case "request":
                    var steps = (message["steps"] as JsonArray ?? []).Select(n => Enum.TryParse<ShipStep>((string?)n, ignoreCase: true, out var s) && Enum.IsDefined(s) ? s : (ShipStep?)null)
                        .OfType<ShipStep>().ToList();
                    board.ChangeShip(() => ShipFlow.Request(project, steps, me));
                    _state.StatusText = $"Asked to ship {project.Name} from {from}.";
                    return null;
                case "approve" or "decline" when request == null || request != project.OpenShip:
                    return Refuse("That ship plan isn't waiting for you any more.");
                case "approve" or "decline":
                    var approve = (string?)message["op"] == "approve";
                    board.ChangeShip(() => ShipFlow.Decide(request!, approve, me));
                    _state.StatusText = approve ? $"Approved shipping {project.Name} from {from}." : $"Declined the plan for {project.Name} from {from}.";
                    return null;
                case "withdraw" when request is not { IsOpen: true }:
                    return Refuse("That ship request isn't open any more.");
                case "withdraw":
                    board.ChangeShip(() => ShipFlow.Withdraw(request!, me));
                    _state.StatusText = $"Withdrew the ship request for {project.Name} from {from}.";
                    return null;
                case "dismiss":
                    // Closing a finished request's card: it's closed here too.
                    if (request is { IsOpen: false, Dismissed: false }) board.ChangeShip(() => request.Dismissed = true);
                    return null;
                default:
                    return Refuse("Your ProJict asked for something about shipping this one doesn't know.");
            }
        }
        catch (InvalidOperationException ex)
        {
            return Refuse(ex.Message);
        }
    }
}
