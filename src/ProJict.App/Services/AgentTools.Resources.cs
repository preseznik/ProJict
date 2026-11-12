using System.Globalization;
using System.Text.Json.Nodes;
using ProJict.Core.Agents;
using ProJict.Core.Model;

namespace ProJict.App.Services;

/// <summary>
/// Shared things agents take turns with (#bb95b66d), such as "PC4080S game + GPU": claim_resource takes one for a while
/// (a lease the holder extends by claiming again), release_resource frees it, list_resources says who holds what. Those
/// who wait are told when it's free; a holder whose time ran out is asked to extend or release, and others may take it
/// over then. Nothing is freed in the middle of a long run just because the agent was quiet; a holder that disconnects
/// and doesn't come back within a couple of minutes lets it go. The user sees who holds what in a bar at the bottom.
/// </summary>
public sealed partial class AgentTools
{
    /// <summary>A shared thing was claimed, extended, released or freed (UI thread).</summary>
    public event Action? ResourcesChanged;

    private void AddResourceTools()
    {
        Add("claim_resource", "Claim a shared resource",
            "Takes a shared thing that only one agent should use at a time (a test PC, a GPU, a game install, a device), by name, for about how long you need " +
            "it; every agent in every project sees who has it. Claim it again to extend it. If another agent has it, or the user reserved it in ProJict " +
            "(maybe until they free it; they may also take it from you, and you're told to stop), you're told who, what for and until when, " +
            "and you're told when it's free (don't use it meanwhile). Release it with release_resource as soon as you're done. Use the same name others use: " +
            "list_resources shows them.",
            Schema(("name", "string", $"The shared thing, e.g. \"PC4080S game + GPU\" (up to {ResourceLocks.MaxName} characters).", true),
                ("for", "string", "What you use it for, in a few words, e.g. \"GRID2VR VR test\".", false),
                ("minutes", "integer", $"About how long you need it (default {ResourceLocks.DefaultMinutes}, at most {ResourceLocks.MaxMinutes}).", false)),
            ClaimResource);
        Add("release_resource", "Release a shared resource",
            "Frees a shared thing you claimed with claim_resource, so the next one can use it; those waiting for it are told.",
            Schema(("name", "string", "The shared thing.", true)), ReleaseResource);
        Add("list_resources", "List shared resources",
            "The shared things agents take turns with: who holds each, what for, until when, and who waits for it.",
            Schema(), ListResources, readOnly: true);
    }

    private string ClaimResource(McpSession session, Args args, List<Attachment> files)
    {
        RequireOwnName(session, "claim it");
        var name = args.Required("name");
        var purpose = args.String("for");
        if (ResourceLocks.Problem(name, purpose) is { } problem) throw new McpToolException(problem);
        var now = DateTime.Now;
        var (outcome, r, before) = ResourceLocks.Claim(_state.Settings, name, session.AgentName, purpose, args.Int("minutes") ?? ResourceLocks.DefaultMinutes, now);
        ResourcesTouched();
        if (outcome == ClaimOutcome.Busy)
            throw new McpToolException($"{r.HeldBy} has “{r.Name}”" + (r.Person ? " (reserved in ProJict)" : "") + (r.For is { } why ? $" for {why}" : "") +
                                       $" since {Clock(r.Since)}, {ResourceLocks.UntilPhrase(r, now, about: true)}. You're waiting for it " +
                                       $"({Ordinal(r.Waiting.FindIndex(w => string.Equals(w, session.AgentName, StringComparison.OrdinalIgnoreCase)) + 1)} in line): " +
                                       "you'll be told when it's free. Don't use it meanwhile; if it's urgent, ask " + r.HeldBy + " in Chat.", ToolProblem.Conflict);
        var json = new JsonObject
        {
            ["resource"] = r.Name,
            ["heldBy"] = r.HeldBy,
            ["for"] = r.For,
            ["until"] = Clock(r.Until),
            ["next"] = outcome == ClaimOutcome.Extended
                ? $"Extended: it's yours until about {Clock(r.Until)}."
                : $"It's yours until about {Clock(r.Until)}: claim it again to extend, and release_resource as soon as you're done. Others wait meanwhile.",
        };
        if (outcome == ClaimOutcome.TakenOver)
            json["note"] = $"{before}'s time on it ran out at {Clock(r.LastUntil ?? now)}, so you took it over; they're told.";
        if (r.Waiting.Count > 0) json["waiting"] = new JsonArray(r.Waiting.Select(w => (JsonNode)w).ToArray());
        return Json(json);
    }

