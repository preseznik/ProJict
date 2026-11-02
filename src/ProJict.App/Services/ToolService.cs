using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using ProJict.Core.Tools;

namespace ProJict.App.Services;

public enum ToolKeyAnswer { Once, Always, No }

/// <summary>An agent asks for a tool's API key; it waits in ProJict's bar until you answer.</summary>
public sealed class ToolKeyRequest(string agent, ExternalTool tool, string? why)
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Agent { get; } = agent;
    public ExternalTool Tool { get; } = tool;
    public string? Why { get; } = why;
    public DateTime At { get; } = DateTime.Now;
    public ToolKeyAnswer? Answer { get; internal set; }
    public string Text => $"{Agent} wants the {Tool.Name} API key" + (Why != null ? $": “{Why}”" : ".");
}

/// <summary>
/// The outside tools agents may use (Settings › Tools), with their API keys protected for your Windows user. An agent
/// gets a key only when you allow it: once, or always for that agent. Without a path (tests) it lives in memory only.
/// Used on the UI thread.
/// </summary>
public sealed class ToolService
{
    private static readonly byte[] Entropy = "ProJict tools"u8.ToArray();
    private readonly string? _path;
    /// <summary>Keys you let an agent have once (by agent name and tool), until it takes them.</summary>
    private readonly HashSet<(string Agent, Guid Tool)> _grants = [];

    public ToolService(string? path)
    {
        _path = path;
        Data = path != null ? ToolData.Load(path) : new ToolData();
        if (Data.SeedOnce()) Save();
    }

    public ToolData Data { get; }
    public IReadOnlyList<ExternalTool> Tools => Data.Tools;

    /// <summary>Requests waiting for your answer, oldest first; answered ones stay until the agent hears it.</summary>
    public ObservableCollection<ToolKeyRequest> Requests { get; } = [];
    public IEnumerable<ToolKeyRequest> Waiting => Requests.Where(r => r.Answer == null);

    /// <summary>A tool or a request changed.</summary>
    public event Action? Changed;

    public void Save()
    {
        if (_path != null)
        {
            try { Data.Save(_path); }
            catch (Exception) { /* kept in memory; tried again on the next change */ }
        }
        Changed?.Invoke();
    }

    private static string Key(string agent) => agent.Trim().ToLowerInvariant();

    public string? KeyOf(ExternalTool tool)
    {
        if (string.IsNullOrEmpty(tool.ProtectedKey)) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(tool.ProtectedKey), Entropy, DataProtectionScope.CurrentUser)); }
        catch (Exception) { return null; }
    }

    /// <summary>Sets (or with null, removes) a tool's API key.</summary>
    public void SetKey(ExternalTool tool, string? key)
    {
        key = key?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            tool.ProtectedKey = null;
            tool.KeyEnding = null;
        }
        else
        {
            tool.ProtectedKey = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key), Entropy, DataProtectionScope.CurrentUser));
            tool.KeyEnding = key.Length > 8 ? key[^4..] : null;
        }
        Save();
    }

    public void Remove(ExternalTool tool)
    {
        if (!Data.Tools.Remove(tool)) return;
        foreach (var request in Requests.Where(r => r.Tool == tool).ToList()) Requests.Remove(request);
        _grants.RemoveWhere(g => g.Tool == tool.Id);
        Save();
    }

    /// <summary>
    /// What an agent gets when it asks for a key: the key when you allowed it (always, or once and not taken yet); else
    /// null, with your "no" (once) or the request that waits for you.
    /// </summary>
    public (string? Key, ToolKeyRequest? Request, bool Refused) Ask(string agent, ExternalTool tool, string? why)
    {
        if (tool.IsAlwaysAllowed(agent) || _grants.Remove((Key(agent), tool.Id))) return (KeyOf(tool), null, false);
        var mine = Requests.LastOrDefault(r => r.Tool == tool && Key(r.Agent) == Key(agent));
        if (mine is { Answer: ToolKeyAnswer.No })
        {
            Requests.Remove(mine);
            Changed?.Invoke();
            return (null, mine, true);
        }
        if (mine is { Answer: null }) return (null, mine, false);
        var request = new ToolKeyRequest(agent.Trim(), tool, string.IsNullOrWhiteSpace(why) ? null : why.Trim());
        Requests.Add(request);
        Changed?.Invoke();
        return (null, request, false);
    }

    /// <summary>Your answer to a request: the key once, always for that agent, or no.</summary>
    public void Answer(ToolKeyRequest request, ToolKeyAnswer answer)
    {
        if (request.Answer != null) return;
        request.Answer = answer;
        switch (answer)
        {
            case ToolKeyAnswer.Once:
                _grants.Add((Key(request.Agent), request.Tool.Id));
                break;
            case ToolKeyAnswer.Always:
                if (!request.Tool.IsAlwaysAllowed(request.Agent)) request.Tool.AlwaysAllowed.Add(request.Agent);
                break;
        }
        // An allowed request is done once the agent heard; a "no" stays until it asks again.
        if (answer != ToolKeyAnswer.No) Requests.Remove(request);
        Answered.RemoveAll(r => DateTime.Now - r.At > AnswersKeptFor);
        Answered.Add(request);
        Save();
    }

    /// <summary>Your answers, for the agents to hear (waitingForYou, once each), from the last day.</summary>
    public List<ToolKeyRequest> Answered { get; } = [];
    private static readonly TimeSpan AnswersKeptFor = TimeSpan.FromDays(1);

    public void Disallow(ExternalTool tool, string agent)
    {
        if (tool.AlwaysAllowed.RemoveAll(a => string.Equals(a, agent, StringComparison.OrdinalIgnoreCase)) > 0) Save();
    }
}
