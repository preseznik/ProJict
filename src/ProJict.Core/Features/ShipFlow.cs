using ProJict.Core.Model;

namespace ProJict.Core.Features;

/// <summary>
/// The ship request's rules. You ask for steps; an AI agent posts a plan; you approve or decline it; only an approved
/// request is run, by the agent reporting each step and then the result. Push and release always need your approval;
/// commit, build and package run without it only if the project allows that. Agents can't ask or approve. Every
/// change is signed in the request's log. Breaking a rule throws InvalidOperationException with the reason.
/// </summary>
public static class ShipFlow
{
    /// <summary>Requests kept per project (the oldest finished ones go).</summary>
    public const int Kept = 20;

    public static ShipSettings SettingsOf(Project project) => project.Shipping ?? new ShipSettings();

    public static bool Publishes(ShipStep step) => step is ShipStep.Push or ShipStep.Release;

    public static bool NeedsApproval(IEnumerable<ShipStep> steps, ShipSettings settings) =>
        !settings.LocalWithoutApproval || settings.ChangedBy != null || steps.Any(Publishes);

    /// <summary>
    /// An AI agent changes the steps the project has and/or how it's shipped. Not while a request is open, and until you
    /// check the change every request needs your approval. "Without approval" stays yours to set.
    /// </summary>
    public static void AgentChange(Project project, IReadOnlyCollection<ShipStep>? steps, string? howTo, string by, DateTime now)
    {
        CheckAgentChange(project, steps);
        var settings = project.Shipping ??= new ShipSettings();
        if (steps != null) settings.Steps = steps.Distinct().Order().ToList();
        if (howTo != null) settings.HowTo = howTo.Trim().Length == 0 ? null : howTo;
        settings.ChangedBy = by;
        settings.ChangedAt = now;
    }

    /// <summary>Throws when an agent may not change how the project is shipped right now.</summary>
    public static void CheckAgentChange(Project project, IReadOnlyCollection<ShipStep>? steps)
    {
        if (project.IsInbox) throw new InvalidOperationException("The Inbox isn't shipped.");
        if (project.OpenShip is { } open)
            throw new InvalidOperationException($"A ship request is open ({Describe(open.Steps)}, {StateName(open.State)}): change how {project.Name} is shipped once it's finished.");
        if (steps is { Count: 0 }) throw new InvalidOperationException("Give at least one step.");
    }

    /// <summary>You checked what an agent changed.</summary>
    public static void AcceptChange(ShipSettings settings)
    {
        settings.ChangedBy = null;
        settings.ChangedAt = null;
    }

    public static string Name(ShipStep step) => step.ToString().ToLowerInvariant();

    /// <summary>"commit, build and release".</summary>
    public static string Describe(IEnumerable<ShipStep> steps)
    {
        var names = steps.Order().Select(Name).ToList();
        return names.Count switch
        {
            0 => "nothing",
            1 => names[0],
            _ => string.Join(", ", names.SkipLast(1)) + " and " + names[^1],
        };
    }

    /// <summary>
    /// The project's ship plan waits for your approval: your own project's, or one shared by your own ProJict on another
    /// computer. A guest's copy of someone else's project shows their plan, but it isn't for the guest to approve.
    /// </summary>
    public static bool WaitsForYou(Project project) => project.OpenShip?.State == ShipState.Planned && project.Remote is null or { Self: true };

    /// <summary>A new request for these steps (in the project's step order); fails while another one is open.</summary>
    public static ShipRequest Request(Project project, IEnumerable<ShipStep> steps, string by)
    {
        if (project.OpenShip is { } open) throw new InvalidOperationException($"There's already a ship request ({Describe(open.Steps)}); withdraw it first.");
        var settings = SettingsOf(project);
        var chosen = steps.Distinct().Order().ToList();
        if (chosen.Count == 0) throw new InvalidOperationException("Choose at least one step.");
        var missing = chosen.Where(s => !settings.Steps.Contains(s)).ToList();
        if (missing.Count > 0) throw new InvalidOperationException($"{project.Name} doesn't ship with {Describe(missing)} (Details › Shipping).");
        var auto = !NeedsApproval(chosen, settings);
        var request = new ShipRequest { Steps = chosen, RequestedBy = by, State = auto ? ShipState.Approved : ShipState.Requested };
        Log(request, by, false, $"Asked to ship it: {Describe(chosen)}{(auto ? " (runs without approval)" : "")}");
        var ships = project.Ships ??= [];
        ships.Add(request);
        // Old finished requests go; open ones (there's at most this one) always stay.
        while (ships.Count > Kept && ships.FindIndex(s => !s.IsOpen) is >= 0 and var oldest) ships.RemoveAt(oldest);
        return request;
    }

