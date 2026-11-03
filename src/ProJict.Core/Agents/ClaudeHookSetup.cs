using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProJict.Core.Agents;

/// <summary>
/// Settings › AI agents › Set up in Claude Code: adds ProJict's PermissionRequest hook to Claude Code's settings
/// (<c>~/.claude/settings.json</c>), keeping everything else in it; or takes it out again. Only ever when you click it.
/// </summary>
public static class ClaudeHookSetup
{
    public const string Marker = "--permission-hook";

    public static string SettingsPath(string home) => Path.Combine(home, ".claude", "settings.json");

    /// <summary>Whether the settings already have ProJict's hook.</summary>
    public static bool IsSetUp(string? json) =>
        json != null && Parse(json) is { } root && Hooks(root).Any(IsOurs);

    /// <summary>The settings with ProJict's hook added (the same when it's there); throws when they aren't JSON it can read.</summary>
    public static string Add(string? json, string command)
    {
        var root = json is { Length: > 0 } ? Parse(json) ?? throw new InvalidOperationException("Claude Code's settings.json isn't JSON ProJict can read, so it was left as it is.") : [];
        if (Hooks(root).Any(IsOurs)) return json!;
        var hooks = root["hooks"] as JsonObject ?? (JsonObject)(root["hooks"] = new JsonObject());
        var list = hooks["PermissionRequest"] as JsonArray ?? (JsonArray)(hooks["PermissionRequest"] = new JsonArray());
        list.Add(new JsonObject
        {
            ["matcher"] = "*",
            ["hooks"] = new JsonArray(new JsonObject
            {
                ["type"] = "command",
                ["command"] = command,
                ["timeout"] = PermissionAsk.HookTimeoutSeconds,
            }),
        });
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>The settings without ProJict's hook.</summary>
    public static string Remove(string json)
    {
        var root = Parse(json) ?? throw new InvalidOperationException("Claude Code's settings.json isn't JSON ProJict can read, so it was left as it is.");
        if (root["hooks"] is JsonObject hooks && hooks["PermissionRequest"] is JsonArray list)
        {
            foreach (var group in list.OfType<JsonObject>().ToList())
            {
                if (group["hooks"] is JsonArray inner)
                    foreach (var hook in inner.OfType<JsonObject>().Where(IsOurs).ToList()) inner.Remove(hook);
                if (group["hooks"] is JsonArray { Count: 0 }) list.Remove(group);
            }
            if (list.Count == 0) hooks.Remove("PermissionRequest");
            if (hooks.Count == 0) root.Remove("hooks");
        }
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject? Parse(string json)
    {
        try { return JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static IEnumerable<JsonObject> Hooks(JsonObject root) =>
        root["hooks"] is JsonObject { } hooks && hooks["PermissionRequest"] is JsonArray list
            ? list.OfType<JsonObject>().SelectMany(g => (g["hooks"] as JsonArray ?? []).OfType<JsonObject>())
            : [];

    private static bool IsOurs(JsonObject hook) =>
        hook["command"] is JsonValue v && v.TryGetValue<string>(out var c) && c.Contains("ProJictBridge", StringComparison.OrdinalIgnoreCase) && c.Contains(Marker, StringComparison.Ordinal);
}
