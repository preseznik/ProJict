using ProJict.Core.Model;

namespace ProJict.Core.Features;

/// <summary>
/// The board's filter, without any screen: the filter box's words, a tag (or none), a state, the priority, and who worked
/// on it. A match keeps the items it's under and its own sub-items (in the chosen state) showing, so the tree still reads;
/// words that match a category's name show all of it, when nothing else is filtered.
/// </summary>
public sealed record BoardFilter
{
    public string? Text { get; init; }
    /// <summary>Only items with this tag (in any case); null for any.</summary>
    public string? Tag { get; init; }
    /// <summary>Only items without a tag.</summary>
    public bool Untagged { get; init; }
    public ItemState? State { get; init; }
    /// <summary>Only an agent's work waiting for your review.</summary>
    public bool ReviewOnly { get; init; }
    /// <summary>"Needs you": an agent's work to review, or comments you haven't read (<see cref="Unread"/> says which).</summary>
    public bool AttentionOnly { get; init; }
    public Func<TodoItem, bool>? Unread { get; init; }
    /// <summary>Priority: at least this one…</summary>
    public Priority? PriorityAtLeast { get; init; }
    /// <summary>…or exactly this one (no priority, on hold).</summary>
    public Priority? PriorityOnly { get; init; }
    /// <summary>Only items this person or agent worked on.</summary>
    public string? WorkedBy { get; init; }

    private string[] Terms => (Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public bool FiltersPriority => PriorityAtLeast != null || PriorityOnly != null;
    public bool IsActive => Terms.Length > 0 || Tag != null || Untagged || State != null || AttentionOnly || WorkedBy != null || FiltersPriority;

    /// <summary>Whether the item is in the chosen state (which the sub-items shown under a match must be too).</summary>
    public bool StateMatches(TodoItem item) =>
        (State == null || item.State == State) && (!ReviewOnly || item.NeedsReview) &&
        (!AttentionOnly || item.NeedsReview || Unread?.Invoke(item) == true);

    public bool PriorityMatches(TodoItem item) =>
        PriorityOnly is { } only ? item.Priority == only : PriorityAtLeast is not { } min || Priorities.AtLeast(item.Priority, min);

    public bool Matches(TodoItem item)
    {
        var tagOk = Untagged ? string.IsNullOrEmpty(item.Tag) : Tag == null || string.Equals(item.Tag, Tag, StringComparison.OrdinalIgnoreCase);
        var text = item.SearchText();
        return tagOk && StateMatches(item) && PriorityMatches(item) &&
               Terms.All(t => text.Contains(t, StringComparison.OrdinalIgnoreCase) || IsIdOf(t, item)) &&
               (WorkedBy == null || item.Contributors().Any(c => string.Equals(c.Name, WorkedBy, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// The word is the item's id, or its start, as Chat and agents write it (#bebbd825): "a72b44ea", "#a72b44ea" or the whole
    /// id. At least six of its characters, so an ordinary word doesn't find an item by chance.
    /// </summary>
    public static bool IsIdOf(string term, TodoItem item)
    {
        var id = term.Trim().TrimStart('#');
        return id.Length >= 6 && id.All(c => char.IsAsciiHexDigit(c) || c == '-') &&
               item.Id.ToString().StartsWith(id, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The one item the words name by its id (the filter holds just an id, and only that item has it); null otherwise. The
    /// board goes to it on Enter.
    /// </summary>
    public TodoItem? ItemNamedById(Project project)
    {
        if (Terms is not [var only]) return null;
        var found = project.Walk().Select(l => l.Item).Where(i => IsIdOf(only, i)).Take(2).ToList();
        return found.Count == 1 ? found[0] : null;
    }

    /// <summary>
    /// What of the category shows: whether its name matches the words (then all of it does), and the items that do: the
    /// matches, the items they're under, and their sub-items in the chosen state.
    /// </summary>
    public (bool NameMatches, HashSet<TodoItem> Visible) Apply(Category category)
    {
        var parents = new Dictionary<TodoItem, TodoItem>();
        var matches = new List<TodoItem>();
        foreach (var loc in category.Walk())
        {
            if (loc.Parent != null) parents[loc.Item] = loc.Parent;
            if (Matches(loc.Item)) matches.Add(loc.Item);
        }
        var visible = new HashSet<TodoItem>();
        foreach (var item in matches.SelectMany(m => m.Descendants().Where(StateMatches).Prepend(m)))
            for (TodoItem? p = item; p != null && visible.Add(p); p = parents.GetValueOrDefault(p)) { }
        var terms = Terms;
        var nameMatches = terms.Length > 0 && Tag == null && !Untagged && State == null && !AttentionOnly && WorkedBy == null && !FiltersPriority &&
                          terms.All(t => category.Name.Contains(t, StringComparison.OrdinalIgnoreCase));
        return (nameMatches, visible);
    }
}
