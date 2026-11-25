using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProJict.Core.Chat;

/// <summary>
/// Questions an agent asks in Chat with options to click (ask_question), in the shape of Claude Code's AskUserQuestion:
/// [{"question","header","options":[{"label","description"}],"multiSelect"}]. The person picks an option (several when
/// the question allows it) or writes their own answer; the agent hears the answers. A question that doesn't fit throws
/// <see cref="ChatException"/> with the reason.
/// </summary>
public static class ChatQuestions
{
    public const int MaxQuestions = 4, MinOptions = 2, MaxOptions = 6;
    public const int MaxQuestion = 500, MaxHeader = 30, MaxLabel = 120, MaxDescription = 400, MaxOwnWords = 1000;

    /// <summary>
    /// The questions from an agent: AskUserQuestion's array (also sent as a JSON string), or lines
    /// "question | option | option" as in an item's discussion.
    /// </summary>
    public static List<ChatQuestion> Parse(JsonNode? node)
    {
        if (node is JsonValue v && v.TryGetValue<string>(out var s))
        {
            s = s.Trim();
            if (s.StartsWith('[') || s.StartsWith('{'))
            {
                try { node = JsonNode.Parse(s); }
                catch (JsonException) { throw new ChatException("questions isn't valid JSON: give an array of {question, header, options: [{label, description}], multiSelect}."); }
            }
            else node = new JsonArray(s);
        }
        // One question on its own, or AskUserQuestion's whole input ({"questions": […]}).
        if (node is JsonObject one) node = one["questions"] is JsonArray inner ? inner : new JsonArray(one.DeepClone());
        if (node is not JsonArray list || list.Count == 0) throw new ChatException("Give at least one question.");
        var questions = list.Select(Question).ToList();
        Check(questions);
        return questions;
    }

    private static ChatQuestion Question(JsonNode? node)
    {
        if (node is JsonValue v && v.TryGetValue<string>(out var line))
        {
            var parts = line.Split('|', StringSplitOptions.TrimEntries);
            return new ChatQuestion { Question = parts[0], Options = [.. parts.Skip(1).Where(p => p.Length > 0).Select(p => new ChatOption { Label = p })] };
        }
        if (node is not JsonObject o) throw new ChatException("Each question is an object: {question, header, options: [{label, description}], multiSelect}.");
        return new ChatQuestion
        {
            Question = Text(o["question"]) ?? Text(o["text"]) ?? "",
            Header = Text(o["header"]),
            Multiple = o["multiSelect"] is JsonValue m && m.TryGetValue<bool>(out var many) && many,
            Options = [.. (o["options"] as JsonArray ?? []).Select(x => x is JsonObject option
                ? new ChatOption { Label = Text(option["label"]) ?? "", Description = Text(option["description"]) }
                : new ChatOption { Label = Text(x) ?? "" })],
        };
    }

    /// <summary>Refuses questions that don't fit: too many, too long, too few options, the same option twice.</summary>
    public static void Check(IReadOnlyList<ChatQuestion> questions)
    {
        if (questions.Count == 0) throw new ChatException("Give at least one question.");
        if (questions.Count > MaxQuestions) throw new ChatException($"At most {MaxQuestions} questions at a time; ask the rest after.");
        foreach (var q in questions)
        {
            q.Question = q.Question.Trim();
            q.Header = q.Header?.Trim() is { Length: > 0 } h ? h : null;
            if (q.Question.Length == 0) throw new ChatException("A question has no text.");
            if (q.Question.Length > MaxQuestion) throw new ChatException($"\"{Short(q.Question)}\": ask it in {MaxQuestion} characters at most.");
            if (q.Header is { Length: > MaxHeader }) throw new ChatException($"\"{q.Header}\": a header is a short label, {MaxHeader} characters at most.");
            foreach (var option in q.Options)
            {
                option.Label = option.Label.Trim();
                option.Description = option.Description?.Trim() is { Length: > 0 } d ? d : null;
            }
            q.Options = [.. q.Options.Where(o => o.Label.Length > 0)];
            if (q.Options.Count < MinOptions) throw new ChatException($"\"{Short(q.Question)}\": give at least {MinOptions} options (your suggestion first).");
            if (q.Options.Count > MaxOptions) throw new ChatException($"\"{Short(q.Question)}\": at most {MaxOptions} options.");
            if (q.Options.Select(o => o.Label).Distinct(StringComparer.OrdinalIgnoreCase).Count() != q.Options.Count)
                throw new ChatException($"\"{Short(q.Question)}\": two options have the same label.");
            if (q.Options.FirstOrDefault(o => o.Label.Length > MaxLabel) is { } longLabel)
                throw new ChatException($"\"{Short(longLabel.Label)}\": an option's label is {MaxLabel} characters at most; say more in its description.");
            if (q.Options.FirstOrDefault(o => o.Description is { Length: > MaxDescription }) is { } longOne)
                throw new ChatException($"\"{longOne.Label}\": its description is {MaxDescription} characters at most.");
            q.Answer = null;
        }
    }

