using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using ProJict.App.Views;
using ProJict.Core.Tools;

namespace ProJict.App.ViewModels;

/// <summary>
/// A tool in the browser, signed in once in ProJict's own browser (Sign in…), with a profile of its own on this
/// computer; and its API key, when it also has an API (Tripo3D).
/// </summary>
public sealed partial class ToolEditorViewModel
{
    /// <summary>Agents can get its key: an API tool's, or a browser tool's that also has an API.</summary>
    public bool TakesKey => tool.TakesKey;
    public string KeyLabel => tool.Access == ToolAccess.Browser ? "API key (only if it also has an API, like Tripo3D)" : "API key";

    public bool IsSignedIn => tool.SignedInAt != null;

    /// <summary>"Signed in on 3 Nov, 10:58." or how to.</summary>
    public string SignInText => tool.SignedInAt is { } at
        ? $"Signed in on {at.ToString("d MMM, HH:mm", CultureInfo.CurrentCulture)}. The sign-in is kept in a browser of its own, on this computer only."
        : "Not signed in yet: Sign in… opens its website in a browser of its own, where you sign in yourself.";

    private BrowserSignInWindow? _signIn;

    [RelayCommand]
    private void OpenSignIn()
    {
        if (_signIn != null)
        {
            _signIn.Activate();
            return;
        }
        var owner = Application.Current.Windows.OfType<ToolsWindow>().FirstOrDefault() ?? Application.Current.MainWindow;
        _signIn = new BrowserSignInWindow(Service, tool) { Owner = owner };
        _signIn.Closed += (_, _) =>
        {
            _signIn = null;
            OnPropertyChanged(nameof(IsSignedIn));
            OnPropertyChanged(nameof(SignInText));
            OnPropertyChanged(nameof(Subtitle));
        };
        _signIn.Show();
    }

    [RelayCommand]
    private async Task SignOut()
    {
        _signIn?.Close();
        var done = await Service.SignOutAsync(tool);
        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(SignInText));
        OnPropertyChanged(nameof(Subtitle));
        main.State.StatusText = done ? $"Signed out of {tool.Name}: ProJict's browser forgot its sign-in."
            : $"Couldn't sign out of {tool.Name} yet (its browser is still closing): try again in a moment.";
    }
}
