using CommunityToolkit.Mvvm.Input;

namespace ProJict.App.ViewModels;

/// <summary>What a shared thing's row can do, by its name (#6f04e17e, #68c97287).</summary>
public sealed record ResourceActions(Action<string, int?> Reserve, Action<string, int?> Queue, Action<string, Guid> Cancel, Action<string> Free,
    Action<string?> ShowCalendar);

/// <summary>
/// A shared thing (claim_resource, #bb95b66d): in the bar at the bottom while someone holds it (who, what for, until when),
/// and in the Agents view's Shared machines (#6f04e17e). Free lets it go (its holder is told, and the next one waiting may
/// take it); Reserve makes it yours now, or queues you for it so it's yours the moment it's freed (#68c97287).
/// </summary>
public sealed partial class ResourceBarItem(string name, string text, string state, bool overdue, bool held, bool yours, Guid? queued, ResourceActions actions)
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
    /// <summary>You're queued for it: your place's id (to leave the queue).</summary>
    public Guid? QueuedId => queued;
    public bool CanReserve => !yours;
    public string ReserveToolTip => held
        ? "Take it now (the agent using it is told to stop, and waits for it), or queue for it: it's yours the moment it's freed"
        : "Make it yours: agents wait for it until you free it (or your time is up)";

    /// <summary>Yours now, for this many minutes; null: until you free it.</summary>
    public void Reserve(int? minutes) => actions.Reserve(name, minutes);
    /// <summary>Yours the moment it's freed, for this many minutes; null: until you free it.</summary>
    public void Queue(int? minutes) => actions.Queue(name, minutes);
    public void LeaveQueue()
    {
        if (queued is { } id) actions.Cancel(name, id);
    }
    public void ShowCalendar() => actions.ShowCalendar(name);

    [RelayCommand] private void Free() => actions.Free(name);
    [RelayCommand] private void Calendar() => actions.ShowCalendar(null);
}
