using System.Globalization;
using System.Text.Json.Nodes;
using ProJict.Core.Agents;
using ProJict.Core.Features;
using ProJict.Core.Model;

namespace ProJict.App.Services;

/// <summary>
/// Asking people to test by hand (#09a108b5): an agent asks on an item it has checked out or finished (ask_to_test, or
/// complete_item's test), the user answers in the project's To test tab (works, or doesn't with a note and files), and
/// the agent hears it like an approval. One settled elsewhere (in Chat, say) the agent closes with close_test. See TestFlow.
/// </summary>
public sealed partial class AgentTools
{
    private void AddTestTools()
    {
        Add("ask_to_test", "Ask the user to test",
            "Asks the user to try something by hand, on an item you have checked out or finished: short steps and what should happen, with files " +
            "if they help (a build, a screenshot of how it should look). It shows in the project's To test tab; their answer (it works, or it " +
            "doesn't with a note and files) comes back to you like an approval. If it doesn't work on finished work waiting for review, the item " +
            "comes back to you as not accepted yet. complete_item's test does the same as you finish.",
            Schema(("item", "string", "The item's id (one you have checked out or finished).", true),
                ("steps", "string", "What to do, in a few short steps a person can follow.", true),
                ("expected", "string", "What should happen.", false),
                ("files", "string[]", "Files that help (absolute or relative to your working directory).", false)),
            AskToTest, filesArgument: "files");
        Add("list_tests", "List tests",
            "The project's tests: the ones waiting for the user, and your recent ones with their answers (the user's note and files).",
            Schema(ProjectArg), ListTests, readOnly: true);
        Add("close_test", "Close a test",
            "Closes a test that was settled elsewhere (the user confirmed it in Chat, say), saying where; only yours, or one on an item you have " +
            "checked out. It shows as closed by you, with your line.",
            Schema(("test", "string", "The test's id (ask_to_test or list_tests gives it).", true),
                ("note", "string", "Where and how it was settled, e.g. iztok confirmed it works in #projict.", true), ProjectArg),
            CloseTest);
    }

    private string AskToTest(McpSession session, Args args, List<Attachment> files)
    {
        var (project, loc) = FindItem(session, args.Required("item"));
        var test = AskTest(session, project, loc.Item, args.Required("steps"), args.String("expected"), files);
        return Json(new JsonObject
        {
            ["test"] = ShortId(test.Id),
            ["item"] = ShortId(loc.Item.Id),
            ["files"] = files.Count,
            ["next"] = "It waits in the project's To test tab. The user's answer comes to you like an approval (your chat watcher, or waitingForYou); " +
                       "if it's settled elsewhere, close it with close_test.",
        });
    }

    /// <summary>Asks for a test on an item the agent has checked out or finished (also from complete_item).</summary>
    private TestRequest AskTest(McpSession session, Project project, TodoItem item, string steps, string? expected, List<Attachment> files)
    {
        // The user's own tests: an agent working in a project shared with this ProJict asks its host's user there.
        MayChange(project, Change.HostOnly);
        var agent = SignAs(session, project);
        var claimed = item.ClaimedBy is { } by && OwnIn(session, project, by);
        var finished = (item.Activity ?? []).Any(a => a.Agent && OwnIn(session, project, a.By) && a.Text.StartsWith("Done:", StringComparison.Ordinal));
        if (!claimed && !finished)
            throw new McpToolException($"Ask for tests on items you're working on (claim_item) or have finished; “{Short(item.Text)}” is neither.", ToolProblem.Forbidden);
        TestRequest? test = null;
        ChangeTests(project, () => test = TestFlow.Ask(project, item.Id, steps, expected, files, agent));
        _state.StatusText = $"{agent} asks you to test “{Short(item.Text)}” ({project.Name}): the To test tab.";
        return test!;
    }

    private string ListTests(McpSession session, Args args, List<Attachment> files)
    {
        var project = ProjectFor(session, args);
        bool Mine(TestRequest t) => OwnIn(session, project, t.AskedBy);
        return Json(new JsonObject
        {
            ["project"] = project.Name,
            ["waiting"] = new JsonArray(TestFlow.Open(project).Select(t => (JsonNode)TestJson(project, t)).ToArray()),
            ["yoursAnswered"] = new JsonArray(TestFlow.Done(project).Where(Mine).Take(10).Select(t => (JsonNode)TestJson(project, t)).ToArray()),
        });
    }

