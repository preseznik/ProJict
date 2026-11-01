using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProJict.Core.Features;
using ProJict.Core.Model;

namespace ProJict.App.ViewModels;

/// <summary>
/// The project's clean-up card: you ask for the repo folder to be cleaned up (header › Clean up, or right-click the
/// project), an agent posts what could go, you untick what stays and approve the safe part or everything ticked, and
/// ProJict moves exactly those to the Recycle Bin. Like shipping, it's not part of undo.
/// </summary>
public sealed partial class ProjectViewModel
{
    /// <summary>How a path goes to the Recycle Bin (a test can put something else here).</summary>
    public static Action<string, bool> RemoveToRecycleBin { get; set; } = (path, folder) =>
    {
        if (folder)
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        else
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
    };

    public bool CanCleanUp => !Project.IsInbox && Project.Remote == null;
    /// <summary>The last clean-up, while it's open or its card wasn't closed.</summary>
    public CleanupRequest? Cleanup => Project.Cleanups?.LastOrDefault() is { } c && (c.IsOpen || !c.Dismissed) && Project.Remote == null ? c : null;
    public bool HasCleanupCard => Cleanup != null;
    public string CleanupStateText => Cleanup is { } c ? CleanupFlow.StateName(c.State) : "";
    public bool CleanupWaitsForYou => Cleanup?.State == CleanupState.Planned;
    public bool CanWithdrawCleanup => Cleanup?.IsOpen == true;
    public bool CanDismissCleanup => Cleanup is { IsOpen: false };
    public string CleanupStatusText => Cleanup switch
    {
        { State: CleanupState.Requested } => "Waiting for an AI agent working here to scan the folder and post what could go. No other work here until it's decided.",
        { State: CleanupState.Planned } c => $"{c.PlannedBy} proposes {CleanupFlow.Summary(c)}. Untick what should stay, then clean up the safe part or everything ticked. " +
                                             "ProJict moves them to the Recycle Bin, so you can get them back.",
        { State: CleanupState.Done } c => $"{c.DecidedBy} approved it: {c.Moved} path(s), {CleanupFlow.Size(c.Freed)}, moved to the Recycle Bin.",
        { State: CleanupState.Declined } c => $"{c.DecidedBy} declined the plan: nothing was removed.",
        { } => "Withdrawn: nothing was removed.",
        _ => "",
    };

    private IReadOnlyList<CleanupEntryRow>? _cleanupEntries;
    /// <summary>The plan's lines, kept until the clean-up changes, so a tick stays on the row you see.</summary>
    public IReadOnlyList<CleanupEntryRow> CleanupEntries => _cleanupEntries ??= Cleanup?.Entries?.Select(e => new CleanupEntryRow(e, Cleanup.State == CleanupState.Planned, () =>
    {
        Touch();
        RefreshCleanupTotals();
    })).ToList() ?? [];
    public bool HasCleanupEntries => CleanupEntries.Count > 0;
    public string CleanUpSafeText => Totals(CleanupLevel.Safe, "Clean up safe");
    public string CleanUpAllText => Totals(CleanupLevel.Thorough, "Clean up all ticked");
    public bool CanCleanUpSafe => Cleanup is { State: CleanupState.Planned } c && CleanupFlow.Selected(c, CleanupLevel.Safe).Count > 0;
    public bool CanCleanUpAll => Cleanup is { State: CleanupState.Planned } c && CleanupFlow.Selected(c, CleanupLevel.Thorough).Count > 0;
    public IReadOnlyList<ShipLogLine> CleanupLog => Cleanup?.Log.Select(l => new ShipLogLine(l)).ToList() ?? [];

    private string Totals(CleanupLevel level, string text) =>
        Cleanup is { } c && CleanupFlow.Selected(c, level) is { Count: > 0 } chosen ? $"{text} ({chosen.Count}, {CleanupFlow.Size(chosen.Sum(e => e.Size))})" : text;

    /// <summary>A change to the clean-up (yours or an agent's plan): saved, not undone by Ctrl+Z.</summary>
    public void ChangeCleanup(Action change)
    {
        change();
        Touch();
        RefreshCleanup();
    }

    private bool TryCleanup(Action change)
    {
        try
        {
            ChangeCleanup(change);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            _state.StatusText = ex.Message;
            return false;
        }
    }

