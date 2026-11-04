using CommunityToolkit.Mvvm.Input;

namespace ProJict.App.ViewModels;

/// <summary>
/// A shared thing an agent holds (claim_resource, #bb95b66d), in the bar at the bottom: who has it, what for, until when;
/// Free lets it go (its holder is told, and the next one waiting may take it).
/// </summary>
public sealed partial class ResourceBarItem(string name, string text, bool overdue, Action free)
{
    public string Name => name;
    public string Text => text;
    /// <summary>Its holder's time ran out: others may take it over.</summary>
    public bool IsOverdue => overdue;

    [RelayCommand] private void Free() => free();
}
