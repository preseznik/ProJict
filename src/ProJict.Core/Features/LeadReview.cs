using ProJict.Core.Model;
using ProJict.Core.Wiki;

namespace ProJict.Core.Features;

/// <summary>
/// A studio's lead reviewing wiki pages instead of you, when you turn it on for the studio: pages about its project (in
/// its folder, if you name one) that the studio's other agents changed since you last checked. The lead accepts them or
/// sends them back. The lead's own changes, other agents', clashing edits and every other page still wait for you.
/// </summary>
public static class LeadReview
{
    /// <summary>The studio's lead role: Lead designer in a game studio, Director in a film studio.</summary>
    public static StudioRole? LeadRole(Studio studio) => studio.Roles.FirstOrDefault(r => r.Station is StudioStation.Lead or StudioStation.Director);

    /// <summary>Who has the lead role, if anyone.</summary>
    public static string? LeadOf(Studio studio) => LeadRole(studio) is { } role ? StudioFlow.HolderOf(studio, role) : null;

    public static bool IsLead(Studio studio, string agent) => LeadOf(studio) is { } lead && Same(lead, agent);

    /// <summary>Whether the page is one the studio's lead reviews (whoever changed it).</summary>
    public static bool Covers(Studio studio, WikiPage page) =>
        studio.LeadReviewsWiki && (studio.LeadReviewFolder is not { Length: > 0 } folder || WikiPaths.IsIn(page.Path, folder));

    /// <summary>The agents whose changes wait for a review: who saved the versions since it was last checked.</summary>
    public static IReadOnlyList<string> Authors(WikiPage page) =>
        page.Versions.Where(v => v.Agent && v.Number > (page.ReviewedVersion ?? 0)).Select(v => v.By).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Why <paramref name="agent"/> can't review the page's changes for you, in words for it; null when it can: it's the
    /// lead of the studio of the page's project, the studio has its lead review pages, and everything that waits was
    /// changed by the studio's other agents.
    /// </summary>
    public static string? Problem(Project? project, WikiPage page, string agent)
    {
        // The studio the agent leads, else the project's first open one, for what it's told.
        if (project == null || project.Remote != null || StudioFlow.OpenStudiosOf(project).ToList() is not [var first, ..] studios)
            return "The page isn't about a project with a studio, so it waits for the user's review.";
        var studio = studios.FirstOrDefault(s => IsLead(s, agent)) ?? first;
        var role = LeadRole(studio);
        if (!studio.LeadReviewsWiki)
            return $"In {project.Name}'s {StudioFlow.KindName(studio.Kind)} the user reviews wiki pages themself (they can let the {role?.Name ?? "lead"} do it, in the project's Details).";
        if (LeadOf(studio) is not { } lead || !Same(lead, agent))
            return $"Only {project.Name}'s {role?.Name ?? "lead"}{(LeadOf(studio) is { } holder ? $", {holder}," : "")} reviews its wiki pages for the user.";
        if (!Covers(studio, page))
            return $"You review only the pages in {WikiPaths.Display(studio.LeadReviewFolder!)}; this one waits for the user.";
        if (!page.NeedsReview) return "The page doesn't wait for a review: nothing changed since it was last checked.";
        if (page.HasClashes) return "Two edits of the page clashed: the user picks which to keep.";
        var authors = Authors(page);
        if (authors.Count == 0) return "No agent changed the page since it was last checked.";
        if (authors.Any(a => Same(a, lead)))
            return "The changes include your own, and nobody reviews their own work: the page waits for the user.";
        var outsiders = authors.Where(a => StudioFlow.HireOf(studio, a) == null).ToList();
        if (outsiders.Count > 0)
            return $"{string.Join(", ", outsiders)} changed it too, and {(outsiders.Count == 1 ? "isn't" : "aren't")} in the studio: the page waits for the user.";
        return null;
    }

    /// <summary>Whether the lead may review the page and hasn't sent it back as it is now.</summary>
    public static bool WaitsFor(Project? project, WikiPage page, string agent) => page.SentBackReview == null && Problem(project, page, agent) == null;

    /// <summary>The lead who reviews the page for you, and their role, while it waits; null when it waits for you alone.</summary>
    public static (string Lead, string Role)? ReviewerOf(Project? project, WikiPage page) =>
        project == null ? null
        : StudioFlow.OpenStudiosOf(project)
            .Select(s => LeadOf(s) is { } lead && LeadRole(s) is { } role && Problem(project, page, lead) == null ? (lead, role.Name) : ((string, string)?)null)
            .FirstOrDefault(r => r != null);

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
