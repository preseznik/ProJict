using System.IO;
using ProJict.Core.Agents;

namespace ProJict.App.Services;

/// <summary>
/// Disconnecting agents (#7559d088): you tell an agent (or all of a room's or a channel's) to leave ProJict. Its calls are
/// refused with why, its bridge leaves (ProJictBridge 3 and newer; it doesn't reconnect), and its chat watchers print a
/// last line and stop. An older bridge reconnecting at once, or a watcher trying again, is turned away; an agent you
/// start again later connects as usual.
/// </summary>
public sealed partial class AgentTools
{
    /// <summary>How long after you disconnect an agent a new session under its name is still turned away (an old bridge reconnecting).</summary>
    public static readonly TimeSpan CutOffWindow = TimeSpan.FromMinutes(2);

    /// <summary>Agents you disconnected, and when; their watchers are turned away until they connect again later.</summary>
    private readonly Dictionary<string, DateTime> _cutOff = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Agents were disconnected (UI thread): their watchers wake to end.</summary>
    public event Action? AgentsCutOff;

    private string CutOffMessage => $"{_state.UserName} disconnected you from ProJict: stop using ProJict's tools, and don't reconnect or start a chat " +
                                    "watcher again unless they ask you to.";

    private string CutOffWatchLine => $"[ProJict] {_state.UserName} disconnected you from ProJict: your chat watcher stops here. Stop using ProJict's " +
                                      "tools, and don't start it again unless they ask you to.";

    /// <summary>Your agents connected to this ProJict now (not a connected ProJict's), by name.</summary>
    public IReadOnlyList<string> ConnectedAgentNames()
    {
        List<string> names;
        lock (_live) names = [.. _live.Where(HasOwnName).Select(s => s.AgentName)];
        lock (_watchers) names.AddRange(_watchers.Keys);
        return [.. names.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// (UI thread.) Disconnects the agents: their sessions are refused from now on and their bridges told to leave, and
    /// their watchers end. Returns how many of them were connected.
    /// </summary>
    public int Disconnect(IEnumerable<string> agents)
    {
        var now = DateTime.Now;
        var connected = 0;
        foreach (var agent in agents.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            List<McpSession> sessions;
            lock (_live) sessions = [.. _live.Where(s => string.Equals(s.AgentName, agent, StringComparison.OrdinalIgnoreCase))];
            bool watched;
            lock (_watchers) watched = _watchers.ContainsKey(agent);
            if (sessions.Count == 0 && !watched) continue;
            connected++;
            lock (_cutOff) _cutOff[agent] = now;
            foreach (var session in sessions)
            {
                session.CutOff = true;
                // A bridge that knows how leaves now, so the agent's client sees ProJict go; an older one is refused on every call.
                if (session.BridgeProtocol >= McpServer.DisconnectProtocol && session.SendLine is { } send) _ = SendQuietly(send, McpServer.DisconnectLine(CutOffMessage));
            }
            // A tool's browser it had is free for others now.
            foreach (var session in sessions) _main.Browsers.ReleaseHeldBy(session);
        }
        if (connected > 0) AgentsCutOff?.Invoke();
        return connected;
    }

    private static async Task SendQuietly(Func<string, Task> send, string line)
    {
        try { await send(line); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException) { }
    }

    /// <summary>(UI thread.) The session belongs to an agent you disconnected: its calls are refused.</summary>
    private bool IsCutOff(McpSession session)
    {
        if (session.CutOff) return true;
        if (!HasOwnName(session) || CutOffAt(session.AgentName) is not { } at) return false;
        // Back right away under the same name: an older bridge reconnecting, turned away too.
        if (DateTime.Now - at < CutOffWindow) return session.CutOff = true;
        return false;
    }

    /// <summary>
    /// (UI thread.) A session takes a name: refused while that agent was just disconnected; later, it's connecting again
    /// (you started it), and its watchers may come back too.
    /// </summary>
    private void CheckNotCutOff(McpSession session, string name)
    {
        if (JustCutOff(name))
        {
            session.CutOff = true;
            throw new McpToolException(CutOffMessage, ToolProblem.Forbidden);
        }
        lock (_cutOff) _cutOff.Remove(name);
    }

    /// <summary>The agent was disconnected a moment ago: a session under its name now is an old bridge coming back.</summary>
    private bool JustCutOff(string name) => CutOffAt(name) is { } at && DateTime.Now - at < CutOffWindow;

    /// <summary>When the agent was disconnected, if it is (its watchers are turned away meanwhile).</summary>
    private DateTime? CutOffAt(string agent)
    {
        lock (_cutOff) return _cutOff.TryGetValue(agent, out var at) ? at : null;
    }
}
