using System.Text;
using System.Text.Json.Nodes;

namespace ProJict.Bridge;

/// <summary>
/// A chat watcher: asks ProJict for the agent's chat messages and other news and prints them, a line each. When ProJict
/// goes away (it restarts, say for an update) it says so, waits for it, and goes on watching.
/// </summary>
internal static class Watcher
{
    private static readonly UTF8Encoding Utf8 = new(false);

    public static async Task<int> RunAsync(Options options)
    {
        if (string.IsNullOrWhiteSpace(options.Agent))
        {
            Console.Error.WriteLine("ProJict: say whose chat to watch: ProJictBridge.exe --watch-chat --as <agent name>.");
            return 1;
        }
        await using var stdout = Console.OpenStandardOutput();
        async Task<bool> Print(string line)
        {
            try
            {
                await stdout.WriteAsync(Utf8.GetBytes(line + "\n"));
                await stdout.FlushAsync();
                return true;
            }
            catch (IOException) { return false; }
        }

        var first = true;
        while (true)
        {
            var (pipe, problem) = first
                ? await ProJictPipe.OpenAsync(options, options.Launch)
                : (await ProJictPipe.ConnectAsync(options.DataRoot, TimeSpan.FromSeconds(2)), null);
            if (pipe == null)
            {
                if (first)
                {
                    Console.Error.WriteLine("ProJict: " + problem);
                    return 1;
                }
                await Task.Delay(1000);
                continue;
            }
            await using (pipe)
            {
                var hello = new JsonObject { ["projictWatch"] = 1, ["as"] = options.Agent.Trim(), ["bridge"] = McpRelay.Version };
                await pipe.WriteAsync(Utf8.GetBytes(hello.ToJsonString() + "\n"));
                await pipe.FlushAsync();
                // Back after a restart: one line instead of ProJict's "Watching …" introduction again.
                var again = !first;
                if (again && !await Print($"[ProJict] ProJict is back (it restarted, perhaps for an update); still watching Chat for {options.Agent.Trim()}."))
                    return 0;
                using var reader = new StreamReader(pipe, Utf8);
                try
                {
                    while (await reader.ReadLineAsync() is { } line)
                    {
                        if (again && line.StartsWith("Watching ProJict Chat for", StringComparison.Ordinal)) continue;
                        again = false;
                        if (!await Print(line)) return 0;
                    }
                }
                catch (IOException) { }
            }
            first = false;
            if (!await Print("[ProJict] ProJict went away (restarting, perhaps for an update); waiting for it to come back.")) return 0;
        }
    }
}
