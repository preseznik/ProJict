using ProJict.Core.Model;

namespace ProJict.Core.Features;

/// <summary>How the board orders its categories: as you arranged them, or by name or when they were added (only on screen).</summary>
public enum CategorySort { Board, Name, Newest, Oldest }

/// <summary>
/// 0.1.41's one sort for the board's items, kept only to read its settings: by priority still sorts the items, and by name
/// or date now sorts the categories (<see cref="CategorySort"/>).
/// </summary>
public enum BoardSort { Board, Priority, Name, Newest, Oldest }

/// <summary>
/// Sorting the board's categories, without any screen. Only what you see changes: the categories' own order stays (exports
/// and moving them by hand use it), and categories that sort alike keep it among themselves.
/// </summary>
public static class CategorySorting
{
    /// <summary>The choices, in the Priority menu's order.</summary>
    public static readonly CategorySort[] All = [CategorySort.Board, CategorySort.Name, CategorySort.Newest, CategorySort.Oldest];

    /// <summary>"Board order", "By name (A–Z)", "Newest first", "Oldest first".</summary>
    public static string Label(CategorySort sort) => sort switch
    {
        CategorySort.Name => "By name (A–Z)",
        CategorySort.Newest => "Newest first",
        CategorySort.Oldest => "Oldest first",
        _ => "Board order",
    };

    /// <summary>When the category was added: as it says, else (one from before categories said) its oldest item's date.</summary>
    public static DateTime AddedAt(Category category) =>
        category.CreatedAt ?? category.Walk().Select(l => l.Item.CreatedAt).DefaultIfEmpty(DateTime.MinValue).Min();

    /// <summary>The categories as the board shows them sorted so (OrderBy is stable, so ties keep their own order).</summary>
    public static IEnumerable<Category> Sorted(this IEnumerable<Category> categories, CategorySort sort) => sort switch
    {
        CategorySort.Name => categories.OrderBy(c => c.Name.Trim(), StringComparer.CurrentCultureIgnoreCase),
        CategorySort.Newest => categories.OrderByDescending(AddedAt),
        CategorySort.Oldest => categories.OrderBy(AddedAt),
        _ => categories,
    };
}
