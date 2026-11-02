using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using ProJict.Core.Tools;

namespace ProJict.App.Services;

/// <summary>How a ComfyUI server is doing: reachable or not, how many runs it has, and its graphics card.</summary>
public sealed record ComfyStatus(bool Reachable, int Running, int Pending, string? Device, string? Problem, bool Answers = false)
{
    /// <summary>Something answers at the address, but not as ComfyUI does (a 404): the Problem says so.</summary>
    public string Describe() => Answers ? Problem ?? "" : !Reachable ? $"Not reachable{(Problem != null ? $": {Problem}" : "")}"
        : $"Reachable · {(Running + Pending == 0 ? "nothing queued" : $"{Running} running, {Pending} waiting")}{(Device != null ? $" · {Device}" : "")}";
}

/// <summary>A file a workflow made, as ComfyUI keeps it.</summary>
public sealed record ComfyOutput(string FileName, string Subfolder, string Type);

/// <summary>
/// Talks to a ComfyUI server over its HTTP API: checks it (/queue, /system_stats), uploads a picture for a workflow's
/// LoadImage (/upload/image), queues a workflow (/prompt), waits for it (/history) and fetches what it made (/view).
/// </summary>
public sealed class ComfyClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    /// <summary>How often a run is looked at while it waits.</summary>
    public TimeSpan PollEvery { get; set; } = TimeSpan.FromSeconds(1);
    /// <summary>A run that hasn't finished by then is given up on.</summary>
    public TimeSpan GiveUpAfter { get; set; } = TimeSpan.FromMinutes(30);

    private static string Url(ComfyEndpoint endpoint, string path) => endpoint.Address.TrimEnd('/') + path;

    public async Task<ComfyStatus> CheckAsync(ComfyEndpoint endpoint, CancellationToken stop = default)
    {
        try
        {
            using var quick = CancellationTokenSource.CreateLinkedTokenSource(stop);
            quick.CancelAfter(TimeSpan.FromSeconds(4));
            var queue = JsonNode.Parse(await Http.GetStringAsync(Url(endpoint, "/queue"), quick.Token));
            string? device = null;
            try
            {
                var stats = JsonNode.Parse(await Http.GetStringAsync(Url(endpoint, "/system_stats"), quick.Token));
                device = (string?)stats?["devices"]?[0]?["name"];
            }
            catch (Exception) { /* older servers: the queue is enough */ }
            return new ComfyStatus(true, (queue?["queue_running"] as JsonArray)?.Count ?? 0, (queue?["queue_pending"] as JsonArray)?.Count ?? 0, device, null);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new ComfyStatus(false, 0, 0, null, NotThere(endpoint, "/queue"), Answers: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or UriFormatException or InvalidOperationException)
        {
            return new ComfyStatus(false, 0, 0, null, ex is TaskCanceledException ? "no answer" : ex.Message);
        }
    }

    /// <summary>What a 404 means here: something answers at the address, but ComfyUI's API isn't there.</summary>
    private static string NotThere(ComfyEndpoint endpoint, string path) =>
        $"Something answers at {endpoint.Address}, but not as ComfyUI does (404 for {path}). Give the ComfyUI server's own address, e.g. http://127.0.0.1:8188.";

    private static async Task<string> GetAsync(ComfyEndpoint endpoint, string path, CancellationToken stop)
    {
        using var response = await Http.GetAsync(Url(endpoint, path), stop);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) throw new InvalidOperationException(NotThere(endpoint, path));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(stop);
    }

    /// <summary>Uploads a picture for a LoadImage input; returns the name ComfyUI knows it by.</summary>
    public async Task<string> UploadImageAsync(ComfyEndpoint endpoint, string path, CancellationToken stop = default)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(await File.ReadAllBytesAsync(path, stop));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "image", Path.GetFileName(path));
        form.Add(new StringContent("true"), "overwrite");
        using var response = await Http.PostAsync(Url(endpoint, "/upload/image"), form, stop);
        var body = await response.Content.ReadAsStringAsync(stop);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) throw new InvalidOperationException(NotThere(endpoint, "/upload/image"));
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"ComfyUI didn't take the picture ({(int)response.StatusCode}): {Short(body)}");
        var answer = JsonNode.Parse(body);
        var name = (string?)answer?["name"] ?? throw new InvalidOperationException("ComfyUI didn't say what it called the picture.");
        return (string?)answer?["subfolder"] is { Length: > 0 } sub ? $"{sub}/{name}" : name;
    }

    /// <summary>Queues a workflow; returns its prompt id. ComfyUI's own complaint (a node it doesn't know, say) is thrown.</summary>
    public async Task<string> QueueAsync(ComfyEndpoint endpoint, JsonObject graph, CancellationToken stop = default)
    {
        var request = new JsonObject { ["prompt"] = graph.DeepClone(), ["client_id"] = "projict-" + Guid.NewGuid().ToString("N") };
        using var response = await Http.PostAsync(Url(endpoint, "/prompt"), new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"), stop);
        var body = await response.Content.ReadAsStringAsync(stop);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) throw new InvalidOperationException(NotThere(endpoint, "/prompt"));
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"ComfyUI refused the workflow: {Refusal(body)}");
        return (string?)JsonNode.Parse(body)?["prompt_id"] ?? throw new InvalidOperationException("ComfyUI didn't give the run an id.");
    }

    /// <summary>
    /// ComfyUI's complaint with its reasons per node ("Prompt outputs failed validation: Load LoRA (105:124): Value not in
    /// list: lora_name …"); its headline alone doesn't say what to fix.
    /// </summary>
    internal static string Refusal(string body)
    {
        JsonNode? answer;
        try { answer = JsonNode.Parse(body); }
        catch (System.Text.Json.JsonException) { return Short(body); }
        if (answer is not JsonObject error) return Short(body);
        var headline = (string?)error["error"]?["message"] ?? "";
        var reasons = (error["node_errors"] as JsonObject ?? []).SelectMany(node =>
            (node.Value?["errors"] as JsonArray ?? []).OfType<JsonObject>().Select(e =>
                $"{(string?)node.Value?["class_type"] ?? "a node"} ({node.Key}): {(string?)e["message"]}" + ((string?)e["details"] is { Length: > 0 } d ? $": {d}" : "")))
            .ToList();
        var text = string.Join("; ", new[] { headline }.Concat(reasons).Where(t => t.Length > 0));
        return text.Length == 0 ? Short(body) : text.Length > 600 ? text[..600] + "…" : text;
    }

    /// <summary>Waits for a queued run to finish; returns the files it made. A run ComfyUI says failed is thrown.</summary>
    public async Task<List<ComfyOutput>> WaitAsync(ComfyEndpoint endpoint, string promptId, CancellationToken stop = default)
    {
        var until = DateTime.Now + GiveUpAfter;
        while (DateTime.Now < until)
        {
            stop.ThrowIfCancellationRequested();
            var history = JsonNode.Parse(await GetAsync(endpoint, "/history/" + Uri.EscapeDataString(promptId), stop));
            if (history?[promptId] is JsonObject run)
            {
                if ((string?)run["status"]?["status_str"] == "error")
                {
                    var why = (run["status"]?["messages"] as JsonArray)?.OfType<JsonArray>().Where(m => (string?)m[0] == "execution_error")
                        .Select(m => (string?)m[1]?["exception_message"]).FirstOrDefault(m => m != null);
                    throw new InvalidOperationException($"The workflow failed in ComfyUI{(why != null ? ": " + why.Trim() : "")}.");
                }
                if (run["outputs"] is JsonObject outputs)
                {
                    var files = Outputs(outputs);
                    if ((bool?)run["status"]?["completed"] != false) return files;
                }
            }
            await Task.Delay(PollEvery, stop);
        }
        throw new TimeoutException($"The workflow hadn't finished after {GiveUpAfter.TotalMinutes:0} minutes.");
    }

    /// <summary>
    /// The files a run made, of any kind (pictures, video, sound, 3D models): what its Save nodes saved, or when it saved
    /// nothing, its previews (ComfyUI keeps those only for a while).
    /// </summary>
    internal static List<ComfyOutput> Outputs(JsonObject outputs)
    {
        var files = outputs.SelectMany(node => (node.Value as JsonObject ?? []).Select(kind => kind.Value).OfType<JsonArray>())
            .SelectMany(list => list.OfType<JsonObject>())
            .Where(file => (string?)file["filename"] is { Length: > 0 })
            .Select(file => new ComfyOutput((string)file["filename"]!, (string?)file["subfolder"] ?? "", (string?)file["type"] ?? "output"))
            .Distinct()
            .ToList();
        return files.Any(f => f.Type == "output") ? [.. files.Where(f => f.Type == "output")] : files;
    }

    public async Task<byte[]> FetchAsync(ComfyEndpoint endpoint, ComfyOutput output, CancellationToken stop = default) =>
        await Http.GetByteArrayAsync(Url(endpoint,
            $"/view?filename={Uri.EscapeDataString(output.FileName)}&subfolder={Uri.EscapeDataString(output.Subfolder)}&type={Uri.EscapeDataString(output.Type)}"), stop);

    private static string Short(string text) => text.Length > 300 ? text[..300] + "…" : text;
}
