using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProJict.Core.Features;
using ProJict.Core.Model;

namespace ProJict.App.ViewModels;

/// <summary>Videos' publish plans on the board: pick a thumbnail, edit the text, approve or decline. See PublishFlow.</summary>
public sealed partial class ProjectViewModel
{
    /// <summary>Open plans, and finished ones until their card is closed (not on a shared project's guest: the host decides).</summary>
    public ObservableCollection<PublishPlanViewModel> PublishCards { get; } = [];
    public bool HasPublishCards => PublishCards.Count > 0;

    /// <summary>
    /// Changes a publish plan (yours here, an agent's through its tools). Not part of undo: plans stand for videos that
    /// really went out, and for what you approved. Throws InvalidOperationException with the reason.
    /// </summary>
    public void ChangePublish(Action change)
    {
        change();
        Touch();
        RefreshPublish();
    }

    internal bool TryPublish(Action change)
    {
        try
        {
            ChangePublish(change);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            _state.StatusText = ex.Message;
            RefreshPublish();
            return false;
        }
    }

    internal void SetStatus(string text) => _state.StatusText = text;

    /// <summary>The board shows this plan's card again (from a notification).</summary>
    public void RevealPublish(PublishPlan plan)
    {
        SelectedTab = 0;
        if (!plan.Dismissed) return;
        plan.Dismissed = false;
        Touch();
        RefreshPublish();
    }

    /// <summary>Brings the cards in line with the plans, keeping the ones shown (and what's being typed in them).</summary>
    public void RefreshPublish()
    {
        var plans = Project.Remote == null ? (Project.Publishes ?? []).Where(p => p.IsOpen || !p.Dismissed).ToList() : [];
        for (var i = PublishCards.Count - 1; i >= 0; i--)
            if (!plans.Contains(PublishCards[i].Model)) PublishCards.RemoveAt(i);
        for (var i = 0; i < plans.Count; i++)
        {
            var card = PublishCards.FirstOrDefault(c => c.Model == plans[i]);
            if (card == null) PublishCards.Insert(Math.Min(i, PublishCards.Count), new PublishPlanViewModel(this, plans[i]));
            else
            {
                card.Refresh();
                var at = PublishCards.IndexOf(card);
                if (at != i) PublishCards.Move(at, Math.Min(i, PublishCards.Count - 1));
            }
        }
        OnPropertyChanged(nameof(HasPublishCards));
    }
}

/// <summary>A publish plan's card.</summary>
public sealed partial class PublishPlanViewModel : ObservableObject
{
    private readonly ProjectViewModel _owner;

    public PublishPlanViewModel(ProjectViewModel owner, PublishPlan model)
    {
        _owner = owner;
        Model = model;
        Thumbnails = MakeThumbnails();
    }

    public PublishPlan Model { get; }
    public IReadOnlyList<PublishThumbnailViewModel> Thumbnails { get; private set; }
    public bool HasThumbnails => Thumbnails.Count > 0;

    public string Heading => $"Publish on {Model.Platform}: “{Model.Title}”";
    public string? ItemText => Model.ItemId is { } id && _owner.Project.Find(id) is { } loc ? $"For “{loc.Item.Text}”" : null;
    public bool HasItemText => ItemText != null;
    public string StateText => char.ToUpperInvariant(PublishFlow.StateName(Model.State)[0]) + PublishFlow.StateName(Model.State)[1..];
    public string StatusText => Model.State switch
    {
        PublishState.Planned => $"{Model.PlannedBy} planned it." + (Model.Thumbnails.Count > 1 ? " Pick a thumbnail, change" : " Change") +
                                " anything you like, then approve. Nothing goes out before that.",
        PublishState.Approved => $"Approved by {Model.DecidedBy}: {Model.PlannedBy} can publish it" + (Model.EditedByYou ? ", with your text." : "."),
        PublishState.Published => "Published.",
        PublishState.Failed => "It wasn't published; the log below says why.",
        PublishState.Declined => "Declined: nothing goes out.",
        _ => "Withdrawn: nothing goes out.",
    };
    public string? Note => Model.Note;
    /// <summary>The agent's note is for your decision: it goes once the plan is finished.</summary>
    public bool HasNote => Model.Note != null && Model.IsOpen;
    public string? Url => Model.Url;
    public bool HasUrl => Model.Url != null;
    public bool WaitsForYou => Model.State == PublishState.Planned;
    public bool IsReadOnly => !WaitsForYou;
    public bool CanWithdraw => Model.IsOpen;
    public bool CanDismiss => !Model.IsOpen;
    public IReadOnlyList<ShipLogLine> Log => Model.Log.Select(e => new ShipLogLine(e)).ToList();

