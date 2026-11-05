using ProJict.Core.Model;

namespace ProJict.Core.Features;

/// <summary>
/// What agents ask people to try by hand (#09a108b5): an agent asks on an item it has checked out or finished, a person
/// answers that it works or doesn't (with a note and files), and an agent closes one that was settled elsewhere. Each
/// step goes into the item's history, signed. Throws InvalidOperationException with the reason when a step isn't allowed.
/// </summary>
public static class TestFlow
{
    /// <summary>Answered tests kept per project (the oldest go first); open ones always stay.</summary>
    public const int DoneKept = 200;
    public const int MaxSteps = 4000;
    public const int MaxNote = 4000;

    /// <summary>Waiting tests, oldest first.</summary>
    public static IEnumerable<TestRequest> Open(Project project) => (project.Tests ?? []).Where(t => t.IsOpen);

    /// <summary>Answered or closed tests, newest first.</summary>
    public static IEnumerable<TestRequest> Done(Project project) =>
        (project.Tests ?? []).Where(t => !t.IsOpen).OrderByDescending(t => t.AnsweredAt ?? t.AskedAt);

    /// <summary>Waiting tests for the item.</summary>
    public static IEnumerable<TestRequest> OpenFor(Project project, Guid itemId) => Open(project).Where(t => t.ItemId == itemId);

    /// <summary>An agent asks to try something on an item; the item's history says so.</summary>
    public static TestRequest Ask(Project project, Guid itemId, string steps, string? expected, IReadOnlyCollection<Attachment>? files, string by, DateTime? now = null)
    {
        steps = steps.Trim();
        if (steps.Length == 0) throw new InvalidOperationException("Say what to try: a few short steps.");
        if (steps.Length > MaxSteps) throw new InvalidOperationException($"Keep the steps short (at most {MaxSteps} characters); put details in the item's discussion.");
        if (project.Find(itemId) is not { } loc) throw new InvalidOperationException("That item isn't in the project.");
        var test = new TestRequest
        {
            ItemId = itemId, Steps = steps, Expected = string.IsNullOrWhiteSpace(expected) ? null : expected.Trim(),
            Files = files is { Count: > 0 } ? [.. files] : null, AskedBy = by, AskedAt = now ?? DateTime.Now,
        };
        (project.Tests ??= []).Add(test);
        loc.Item.AddActivity(by, "Asked you to test it: " + Short(steps), agent: true);
        Trim(project);
        return test;
    }

    /// <summary>A person says it works or doesn't, with a note and files; the item's history says so.</summary>
    public static void Answer(Project project, TestRequest test, bool works, string? note, IReadOnlyCollection<Attachment>? files, string by, DateTime? now = null)
    {
        if (!test.IsOpen) throw new InvalidOperationException(Answered(test));
        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (note?.Length > MaxNote) throw new InvalidOperationException($"Keep the note shorter (at most {MaxNote} characters).");
        test.State = works ? TestState.Works : TestState.DoesntWork;
        test.AnsweredBy = by;
        test.AnsweredByAgent = false;
        test.AnsweredAt = now ?? DateTime.Now;
        test.Note = note;
        test.AnswerFiles = files is { Count: > 0 } ? [.. files] : null;
        project.Find(test.ItemId)?.Item.AddActivity(by, (works ? "Tested it: it works." : "Tested it: it doesn't work.") + (note != null ? " " + Short(note) : ""), agent: false);
    }

    /// <summary>An agent closes a test that was settled elsewhere, saying where; the item's history says so.</summary>
    public static void Close(Project project, TestRequest test, string note, string by, DateTime? now = null)
    {
        if (!test.IsOpen) throw new InvalidOperationException(Answered(test));
        note = note.Trim();
        if (note.Length == 0) throw new InvalidOperationException("Say where it was settled (e.g. iztok confirmed it in #projict).");
        if (note.Length > MaxNote) throw new InvalidOperationException($"Keep it shorter (at most {MaxNote} characters).");
        test.State = TestState.Closed;
        test.AnsweredBy = by;
        test.AnsweredByAgent = true;
        test.AnsweredAt = now ?? DateTime.Now;
        test.Note = note;
        project.Find(test.ItemId)?.Item.AddActivity(by, "Closed the test: " + Short(note), agent: true);
    }

    /// <summary>"Works", "Doesn't work", "Closed by Lark", or "Waiting".</summary>
    public static string StateText(TestRequest test) => test.State switch
    {
        TestState.Works => "Works",
        TestState.DoesntWork => "Doesn't work",
        TestState.Closed => $"Closed by {test.AnsweredBy}",
        _ => "Waiting",
    };

    /// <summary>Finds a test by (the start of) its id.</summary>
    public static TestRequest? Find(Project project, string id)
    {
        var key = id.Trim().TrimStart('#').ToLowerInvariant();
        if (key.Length == 0) return null;
        return (project.Tests ?? []).LastOrDefault(t => t.Id.ToString("N").StartsWith(key, StringComparison.Ordinal) || t.Id.ToString().StartsWith(key, StringComparison.Ordinal));
    }

    private static string Answered(TestRequest test) => test.State switch
    {
        TestState.Closed => $"{test.AnsweredBy} closed that test already.",
        _ => $"{test.AnsweredBy} answered that test already: {StateText(test).ToLowerInvariant()}.",
    };

    /// <summary>Only the newest answered tests are kept; waiting ones always are.</summary>
    private static void Trim(Project project)
    {
        var done = (project.Tests ?? []).Where(t => !t.IsOpen).OrderBy(t => t.AnsweredAt ?? t.AskedAt).ToList();
        foreach (var old in done.Take(Math.Max(0, done.Count - DoneKept))) project.Tests!.Remove(old);
    }

    private static string Short(string text)
    {
        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length <= 160 ? text : text[..157].TrimEnd() + "…";
    }
}
