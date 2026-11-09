using System.Text.Json.Nodes;
using ProJict.Core.Agents;
using ProJict.Core.Chat;
using ProJict.Core.Storage;

namespace ProJict.App.Services;

/// <summary>
/// Putting every agent to sleep (the Agents view's big button): each is told to finish what it's doing and stop. Starting
/// new work is refused, and Chat and other news wait (so nothing wakes them) until you wake them up, when they're told to
/// carry on and the news comes through.
/// </summary>
public sealed partial class AgentTools
{
    public bool IsAsleep => _state.Settings.Sleep is { WokenAt: null };

    /// <summary>Everyone was put to sleep or woken: chat watchers say so at once.</summary>
    public event Action? SleepChanged;

    public void PutToSleep()
    {
        if (IsAsleep) return;
        _state.Settings.Sleep = new AgentsSleep { By = _state.UserName };
        _state.MarkSettingsDirty();
        SleepChanged?.Invoke();
    }

    public void WakeUp()
    {
        if (_state.Settings.Sleep is not { WokenAt: null } sleep) return;
        sleep.WokenAt = DateTime.Now;
        sleep.WokenBy = _state.UserName;
        _state.MarkSettingsDirty();
        SleepChanged?.Invoke();
    }

    private string AsleepText(AgentsSleep sleep) =>
        $"ProJict is asleep: {sleep.By} put every agent to sleep at {sleep.At:HH:mm}. Finish what you're doing (complete it, or comment where you got to " +
        "and release it with update_item state todo), then stop and stay idle; start nothing new until you're woken.";

    /// <summary>Starting work while everyone's asleep: refused.</summary>
    private void CheckAwake()
    {
        if (_state.Settings.Sleep is { WokenAt: null } sleep) throw new McpToolException(AsleepText(sleep), ToolProblem.Conflict);
    }

    /// <summary>For waitingForYou and the watcher: going to sleep, and waking up again (each once).</summary>
    private List<string> SleepLines(McpSession session)
    {
        if (_state.Settings.Sleep is not { } sleep) return [];
        var key = $"sleep:{sleep.At.Ticks}";
        if (sleep.WokenAt is not { } woken)
            return session.Noticed.Add(key)
                ? [$"{sleep.By} put ProJict to sleep ({sleep.At:HH:mm}): finish what you're doing now (complete the item you're on, or comment where you " +
                    "got to and release it with update_item state todo), then stop and stay idle. Start nothing new: Chat and other news wait until you're " +
                    "woken, and you'll hear it then."]
                : [];
        return session.Noticed.Contains(key) && session.Noticed.Add($"wake:{woken.Ticks}")
            ? [$"{sleep.WokenBy} woke ProJict up ({woken:HH:mm}): carry on where you left off; what waited for you comes through now."]
            : [];
    }

    /// <summary>Every tool result says it while everyone's asleep.</summary>
    private void MarkAsleep(JsonObject result)
    {
        if (_state.Settings.Sleep is { WokenAt: null } sleep) result["asleep"] = AsleepText(sleep);
        if (_main.Agents.MyStatus != UserStatus.Online &&
            Presence.UnavailableNotice(_state.UserName, _main.Agents.MyStatus, _main.Agents.MyState) is { } unavailable)
            result["userUnavailable"] = unavailable;
    }

    /// <summary>Wakes chat watchers when the user changes their availability status (#c1c1a259).</summary>
    public void NotifyUserStatusChanged() => SleepChanged?.Invoke();

    /// <summary>For waitingForYou and the watcher: when the user sets Away, Busy or Appear offline, and when they're back (#c1c1a259).</summary>
    private List<string> UserStatusLines(McpSession session)
    {
        var status = _main.Agents.MyStatus;
        var changedAt = _main.Agents.MyStatusChangedAt;
        if (status != UserStatus.Online)
        {
            var key = $"userstatus:{status}:{changedAt.Ticks}";
            if (session.Noticed.Add(key) && Presence.UnavailableNotice(_state.UserName, status, _main.Agents.MyState) is { } notice)
                return [notice];
            return [];
        }
        if (changedAt > DateTime.MinValue &&
            session.Noticed.Any(k => k.StartsWith("userstatus:", StringComparison.Ordinal) && !k.StartsWith("userstatus:online:", StringComparison.Ordinal)) &&
            session.Noticed.Add($"userstatus:online:{changedAt.Ticks}"))
            return [$"{_state.UserName} is online and available again."];
        return [];
    }
}