    private string CloseTest(McpSession session, Args args, List<Attachment> files)
    {
        var id = args.Required("test");
        var project = args.String("project") != null ? ProjectFor(session, args)
            : LocalProjects.FirstOrDefault(p => TestFlow.Find(p, id) != null) ?? throw new McpToolException($"No test {id}: list_tests shows them.", ToolProblem.NotFound);
        MayChange(project, Change.HostOnly);
        var test = TestFlow.Find(project, id) ?? throw new McpToolException($"{project.Name} has no test {id}: list_tests shows them.", ToolProblem.NotFound);
        var item = project.Find(test.ItemId)?.Item;
        if (!OwnIn(session, project, test.AskedBy) && !(item?.ClaimedBy is { } by && OwnIn(session, project, by)))
            throw new McpToolException($"That's {test.AskedBy}'s test: close your own, or one on an item you have checked out.", ToolProblem.Forbidden);
        ChangeTests(project, () => TestFlow.Close(project, test, args.Required("note"), SignAs(session, project)));
        return Json(new JsonObject { ["test"] = ShortId(test.Id), ["state"] = "closed" });
    }

    private JsonObject TestJson(Project project, TestRequest test)
    {
        var json = new JsonObject
        {
            ["test"] = ShortId(test.Id),
            ["item"] = project.Find(test.ItemId) is { } loc ? new JsonObject { ["id"] = ShortId(test.ItemId), ["text"] = Short(loc.Item.Text) } : null,
            ["askedBy"] = test.AskedBy,
            ["askedAt"] = test.AskedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            ["steps"] = test.Steps,
            ["state"] = test.State switch { TestState.Works => "works", TestState.DoesntWork => "doesnt_work", TestState.Closed => "closed", _ => "waiting" },
        };
        if (test.Expected != null) json["expected"] = test.Expected;
        if (test.Files is { Count: > 0 }) json["files"] = Paths(test.Files, project);
        if (test.AnsweredBy != null) json["answeredBy"] = test.AnsweredBy;
        if (test.AnsweredAt is { } at) json["answeredAt"] = at.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        if (test.Note != null) json["note"] = test.Note;
        if (test.AnswerFiles is { Count: > 0 }) json["answerFiles"] = Paths(test.AnswerFiles, project);
        return json;
    }

    private void ChangeTests(Project project, Action change)
    {
        try { _main.GetProjectViewModel(project).ChangeTests(change); }
        catch (InvalidOperationException ex) { throw new McpToolException(ex.Message, ToolProblem.Conflict); }
    }

    /// <summary>For waitingForYou and the watcher: the user's answers to the agent's tests, as "[approval]"-like news.</summary>
    private IEnumerable<(string Key, string Line)> TestLines(McpSession session, Project project)
    {
        foreach (var test in TestFlow.Done(project).Where(t => !t.AnsweredByAgent && AgentNaming.IsOwnWork(session, t.AskedBy)))
        {
            if (DateTime.Now - (test.AnsweredAt ?? test.AskedAt) > CommentsKeptFor) continue;
            var what = project.Find(test.ItemId) is { } loc ? $"“{Short(loc.Item.DisplayText())}” (#{ShortId(test.ItemId)}) in {project.Name}" : $"an item in {project.Name}";
            var files = test.AnswerFiles is { Count: > 0 } answer ? $" with {Count(answer.Count, "file")}" : "";
            var note = test.Note != null ? $": “{Short(test.Note)}”" : "";
            yield return ($"test:{test.Id}:{test.State}",
                test.State == TestState.Works
                    ? $"{test.AnsweredBy} tested {what}: it works{note}. (test {ShortId(test.Id)})"
                    : $"{test.AnsweredBy} tested {what}: it doesn't work{note}{files}. list_tests project={project.Name} shows it all (test {ShortId(test.Id)}); " +
                      "if it was finished work, it's back with you as not accepted yet.");
        }
    }
}
