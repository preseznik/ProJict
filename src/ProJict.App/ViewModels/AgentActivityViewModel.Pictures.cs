using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;

namespace ProJict.App.ViewModels;

/// <summary>
/// Agents' pictures in the Agents view: on their cards and in All agents (lists elsewhere keep the initials). Agents
/// make their own (set_picture); you change or remove one with right-click.
/// </summary>
public sealed partial class AgentActivityViewModel
{
    internal ImageSource? PictureOf(string agent) => _state.Pictures.Of(agent);
    internal bool PictureWanted(string agent) => _state.Settings.PicturesWanted?.Contains(agent, StringComparer.OrdinalIgnoreCase) == true;

    /// <summary>Asks for an image file; tests answer for you. Null when cancelled.</summary>
    public Func<string, string?> PickPicture { get; set; } = agent =>
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = $"A picture for {agent}", Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All files|*.*" };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    };

    internal void ChangePicture(string agent)
    {
        if (PickPicture(agent) is not { } file) return;
        try
        {
            _state.Pictures.Set(agent, file);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        {
            _state.StatusText = ex.Message;
            return;
        }
        if (_state.Settings.PicturesWanted is { } wanted && wanted.RemoveAll(n => string.Equals(n, agent, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            if (wanted.Count == 0) _state.Settings.PicturesWanted = null;
            _state.MarkSettingsDirty();
        }
        _state.StatusText = $"{agent} has a new picture.";
        RefreshPictures();
    }

    internal void RemovePicture(string agent)
    {
        _state.Pictures.Remove(agent);
        _state.StatusText = $"Removed {agent}'s picture: its initials show again.";
        RefreshPictures();
    }

    /// <summary>A picture was set, removed or asked for: cards and rows follow.</summary>
    public void RefreshPictures()
    {
        foreach (var card in Live) card.RefreshPicture();
        foreach (var row in AllAgents) row.RefreshPicture();
    }
}

public sealed partial class AgentCardViewModel
{
    public ImageSource? Picture => _owner.PictureOf(Name);
    public bool HasPicture => Picture != null;
    /// <summary>It can't make one and asked another agent to.</summary>
    public bool PictureWanted => !HasPicture && _owner.PictureWanted(Name);
    public void RefreshPicture()
    {
        OnPropertyChanged(nameof(Picture));
        OnPropertyChanged(nameof(HasPicture));
        OnPropertyChanged(nameof(PictureWanted));
    }
    [RelayCommand] private void ChangePicture() => _owner.ChangePicture(Name);
    [RelayCommand] private void RemovePicture() => _owner.RemovePicture(Name);
}

public sealed partial class AgentListRow
{
    public ImageSource? Picture => _owner.PictureOf(Name);
    public bool HasPicture => Picture != null;
    public bool PictureWanted => !HasPicture && _owner.PictureWanted(Name);
    public void RefreshPicture()
    {
        OnPropertyChanged(nameof(Picture));
        OnPropertyChanged(nameof(HasPicture));
        OnPropertyChanged(nameof(PictureWanted));
    }
    [RelayCommand] private void ChangePicture() => _owner.ChangePicture(Name);
    [RelayCommand] private void RemovePicture() => _owner.RemovePicture(Name);
}
