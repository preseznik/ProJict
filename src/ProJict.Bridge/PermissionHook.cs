using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace ProJict.Bridge;

/// <summary>
/// <c>ProJictBridge.exe --permission-hook</c>: Claude Code's (or Codex's) PermissionRequest hook. It brings the request to
/// the running ProJict, where the agent asks you privately in Chat, and prints your 👍 or 👎 as the hook's decision.
/// With ProJict closed, no answer in time, or anything amiss, it prints nothing, so the usual prompt is there; it always
/// exits 0, as anything else could block the agent.
/// </summary>
internal static class PermissionHook
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(285);

    public static async Task<int> RunAsync(Options options, string[] args)
    {
        try
        {
            // The hook's request comes as UTF-8 JSON on stdin (not in the console's code page).
            using var stdin = new StreamReader(Console.OpenStandardInput(), Utf8);
            if (JsonNode.Parse(await stdin.ReadToEndAsync()) is not JsonObject hook) return 0;
            await using var pipe = await ProJictPipe.ConnectAsync(options.DataRoot, TimeSpan.FromSeconds(2));
            if (pipe == null) return 0;
            var wait = Array.IndexOf(args, "--wait") is >= 0 and var i && i + 1 < args.Length &&
                       int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
                ? TimeSpan.FromSeconds(seconds) : DefaultWait;
            var hello = new JsonObject { ["projictApproval"] = 1, ["hook"] = hook, ["as"] = options.Agent, ["bridge"] = McpRelay.Version };
            await pipe.WriteAsync(Utf8.GetBytes(hello.ToJsonString() + "\n"));
            await pipe.FlushAsync();
            using var reader = new StreamReader(pipe, Utf8);
            using var timeout = new CancellationTokenSource(wait);
            if (await reader.ReadLineAsync(timeout.Token) is { } line && JsonNode.Parse(line) is JsonObject answer && answer["hookOutput"] is JsonObject output)
            {
                await using var stdout = Console.OpenStandardOutput();
                await stdout.WriteAsync(Utf8.GetBytes(output.ToJsonString() + "\n"));
                await stdout.FlushAsync();
            }
        }
        catch (Exception)
        {
            // No answer from ProJict: the usual prompt decides.
        }
        return 0;
    }
}
