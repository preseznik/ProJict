using ProJict.Core.Model;

namespace ProJict.Core.Features;

/// <summary>
/// The board's edits on a project's items and categories, without any screen: where an item goes when it's indented,
/// outdented, moved up or down (into the neighbouring category at an edge) or dragged, and how categories are put in
/// order. The page around them takes the undo step, redraws and puts the cursor back. A category's top-level open and
/// done items share one list, but an item only moves among its own kind there.
/// </summary>
public static class BoardEdits
{
    /// <summary>The item it would go under when indented: the nearest one above it (of its own kind at the top level), or null.</summary>
    public static TodoItem? IndentTarget(IList<TodoItem> siblings, TodoItem item, bool topLevel)
    {
        for (var i = siblings.IndexOf(item) - 1; i >= 0; i--)
            if (!topLevel || siblings[i].IsDone == item.IsDone) return siblings[i];
        return null;
    }

    /// <summary>Indents the item under <paramref name="under"/>, as its last sub-item.</summary>
    public static void Indent(IList<TodoItem> siblings, TodoItem item, TodoItem under)
    {
        siblings.Remove(item);
        under.Children.Add(item);
    }

    /// <summary>Takes a sub-item out from under its parent, to just after it. False when it has none.</summary>
    public static bool Outdent(Project project, TodoItem item, TodoItem? parent)
    {
        if (parent == null || project.Find(parent.Id) is not { } parentLoc) return false;
        parent.Children.Remove(item);
        parentLoc.Siblings.Insert(parentLoc.Siblings.IndexOf(parent) + 1, item);
        return true;
    }

    /// <summary>
    /// Where the item goes among its siblings moved up (<paramref name="delta"/> -1) or down (+1), past the other kind
    /// at the top level; null at the edge (a top-level one then goes into the neighbouring category: <see cref="MoveInto"/>).
    /// </summary>
    public static int? MoveTarget(IList<TodoItem> siblings, TodoItem item, int delta, bool topLevel)
    {
        var target = siblings.IndexOf(item) + delta;
        while (target >= 0 && target < siblings.Count && topLevel && siblings[target].IsDone != item.IsDone) target += delta;
        return target >= 0 && target < siblings.Count ? target : null;
    }

    /// <summary>Moves the item to <paramref name="target"/> among its siblings (as <see cref="MoveTarget"/> gave).</summary>
    public static void MoveTo(IList<TodoItem> siblings, TodoItem item, int target)
    {
        siblings.RemoveAt(siblings.IndexOf(item));
        siblings.Insert(target, item);
    }

    /// <summary>A top-level item moved past its category's edge goes into the neighbouring one: at its end going up, at its start going down.</summary>
    public static void MoveInto(IList<TodoItem> from, TodoItem item, Category to, int delta)
    {
        from.Remove(item);
        if (delta < 0) to.Items.Add(item);
        else to.Items.Insert(0, item);
    }

    /// <summary>Whether an item can be dropped beside another: not onto itself, nor among its own sub-items.</summary>
    public static bool CanPlaceBeside(TodoItem item, TodoItem target) => item != target && !item.Descendants().Contains(target);

    /// <summary>Dropped beside <paramref name="target"/>: before or after it, as its sibling.</summary>
    public static void PlaceBeside(IList<TodoItem> from, TodoItem item, IList<TodoItem> into, TodoItem target, bool after)
    {
        from.Remove(item);
        into.Insert(into.IndexOf(target) + (after ? 1 : 0), item);
    }

    /// <summary>Moves a category to <paramref name="target"/> in the project's order. False when that isn't a place or it's there already.</summary>
    public static bool MoveCategory(Project project, Category category, int target)
    {
        var index = project.Categories.IndexOf(category);
        if (index < 0 || target < 0 || target >= project.Categories.Count || target == index) return false;
        project.Categories.RemoveAt(index);
        project.Categories.Insert(target, category);
        return true;
    }

    /// <summary>A category dropped onto another goes just before it.</summary>
    public static bool MoveCategoryBefore(Project project, Category category, Category before)
    {
        if (category == before || !project.Categories.Contains(category) || !project.Categories.Contains(before)) return false;
        project.Categories.Remove(category);
        project.Categories.Insert(project.Categories.IndexOf(before), category);
        return true;
    }
}
