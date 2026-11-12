using CommunityToolkit.Mvvm.Input;

namespace ProJict.App.ViewModels;

/// <summary>
/// A shared thing (claim_resource, #bb95b66d): in the bar at the bottom while someone holds it (who, what for, until when),
/// and in the Agents view's Shared machines (#6f04e17e). Free lets it go (its holder is told, and the next one waiting may
/// take it); Reserve makes it yours, for a while or until you free it, so agents wait for it.
/// </summary>
public sealed partial class ResourceBarItem(string name, string text, string state, bool overdue, bool held, bool yours, Action<int?> reserve, Action free)
{
    public string Name => name;
    /// <summary>The bar's line: "“PC4080S game + GPU”: Salto (VR test) until 23:01 (40 min left) · 1 waiting".</summary>
    public string Text => text;
    /// <summary>The Agents view's line, after the name: "Free", "Salto (VR test) until 23:01", "Yours until you free it".</summary>
    public string State => state;
    /// <summary>Its holder's time ran out: others may take it over.</summary>
    public bool IsOverdue => overdue;
    public bool IsHeld => held;
    /// <summary>You reserved it.</summary>
    public bool IsYours => yours;
    public bool CanReserve => !yours;
    public string ReserveToolTip => held
        ? "Take it for yourself now: the agent using it is told to stop, and waits for it until you free it"
        : "Make it yours: agents wait for it until you free it (or your time is up)";

    /// <summary>For this many minutes; null: until you free it.</summary>
    public void Reserve(int? minutes) => reserve(minutes);

    [RelayCommand] private void Free() => free();
}
