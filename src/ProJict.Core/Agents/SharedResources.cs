using ProJict.Core.Storage;

namespace ProJict.Core.Agents;

/// <summary>
/// A shared thing agents take turns with (#bb95b66d), such as "PC4080S game + GPU": who holds it, what for, since when
/// and until about when (a lease the holder sets and extends), and who waits for it. Kept in the workspace settings, so
/// every project's agents see it.
/// </summary>
public sealed class SharedResource
{
    public string Name { get; set; } = "";
    /// <summary>The agent holding it; null while it's free.</summary>
    public string? HeldBy { get; set; }
    /// <summary>What for, in a few words ("GRID2VR VR test").</summary>
    public string? For { get; set; }
    public DateTime Since { get; set; }
    /// <summary>Until about when the holder needs it; after that it's overdue and others may take it over.</summary>
    public DateTime Until { get; set; }
    /// <summary>The holder disconnected with no other session left; after <see cref="ResourceLocks.Grace"/> it counts as free.</summary>
    public DateTime? HolderLeftAt { get; set; }
    /// <summary>Who asked for it while someone else held it, first first; they're told when it's free.</summary>
    public List<string> Waiting { get; set; } = [];
    /// <summary>When it was last freed (released, or its holder left), and by whom, for telling those who wait.</summary>
    public DateTime? FreedAt { get; set; }
    public string? FreedBy { get; set; }
    /// <summary>Who held it before it was freed or taken over, and until when they'd said, for telling them.</summary>
    public string? LastHolder { get; set; }
    public DateTime? LastUntil { get; set; }
    /// <summary>The holder now took it over from <see cref="LastHolder"/>, whose time had run out (or the user took it, #6f04e17e).</summary>
    public bool TakenOver { get; set; }
    /// <summary>
    /// Reserved by the user in ProJict, not by an agent (#6f04e17e): agents wait for it like for any other holder, and it
    /// isn't let go when an agent disconnects. With no end time (<see cref="ResourceLocks.OpenEnded"/>) it's never overdue.
    /// </summary>
    public bool Person { get; set; }
}

/// <summary>What became of a claim.</summary>
public enum ClaimOutcome { Taken, Extended, TakenOver, Busy }

/// <summary>The rules for taking turns with shared things: claim with a lease, extend, release, take over when overdue.</summary>
public static class ResourceLocks
{
    /// <summary>How long a holder that disconnected has to come back before the thing counts as free.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(2);
    public const int MinMinutes = 1, MaxMinutes = 8 * 60, DefaultMinutes = 30, MaxName = 60, MaxPurpose = 120;
    /// <summary>Free things nobody waits for go after this long.</summary>
    public static readonly TimeSpan ForgottenAfter = TimeSpan.FromDays(14);

    public static List<SharedResource> Of(WorkspaceSettings settings) => settings.SharedResources ??= [];

