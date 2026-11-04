using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProJict.Core.Chat;

namespace ProJict.App.ViewModels;

/// <summary>
/// The phone in a channel's header (#80a4e9c9): in a private conversation with an agent it calls it; in a channel you
/// pick which of its agents to call. Only agents that hear Chat right away can take a call.
/// </summary>
public sealed partial class ChatViewModel
{
    /// <summary>
    /// There's someone here to call: agents in your own ProJict's channels you can write in, or the person in a private
    /// conversation while their ProJict is connected to yours.
    /// </summary>
    public bool CanCall => Channel is { } channel && CanWrite &&
                           ((Here.Host == null && AgentsIn(channel).Any()) || (PersonIn(channel) is { } person && _main.Calls.LinkFor(channel, person) != null));

    /// <summary>The other one in a private conversation, when it's a person.</summary>
    private string? PersonIn(ChatChannel channel) =>
        channel.Direct ? channel.Members.FirstOrDefault(m => !m.Agent && !string.Equals(m.Name, NameIn(channel), StringComparison.OrdinalIgnoreCase))?.Name : null;

    public string CallToolTip => Channel is { Direct: true } direct
        ? PersonIn(direct) is { } person ? $"Call {person}: a voice call, both ways" : $"Call {ChatRoom.Title(direct, MyName)}: a voice call (you speak, it answers aloud)"
        : "Call agents in this channel: pick who";

    /// <summary>The channel's picker of who to call is open.</summary>
    [ObservableProperty] private bool _isPickingCall;

    public List<CallChoice> CallChoices { get; private set; } = [];

    private static IEnumerable<ChatMember> AgentsIn(ChatChannel channel) => channel.Members.Where(m => m.Agent);

    [RelayCommand]
    private void Call()
    {
        if (Channel is not { } channel) return;
        if (channel.Direct)
        {
            StartCallWith(channel, PersonIn(channel) is { } person ? [person] : [.. AgentsIn(channel).Select(m => m.Name)]);
            return;
        }
        CallChoices = [.. AgentsIn(channel).OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).Select(m => new CallChoice(m.Name, _main.AgentTools.HearsChat(m.Name)))];
        OnPropertyChanged(nameof(CallChoices));
        IsPickingCall = true;
    }

    [RelayCommand]
    private void StartCall()
    {
        IsPickingCall = false;
        if (Channel is { } channel) StartCallWith(channel, [.. CallChoices.Where(c => c.IsPicked).Select(c => c.Name)]);
    }

    private void StartCallWith(ChatChannel channel, List<string> agents)
    {
        if (_main.Calls.Start(channel, agents) is { } problem) _state.StatusText = problem;
    }
}

/// <summary>An agent in the channel's picker: whether it can take a call now (it hears Chat right away), and whether you picked it.</summary>
public sealed partial class CallChoice(string name, bool canTakeCall) : ObservableObject
{
    public string Name => name;
    public bool CanTakeCall => canTakeCall;
    public string Note => canTakeCall ? "" : "can't take calls now: it doesn't hear Chat right away";
    [ObservableProperty] private bool _isPicked;
}
