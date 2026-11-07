using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProJict.App.Services;
using ProJict.Core.Features;
using ProJict.Core.Model;

namespace ProJict.App.ViewModels;

/// <summary>
/// The To test tab (#09a108b5): what agents ask you to try by hand on items they have checked out or finished. Works
/// or Doesn't work (with a note and files) tells the agent; Doesn't work on finished work waiting for your review also
/// sends it back, as not accepted yet. Works doesn't accept it: the review stays yours. See TestFlow.
/// </summary>
public sealed partial class ProjectViewModel
{
    /// <summary>Waiting tests, oldest first.</summary>
    public ObservableCollection<TestCardViewModel> TestCards { get; } = [];
    /// <summary>Answered or closed, newest first.</summary>
    public List<TestCardViewModel> DoneTests { get; private set; } = [];
    public bool HasTests => TestCards.Count > 0;
    public bool HasDoneTests => DoneTests.Count > 0;
    public string TestsHeader => TestCards.Count > 0 ? $"To test ({TestCards.Count})" : "To test";
    public string DoneTestsHeader => $"Done ({DoneTests.Count})";
    /// <summary>Answered tests shown in the tab (the project keeps more).</summary>
    private const int DoneShown = 50;

    /// <summary>
    /// Changes the project's tests (yours here, an agent's through its tools). Not part of undo: they are what you said
    /// and what agents were told. Throws InvalidOperationException with the reason.
    /// </summary>
    public void ChangeTests(Action change)
    {
        change();
        Touch();
        RefreshTests();
    }

    /// <summary>The tab and the board's "to test" chips, in line with the tests (keeping a note being written).</summary>
    public void RefreshTests()
    {
        var open = TestFlow.Open(Project).ToList();
        for (var i = TestCards.Count - 1; i >= 0; i--)
            if (!open.Contains(TestCards[i].Model)) TestCards.RemoveAt(i);
        for (var i = 0; i < open.Count; i++)
        {
            var card = TestCards.FirstOrDefault(c => c.Model == open[i]);
            if (card == null) TestCards.Insert(Math.Min(i, TestCards.Count), new TestCardViewModel(this, open[i]));
            else card.Refresh();
        }
        DoneTests = TestFlow.Done(Project).Take(DoneShown).Select(t => new TestCardViewModel(this, t)).ToList();
        foreach (var p in new[] { nameof(HasTests), nameof(HasDoneTests), nameof(TestsHeader), nameof(DoneTestsHeader), nameof(DoneTests) }) OnPropertyChanged(p);
        foreach (var row in AllRows) row.RefreshTest();
    }

    /// <summary>The item waits for a test of yours.</summary>
    internal bool HasOpenTest(Guid itemId) => TestFlow.OpenFor(Project, itemId).Any();

    internal string TestToolTip(Guid itemId) =>
        TestFlow.OpenFor(Project, itemId).FirstOrDefault() is { } test ? $"{test.AskedBy} asks you to try it: {Shorten(test.Steps)} (the To test tab)" : "";

    /// <summary>The To test tab, scrolled to the item's test (from its chip).</summary>
    public void RevealTests(Guid? itemId = null)
    {
        SelectedTab = TestsTab;
        if (itemId is { } id && TestCards.FirstOrDefault(c => c.Model.ItemId == id) is { } card) TestRevealRequested?.Invoke(card);
    }

    /// <summary>The view scrolls to a test card.</summary>
    public event Action<TestCardViewModel>? TestRevealRequested;

    /// <summary>
    /// Your answer: the agent is told, and the item's history says so. Doesn't work on finished work waiting for your
    /// review sends it back, with your note as the reason.
    /// </summary>
    internal bool AnswerTest(TestCardViewModel card, bool works, string? note, List<Attachment> files)
        => AnswerTest(card.Model, works, note, files);

