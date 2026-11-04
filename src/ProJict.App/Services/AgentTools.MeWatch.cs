using System.Text.Json.Nodes;
using System.Threading.Channels;
using ProJict.Core.Chat;
using ProJict.Core.Model;

namespace ProJict.App.Services;

/// <summary>
/// /me/watch (#907c9c4c): ProJict on your phone hears at once what waits for you, a JSON line per event. "waiting" gives
/// the counts (agents' questions, ship plans, work to review, unread messages) when it starts and whenever they change;
/// "message" is a new message in one of your channels or private conversations, by someone else; "ping" comes when
/// nothing else has for a while, so the phone knows the stream is alive.
/// </summary>
public sealed partial class AgentTools
{
    /// <summary>The longest the stream stays quiet before a "ping".</summary>
    public static TimeSpan MeWatchKeepAlive { get; set; } = TimeSpan.FromSeconds(30);

    public async Task WatchMeAsync(Func<string, Task> write, CancellationToken stop)
    {
        var lines = System.Threading.Channels.Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        JsonNode? told = null;

        // On the UI thread, where ProJict changes things.
        void Counts()
        {
            var now = MeWho()["waiting"]!;
            if (told != null && JsonNode.DeepEquals(now, told)) return;
            told = now.DeepClone();
            lines.Writer.TryWrite(MeLine("waiting", now.DeepClone()));
        }
        void Later(Action action)
        {
            if (_dispatcher.CheckAccess()) action();
            else _dispatcher.BeginInvoke(action);
        }
        void OnPosted(ChatChannel channel, ChatMessage message) => Later(() =>
        {
            if (ChatRoom.Member(channel, YourName) != null && !message.Notice && !string.Equals(message.By, YourName, StringComparison.OrdinalIgnoreCase))
                lines.Writer.TryWrite(MeLine("message", new JsonObject
                {
                    ["channel"] = ChannelRef(channel),
                    ["mentionsYou"] = ChatRoom.Mentions(channel, message, YourName),
                    ["message"] = MessageForMe(message),
                }));
            Counts();
        });
        void OnChatChanged(ChatChannel channel) => Later(Counts);
        void OnProjectChanged(Project project) => Later(Counts);

        await _dispatcher.InvokeAsync(() =>
        {
            Chat.Posted += OnPosted;
            Chat.Changed += OnChatChanged;
            _state.ProjectChanged += OnProjectChanged;
            Counts();
        });
        try
        {
            while (!stop.IsCancellationRequested)
            {
                using var quiet = CancellationTokenSource.CreateLinkedTokenSource(stop);
                quiet.CancelAfter(MeWatchKeepAlive);
                string line;
                try { line = await lines.Reader.ReadAsync(quiet.Token); }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested) { line = MeLine("ping", null); }
                await write(line);
            }
        }
        finally
        {
            await _dispatcher.InvokeAsync(() =>
            {
                Chat.Posted -= OnPosted;
                Chat.Changed -= OnChatChanged;
                _state.ProjectChanged -= OnProjectChanged;
            });
        }
    }

    private static string MeLine(string type, JsonNode? data)
    {
        var json = new JsonObject { ["type"] = type, ["at"] = DateTime.Now };
        if (data != null) json["data"] = data;
        return json.ToJsonString();
    }
}
