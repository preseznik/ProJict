using System.Text.Json.Nodes;
using ProJict.Core.Features;
using ProJict.Core.Model;
using ProJict.Core.Sharing;

namespace ProJict.App.Services.Sharing;

/// <summary>
/// Answering what agents ask you to test (#09a108b5) from your own ProJict on another computer ("this is me"): works
/// or doesn't work, with a note, as you would here, signed with your name. Other guests can't: the tests are yours.
/// </summary>
public sealed partial class HostService
{
    private JsonObject? TestOp(GuestSession session, JsonObject message)
    {
        var account = session.Account;
        var id = Guid.TryParse((string?)message["projectId"], out var g) ? g : Guid.Empty;
        JsonObject Refuse(string why) => Wire.Message("testError", ("projectId", id.ToString()), ("message", why));
        if (!account.Self) return Refuse($"Only {_state.UserName} answers what agents ask to test here.");
        if (Visible(account).FirstOrDefault(p => p.Id == id) is not { } project) return Refuse("That project isn't shared with you any more.");
        if (!Guid.TryParse((string?)message["testId"], out var testId) || project.Tests?.FirstOrDefault(t => t.Id == testId) is not { } test)
            return Refuse("That test isn't there any more.");
        if ((string?)message["op"] is not ("works" or "doesntWork") || !test.IsOpen)
            return Refuse(test.IsOpen ? "Your ProJict asked for something about tests this one doesn't know." : "That test was answered already.");
        var works = (string?)message["op"] == "works";
        var note = (string?)message["note"];
        var board = _main.GetProjectViewModel(project);
        var sendBack = !works && project.Find(test.ItemId)?.Item is { NeedsReview: true, IsDone: true };
        try
        {
            board.ChangeTests(() => TestFlow.Answer(project, test, works, note, null, _state.UserName));
        }
        catch (InvalidOperationException ex)
        {
            return Refuse(ex.Message);
        }
        if (sendBack) board.SendBackAfterTest(test.ItemId, note);
        _state.StatusText = $"Told {test.AskedBy} it {(works ? "works" : "doesn't work")}, from {account.Device ?? account.Name}.";
        return null;
    }
}