    /// <summary>Same answer operation for a phone request, without constructing a UI card.</summary>
    internal bool AnswerTest(TestRequest test, bool works, string? note, List<Attachment> files)
    {
        if (Project.Remote != null)
        {
            // A project shared by your own ProJict: it's answered there.
            if (Main.Guest.Test(Project, test, works, note) is { } problem)
            {
                _state.StatusText = problem;
                return false;
            }
            _state.StatusText = works ? "Told it works." : "Told it doesn't work.";
            return true;
        }
        var sendBack = !works && Project.Find(test.ItemId)?.Item is { NeedsReview: true, IsDone: true };
        try
        {
            ChangeTests(() => TestFlow.Answer(Project, test, works, note, files, UserName));
        }
        catch (InvalidOperationException ex)
        {
            _state.StatusText = ex.Message;
            RefreshTests();
            return false;
        }
        if (sendBack) SendBackAfterTest(test.ItemId, note);
        _state.StatusText = works ? $"Told {test.AskedBy} it works."
            : sendBack ? $"Told {test.AskedBy} it doesn't work, and sent the item back."
            : $"Told {test.AskedBy} it doesn't work.";
        return true;
    }

    /// <summary>The finished item goes back to the agent as not accepted yet (as Send back does), with the note as the reason.</summary>
    internal void SendBackAfterTest(Guid itemId, string? note)
    {
        if (Project.Find(itemId) is not { Item: { NeedsReview: true, IsDone: true } }) return;
        AgentEdit(itemId, item =>
        {
            item.SetState(ItemState.Todo, UserName);
            item.AddActivity(UserName, $"{ActivityEntry.SentBack}: the test didn't work." + (string.IsNullOrWhiteSpace(note) ? "" : " " + note.Trim()), agent: false);
        });
    }

    /// <summary>Picks files for an answer and copies them into the data folder.</summary>
    internal async Task<List<Attachment>> ImportTestFiles(IReadOnlyList<string> paths)
    {
        var files = paths.Where(File.Exists).ToList();
        if (files.Count == 0) return [];
        try
        {
            return await Task.Run(() => files.Select(_state.Store.ImportAttachment).ToList());
        }
        catch (Exception ex)
        {
            _state.StatusText = "Could not add: " + ex.Message;
            return [];
        }
    }

    /// <summary>A screenshot (or files) from the clipboard, for an answer.</summary>
    internal async Task<List<Attachment>> PasteTestFiles()
    {
        var (files, png) = ClipboardAttachments.Read();
        if (files.Count > 0) return await ImportTestFiles(files);
        if (png == null)
        {
            _state.StatusText = "The clipboard has no image or files.";
            return [];
        }
        try { return [_state.Store.WriteAttachment($"Pasted image {DateTime.Now:yyyy-MM-dd HHmmss}.png", png)]; }
        catch (Exception ex)
        {
            _state.StatusText = "Could not add: " + ex.Message;
            return [];
        }
    }

    internal void SetTestStatus(string text) => _state.StatusText = text;

    /// <summary>"today 13:40", "yesterday 9:05", "3 Nov 14:00".</summary>
    internal static string When(DateTime at)
    {
        var culture = CultureInfo.CurrentCulture;
        return at.Date == DateTime.Today ? "today " + at.ToString("HH:mm", culture)
            : at.Date == DateTime.Today.AddDays(-1) ? "yesterday " + at.ToString("HH:mm", culture)
            : at.ToString(at.Year == DateTime.Today.Year ? "d MMM HH:mm" : "d MMM yyyy HH:mm", culture);
    }
}

/// <summary>A test in the To test tab: what to try, and your answer (or the agent's line when it closed it).</summary>
public sealed partial class TestCardViewModel : ObservableObject
{
    private readonly ProjectViewModel _owner;

    public TestCardViewModel(ProjectViewModel owner, TestRequest model)
    {
        _owner = owner;
        Model = model;
        Files = MakeFiles(model.Files);
        AnswerFiles = MakeFiles(model.AnswerFiles);
    }

    public TestRequest Model { get; }
    private TodoItem? Item => _owner.Project.Find(Model.ItemId)?.Item;

    public string ItemText => Item?.DisplayText() is { } text ? text.ReplaceLineEndings(" ") : "(the item was deleted)";
    public bool HasItem => Item != null;
    public string AskedText => $"{Model.AskedBy} asked {ProjectViewModel.When(Model.AskedAt)}";
    public string Steps => Model.Steps;
    public string? Expected => Model.Expected;
    public bool HasExpected => Model.Expected != null;
    public IReadOnlyList<AttachmentViewModel> Files { get; private set; }
    public bool HasFiles => Files.Count > 0;

