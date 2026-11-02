using CommunityToolkit.Mvvm.Input;
using ProJict.Core.Chat;
using ProJict.Core.Model;

namespace ProJict.App.ViewModels;

/// <summary>
/// A channel's project: a button by its name opens the project's board, and a channel not linked to one yet but named
/// after a project (#dirt2vr for DIRT2VR) offers to link it. Moderators link or unlink from the channel's ⋯ menu.
/// Only this ProJict's own channels; another ProJict's keep what they're about there.
/// </summary>
public sealed partial class ChatViewModel
{
    /// <summary>The project the open channel is about, when it's one of yours.</summary>
    private Project? LinkedProject => Channel is { Link: { } link } && Here.Host == null ? _state.Projects.FirstOrDefault(p => p.Id == link.ProjectId) : null;
    public bool HasLinkedProject => LinkedProject != null;
    public string LinkedProjectName => LinkedProject?.Name ?? "";
    public string LinkedProjectToolTip => $"Open {LinkedProjectName}'s board" + (Channel?.Link is { CategoryId: null, ItemId: null } ? "" : $": {LinkText}");

    /// <summary>Not linked yet, named after one of your projects (#dirt2vr, #dirt2vr-hud): that project, for a moderator to link it to.</summary>
    private Project? SuggestedProject => Channel is { Direct: false, Link: null } channel && Here.Host == null && IsModerator
        ? LinkableProjects.FirstOrDefault(p => Slug(p.Name) is { Length: > 0 } slug && (channel.Name == slug || channel.Name.StartsWith(slug + "-", StringComparison.Ordinal)))
        : null;
    public bool HasSuggestedProject => SuggestedProject != null;
    public string SuggestLinkText => SuggestedProject is { } project ? $"Link to {project.Name}" : "";
    public string SuggestLinkToolTip => SuggestedProject is { } project
        ? $"This channel isn't linked to a project yet. Linked to {project.Name}, it opens from the project's Chat button and its chip, and the project opens from here."
        : "";

    /// <summary>Your projects a channel can be about (not the inbox, not another ProJict's).</summary>
    internal IReadOnlyList<Project> LinkableProjects =>
        [.. _state.Projects.Where(p => !p.IsInbox && p.Remote == null).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>The channel can be linked here: one of yours, not private, and you moderate it.</summary>
    internal bool CanLink => Channel is { Direct: false } && Here.Host == null && IsModerator;

    private static string Slug(string name) =>
        new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');

    [RelayCommand]
    private void LinkToSuggested()
    {
        if (SuggestedProject is { } project) LinkTo(project);
    }

    /// <summary>Makes the open channel about a project (its whole board), or about nothing in particular.</summary>
    internal void LinkTo(Project? project)
    {
        if (Channel is not { } channel || !CanLink) return;
        if (!Try(() => Room.SetLink(channel, Me, project == null ? null : new ChannelLink { ProjectId = project.Id }, project?.Name))) return;
        _state.StatusText = project == null ? $"#{channel.Name} isn't about a project any more." : $"#{channel.Name} is about {project.Name} now: it opens from the project, and the project from here.";
        OpenChannel(channel);
    }

    private void SyncLink()
    {
        foreach (var name in new[] { nameof(HasLinkedProject), nameof(LinkedProjectName), nameof(LinkedProjectToolTip), nameof(HasSuggestedProject),
                     nameof(SuggestLinkText), nameof(SuggestLinkToolTip) })
            OnPropertyChanged(name);
    }
}
