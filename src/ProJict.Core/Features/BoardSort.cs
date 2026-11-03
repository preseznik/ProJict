using ProJict.Core.Model;

namespace ProJict.Core.Features;

/// <summary>How the board lists each category's open items: in their own order, or sorted (only on screen).</summary>
public enum BoardSort { Board, Priority, Name, Newest, Oldest }

/// <summary>
/// Sorting the board's open items, without any screen. Only what you see changes: the items' own order stays (exports and
/// moving items by hand use it), and items that sort alike keep it among themselves.
/// </summary>
public static class BoardSorting
{
    /// <summary>The choices, in the Priority menu's order.</summary>
    public static readonly BoardSort[] All = [BoardSort.Board, BoardSort.Priority, BoardSort.Name, BoardSort.Newest, BoardSort.Oldest];

    /// <summary>"Board order", "By priority", "By name (A–Z)", "Newest first", "Oldest first".</summary>
    public static string Label(BoardSort sort) => sort switch
    {
        BoardSort.Priority => "By priority",
        BoardSort.Name => "By name (A–Z)",
        BoardSort.Newest => "Newest first",
        BoardSort.Oldest => "Oldest first",
        _ => "Board order",
    };

    /// <summary>The items as the board lists them sorted so (OrderBy is stable, so ties keep their own order).</summary>
    public static IEnumerable<TodoItem> Sorted(this IEnumerable<TodoItem> items, BoardSort sort) => sort switch
    {
        BoardSort.Priority => items.ByPriority(),
        BoardSort.Name => items.OrderBy(i => i.Text.Trim(), StringComparer.CurrentCultureIgnoreCase),
        BoardSort.Newest => items.OrderByDescending(i => i.CreatedAt),
        BoardSort.Oldest => items.OrderBy(i => i.CreatedAt),
        _ => items,
    };
}