    // Answered
    public bool IsOpen => Model.IsOpen;
    public bool Works => Model.State == TestState.Works;
    public bool DoesntWork => Model.State == TestState.DoesntWork;
    public bool IsClosed => Model.State == TestState.Closed;
    public string StateText => TestFlow.StateText(Model);
    public string AnsweredText => Model.AnsweredAt is { } at
        ? (Model.AnsweredByAgent ? $"{ProjectViewModel.When(at)}" : $"{Model.AnsweredBy} · {ProjectViewModel.When(at)}")
        : "";
    public string? Note => Model.Note;
    public bool HasNote => Model.Note != null;
    public IReadOnlyList<AttachmentViewModel> AnswerFiles { get; private set; }
    public bool HasAnswerFiles => AnswerFiles.Count > 0;

    // Your answer while you write it: Doesn't work opens a note.
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowsButtons))] private bool _isWritingNote;
    /// <summary>Works and Doesn't work…, until a note is being written (it has its own Send).</summary>
    public bool ShowsButtons => CanAnswer && !IsWritingNote;
    [ObservableProperty] private string _noteDraft = "";
    public ObservableCollection<Attachment> DraftFiles { get; } = [];
    public bool HasDraftFiles => DraftFiles.Count > 0;
    public string DraftFilesText => string.Join(", ", DraftFiles.Select(f => f.FileName));
    /// <summary>Files can go with an answer here; a shared project's host takes the note only.</summary>
    public bool CanAddFiles => _owner.Project.Remote == null;
    /// <summary>You answer here, or in a project shared by your own ProJict on another computer; someone else's are theirs.</summary>
    public bool CanAnswer => Model.IsOpen && _owner.Project.Remote is null or { Self: true };
    /// <summary>What Doesn't work will do.</summary>
    public string DoesntWorkHint => Item is { NeedsReview: true, IsDone: true }
        ? $"{Model.AskedBy} is told, and the item goes back to it as not accepted yet, with your note."
        : $"{Model.AskedBy} is told, with your note.";

    [RelayCommand] private void ShowItem()
    {
        if (Item is { } item) _owner.Reveal(item);
    }

    [RelayCommand] private void MarkWorks() => _owner.AnswerTest(this, works: true, null, []);

    [RelayCommand] private void StartDoesntWork()
    {
        IsWritingNote = true;
        OnPropertyChanged(nameof(DoesntWorkHint));
    }

    [RelayCommand] private void SendDoesntWork()
    {
        if (_owner.AnswerTest(this, works: false, NoteDraft, [.. DraftFiles])) Clear();
    }

    [RelayCommand] private void CancelNote() => Clear();

    [RelayCommand] private void ClearFiles()
    {
        DraftFiles.Clear();
        NotifyDraft();
    }

    /// <summary>Files picked or dropped for the answer.</summary>
    public async Task AddFiles(IReadOnlyList<string> paths) => Add(await _owner.ImportTestFiles(paths));

    [RelayCommand] private async Task PasteFiles() => Add(await _owner.PasteTestFiles());

    private void Add(List<Attachment> files)
    {
        foreach (var file in files) DraftFiles.Add(file);
        if (files.Count > 0) IsWritingNote = true;
        NotifyDraft();
    }

    private void Clear()
    {
        IsWritingNote = false;
        NoteDraft = "";
        DraftFiles.Clear();
        NotifyDraft();
    }

    private void NotifyDraft()
    {
        OnPropertyChanged(nameof(HasDraftFiles));
        OnPropertyChanged(nameof(DraftFilesText));
    }

    internal void Refresh()
    {
        Files = MakeFiles(Model.Files);
        AnswerFiles = MakeFiles(Model.AnswerFiles);
        foreach (var p in new[] { nameof(ItemText), nameof(HasItem), nameof(AskedText), nameof(Files), nameof(HasFiles), nameof(DoesntWorkHint), nameof(CanAddFiles), nameof(CanAnswer) })
            OnPropertyChanged(p);
    }

    private List<AttachmentViewModel> MakeFiles(List<Attachment>? files) =>
        [.. (files ?? []).Select(a => new AttachmentViewModel(_owner, a, () => { }))];
}
