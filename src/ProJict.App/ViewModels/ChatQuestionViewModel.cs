using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProJict.Core.Chat;

namespace ProJict.App.ViewModels;

/// <summary>
/// One question an agent asks in Chat (ask_question, or Claude Code's AskUserQuestion): its options to click (one, or
/// any that apply), or your own words instead. The answers are kept here until they're sent.
/// </summary>
public sealed partial class ChatQuestionViewModel : ObservableObject
{
    private readonly ChatMessageViewModel _message;
    private readonly List<string> _picked = [];

    public ChatQuestionViewModel(ChatMessageViewModel message, ChatQuestion model)
    {
        _message = message;
        Model = model;
        Options = [.. model.Options.Select((o, i) => new ChatOptionViewModel(this, o, i == 0))];
    }

    public ChatQuestion Model { get; }
    public string Question => Model.Question;
    public string? Header => Model.Header;
    public bool HasHeader => Model.Header != null;
    public bool Multiple => Model.Multiple;
    public IReadOnlyList<ChatOptionViewModel> Options { get; }
    public bool IsAnswered => Model.Answer != null;
    public string AnswerText => ChatQuestions.AnswerLine(Model);
    /// <summary>The answer is the person's own words, not one of the options.</summary>
    public bool AnsweredInOwnWords => Model.Answer?.Any(a => !ChatQuestions.IsOption(Model, a)) == true;

    /// <summary>Your own answer instead (or, where several apply, as well as the options picked).</summary>
    [ObservableProperty] private string _ownWords = "";

    partial void OnOwnWordsChanged(string value)
    {
        // One answer: your own words take the place of an option picked.
        if (!Multiple && value.Trim().Length > 0 && _picked.Count > 0)
        {
            _picked.Clear();
            foreach (var option in Options) option.Refresh();
        }
        _message.PicksChanged();
    }

    public bool IsPicked(string label) => _picked.Contains(label);

    internal void Toggle(string label)
    {
        if (IsAnswered || !_message.CanAnswer) return;
        if (Multiple)
        {
            if (!_picked.Remove(label)) _picked.Add(label);
        }
        else
        {
            _picked.Clear();
            _picked.Add(label);
            if (OwnWords.Length > 0) OwnWords = "";
        }
        foreach (var option in Options) option.Refresh();
        _message.Picked(this);
    }

    /// <summary>What you'd send: the options picked (in their order), then your own words.</summary>
    public List<string> Picks
    {
        get
        {
            var picks = Options.Select(o => o.Label).Where(_picked.Contains).ToList();
            if (OwnWords.Trim() is { Length: > 0 } own) picks.Add(own);
            return picks;
        }
    }

    internal void Refresh()
    {
        OnPropertyChanged(string.Empty);
        foreach (var option in Options) option.Refresh();
    }
}

public sealed partial class ChatOptionViewModel(ChatQuestionViewModel question, ChatOption model, bool suggested) : ObservableObject
{
    public string Label => model.Label;
    public string? Description => model.Description;
    public bool HasDescription => model.Description != null;
    public bool IsSuggested => suggested;
    public bool IsPicked => question.IsAnswered ? question.Model.Answer!.Contains(model.Label, StringComparer.OrdinalIgnoreCase) : question.IsPicked(model.Label);
    public string? ToolTip => ((suggested ? "The suggestion. " : "") + (question.Multiple ? "Pick any that apply." : "")).Trim() is { Length: > 0 } tip ? tip : null;
    [RelayCommand] private void Pick() => question.Toggle(model.Label);
    internal void Refresh() => OnPropertyChanged(nameof(IsPicked));
}
