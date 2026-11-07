using System.Globalization;
using System.Text.Json.Nodes;
using ProJict.Core.Agents;
using ProJict.Core.Features;
using ProJict.Core.Model;

namespace ProJict.Core.Api;

/// <summary>
/// Phone-token To test contract v1. Only metadata leaves the PC; actual answers
/// are applied through the same project-view-model operation as a PC click.
/// No agent/MCP session, attachment path, download URL or provider is involved.
/// </summary>
public static class PhoneTestsApi
{
    public const int Version = 1;

    public static JsonObject Pending(IEnumerable<Project> projects, ApiToken? token, string? projectId = null)
    {
        var scope = Scope(projects, token, write: false);
        Project? selected = string.IsNullOrWhiteSpace(projectId) ? null : FindProject(scope, projectId);
        var waiting = scope.Where(p => selected == null || p == selected)
            .SelectMany(p => TestFlow.Open(p).Select(t => (Project: p, Test: t)))
            .OrderBy(x => x.Test.AskedAt).ThenBy(x => x.Project.Id).ThenBy(x => x.Test.Id);
        return new JsonObject
        {
            ["apiVersion"] = Version,
            ["project"] = selected?.Id.ToString(),
            ["projects"] = new JsonArray(scope.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(p => (JsonNode)new JsonObject { ["id"] = p.Id.ToString(), ["name"] = p.Name }).ToArray()),
            ["tests"] = new JsonArray(waiting.Select(x => (JsonNode)Describe(x.Project, x.Test, !token!.ReadOnly)).ToArray()),
        };
    }

    public static JsonObject Detail(IEnumerable<Project> projects, ApiToken? token, string projectId, string testId)
    {
        var (project, test) = Find(Scope(projects, token, write: false), projectId, testId);
        return new JsonObject { ["apiVersion"] = Version, ["test"] = Describe(project, test, !token!.ReadOnly) };
    }

    /// <summary>The callback is internal application policy, never client-provided code.</summary>
    public static JsonObject Answer(IEnumerable<Project> projects, ApiToken? token, string projectId, string testId,
        JsonObject body, Action<Project, TestRequest, bool, string?> apply)
    {
        var scope = Scope(projects, token, write: true);
        if (body.Any(p => p.Key is not ("works" or "note")))
            throw new McpToolException("Give only works and an optional note; file uploads are not supported by this phone endpoint.");
        if (body["works"] is not JsonValue value || !value.TryGetValue<bool>(out var works))
            throw new McpToolException("Say \"works\": true or false.");
        string? note = null;
        if (body["note"] != null)
        {
            if (body["note"] is not JsonValue text || !text.TryGetValue<string>(out note))
                throw new McpToolException("The note must be text.");
            note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
            if (note?.Length > TestFlow.MaxNote)
                throw new McpToolException($"Keep the note shorter (at most {TestFlow.MaxNote} characters).");
        }
        var (project, test) = Find(scope, projectId, testId);
        if (!test.IsOpen) throw new McpToolException("That test was already answered or closed. Refresh To test.", ToolProblem.Conflict);
        var reviewedItem = project.Find(test.ItemId)?.Item;
        var wasReview = reviewedItem is { IsDone: true, NeedsReview: true };
        try { apply(project, test, works, note); }
        catch (InvalidOperationException) { throw new McpToolException("That test changed. Refresh To test before trying again.", ToolProblem.Conflict); }
        if (test.State != (works ? TestState.Works : TestState.DoesntWork))
            throw new McpToolException("The test result was not recorded. Refresh To test before trying again.", ToolProblem.Conflict);
        return new JsonObject
        {
            ["apiVersion"] = Version,
            ["test"] = Describe(project, test, canWrite: true),
            ["sentBack"] = !works && wasReview && reviewedItem is { IsDone: false, NeedsReview: false },
        };
    }

    private static List<Project> Scope(IEnumerable<Project> projects, ApiToken? token, bool write)
    {
        if (token is not { ActsAsYou: true }) throw new McpToolException("Only a phone token reaches To test.", ToolProblem.Forbidden);
        if (token.Frozen) throw new McpToolException("This phone token is paused.", ToolProblem.Forbidden);
        // Normal expiry/revocation is rejected by the API gate before dispatch.
        // Recheck expiry and mutable access here in case a queued request outlives it.
        if (token.IsExpired(DateTime.Now)) throw new McpToolException("This phone token expired. Pair again.", ToolProblem.Forbidden);
        if (write && token.ReadOnly) throw new McpToolException("This phone token is read only.", ToolProblem.Forbidden);
        return projects.Where(p => p.Remote == null && !p.IsInbox &&
            (token.Projects == null || token.Projects.Contains(p.Id))).ToList();
    }

    private static Guid Id(string value, string kind)
    {
        if (Guid.TryParseExact(value, "D", out var id) || Guid.TryParseExact(value, "N", out id)) return id;
        throw new McpToolException($"Use the full {kind} ID from the phone API.");
    }

    private static Project FindProject(List<Project> scope, string id)
    {
        var key = Id(id, "project");
        return scope.FirstOrDefault(p => p.Id == key)
            ?? throw new McpToolException("That project is not available to this phone token.", ToolProblem.NotFound);
    }

    private static (Project Project, TestRequest Test) Find(List<Project> scope, string projectId, string testId)
    {
        var project = FindProject(scope, projectId);
        var key = Id(testId, "test");
        var test = (project.Tests ?? []).FirstOrDefault(t => t.Id == key)
            ?? throw new McpToolException("That test is not available in this project.", ToolProblem.NotFound);
        return (project, test);
    }

    private static JsonObject Describe(Project project, TestRequest test, bool canWrite) => new()
    {
        ["id"] = test.Id.ToString(),
        ["projectId"] = project.Id.ToString(),
        ["projectName"] = project.Name,
        ["itemId"] = test.ItemId.ToString(),
        ["itemText"] = project.Find(test.ItemId)?.Item.DisplayText() ?? "(the item was deleted)",
        ["steps"] = test.Steps,
        ["expected"] = test.Expected,
        ["askedBy"] = test.AskedBy,
        ["askedAt"] = test.AskedAt.ToString("O", CultureInfo.InvariantCulture),
        ["state"] = test.State switch { TestState.Works => "works", TestState.DoesntWork => "doesnt_work", TestState.Closed => "closed", _ => "waiting" },
        ["canAnswer"] = canWrite && test.IsOpen,
        ["files"] = new JsonArray((test.Files ?? []).Select(a => (JsonNode)new JsonObject
        {
            ["id"] = a.Id.ToString(), ["name"] = FileName(a.FileName), ["size"] = Math.Max(0, a.Size),
        }).ToArray()),
        ["answeredBy"] = test.AnsweredBy,
        ["answeredAt"] = test.AnsweredAt?.ToString("O", CultureInfo.InvariantCulture),
        ["note"] = test.Note,
    };

    private static string FileName(string? value)
    {
        var name = (value ?? "").Replace('\\', '/').Split('/').Last();
        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return name.Length == 0 || name is "." or ".." ? "attachment" : name;
    }
}