    /// <summary>
    /// The person's answers, one list per question: the options picked (their labels), or their own words. Throws when
    /// one is missing, or several are picked where only one may be.
    /// </summary>
    public static List<List<string>> CheckAnswers(IReadOnlyList<ChatQuestion> questions, IReadOnlyList<IReadOnlyList<string>> picks)
    {
        if (picks.Count != questions.Count) throw new ChatException("Answer every question.");
        var answers = new List<List<string>>();
        for (var i = 0; i < questions.Count; i++)
        {
            var q = questions[i];
            var picked = picks[i].Select(p => p.Trim()).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
                // An option as the agent spelled it.
                .Select(p => q.Options.FirstOrDefault(o => string.Equals(o.Label, p, StringComparison.OrdinalIgnoreCase))?.Label ?? p).ToList();
            if (picked.Count == 0) throw new ChatException($"\"{Short(q.Question)}\" has no answer yet.");
            if (!q.Multiple && picked.Count > 1) throw new ChatException($"\"{Short(q.Question)}\" takes one answer.");
            if (picked.Any(p => p.Length > MaxOwnWords)) throw new ChatException($"Answer in {MaxOwnWords} characters at most.");
            answers.Add(picked);
        }
        return answers;
    }

    public static bool IsOption(ChatQuestion question, string answer) => question.Options.Any(o => string.Equals(o.Label, answer, StringComparison.OrdinalIgnoreCase));

    /// <summary>One question's answer as a line: the options picked joined with commas (how Claude Code takes several).</summary>
    public static string AnswerLine(ChatQuestion question) => string.Join(", ", question.Answer ?? []);

    /// <summary>The answers for the agent: "Cockpit: Rally-prepped" for one question, else a line per question.</summary>
    public static string AnswerText(IReadOnlyList<ChatQuestion> questions) => questions.Count == 1
        ? $"{Label(questions[0])}: {AnswerLine(questions[0])}"
        : string.Join("\n", questions.Select((q, i) => $"{i + 1}. {Label(q)}: {AnswerLine(q)}"));

    /// <summary>{"question": "answer"}, as Claude Code's AskUserQuestion takes its answers.</summary>
    public static JsonObject AnswerMap(IReadOnlyList<ChatQuestion> questions)
    {
        var map = new JsonObject();
        foreach (var q in questions) map[q.Question] = AnswerLine(q);
        return map;
    }

    /// <summary>The questions (and answers) as an agent reads them in read_channel.</summary>
    public static JsonArray Json(IReadOnlyList<ChatQuestion> questions) => new(questions.Select(q =>
    {
        var json = new JsonObject { ["question"] = q.Question };
        if (q.Header != null) json["header"] = q.Header;
        if (q.Multiple) json["multiSelect"] = true;
        json["options"] = new JsonArray(q.Options.Select(o => (JsonNode)(o.Description != null
            ? new JsonObject { ["label"] = o.Label, ["description"] = o.Description } : new JsonObject { ["label"] = o.Label })).ToArray());
        if (q.Answer != null) json["answer"] = AnswerLine(q);
        return (JsonNode)json;
    }).ToArray());

    private static string Label(ChatQuestion q) => q.Header ?? Short(q.Question);

    private static string Short(string text) => text.Length <= 60 ? text : text[..57] + "…";

    private static string? Text(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Length > 0 ? s.Trim() : null;
}
