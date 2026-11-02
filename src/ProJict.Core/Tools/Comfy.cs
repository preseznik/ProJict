using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ProJict.Core.Storage;

namespace ProJict.Core.Tools;

/// <summary>A ComfyUI server a tool runs workflows on: a name and its address (http://127.0.0.1:8188, or a PC on your network).</summary>
public sealed class ComfyEndpoint
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";

    /// <summary>The server's own address, however it was typed or pasted (see <see cref="ServerAddress"/>).</summary>
    public string Address
    {
        get;
        set => field = ServerAddress(value);
    } = "";

    /// <summary>ComfyUI's own endpoints: an address pasted with one of these on the end means the server before it.</summary>
    private static readonly string[] ApiPaths = ["/prompt", "/queue", "/history", "/system_stats", "/object_info", "/upload/image", "/view", "/ws", "/api"];

    /// <summary>
    /// "192.168.1.173:9696/prompt" → "http://192.168.1.173:9696": http:// when it has no scheme, and without ComfyUI's
    /// endpoint paths, a query, a # or a trailing slash, since ProJict adds the paths itself. A base path a proxy puts
    /// ComfyUI under ("https://gpu.example/comfy") stays.
    /// </summary>
    public static string ServerAddress(string? text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return "";
        if (!text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;
        // Not an address yet ("http://" while it's being typed): kept as it is.
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Host.Length == 0) return text;
        var path = uri.AbsolutePath.TrimEnd('/');
        for (var cut = true; cut;)
        {
            cut = false;
            foreach (var api in ApiPaths)
                if (path.EndsWith(api, StringComparison.OrdinalIgnoreCase))
                {
                    path = path[..^api.Length].TrimEnd('/');
                    cut = true;
                }
        }
        return uri.GetLeftPart(UriPartial.Authority) + path;
    }
}

/// <summary>What a workflow's input takes: text (a prompt), a number (seed, size) or a picture (uploaded to ComfyUI first).</summary>
public enum WorkflowInputKind { Text, Number, Image }

/// <summary>Something an agent fills in when it runs a workflow: "prompt", "seed", "width", "image"; where it goes in the graph.</summary>
public sealed record WorkflowInput(string Key, string Node, string Field, WorkflowInputKind Kind, string? Default, string? Title)
{
    /// <summary>"prompt (text): a castle on a hill".</summary>
    public string Describe() =>
        $"{Key} ({Kind.ToString().ToLowerInvariant()}){(Title != null ? $", {Title}" : "")}" + (Default is { Length: > 0 } d ? $": {(d.Length > 80 ? d[..80] + "…" : d)}" : "");
}

