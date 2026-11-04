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
    /// <summary>There's an agent here to call: in your own ProJict's channels you can write in.</summary>
    public bool CanCall => Channel is { } channel && Here.Host == null && CanWrite && AgentsIn(channel).Any();

    public string CallToolTip => Channel is { Direct: true } direct
        ? $"Call {ChatRoom.Title(direct, MyName)}: a voice call (you speak, it answers aloud)"
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
            StartCallWith(channel, [.. AgentsIn(channel).Select(m => m.Name)]);
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