    /// <summary>An agent's plan. It can be replaced until you decide; a request that runs without approval just records it.</summary>
    public static void Plan(ShipRequest request, string plan, string? version, string agent)
    {
        if (string.IsNullOrWhiteSpace(plan)) throw new InvalidOperationException("The plan can't be empty.");
        // Approved without anyone deciding: it runs without approval, and a plan is just for the record.
        var runsWithoutApproval = request.State == ShipState.Approved && request.DecidedBy == null;
        if (request.State is not (ShipState.Requested or ShipState.Planned) && !runsWithoutApproval)
            throw new InvalidOperationException(request.State switch
            {
                ShipState.Approved => "The plan is approved already; run it.",
                ShipState.Running => "It's running already.",
                _ => $"The request is {StateName(request.State)}.",
            });
        request.Plan = plan.Trim();
        request.Version = string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        request.PlannedBy = agent;
        if (request.State != ShipState.Approved) request.State = ShipState.Planned;
        Log(request, agent, true, request.Version is { } v ? $"Planned it: version {v}" : "Planned it.");
    }

    /// <summary>Your answer to a plan. Only a person can give it, and only to a plan that waits for one.</summary>
    public static void Decide(ShipRequest request, bool approve, string by)
    {
        if (request.State != ShipState.Planned) throw new InvalidOperationException(request.State == ShipState.Requested
            ? "There's no plan to approve yet." : $"The request is {StateName(request.State)}.");
        request.State = approve ? ShipState.Approved : ShipState.Declined;
        request.DecidedBy = by;
        if (!approve) request.FinishedAt = DateTime.Now;
        Log(request, by, false, approve ? "Approved the plan." : "Declined the plan.");
    }

    public enum Outcome { Done, Failed, Skipped }

    /// <summary>One step's result, by the agent running an approved request.</summary>
    public static void Report(ShipRequest request, ShipStep step, Outcome outcome, string? details, string agent)
    {
        RequireApproved(request);
        if (!request.Steps.Contains(step)) throw new InvalidOperationException($"The request doesn't include {Name(step)} ({Describe(request.Steps)}).");
        request.State = ShipState.Running;
        var text = outcome switch
        {
            Outcome.Done => $"Finished the {Name(step)} step",
            Outcome.Failed => $"The {Name(step)} step failed",
            _ => $"Skipped the {Name(step)} step",
        };
        Log(request, agent, true, string.IsNullOrWhiteSpace(details) ? text + "." : $"{text}: {details.Trim()}");
    }

    /// <summary>The end of an approved request: shipped, or not.</summary>
    public static void Finish(ShipRequest request, bool shipped, string summary, string agent)
    {
        RequireApproved(request);
        request.State = shipped ? ShipState.Done : ShipState.Failed;
        request.FinishedAt = DateTime.Now;
        Log(request, agent, true, (shipped ? "Shipped it" : "Couldn't ship it") + (string.IsNullOrWhiteSpace(summary) ? "." : ": " + summary.Trim()));
    }

    /// <summary>You take the request back, at any point before it's finished.</summary>
    public static void Withdraw(ShipRequest request, string by)
    {
        if (!request.IsOpen) throw new InvalidOperationException($"The request is {StateName(request.State)}.");
        request.State = ShipState.Withdrawn;
        request.FinishedAt = DateTime.Now;
        Log(request, by, false, "Withdrew the request.");
    }

    public static string StateName(ShipState state) => state switch
    {
        ShipState.Requested => "waiting for a plan",
        ShipState.Planned => "waiting for your approval",
        ShipState.Approved => "approved",
        ShipState.Running => "running",
        ShipState.Done => "shipped",
        ShipState.Failed => "failed",
        ShipState.Declined => "declined",
        _ => "withdrawn",
    };

    private static void RequireApproved(ShipRequest request)
    {
        if (request.State is ShipState.Approved or ShipState.Running) return;
        throw new InvalidOperationException(request.State switch
        {
            ShipState.Requested => "Post a plan first and wait for the user's approval.",
            ShipState.Planned => "The plan waits for the user's approval; don't run anything yet.",
            _ => $"The request is {StateName(request.State)}.",
        });
    }

    private static void Log(ShipRequest request, string by, bool agent, string text) =>
        request.Log.Add(new ActivityEntry { By = by, Agent = agent, Text = text });
}