    private string ReleaseResource(McpSession session, Args args, List<Attachment> files)
    {
        var name = args.Required("name");
        var now = DateTime.Now;
        if (ResourceLocks.Release(_state.Settings, name, session.AgentName, session.AgentName, now) is not { } r)
            throw new McpToolException(ResourceLocks.Find(_state.Settings, name) is { HeldBy: { } holder } held && ResourceLocks.IsHeld(held, now)
                ? $"You don't hold “{held.Name}”: {holder} does."
                : $"Nobody holds “{name.Trim()}”.", ToolProblem.Conflict);
        ResourcesTouched();
        return Json(new JsonObject
        {
            ["released"] = r.Name,
            ["told"] = r.Waiting.Count > 0 ? new JsonArray(r.Waiting.Select(w => (JsonNode)w).ToArray()) : null,
        });
    }

    private string ListResources(McpSession session, Args args, List<Attachment> files)
    {
        var now = DateTime.Now;
        var all = _state.Settings.SharedResources ?? [];
        return Json(new JsonObject
        {
            ["resources"] = new JsonArray(all.Select(r => (JsonNode)new JsonObject
            {
                ["name"] = r.Name,
                ["heldBy"] = ResourceLocks.IsHeld(r, now) ? r.HeldBy : null,
                ["reservedByUser"] = ResourceLocks.IsHeld(r, now) && r.Person ? true : null,
                ["for"] = ResourceLocks.IsHeld(r, now) ? r.For : null,
                ["since"] = ResourceLocks.IsHeld(r, now) ? Clock(r.Since) : null,
                ["until"] = !ResourceLocks.IsHeld(r, now) ? null : ResourceLocks.OpenEnded(r) ? "they free it" : ResourceLocks.UntilText(r, now),
                ["overdue"] = ResourceLocks.IsOverdue(r, now) ? true : null,
                ["free"] = ResourceLocks.IsHeld(r, now) ? null : true,
                ["waiting"] = r.Waiting.Count > 0 ? new JsonArray(r.Waiting.Select(w => (JsonNode)w).ToArray()) : null,
            }).ToArray()),
            ["note"] = all.Count == 0 ? "No shared things yet: claim_resource names one the first time it's claimed." : null,
        });
    }

    /// <summary>The user frees a shared thing from the bar; its holder is told (unless it was the user's own).</summary>
    public void FreeResource(string name)
    {
        if (ResourceLocks.Release(_state.Settings, name, null, _state.UserName, DateTime.Now) is { } r)
        {
            ResourcesTouched();
            var waiting = r.Waiting.Count > 0 ? $" {string.Join(", ", r.Waiting)} {(r.Waiting.Count == 1 ? "is" : "are")} told it's free." : "";
            _state.StatusText = string.Equals(r.LastHolder, _state.UserName, StringComparison.OrdinalIgnoreCase)
                ? $"Freed “{r.Name}”.{waiting}"
                : $"Freed “{r.Name}”: {r.LastHolder} is told.{waiting}";
        }
    }

    /// <summary>
    /// The user reserves a shared thing themselves (#6f04e17e), for <paramref name="minutes"/> or until they free it (null):
    /// agents wait for it, and one using it now is told to stop (and waits for it). Null when it went, else why not.
    /// </summary>
    public string? ReserveResource(string name, int? minutes)
    {
        if (ResourceLocks.Problem(name, null) is { } problem) return problem;
        var now = DateTime.Now;
        var (outcome, r, before) = ResourceLocks.Reserve(_state.Settings, name, _state.UserName, null, minutes, now);
        ResourcesTouched();
        _state.StatusText = $"“{r.Name}” is yours {(ResourceLocks.OpenEnded(r) ? "until you free it" : "until " + Clock(r.Until))}" +
                            (outcome == ClaimOutcome.TakenOver ? $": {before} is told to stop using it, and waits for it." : ": agents wait for it.");
        return null;
    }

    private void ResourcesTouched()
    {
        _state.MarkSettingsDirty();
        ResourcesChanged?.Invoke();
    }