    // The text, as you may edit it while the plan waits for you (applied when you leave the box).
    public string Title { get => Model.Title; set => Edit(value, Description, TagsText, PlaylistsText, When); }
    public string Description { get => Model.Description; set => Edit(Title, value, TagsText, PlaylistsText, When); }
    public string TagsText { get => string.Join(", ", Model.Tags); set => Edit(Title, Description, value, PlaylistsText, When); }
    public string PlaylistsText { get => string.Join(", ", Model.Playlists); set => Edit(Title, Description, TagsText, value, When); }
    public string? When { get => Model.When; set => Edit(Title, Description, TagsText, PlaylistsText, value); }

    private void Edit(string title, string description, string tags, string playlists, string? when) =>
        _owner.TryPublish(() => PublishFlow.Edit(Model, new PublishFlow.Text(title ?? "", description ?? "", PublishFlow.Split(tags), PublishFlow.Split(playlists), when),
            _owner.UserName));

    internal void Choose(int index) => _owner.TryPublish(() => PublishFlow.Choose(Model, index, _owner.UserName));

    [RelayCommand]
    private void Approve()
    {
        if (_owner.TryPublish(() => PublishFlow.Decide(Model, approve: true, _owner.UserName)))
            _owner.SetStatus($"Approved: {Model.PlannedBy} can publish “{Model.Title}”.");
    }

    [RelayCommand]
    private void Decline()
    {
        if (_owner.TryPublish(() => PublishFlow.Decide(Model, approve: false, _owner.UserName))) _owner.SetStatus("Declined the publish plan.");
    }

    [RelayCommand]
    private void Withdraw()
    {
        if (_owner.TryPublish(() => PublishFlow.Withdraw(Model, _owner.UserName))) _owner.SetStatus("Publish plan withdrawn.");
    }

    [RelayCommand]
    private void Dismiss()
    {
        if (Model.IsOpen) return;
        _owner.ChangePublish(() => Model.Dismissed = true);
    }

    [RelayCommand]
    private void OpenUrl()
    {
        if (Model.Url is not { } url || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception) { /* no browser: the link stays shown */ }
    }

    internal void Refresh()
    {
        if (!Thumbnails.Select(t => t.Attachment.Model).SequenceEqual(Model.Thumbnails)) Thumbnails = MakeThumbnails();
        else foreach (var t in Thumbnails) t.Refresh();
        OnPropertyChanged(string.Empty);
    }

    private List<PublishThumbnailViewModel> MakeThumbnails() =>
        [.. Model.Thumbnails.Select((a, i) => new PublishThumbnailViewModel(this, new AttachmentViewModel(_owner, a, () => { }), i))];
}

/// <summary>A thumbnail to choose: click it to pick it while the plan waits for you.</summary>
public sealed partial class PublishThumbnailViewModel(PublishPlanViewModel plan, AttachmentViewModel attachment, int index) : ObservableObject
{
    public AttachmentViewModel Attachment { get; } = attachment;
    public int Number => index + 1;
    public bool IsChosen => plan.Model.Chosen == index;
    public bool CanChoose => plan.WaitsForYou;
    public string ToolTip => (CanChoose ? $"Pick thumbnail {Number}" : $"Thumbnail {Number}") + $" · {Attachment.FileName}";

    [RelayCommand]
    private void Choose()
    {
        if (CanChoose) plan.Choose(index);
    }

    internal void Refresh()
    {
        OnPropertyChanged(nameof(IsChosen));
        OnPropertyChanged(nameof(CanChoose));
        OnPropertyChanged(nameof(ToolTip));
    }
}
