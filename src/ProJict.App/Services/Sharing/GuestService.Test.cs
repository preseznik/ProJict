using System.Text.Json.Nodes;
using ProJict.Core.Model;
using ProJict.Core.Sharing;

namespace ProJict.App.Services.Sharing;

/// <summary>
/// Answering a test (#09a108b5) in a project shared by your own ProJict on another computer ("this is me"): it goes to
/// that ProJict, which answers it as you would there; the test then comes back answered with the project. A note goes
/// with it, files don't (add them in that ProJict).
/// </summary>
public sealed partial class GuestService
{
    /// <summary>Asks the host to answer the test: works or doesn't, with a note. The problem if it can't, or null.</summary>
    public string? Test(Project project, TestRequest test, bool works, string? note)
    {
        if (project.Remote is not { } remote) return "It isn't shared from another ProJict.";
        if (!remote.Self) return $"Only {remote.HostName} answers what agents ask to test in {project.Name}, in their ProJict.";
        var connection = Connections.FirstOrDefault(c => c.Address == remote.HostKey && c.State == ConnectionState.Connected);
        if (connection == null) return $"Not connected to {remote.HostName}'s ProJict now: you can answer once it reconnects.";
        connection.Send(Wire.Message("testOp", ("projectId", project.Id.ToString()), ("testId", test.Id.ToString()), ("op", works ? "works" : "doesntWork"),
            ("note", string.IsNullOrWhiteSpace(note) ? null : note.Trim())));
        return null;
    }

    /// <summary>The host refused an answer from here: why.</summary>
    private void TestFailed(HostConnection connection, JsonObject message) =>
        _state.StatusText = $"{connection.HostName}'s ProJict: {(string?)message["message"] ?? "it couldn't take the answer."}";
}
