using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;

namespace ProJict.App.ViewModels;

/// <summary>The project's icon: beside its name in the header, set in Details › Icon (or the sidebar's right-click menu).</summary>
public sealed partial class ProjectViewModel
{
    public ImageSource? Icon => Main.IconOf(Project);
    public bool HasIcon => Icon != null;

    [RelayCommand] private void ChooseIcon() => Main.SetIcon(Project);
    [RelayCommand] private void RemoveIcon() => Main.RemoveIcon(Project);

    public void RefreshIcon()
    {
        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(HasIcon));
    }
}
