using System.Text;
using ProJict.Core.Model;

namespace ProJict.Core.Features;

/// <summary>Turns selected items into a Markdown prompt for a coding agent (Claude, Codex, …).</summary>
public static class PromptBuilder
{
    /// <param name="attachmentPath">Where an attached file is stored; attachments are listed by that path so the agent can open them.</param>
    /// <param name="forAllAgents">Your instructions for every agent (Settings › AI agents); they come before the project's own.</param>
    public static string Build(Project project, IEnumerable<TodoItem> selection, Func<Attachment, string>? attachmentPath = null, string? forAllAgents = null)
    {
        var selected = selection.Select(i => i.Id).ToHashSet();
        var sb = new StringBuilder();
        sb.Append("# Project: ").Append(project.Name).Append('\n');
        if (!string.IsNullOrWhiteSpace(project.RepoPath)) sb.Append("Local repo: ").Append(project.RepoPath.Trim()).Append('\n');
        if (!string.IsNullOrWhiteSpace(project.GitHubUrl)) sb.Append("GitHub: ").Append(project.GitHubUrl.Trim()).Append('\n');
        sb.Append('\n');
        if (!string.IsNullOrWhiteSpace(forAllAgents)) sb.Append(forAllAgents.Trim()).Append("\n\n");
        if (!string.IsNullOrWhiteSpace(project.PromptPreamble)) sb.Append(project.PromptPreamble.Trim()).Append("\n\n");
        sb.Append("Please work on the following items:\n");

        foreach (var category in project.Categories)
        {
            // An item is listed once even if its parent is selected too; children come along with the parent.
            var roots = category.Walk()
                .Where(l => selected.Contains(l.Item.Id) && !Ancestors(category, l.Item).Any(a => selected.Contains(a.Id)))
                .Select(l => l.Item)
                .ToList();
            if (roots.Count == 0) continue;
            sb.Append("\n## ").Append(category.Name).Append('\n');
            if (!string.IsNullOrWhiteSpace(category.Description)) sb.Append(category.Description.Trim()).Append('\n');
            foreach (var item in roots) WriteItem(sb, item, 0, attachmentPath);
        }

        return sb.ToString().TrimEnd() + "\n";
    }

    private static void WriteItem(StringBuilder sb, TodoItem item, int depth, Func<Attachment, string>? attachmentPath)
    {
        var indent = new string(' ', depth * 2);
        var lines = item.Text.Replace("\r\n", "\n").Split('\n');
        var box = item.IsDone ? "[x]" : "[ ]";
        sb.Append(indent).Append("- ").Append(box).Append(' ');
        if (!string.IsNullOrWhiteSpace(item.Tag)) sb.Append("**").Append(item.Tag).Append(":** ");
        sb.Append(lines[0].Trim());
        if (item.Priority == Priority.Hold) sb.Append(" _(on hold)_");
        else if (item.Priority != Priority.None) sb.Append(" _(").Append(item.Priority.ToString().ToLowerInvariant()).Append(" priority)_");
        if (item.State == ItemState.InProgress) sb.Append(" _(in progress)_");
        sb.Append('\n');
        foreach (var line in lines.Skip(1).Where(l => l.Trim().Length > 0))
            sb.Append(indent).Append("  ").Append(line.Trim()).Append('\n');
        if (item.Issue is { } issue)
            sb.Append(indent).Append("  GitHub issue #").Append(issue.Number).Append(": ").Append(issue.Url).Append('\n');
        if (attachmentPath != null)
            foreach (var attachment in item.Attachments ?? [])
                sb.Append(indent).Append("  Attached: ").Append(attachmentPath(attachment)).Append('\n');
        foreach (var child in item.Children) WriteItem(sb, child, depth + 1, attachmentPath);
    }

    private static IEnumerable<TodoItem> Ancestors(Category category, TodoItem item)
    {
        var parents = new Dictionary<TodoItem, TodoItem>();
        foreach (var loc in category.Walk())
            if (loc.Parent != null) parents[loc.Item] = loc.Parent;
        for (var cur = item; parents.TryGetValue(cur, out var parent); cur = parent)
            yield return parent;
    }
}