    public void RefreshCleanup()
    {
        _cleanupEntries = null;
        foreach (var p in new[] { nameof(CanCleanUp), nameof(Cleanup), nameof(HasCleanupCard), nameof(CleanupStateText), nameof(CleanupWaitsForYou), nameof(CanWithdrawCleanup),
                     nameof(CanDismissCleanup), nameof(CleanupStatusText), nameof(CleanupEntries), nameof(HasCleanupEntries), nameof(CleanupLog) })
            OnPropertyChanged(p);
        RefreshCleanupTotals();
    }

    private void RefreshCleanupTotals()
    {
        foreach (var p in new[] { nameof(CleanUpSafeText), nameof(CleanUpAllText), nameof(CanCleanUpSafe), nameof(CanCleanUpAll) }) OnPropertyChanged(p);
    }

    /// <summary>Header › Clean up, or right-click the project › Clean up…: asks the agents working here for a plan.</summary>
    [RelayCommand]
    public void StartCleanup()
    {
        SelectedTab = 0;
        if (TryCleanup(() => CleanupFlow.Request(Project, _state.UserName)))
            _state.StatusText = $"Asked for {Project.Name}'s folder to be cleaned up: an AI agent working here posts what could go, and you decide.";
    }

    [RelayCommand] private void CleanUpSafe() => RunCleanup(CleanupLevel.Safe);
    [RelayCommand] private void CleanUpAll() => RunCleanup(CleanupLevel.Thorough);
    [RelayCommand] private void DeclineCleanup() { if (Cleanup is { } c) TryCleanup(() => CleanupFlow.Decline(c, _state.UserName)); }
    [RelayCommand] private void WithdrawCleanup() { if (Cleanup is { } c) TryCleanup(() => CleanupFlow.Withdraw(c, _state.UserName)); }

    [RelayCommand]
    private void DismissCleanup()
    {
        if (Cleanup is not { IsOpen: false } c) return;
        c.Dismissed = true;
        Touch();
        RefreshCleanup();
    }

    /// <summary>Your approval: ProJict moves exactly the chosen paths, inside the repo folder, to the Recycle Bin.</summary>
    private void RunCleanup(CleanupLevel level)
    {
        if (Cleanup is not { } request || Project.RepoPath is not { Length: > 0 } repoPath) return;
        List<CleanupEntry> chosen = [];
        if (!TryCleanup(() => chosen = CleanupFlow.Approve(request, level, _state.UserName))) return;
        var repo = Path.GetFullPath(repoPath).TrimEnd(Path.DirectorySeparatorChar);
        foreach (var entry in chosen)
        {
            var full = Path.GetFullPath(Path.Combine(repo, entry.Path));
            try
            {
                if (!full.StartsWith(repo + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) entry.Result = "outside the repo folder: left alone";
                else if (!(entry.Folder ? Directory.Exists(full) : File.Exists(full))) entry.Result = "not there any more";
                else
                {
                    RemoveToRecycleBin(full, entry.Folder);
                    entry.Result = CleanupFlow.Moved;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException)
            {
                entry.Result = ex.Message;
            }
        }
        ChangeCleanup(() => CleanupFlow.Finish(request, chosen));
        _state.StatusText = $"Moved {request.Moved} path(s) from {Project.Name}'s folder to the Recycle Bin, freeing {CleanupFlow.Size(request.Freed)}.";
    }
}

/// <summary>A line of the clean-up plan: what, how big, why, and your tick.</summary>
public sealed partial class CleanupEntryRow(CleanupEntry entry, bool editable, Action changed) : ObservableObject
{
    public string Path => entry.Path + (entry.Folder ? "\\" : "");
    public string Size => CleanupFlow.Size(entry.Size);
    public string Level => entry.Level == CleanupLevel.Safe ? "safe" : "thorough";
    public bool IsSafe => entry.Level == CleanupLevel.Safe;
    public string Reason => entry.Reason + (entry.Tracked ? " · git tracks it" : "");
    public string? Result => entry.Result;
    public bool IsEditable => editable;

    public bool IsChecked
    {
        get => entry.Checked;
        set
        {
            if (!editable || value == entry.Checked) return;
            entry.Checked = value;
            OnPropertyChanged();
            changed();
        }
    }
}
