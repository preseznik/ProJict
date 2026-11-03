using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProJict.Core.Agents;

/// <summary>
/// Approving an agent's request in ProJict Chat (#54ef1c15). Claude Code and Codex run a PermissionRequest hook just
/// before they'd ask you to allow something (a command, a file change…); set up with <c>ProJictBridge.exe
/// --permission-hook</c>, it brings the request here: the agent asks you privately in Chat what it wants to do, and your
/// 👍 or 👎 goes back as the hook's answer. Without an answer (or with ProJict closed) the usual prompt is there as before.
/// </summary>
public static class PermissionAsk
{
    /// <summary>The first line the bridge sends on ProJict's pipe: <c>{"projictApproval":1,"hook":{…},"as":"Ada"}</c>.</summary>
    public const string HelloKey = "projictApproval";
    /// <summary>How long the bridge waits for your answer before leaving it to the usual prompt; the hook's own timeout is longer.</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(285);
    public const int HookTimeoutSeconds = 300;
    private const int MostShown = 900;

    /// <summary>The request in the bridge's first line, or null when it's something else.</summary>
    public static (JsonObject Hook, string? Agent)? Request(string line)
    {
        if (!line.Contains(HelloKey, StringComparison.Ordinal)) return null;
        try
        {
            if (JsonNode.Parse(line.TrimStart('﻿')) is not JsonObject hello || !hello.ContainsKey(HelloKey)) return null;
            var agent = (hello["as"] as JsonValue)?.TryGetValue<string>(out var a) == true && a.Trim().Length > 0 ? a.Trim() : null;
            return (hello["hook"] as JsonObject ?? [], agent);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>What the agent wants to do, in a few words and the exact command, path or address.</summary>
    public static (string What, string? Exactly) Describe(JsonObject hook)
    {
        var tool = Text(hook["tool_name"]) ?? "a tool";
        var input = hook["tool_input"] as JsonObject;
        string? Field(string name) => input != null ? Text(input[name]) : null;
        return tool switch
        {
            "Bash" or "PowerShell" or "shell" or "exec_command" => ("run a command", Field("command") ?? Json(input)),
            "Write" => ("write a file", Field("file_path")),
            "Edit" or "MultiEdit" => ("change a file", Field("file_path")),
            "NotebookEdit" => ("change a notebook", Field("notebook_path")),
            "Read" => ("read a file", Field("file_path")),
            "WebFetch" => ("open a web page", Field("url")),
            "WebSearch" => ("search the web", Field("query")),
            _ when tool.StartsWith("mcp__", StringComparison.Ordinal) && tool.Split("__") is [_, var server, var name, ..] =>
                ($"use {name} ({server})", Json(input)),
            _ => ($"use {tool}", Json(input)),
        };
    }

    /// <summary>The agent's private message to you: what it wants to do, exactly, and how to answer.</summary>
    public static string Question(JsonObject hook, string client)
    {
        var (what, exactly) = Describe(hook);
        var why = Text((hook["tool_input"] as JsonObject)?["description"]);
        var text = $"May I {what}?";
        if (why != null && why.Length <= 200) text += $" ({why.TrimEnd('.')})";
        if (exactly != null)
        {
            // Chat doesn't format code: each line of it on its own, marked, so it can't pass for the agent's own words.
            var shown = exactly.Length > MostShown ? exactly[..MostShown] + "…" : exactly;
            text += string.Concat(shown.Replace("\r\n", "\n").Split('\n').Select(line => "\n› " + line));
        }
        return text + $"\n👍 allows it, 👎 refuses. {client} asked; you can also answer there.";
    }

    /// <summary>What the hook prints for Claude Code (and Codex): your answer as its decision.</summary>
    public static JsonObject HookOutput(bool allow, string? message) => new()
    {
        ["hookSpecificOutput"] = new JsonObject
        {
            ["hookEventName"] = "PermissionRequest",
            ["decision"] = allow ? new JsonObject { ["behavior"] = "allow" } : new JsonObject { ["behavior"] = "deny", ["message"] = message ?? "Refused in ProJict Chat." },
        },
    };

    private static string? Text(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Length > 0 ? s.Trim() : null;

    private static string? Json(JsonObject? input) => input is { Count: > 0 } ? input.ToJsonString(new JsonSerializerOptions { WriteIndented = false }) : null;
}
