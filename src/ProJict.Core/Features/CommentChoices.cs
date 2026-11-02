using ProJict.Core.Model;

namespace ProJict.Core.Features;

/// <summary>
/// Questions an agent asks in a discussion with options to click, so the user answers without typing: written as lines
/// "question | option | option" (the agent's suggestion first). The answer goes back as the user's reply. A line that
/// doesn't fit throws InvalidOperationException with the reason.
/// </summary>
public static class CommentChoices
{
    public const int MaxQuestions = 8, MaxOptions = 6;

    public static List<CommentChoice> Parse(IEnumerable<string> lines)
    {
        var choices = new List<CommentChoice>();
        foreach (var line in lines.Select(l => l.Trim()).Where(l => l.Length > 0))
        {
            var parts = line.Split('|', StringSplitOptions.TrimEntries);
            var options = parts.Skip(1).Where(o => o.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (parts[0].Length == 0 || options.Count < 2)
                throw new InvalidOperationException($"\"{line}\": write it as question | option | option (at least two options, your suggestion first).");
            if (options.Count > MaxOptions) throw new InvalidOperationException($"\"{parts[0]}\": at most {MaxOptions} options.");
            choices.Add(new CommentChoice { Question = parts[0], Options = options });
        }
        if (choices.Count > MaxQuestions) throw new InvalidOperationException($"At most {MaxQuestions} questions in one comment; ask the rest after.");
        return choices;
    }

    /// <summary>The user's reply: the option alone for one question, else one line per question.</summary>
    public static string AnswerText(IReadOnlyList<CommentChoice> choices) => choices.Count == 1
        ? choices[0].Answer ?? ""
        : string.Join("\n", choices.Select((c, i) => $"{i + 1}. {c.Question} {c.Answer}"));

    public static bool IsAnswered(IReadOnlyList<CommentChoice>? choices) => choices is { Count: > 0 } && choices.All(c => c.Answer != null);
}
