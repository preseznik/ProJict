using ProJict.Core.Model;

namespace ProJict.Core.Features;

public sealed record SearchHit(Project Project, Category? Category, TodoItem? Item, NoteDoc? Note, string Snippet)
{
    public string Location => Category != null ? $"{Project.Name} › {Category.Name}"
        : Note != null ? $"{Project.Name} › Notes › {Note.Title}"
        : Project.Name;
}

public static class GlobalSearch
{
    /// <summary>All whitespace-separated terms must match (case-insensitive) the item, its category or project name.</summary>
    public static List<SearchHit> Search(IEnumerable<Project> projects, string query, int max = 200)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return [];

        var open = new List<SearchHit>();
        var done = new List<SearchHit>();
        var notes = new List<SearchHit>();
        foreach (var project in projects)
        {
            foreach (var loc in project.Walk())
            {
                // An item's id (a72b44ea, #a72b44ea) finds it too (#bebbd825).
                var haystack = $"{project.Name} {loc.Category.Name} {loc.Item.SearchText()}";
                if (!terms.All(t => haystack.Contains(t, StringComparison.OrdinalIgnoreCase) || BoardFilter.IsIdOf(t, loc.Item))) continue;
                (loc.Item.IsDone ? done : open).Add(new SearchHit(project, loc.Category, loc.Item, null, FirstLine(loc.Item.DisplayText())));
            }
            foreach (var note in project.Notes)
            {
                if (!Matches($"{project.Name} {note.Title} {note.Text}", terms)) continue;
                notes.Add(new SearchHit(project, null, null, note, Snippet(note.Text, terms)));
            }
        }
        return open.Concat(done).Concat(notes).Take(max).ToList();
    }

    private static bool Matches(string haystack, string[] terms) =>
        terms.All(t => haystack.Contains(t, StringComparison.OrdinalIgnoreCase));

    private static string FirstLine(string text)
    {
        var line = text.Replace("\r\n", "\n").Split('\n')[0];
        return line.Length > 160 ? line[..160] + "…" : line;
    }

    private static string Snippet(string text, string[] terms)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var hit = lines.FirstOrDefault(l => terms.Any(t => l.Contains(t, StringComparison.OrdinalIgnoreCase))) ?? lines[0];
        return FirstLine(hit.Trim());
    }
}
