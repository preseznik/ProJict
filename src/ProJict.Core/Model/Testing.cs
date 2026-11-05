using System.Text.Json.Serialization;

namespace ProJict.Core.Model;

/// <summary>Waiting for a person to try it; then it works, it doesn't, or the agent closed it (settled elsewhere).</summary>
public enum TestState { Waiting, Works, DoesntWork, Closed }

/// <summary>
/// Something an AI agent asks a person to try by hand (#09a108b5): on an item it has checked out or finished, short
/// steps and what should happen, with files if they help (a build, a screenshot). You answer that it works or doesn't
/// (with a note and files); the agent hears it, and the item's history says so. An agent closes one that was settled
/// elsewhere (in Chat, say), saying where. See TestFlow.
/// </summary>
public sealed class TestRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ItemId { get; set; }
    /// <summary>What to do, in a few short steps.</summary>
    public string Steps { get; set; } = "";
    /// <summary>What should happen.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Expected { get; set; }
    /// <summary>The agent's files for it (a build to run, a screenshot of what it should look like); null while none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Attachment>? Files { get; set; }
    public string AskedBy { get; set; } = "";
    public DateTime AskedAt { get; set; } = DateTime.Now;
    public TestState State { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AnsweredBy { get; set; }
    /// <summary>Closed by an agent (settled elsewhere) rather than answered by a person.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AnsweredByAgent { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? AnsweredAt { get; set; }
    /// <summary>Your note (what went wrong), or the agent's line when it closed it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; set; }
    /// <summary>Files with your answer (a screenshot of what went wrong); null while none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Attachment>? AnswerFiles { get; set; }

    [JsonIgnore] public bool IsOpen => State == TestState.Waiting;
}
