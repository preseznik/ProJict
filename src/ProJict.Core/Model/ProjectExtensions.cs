namespace ProJict.Core.Model;

/// <summary>Where an item lives: its category, the list that holds it and its parent (null for top-level).</summary>
public sealed record ItemLocation(Category Category, List<TodoItem> Siblings, TodoItem? Parent, TodoItem Item, int Depth)
{
    public int Index => Siblings.IndexOf(Item);
}

public static class ProjectExtensions
{
    public static IEnumerable<ItemLocation> Walk(this Project project)
    {
        foreach (var category in project.Categories)
            foreach (var loc in Walk(category))
                yield return loc;
    }

    public static IEnumerable<ItemLocation> Walk(this Category category)
    {
        return WalkList(category, category.Items, null, 0);

        static IEnumerable<ItemLocation> WalkList(Category c, List<TodoItem> list, TodoItem? parent, int depth)
        {
            foreach (var item in list.ToList())
            {
                yield return new ItemLocation(c, list, parent, item, depth);
                foreach (var child in WalkList(c, item.Children, item, depth + 1))
                    yield return child;
            }
        }
    }

    public static IEnumerable<TodoItem> Descendants(this TodoItem item)
    {
        foreach (var child in item.Children)
        {
            yield return child;
            foreach (var d in child.Descendants())
                yield return d;
        }
    }

    public static ItemLocation? Find(this Project project, Guid itemId) =>
        project.Walk().FirstOrDefault(l => l.Item.Id == itemId);

    public static Category? FindCategory(this Project project, string name) =>
        project.Categories.FirstOrDefault(c => CategoryNamesEqual(c.Name, name));

    public static Category GetOrAddCategory(this Project project, string name)
    {
        var existing = project.FindCategory(name);
        if (existing != null) return existing;
        var created = new Category { Name = name.Trim() };
        project.Categories.Add(created);
        return created;
    }

    /// <summary>
    /// Uses another project as a template: adds its categories (name and description, no items) that this project
    /// doesn't have yet, in the source's order, after the existing ones.
    /// </summary>
    public static List<Category> CopyCategoriesFrom(this Project project, Project source)
    {
        var added = new List<Category>();
        foreach (var category in source.Categories)
        {
            if (project.FindCategory(category.Name) != null) continue;
            var copy = new Category { Name = category.Name, Description = category.Description };
            project.Categories.Add(copy);
            added.Add(copy);
        }
        return added;
    }

    public static bool CategoryNamesEqual(string a, string b) =>
        string.Equals(NormalizeCategory(a), NormalizeCategory(b), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeCategory(string name) => name.Trim().TrimEnd(':').Trim();

    public static bool IsBugLike(this TodoItem item) =>
        item.Tag is { } tag && (tag.StartsWith("BUG", StringComparison.OrdinalIgnoreCase) ||
                                tag.StartsWith("ISSUE", StringComparison.OrdinalIgnoreCase) ||
                                tag.StartsWith("FIX", StringComparison.OrdinalIgnoreCase));

    /// <summary>Text as written in the TXT files: "TAG: text".</summary>
    public static string DisplayText(this TodoItem item) =>
        string.IsNullOrEmpty(item.Tag) ? item.Text : $"{item.Tag}: {item.Text}";

    /// <summary>What the filter and search look in: the item's text plus the names of its attached files.</summary>
    public static string SearchText(this TodoItem item)
    {
        var text = item.HasAttachments ? $"{item.DisplayText()} {string.Join(" ", item.Attachments!.Select(a => a.FileName))}" : item.DisplayText();
        return item.Issue is { } issue ? $"{text} #{issue.Number}" : text;
    }

    /// <summary>Files attached to the project's items and notes.</summary>
    /// <summary>Every file in the project: on items, on notes, posted in discussions and offered as videos' thumbnails.</summary>
    public static IEnumerable<Attachment> Attachments(this Project project) =>
        project.Walk().SelectMany(l => (l.Item.Attachments ?? []).Concat(l.Item.Comments.Attachments()))
            .Concat(project.Categories.SelectMany(c => c.Comments.Attachments()))
            .Concat(project.Notes.SelectMany(n => n.Attachments ?? []))
            .Concat((project.Publishes ?? []).SelectMany(p => p.Thumbnails));
}