/// <summary>
/// A ComfyUI workflow in the library: its graph in ComfyUI's API format (Workflow › Export (API)), a name, what it makes
/// and the endpoints it runs on (none named: any of its tool's). Kept in the data folder, one file each.
/// </summary>
public sealed class ComfyWorkflow
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>The ComfyUI tool it belongs to, by name (tools are kept per computer, the library with the workspace).</summary>
    public string Tool { get; set; } = "";
    public string Name { get; set; } = "";
    public string Purpose { get; set; } = "";
    /// <summary>The endpoints it runs on, by name; empty: any of its tool's.</summary>
    public List<string> Endpoints { get; set; } = [];
    public JsonObject Graph { get; set; } = [];
    public DateTime AddedAt { get; set; } = DateTime.Now;
    public string AddedBy { get; set; } = "";

    [JsonIgnore] public IReadOnlyList<WorkflowInput> Inputs => ComfyGraph.FindInputs(Graph);

    public bool RunsOn(ComfyEndpoint endpoint) => Endpoints.Count == 0 || Endpoints.Any(e => string.Equals(e, endpoint.Name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Reading a ComfyUI graph (API format: nodes by id, each a class_type and its inputs): the inputs worth filling in, and
/// filling them in. Prompts are found by what they feed (a sampler's positive or negative), sizes and seeds by node.
/// </summary>
public static class ComfyGraph
{
    private static IEnumerable<(string Id, string Class, JsonObject Inputs, string? Title)> Nodes(JsonObject graph) =>
        graph.Where(n => n.Value is JsonObject node && (string?)node["class_type"] is { } && node["inputs"] is JsonObject)
            .Select(n => (n.Key, (string)n.Value!["class_type"]!, (JsonObject)n.Value["inputs"]!, (string?)n.Value["_meta"]?["title"]));

    /// <summary>Whether it looks like an API-format graph at all (not the UI's "nodes"/"links" save format).</summary>
    public static string? Problem(JsonNode? graph)
    {
        if (graph is not JsonObject json) return "That isn't a ComfyUI workflow (a JSON object).";
        if (json.ContainsKey("nodes") && json.ContainsKey("links"))
            return "That's ComfyUI's own save format. In ComfyUI, use Workflow › Export (API) and add that file.";
        return Nodes(json).Any() ? null : "That file has no ComfyUI nodes (class_type and inputs).";
    }

    private static string? LinkedNode(JsonNode? input) => input is JsonArray { Count: 2 } link ? link[0]?.ToString() : null;

    public static IReadOnlyList<WorkflowInput> FindInputs(JsonObject graph)
    {
        var nodes = Nodes(graph).ToList();
        var positive = new HashSet<string>();
        var negative = new HashSet<string>();
        foreach (var (_, _, inputs, _) in nodes)
        {
            if (LinkedNode(inputs["positive"]) is { } p) positive.Add(p);
            if (LinkedNode(inputs["negative"]) is { } n) negative.Add(n);
        }
        var found = new List<WorkflowInput>();
        var taken = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // "prompt", then "prompt 2" for a second one.
        string Key(string wanted)
        {
            var n = taken.GetValueOrDefault(wanted) + 1;
            taken[wanted] = n;
            return n == 1 ? wanted : $"{wanted} {n}";
        }
        void Add(string key, string node, string field, WorkflowInputKind kind, JsonNode? value, string? title) =>
            found.Add(new WorkflowInput(Key(key), node, field, kind, value?.ToString(), title));
        foreach (var (id, kind, inputs, title) in nodes.OrderBy(n => int.TryParse(n.Id, out var i) ? i : int.MaxValue).ThenBy(n => n.Id, StringComparer.Ordinal))
        {
            // A text that feeds a positive anywhere is the prompt, even if it also feeds a negative.
            if (inputs["text"] is JsonValue text && text.TryGetValue<string>(out _))
                Add(negative.Contains(id) && !positive.Contains(id) ? "negative" : "prompt", id, "text", WorkflowInputKind.Text, text, title);
            foreach (var field in new[] { "seed", "noise_seed" })
                if (inputs[field] is JsonValue seed && seed.TryGetValue<long>(out _)) Add("seed", id, field, WorkflowInputKind.Number, seed, title);
            if (kind.Contains("Latent", StringComparison.OrdinalIgnoreCase) && kind.StartsWith("Empty", StringComparison.OrdinalIgnoreCase))
                foreach (var field in new[] { "width", "height", "batch_size" })
                    if (inputs[field] is JsonValue number && number.TryGetValue<long>(out _))
                        Add(field == "batch_size" ? "batch" : field, id, field, WorkflowInputKind.Number, number, title);
            if (kind == "LoadImage" && inputs["image"] is JsonValue image && image.TryGetValue<string>(out _))
                Add("image", id, "image", WorkflowInputKind.Image, image, title);
        }
        return found;
    }

    /// <summary>
    /// The graph with the values filled in (a copy); a seed of "random" gets a random one. Pictures are given as the name
    /// ComfyUI gave the upload. Throws ArgumentException naming the input that's wrong.
    /// </summary>
    public static JsonObject Fill(JsonObject graph, IReadOnlyDictionary<string, string> values, Random? random = null)
    {
        var copy = (JsonObject)graph.DeepClone();
        var inputs = FindInputs(graph);
        foreach (var (key, value) in values)
        {
            var input = inputs.FirstOrDefault(i => string.Equals(i.Key, key.Trim(), StringComparison.OrdinalIgnoreCase))
                        ?? throw new ArgumentException($"The workflow has no input “{key}”. Its inputs: {string.Join(", ", inputs.Select(i => i.Key))}.");
            var target = (JsonObject)copy[input.Node]!["inputs"]!;
            target[input.Field] = input.Kind switch
            {
                WorkflowInputKind.Number when input.Key == "seed" && value.Trim().Equals("random", StringComparison.OrdinalIgnoreCase) =>
                    JsonValue.Create((random ?? Random.Shared).NextInt64(0, 1L << 48)),
                WorkflowInputKind.Number => long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                    ? JsonValue.Create(n) : throw new ArgumentException($"{input.Key} must be a whole number."),
                _ => JsonValue.Create(value),
            };
        }
        return copy;
    }
}

/// <summary>The workflow library: one JSON file per workflow in the data folder's "workflows" folder (or in memory).</summary>
public sealed class WorkflowLibrary(string? folder)
{
    public List<ComfyWorkflow> Workflows { get; } = Load(folder);

    private static List<ComfyWorkflow> Load(string? folder)
    {
        var list = new List<ComfyWorkflow>();
        if (folder == null || !Directory.Exists(folder)) return list;
        foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
        {
            try
            {
                if (Json.Deserialize<ComfyWorkflow>(File.ReadAllText(file)) is { } workflow) list.Add(workflow);
            }
            catch (Exception) { /* a broken file is skipped */ }
        }
        return [.. list.OrderBy(w => w.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>A tool's workflows, by name.</summary>
    public IEnumerable<ComfyWorkflow> Of(string tool) =>
        Workflows.Where(w => string.Equals(w.Tool, tool, StringComparison.OrdinalIgnoreCase)).OrderBy(w => w.Name, StringComparer.CurrentCultureIgnoreCase);

    /// <summary>The workflow a name means (case doesn't count), or its id (8 characters are enough), in one tool or any.</summary>
    public ComfyWorkflow? Find(string key, string? tool = null)
    {
        key = key.Trim();
        var pool = tool != null ? Of(tool) : Workflows;
        return pool.FirstOrDefault(w => string.Equals(w.Name, key, StringComparison.OrdinalIgnoreCase))
               ?? (key.Length >= 8 ? pool.FirstOrDefault(w => w.Id.ToString().StartsWith(key, StringComparison.OrdinalIgnoreCase)) : null);
    }

    /// <summary>Adds a workflow from a file's text (API format); throws ArgumentException saying why it can't.</summary>
    public ComfyWorkflow Add(string tool, string name, string json, string by)
    {
        JsonNode? graph;
        try { graph = JsonNode.Parse(json); }
        catch (System.Text.Json.JsonException) { throw new ArgumentException("That file isn't JSON."); }
        if (ComfyGraph.Problem(graph) is { } problem) throw new ArgumentException(problem);
        var unique = name.Trim().Length > 0 ? name.Trim() : "Workflow";
        for (var n = 2; Of(tool).Any(w => string.Equals(w.Name, unique, StringComparison.OrdinalIgnoreCase)); n++) unique = $"{name.Trim()} {n}";
        var workflow = new ComfyWorkflow { Tool = tool, Name = unique, Graph = (JsonObject)graph!, AddedBy = by };
        Workflows.Add(workflow);
        Save(workflow);
        return workflow;
    }

    public void Save(ComfyWorkflow workflow)
    {
        if (folder == null) return;
        Directory.CreateDirectory(folder);
        AtomicFile.WriteAllText(Path.Combine(folder, workflow.Id + ".json"), Json.Serialize(workflow));
    }

    public void Remove(ComfyWorkflow workflow)
    {
        Workflows.Remove(workflow);
        if (folder != null && File.Exists(Path.Combine(folder, workflow.Id + ".json"))) File.Delete(Path.Combine(folder, workflow.Id + ".json"));
    }
}
