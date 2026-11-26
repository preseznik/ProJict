using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProJict.Core.Agents;

/// <summary>
/// Pictures in a tool's answer (a screenshot of ProJict's browser, image search results): the tool puts them in its JSON
/// under <see cref="Key"/>, and MCP sends each as an image of its own after the text, so the agent sees it. Over the
/// REST API they're left out (the answer says where each is saved).
/// </summary>
public static class McpImages
{
    public const string Key = "$images";

    /// <summary>Adds a picture to an answer: its bytes and media type ("image/jpeg").</summary>
    public static void Add(JsonObject answer, byte[] data, string mediaType)
    {
        if (answer[Key] is not JsonArray list) answer[Key] = list = [];
        list.Add(new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(data), ["mimeType"] = mediaType });
    }

    /// <summary>Takes the pictures out of an answer's text, as MCP image content; the text is left without them.</summary>
    public static List<JsonNode> Take(ref string text)
    {
        if (!text.Contains("\"" + Key + "\"", StringComparison.Ordinal)) return [];
        try
        {
            if (JsonNode.Parse(text) is not JsonObject json || json[Key] is not JsonArray list) return [];
            json.Remove(Key);
            text = json.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            return [.. list.Select(i => i!.DeepClone())];
        }
        catch (JsonException) { return []; }
    }
}
