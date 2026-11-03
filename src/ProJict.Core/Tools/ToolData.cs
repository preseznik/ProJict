using System.Text.Json.Serialization;
using ProJict.Core.Storage;

namespace ProJict.Core.Tools;

/// <summary>
/// How agents reach a tool: through its API, with a key you set; on its website in the browser; a ComfyUI server of
/// yours, where ProJict runs workflows from the library; or a program on this computer (ffmpeg), which they run themselves.
/// </summary>
public enum ToolAccess { Api, Browser, ComfyUI, Program }

/// <summary>
/// An outside tool agents may use for your work, such as ElevenLabs for voices or Suno for music: what it's for, where
/// it is, how agents reach it, the projects it's set up for and the budget they're told. An API key is kept protected
/// for your Windows user, and an agent gets it only when you allow it.
/// </summary>
public sealed class ExternalTool
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    /// <summary>What it's for, in a sentence, so agents pick the right one.</summary>
    public string Purpose { get; set; } = "";
    public string? Website { get; set; }
    public ToolAccess Access { get; set; }
    /// <summary>The API key, protected for this Windows user (DPAPI); never in the workspace, the mirror folder or backups.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProtectedKey { get; set; }
    /// <summary>The key's last characters, so you can tell which one is set without showing it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? KeyEnding { get; set; }
    /// <summary>For a tool in the browser: how you're signed in there ("Google account, Pro plan"), so agents know what to expect.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SignIn { get; set; }
    /// <summary>A budget agents are told ("$22 a month, Creator plan"); ProJict doesn't count what's used.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Budget { get; set; }
    /// <summary>The projects it's set up for (ticked in a project's Details).</summary>
    public List<Guid> Projects { get; set; } = [];
    /// <summary>Agents you let have the key without asking each time, by name.</summary>
    public List<string> AlwaysAllowed { get; set; } = [];
    /// <summary>For a ComfyUI tool: the servers it runs workflows on (addresses are per computer, like keys).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ComfyEndpoint>? Endpoints { get; set; }
    /// <summary>For a ComfyUI tool: agents may not run its workflows (they can still read them).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool RunsOff { get; set; }
    /// <summary>For a program on this computer: where it is (the program, or the folder it's in).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProgramPath { get; set; }
    /// <summary>For a program: its documentation, a web page or a file or folder on this computer.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Docs { get; set; }
    /// <summary>For a program: the folder where it saves what it makes (VRCapture's recordings).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OutputFolder { get; set; }

    [JsonIgnore] public bool HasKey => ProtectedKey != null;
    /// <summary>
    /// Whether an agent can use it now: a browser tool always, an API tool once its key is set, ComfyUI with an endpoint,
    /// a program once it's where its path says.
    /// </summary>
    [JsonIgnore] public bool Ready => Access switch
    {
        ToolAccess.Browser => true,
        ToolAccess.ComfyUI => Endpoints is { Count: > 0 },
        ToolAccess.Program => ProgramCheck.Of(ProgramPath).Found,
        _ => HasKey,
    };

    /// <summary>The endpoint a name means (case doesn't count), or with none named the first.</summary>
    public ComfyEndpoint? Endpoint(string? name) =>
        string.IsNullOrWhiteSpace(name) ? Endpoints?.FirstOrDefault()
        : Endpoints?.FirstOrDefault(e => string.Equals(e.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    public bool IsAlwaysAllowed(string agent) => AlwaysAllowed.Any(a => string.Equals(a, agent.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>"API", "in the browser", "ComfyUI" or "on this computer", for people.</summary>
    public string AccessText => Access switch
    {
        ToolAccess.Api => "API", ToolAccess.ComfyUI => "ComfyUI", ToolAccess.Program => "on this computer", _ => "in the browser",
    };
}

/// <summary>
/// This computer's outside tools (Settings › Tools), in tools.json next to ProJict's other per-computer settings, so
/// they never go with the workspace, its mirror or backups, or to anyone you share with.
/// </summary>
public sealed class ToolData
{
    public const int MaxName = 40;
    public const int MaxText = 500;

    public List<ExternalTool> Tools { get; set; } = [];
    /// <summary>The starting tools were put in (once, so removing one sticks).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Seeded { get; set; }
    /// <summary>The starting programs (ffmpeg, VRCapture) were put in, also where the other starters already were.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ProgramsSeeded { get; set; }

    public static string DefaultPath => Path.Combine(Path.GetDirectoryName(AppSettings.DefaultPath)!, "tools.json");

    public static ToolData Load(string path)
    {
        try { return File.Exists(path) ? Json.Deserialize<ToolData>(File.ReadAllText(path)) ?? new() : new(); }
        catch (Exception) { return new(); }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllText(path, Json.Serialize(this));
    }

    /// <summary>The tools you start with: ElevenLabs (API), Suno and Tripo3D (in the browser).</summary>
    public static IEnumerable<ExternalTool> Starters()
    {
        yield return new ExternalTool
        {
            Name = "ElevenLabs", Access = ToolAccess.Api, Website = "https://elevenlabs.io",
            Purpose = "Voice-over, voices, sound effects and music from text (text to speech and sound generation), through its API.",
        };
        yield return new ExternalTool
        {
            Name = "Suno", Access = ToolAccess.Browser, Website = "https://suno.com",
            Purpose = "Songs and music from a description or lyrics, made on its website.",
        };
        yield return new ExternalTool
        {
            Name = "Tripo3D", Access = ToolAccess.Browser, Website = "https://www.tripo3d.ai",
            Purpose = "3D models from text or a picture, made on its website (or through its API once you set a key).",
        };
    }

    /// <summary>
    /// The programs you start with, for video work: ffmpeg (found where Windows finds programs, when it's there) and
    /// VRCapture (you say where it is). <paramref name="pathVariable"/> is the PATH to look in (Windows' own by default).
    /// </summary>
    public static IEnumerable<ExternalTool> ProgramStarters(string? pathVariable = null)
    {
        yield return new ExternalTool
        {
            Name = "ffmpeg", Access = ToolAccess.Program, Website = "https://ffmpeg.org", Docs = "https://ffmpeg.org/ffmpeg.html",
            ProgramPath = ProgramCheck.FindOnPath("ffmpeg.exe", pathVariable),
            Purpose = "Converts, cuts, joins and re-encodes video and audio, and makes stills, GIFs and thumbnails from video, from the command line.",
        };
        yield return new ExternalTool
        {
            Name = "VRCapture", Access = ToolAccess.Program,
            Purpose = "Records VR play (what the headset shows, with its sound) to video files. You start and stop it in the headset; agents use the recordings in its output folder.",
        };
    }

    /// <summary>Puts the starting tools in, the first time (and the starting programs, the first time there are any).</summary>
    public bool SeedOnce(string? pathVariable = null)
    {
        if (Seeded && ProgramsSeeded) return false;
        var starters = (Seeded ? [] : Starters()).Concat(ProgramsSeeded ? [] : ProgramStarters(pathVariable));
        foreach (var tool in starters.Where(s => Tools.All(t => !string.Equals(t.Name, s.Name, StringComparison.OrdinalIgnoreCase)))) Tools.Add(tool);
        Seeded = ProgramsSeeded = true;
        return true;
    }

    /// <summary>The tool a name means (case doesn't count), or its id (8 characters are enough).</summary>
    public ExternalTool? Find(string key)
    {
        key = key.Trim();
        if (key.Length == 0) return null;
        return Tools.FirstOrDefault(t => string.Equals(t.Name, key, StringComparison.OrdinalIgnoreCase))
               ?? (key.Length >= 8 ? Tools.FirstOrDefault(t => t.Id.ToString().StartsWith(key, StringComparison.OrdinalIgnoreCase)) : null);
    }

    /// <summary>The tools set up for a project.</summary>
    public IEnumerable<ExternalTool> For(Guid projectId) => Tools.Where(t => t.Projects.Contains(projectId));

    /// <summary>Why a tool can't have that name (empty, too long, or another has it); null when it can.</summary>
    public string? NameProblem(string name, ExternalTool? except = null)
    {
        name = name.Trim();
        if (name.Length == 0) return "Give the tool a name.";
        if (name.Length > MaxName) return $"Keep the name short ({MaxName} characters at most).";
        if (Tools.Any(t => t != except && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))) return $"There's already a tool called “{name}”.";
        return null;
    }

    /// <summary>A new tool, "New tool" (or "New tool 2"), in the browser until you say otherwise.</summary>
    public ExternalTool Add()
    {
        var name = "New tool";
        for (var n = 2; NameProblem(name) != null; n++) name = $"New tool {n}";
        var tool = new ExternalTool { Name = name, Access = ToolAccess.Browser };
        Tools.Add(tool);
        return tool;
    }

    /// <summary>A web address as people type it ("suno.com") made whole ("https://suno.com"); null when empty or not one.</summary>
    public static string? Website(string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && uri.Host.Contains('.') ? uri.ToString().TrimEnd('/') : null;
    }
}