    /// <summary>(UI thread.) The agent's last session ended while ProJict runs: what it holds goes after the grace time unless it's back.</summary>
    private void ResourceHolderLeft(string agent)
    {
        lock (_live)
            if (_live.Any(s => string.Equals(s.AgentName, agent, StringComparison.OrdinalIgnoreCase))) return;
        if (_state.Settings.SharedResources?.Any(r => string.Equals(r.HeldBy, agent, StringComparison.OrdinalIgnoreCase)) != true) return;
        ResourceLocks.Left(_state.Settings, agent, DateTime.Now);
        ResourcesTouched();
    }

    /// <summary>(UI thread.) The agent is here under its name: what it held is still its own (or went, if it was gone too long).</summary>
    private void ResourceHolderBack(McpSession session)
    {
        if (HasOwnName(session) && ResourceLocks.Back(_state.Settings, session.AgentName, DateTime.Now)) ResourcesTouched();
    }

    /// <summary>
    /// What the agent hears about shared things, each once: its time ran out, someone took it over or the user freed it,
    /// and what it waits for is free (or overdue, so it may take it over).
    /// </summary>
    private IEnumerable<(string Key, string Line)> ResourceLines(McpSession session)
    {
        var me = session.AgentName;
        var now = DateTime.Now;
        bool Is(string? name) => string.Equals(name, me, StringComparison.OrdinalIgnoreCase);
        foreach (var r in (_state.Settings.SharedResources ?? []).ToList())
        {
            if (Is(r.HeldBy) && ResourceLocks.IsOverdue(r, now))
                yield return ($"resource-overdue:{r.Name}:{r.Until.Ticks}",
                    $"Your time on “{r.Name}” ran out at {Clock(r.Until)}: claim_resource it again to keep it, or release_resource it if you're done. " +
                    "Others may take it over now.");
            if (r.TakenOver && Is(r.LastHolder) && r.HeldBy != null && r.Person)
                yield return ($"resource-taken:{r.Name}:{r.Since.Ticks}",
                    $"{r.HeldBy} reserved “{r.Name}” for themselves at {Clock(r.Since)}, {ResourceLocks.UntilPhrase(r, now)}: stop using it now (finish " +
                    "or stop what runs on it). You're waiting for it and will be told when it's free.");
            else if (r.TakenOver && Is(r.LastHolder) && r.HeldBy != null && r.LastUntil is { } until)
                yield return ($"resource-taken:{r.Name}:{r.Since.Ticks}",
                    $"{r.HeldBy} took over “{r.Name}” at {Clock(r.Since)}: your time on it had run out at {Clock(until)}. Don't use it any more; claim it again to wait for it.");
            if (!r.TakenOver && Is(r.LastHolder) && r.FreedAt is { } freed && r.FreedBy is { } freer && !Is(freer))
                yield return ($"resource-freed:{r.Name}:{freed.Ticks}",
                    $"{freer} freed “{r.Name}”, which you held, at {Clock(freed)}. Don't use it any more; claim it again if you still need it.");
            if (!r.Waiting.Any(w => Is(w)) || Is(r.HeldBy)) continue;
            if (!ResourceLocks.IsHeld(r, now))
            {
                var at = r.FreedAt ?? (r.HolderLeftAt is { } left ? left + ResourceLocks.Grace : now);
                var who = r.HeldBy != null ? $"{r.HeldBy} left" : r.FreedBy != null && string.Equals(r.FreedBy, r.LastHolder, StringComparison.OrdinalIgnoreCase)
                    ? $"{r.FreedBy} released it" : $"{r.FreedBy} freed it";
                yield return ($"resource-free:{r.Name}:{at.Ticks}", $"“{r.Name}”, which you wait for, is free now ({who}): take it with claim_resource.");
            }
            else if (ResourceLocks.IsOverdue(r, now))
                yield return ($"resource-overdue-wait:{r.Name}:{r.Until.Ticks}",
                    $"{r.HeldBy}'s time on “{r.Name}”, which you wait for, ran out at {Clock(r.Until)}: you may take it over with claim_resource.");
        }
    }

    private static string Clock(DateTime at) => at.ToString(at.Date == DateTime.Today ? "HH:mm" : "d MMM HH:mm", CultureInfo.InvariantCulture);

    private static string Ordinal(int n) => n switch { 1 => "first", 2 => "second", 3 => "third", _ => $"{n}th" };
}