    public static SharedResource? Find(WorkspaceSettings settings, string name) =>
        settings.SharedResources?.FirstOrDefault(r => string.Equals(r.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Held, and its holder still counts (it hasn't been gone past the grace time).</summary>
    public static bool IsHeld(SharedResource r, DateTime now) => r.HeldBy != null && !HasLeft(r, now);

    public static bool HasLeft(SharedResource r, DateTime now) => r.HolderLeftAt is { } left && now - left >= Grace;

    /// <summary>Held past the time its holder said: others may take it over.</summary>
    public static bool IsOverdue(SharedResource r, DateTime now) => IsHeld(r, now) && now > r.Until;

    /// <summary>Why the name or purpose can't be used, or null.</summary>
    public static string? Problem(string name, string? purpose)
    {
        if (name.Trim().Length < 2) return "Name the shared thing, e.g. \"PC4080S game + GPU\".";
        if (name.Trim().Length > MaxName) return $"Keep the name to {MaxName} characters.";
        if (purpose?.Trim().Length > MaxPurpose) return $"Say what for in {MaxPurpose} characters at most.";
        return null;
    }

    /// <summary>
    /// The agent claims it for <paramref name="minutes"/>: taken when free (or its holder left), extended when it's the
    /// agent's already, taken over when overdue, else busy (and the agent waits for it). Returns the outcome and who held it before.
    /// </summary>
    public static (ClaimOutcome Outcome, SharedResource Resource, string? Before) Claim(WorkspaceSettings settings, string name, string agent, string? purpose, int minutes, DateTime now)
    {
        if (Problem(name, purpose) is { } problem) throw new ArgumentException(problem);
        minutes = Math.Clamp(minutes, MinMinutes, MaxMinutes);
        var all = Of(settings);
        Forget(all, now);
        var r = Find(settings, name);
        if (r == null) all.Add(r = new SharedResource { Name = name.Trim() });
        var mine = string.Equals(r.HeldBy, agent, StringComparison.OrdinalIgnoreCase);
        if (mine)
        {
            r.Until = now.AddMinutes(minutes);
            if (!string.IsNullOrWhiteSpace(purpose)) r.For = purpose.Trim();
            r.HolderLeftAt = null;
            return (ClaimOutcome.Extended, r, null);
        }
        if (IsHeld(r, now) && !IsOverdue(r, now))
        {
            if (!r.Waiting.Contains(agent, StringComparer.OrdinalIgnoreCase)) r.Waiting.Add(agent);
            return (ClaimOutcome.Busy, r, r.HeldBy);
        }
        var before = IsHeld(r, now) ? r.HeldBy : null;
        if (before != null)
        {
            r.LastHolder = before;
            r.LastUntil = r.Until;
        }
        r.TakenOver = before != null;
        r.HeldBy = agent;
        r.Person = false;
        r.For = string.IsNullOrWhiteSpace(purpose) ? null : purpose.Trim();
        r.Since = now;
        r.Until = now.AddMinutes(minutes);
        r.HolderLeftAt = null;
        r.Waiting.RemoveAll(w => string.Equals(w, agent, StringComparison.OrdinalIgnoreCase));
        return (before == null ? ClaimOutcome.Taken : ClaimOutcome.TakenOver, r, before);
    }

    /// <summary>
    /// The user reserves it themselves (#6f04e17e), for <paramref name="minutes"/> or, when null, until they free it. Free or
    /// not, it's theirs at once: an agent holding it is taken off it (and waits for it, so it's told when it's free again).
    /// Agents asking for it meanwhile wait, as for any holder. Returns the outcome and the agent that held it before.
    /// </summary>
    public static (ClaimOutcome Outcome, SharedResource Resource, string? Before) Reserve(WorkspaceSettings settings, string name, string person, string? purpose,
        int? minutes, DateTime now)
    {
        if (Problem(name, purpose) is { } problem) throw new ArgumentException(problem);
        var all = Of(settings);
        Forget(all, now);
        var r = Find(settings, name);
        if (r == null) all.Add(r = new SharedResource { Name = name.Trim() });
        var until = minutes is { } m ? now.AddMinutes(Math.Clamp(m, MinMinutes, MaxMinutes)) : DateTime.MaxValue;
        if (string.Equals(r.HeldBy, person, StringComparison.OrdinalIgnoreCase) && IsHeld(r, now))
        {
            r.Until = until;
            r.Person = true;
            if (!string.IsNullOrWhiteSpace(purpose)) r.For = purpose.Trim();
            return (ClaimOutcome.Extended, r, null);
        }
        var before = IsHeld(r, now) ? r.HeldBy : null;
        if (before != null)
        {
            r.LastHolder = before;
            r.LastUntil = r.Until;
            if (!r.Waiting.Contains(before, StringComparer.OrdinalIgnoreCase)) r.Waiting.Insert(0, before);
        }
        r.TakenOver = before != null;
        r.HeldBy = person;
        r.Person = true;
        r.For = string.IsNullOrWhiteSpace(purpose) ? null : purpose.Trim();
        r.Since = now;
        r.Until = until;
        r.HolderLeftAt = null;
        r.Waiting.RemoveAll(w => string.Equals(w, person, StringComparison.OrdinalIgnoreCase));
        return (before == null ? ClaimOutcome.Taken : ClaimOutcome.TakenOver, r, before);
    }

    /// <summary>Reserved with no end time: it's held until its holder frees it, and never overdue.</summary>
    public static bool OpenEnded(SharedResource r) => r.Until == DateTime.MaxValue;

    /// <summary>"until 13:35 (8 min left)" (with "about" when <paramref name="about"/>), or "until they free it".</summary>
    public static string UntilPhrase(SharedResource r, DateTime now, bool about = false) =>
        OpenEnded(r) ? "until they free it" : "until " + (about ? "about " : "") + UntilText(r, now);

    /// <summary>The agent (or the user, <paramref name="by"/>) frees it; null when the agent doesn't hold it.</summary>
    public static SharedResource? Release(WorkspaceSettings settings, string name, string? holder, string by, DateTime now)
    {
        var r = Find(settings, name);
        if (r?.HeldBy == null || (holder != null && !string.Equals(r.HeldBy, holder, StringComparison.OrdinalIgnoreCase))) return null;
        Free(r, by, now);
        return r;
    }

    /// <summary>The agent's last session ended: what it holds counts as free after the grace time unless it comes back.</summary>
    public static void Left(WorkspaceSettings settings, string agent, DateTime now)
    {
        foreach (var r in settings.SharedResources ?? [])
            if (!r.Person && string.Equals(r.HeldBy, agent, StringComparison.OrdinalIgnoreCase) && r.HolderLeftAt == null) r.HolderLeftAt = now;
    }

    /// <summary>The agent is back: what it held is still its own.</summary>
    public static bool Back(WorkspaceSettings settings, string agent, DateTime now)
    {
        var changed = false;
        foreach (var r in settings.SharedResources ?? [])
            if (string.Equals(r.HeldBy, agent, StringComparison.OrdinalIgnoreCase) && r.HolderLeftAt != null)
            {
                if (HasLeft(r, now)) Free(r, r.HeldBy!, r.HolderLeftAt.Value + Grace);
                else r.HolderLeftAt = null;
                changed = true;
            }
        return changed;
    }

    private static void Free(SharedResource r, string by, DateTime at)
    {
        r.LastHolder = r.HeldBy;
        r.LastUntil = r.Until;
        r.TakenOver = false;
        r.FreedBy = by;
        r.FreedAt = at;
        r.HeldBy = null;
        r.Person = false;
        r.For = null;
        r.HolderLeftAt = null;
    }

    /// <summary>Free things nobody waits for, freed long ago, go.</summary>
    private static void Forget(List<SharedResource> all, DateTime now) =>
        all.RemoveAll(r => r.HeldBy == null && r.Waiting.Count == 0 && r.FreedAt is { } freed && now - freed > ForgottenAfter);

    /// <summary>"13:35 (8 min left)" or "13:35 (overdue by 5 min)".</summary>
    public static string UntilText(SharedResource r, DateTime now)
    {
        var minutes = (int)Math.Round(Math.Abs((r.Until - now).TotalMinutes));
        return r.Until.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) +
               (now > r.Until ? $" (overdue by {minutes} min)" : $" ({minutes} min left)");
    }
}
