namespace ProJict.Core.Features;

/// <summary>
/// Undo and redo for a project page, by snapshots of the whole project (its JSON): a step is taken just before each
/// change, so undoing it brings back what was there. Typing into a field is one step, from its first change until the
/// field loses focus or the next step. A new step forgets what could be redone; at most <paramref name="max"/> steps back.
/// </summary>
public sealed class UndoHistory(int max = 100)
{
    private readonly List<string> _undo = [];
    private readonly List<string> _redo = [];

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    /// <summary>Typing is under way: its first change took a step, and the ones after it belong to that step.</summary>
    public bool Typing { get; private set; }

    /// <summary>A step before a change: <paramref name="before"/> is the project as it is now.</summary>
    public void Checkpoint(string before)
    {
        Push(_undo, before);
        _redo.Clear();
        Typing = false;
    }

    /// <summary>
    /// A field is about to change as you type: the first change takes a step (<paramref name="before"/> is only asked
    /// for then, as a snapshot costs more than a keystroke). Returns whether it did.
    /// </summary>
    public bool BeforeTyping(Func<string> before)
    {
        if (Typing) return false;
        Checkpoint(before());
        Typing = true;
        return true;
    }

    /// <summary>Focus moved on: the next typing is a step of its own.</summary>
    public void EndTyping() => Typing = false;

    /// <summary>Forgets every step: the project was replaced from elsewhere, so they'd bring back something else.</summary>
    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        Typing = false;
    }

    /// <summary>The project to bring back for Undo (<paramref name="current"/> can then be redone), or null when there's none.</summary>
    public string? Undo(string current) => Step(_undo, _redo, current);

    /// <summary>The project to bring back for Redo, or null when there's none.</summary>
    public string? Redo(string current) => Step(_redo, _undo, current);

    private string? Step(List<string> from, List<string> to, string current)
    {
        if (from.Count == 0) return null;
        Push(to, current);
        Typing = false;
        var snapshot = from[^1];
        from.RemoveAt(from.Count - 1);
        return snapshot;
    }

    private void Push(List<string> stack, string snapshot)
    {
        stack.Add(snapshot);
        if (stack.Count > max) stack.RemoveAt(0);
    }
}
