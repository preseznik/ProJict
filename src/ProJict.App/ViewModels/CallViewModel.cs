using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProJict.App.Services;
using ProJict.Core.Chat;

namespace ProJict.App.ViewModels;

/// <summary>
/// The call window: a tile per participant (its picture or initials, like a camera that never shows a face) that glows
/// while that one speaks, what's been said as captions, and Mute and Hang up.
/// </summary>
public sealed partial class CallViewModel : ObservableObject
{
    private const int CaptionsShown = 6;
    private readonly CallService _calls;
    private readonly AppState _state;

    public CallViewModel(CallService calls, AppState state)
    {
        _calls = calls;
        _state = state;
        _calls.Changed += Refresh;
        Refresh();
    }

    public string Title { get; private set; } = "";
    public IReadOnlyList<CallTile> Tiles { get; private set; } = [];
    public IReadOnlyList<CallCaption> Captions { get; private set; } = [];
    public string StatusText { get; private set; } = "";
    /// <summary>Why it can't hear you, if so (you can still type in the conversation).</summary>
    public string? Problem => _calls.Problem;
    public bool IsOver => !_calls.IsOn;
    /// <summary>A person is calling you: Accept or Decline.</summary>
    public bool IsIncoming => _calls.IsIncoming;
    public bool IsOngoing => !IsIncoming;
    public int Columns => Math.Min(3, Math.Max(1, Tiles.Count));

    public bool IsMuted
    {
        get => _calls.Muted;
        set => _calls.SetMuted(value);
    }

    /// <summary>The call is over: the window closes.</summary>
    public event Action? Over;

    [RelayCommand] private void HangUp() => _calls.HangUp();
    [RelayCommand] private void Accept() => _calls.Accept();
    [RelayCommand] private void Decline() => _calls.Decline();

    /// <summary>The window closed while the call goes on: you hang up.</summary>
    public void Closed()
    {
        _calls.Changed -= Refresh;
        if (_calls.IsIncoming) _calls.Decline();
        else _calls.HangUp();
    }

    private void Refresh()
    {
        if (_calls.Current is not { } call) return;
        var where = _calls.Channel is { Direct: true } direct ? ChatRoom.Title(direct, _calls.Me) : "#" + _calls.Channel?.Name;
        Title = _calls.Channel is { Direct: true } ? $"Call with {where}" : $"Call in {where}";
        var tiles = new List<CallTile> { Tile(_calls.Me, _calls.Muted ? "muted" : "you", away: false) };
        tiles.AddRange(call.Agents.Select(a => Tile(a.Name, a.IsPerson && call.Incoming && a.State == CallState.Ringing ? "calling you" : ChatCall.Describe(a.State),
            away: a.State != CallState.Joined)));
        Tiles = tiles;
        Captions = [.. _calls.Captions.TakeLast(CaptionsShown).Select(c => new CallCaption(c.Who, c.Text, c.Who == "ProJict"))];
        var ringing = call.Agents.Where(a => a.State == CallState.Ringing).Select(a => a.Name).ToList();
        StatusText = call.IsOver ? $"Call ended · {ChatCall.Duration(call.EndedAt!.Value - call.StartedAt)}"
            : _calls.IsIncoming ? $"{call.Caller} is calling you"
            : call.InIt.Any() ? $"In the call · {ChatCall.Duration(DateTime.Now - call.StartedAt)}" + (ringing.Count > 0 ? $" · ringing {string.Join(", ", ringing)}" : "")
            : $"Ringing {string.Join(", ", ringing)}…";
        OnPropertyChanged(string.Empty);
        if (call.IsOver) Over?.Invoke();
    }

    private CallTile Tile(string name, string stateText, bool away) =>
        new(name, _state.Pictures.Of(name), WorkerBadge.For([name])[0], stateText, _calls.Speaking == name, away);
}

/// <summary>A participant's tile: picture or initials, name, where it is in the call, whether it's speaking now.</summary>
public sealed record CallTile(string Name, ImageSource? Picture, WorkerBadge Badge, string StateText, bool IsSpeaking, bool IsAway)
{
    public bool HasPicture => Picture != null;
}

/// <summary>A caption: who said it (ProJict for its own lines) and what.</summary>
public sealed record CallCaption(string Who, string Text, bool IsNote);
