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
        var gone = false;
        while (true)
        {
            var (pipe, problem) = first
                ? await ProJictPipe.OpenAsync(options, options.Launch, waitForSetup: TimeSpan.FromDays(1))
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
            var heard = false;
            // A ProJict on its way out (or still starting) can take the connection and drop it: then it's tried again.
            try
            {
                await using (pipe)
                {
                    var hello = new JsonObject { ["projictWatch"] = 1, ["as"] = options.Agent.Trim(), ["bridge"] = McpRelay.Version };
                    await pipe.WriteAsync(Utf8.GetBytes(hello.ToJsonString() + "\n"));
                    await pipe.FlushAsync();
                    using var reader = new StreamReader(pipe, Utf8);
                    while (await reader.ReadLineAsync() is { } line)
                    {
                        // Back after a restart (once it answers): one line instead of ProJict's "Watching …" introduction again.
                        if (!heard && gone)
                        {
                            gone = false;
                            if (!await Print($"[ProJict] ProJict is back (it restarted, perhaps for an update); still watching Chat for {options.Agent.Trim()}."))
                                return 0;
                            heard = true;
                            if (line.StartsWith("Watching ProJict Chat for", StringComparison.Ordinal)) continue;
                        }
                        heard = true;
                        if (!await Print(line)) return 0;
                    }
                }
            }
            catch (IOException) { }
            first = false;
            if (!heard) await Task.Delay(1000);
            if (gone) continue;
            gone = true;
            if (!await Print(ProJictPipe.SetupRunning()
                    ? "[ProJict] ProJict is being updated; waiting for it to come back."
                    : "[ProJict] ProJict went away (restarting, perhaps for an update); waiting for it to come back.")) return 0;
        }
    }
}
