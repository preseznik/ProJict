using System.Windows;
using ProJict.Core.Agents;

namespace ProJict.App.Views;

/// <summary>
/// The PIN you approve what can't be taken back with (#8793323e). Approving: "Are you sure?" with what's asked, then
/// the PIN (set here first when this computer has none). Setting it: the PIN twice, and the one you have to change it.
/// </summary>
public partial class PinDialog : Window
{
    internal enum Mode { Approve, Set, Change }

    private readonly AppState _state;
    private readonly Mode _mode;
    private readonly string? _why;
    private bool _sure;
    /// <summary>Approving on a computer without a PIN: it's set here first.</summary>
    private bool _setting;

    internal PinDialog(AppState state, Mode mode, string? by = null, string? asks = null, string? why = null)
    {
        InitializeComponent();
        _state = state;
        _mode = mode;
        _why = why;
        Title = mode == Mode.Approve ? "Approve" : "Your PIN";
        if (mode == Mode.Approve)
        {
            SureWho.Text = $"{by} asks you to approve:";
            SureWhat.Text = asks;
            OkButton.Content = "Yes, I'm sure";
        }
        else ShowPinPage();
        Loaded += (_, _) =>
        {
            if (PinPage.Visibility != Visibility.Visible) OkButton.Focus();
            else if (Current.Visibility == Visibility.Visible) Current.Focus();
            else Pin.Focus();
        };
    }

    /// <summary>"Are you sure?" and the PIN, before approving what <paramref name="by"/> asks; true when you confirmed.</summary>
    public static bool Approve(Window? owner, AppState state, string by, string asks) =>
        Show(new PinDialog(state, Mode.Approve, by, asks), owner);

    /// <summary>Sets this computer's PIN, or changes it with the one it has; true once it's set.</summary>
    public static bool SetUp(Window? owner, AppState state, string? why = null) =>
        Show(new PinDialog(state, state.HasApprovalPin ? Mode.Change : Mode.Set, why: why), owner);

    private static bool Show(PinDialog dialog, Window? owner)
    {
        if (owner is { IsVisible: true }) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog.ShowDialog() == true;
    }

    private void ShowPinPage()
    {
        _setting = _mode == Mode.Set || (_mode == Mode.Approve && !_state.HasApprovalPin);
        var changing = _mode == Mode.Change;
        SurePage.Visibility = Visibility.Collapsed;
        PinPage.Visibility = Visibility.Visible;
        PinTitle.Text = _mode switch
        {
            Mode.Approve => _setting ? "Set your PIN first" : "Your PIN",
            Mode.Change => "Change your PIN",
            _ => "Set your PIN",
        };
        PinText.Text = _why ?? (_mode == Mode.Approve && !_setting
            ? "Type this computer's PIN to approve it."
            : $"{ApprovalPin.MinDigits} to {ApprovalPin.MaxDigits} digits. When an AI agent asks to do what can't be taken back (publish a release, say), " +
              "approving it in ProJict asks if you're sure, then this PIN. It's kept on this computer only, hashed.");
        Current.Visibility = CurrentLabel.Visibility = changing ? Visibility.Visible : Visibility.Collapsed;
        PinLabel.Text = changing ? "New PIN" : "PIN";
        Repeat.Visibility = RepeatLabel.Visibility = _setting || changing ? Visibility.Visible : Visibility.Collapsed;
        OkButton.Content = _mode switch
        {
            Mode.Approve => _setting ? "Set it and approve" : "Approve",
            Mode.Change => "Change PIN",
            _ => "Set PIN",
        };
    }

    /// <summary>OK: on to the PIN, then checks it (or sets it); true once the dialog is done.</summary>
    internal bool Next()
    {
        if (_mode == Mode.Approve && !_sure)
        {
            _sure = true;
            ShowPinPage();
            if (IsLoaded) Pin.Focus();
            return false;
        }
        var problem = Check();
        if (problem == null) return true;
        Problem.Text = problem;
        Problem.Visibility = Visibility.Visible;
        Pin.Clear();
        Repeat.Clear();
        if (Current.Visibility == Visibility.Visible)
        {
            Current.Clear();
            if (IsLoaded) Current.Focus();
        }
        else if (IsLoaded) Pin.Focus();
        return false;
    }

    /// <summary>Why not, or null once the PIN is right (approving) or set.</summary>
    private string? Check()
    {
        if (_mode == Mode.Approve && !_setting) return _state.CheckApprovalPin(Pin.Password);
        if (_mode == Mode.Change && _state.CheckApprovalPin(Current.Password) is { } wrong) return wrong;
        if (ApprovalPin.Problem(Pin.Password) is { } bad) return bad;
        if (Pin.Password != Repeat.Password) return "The two PINs aren't the same.";
        _state.SetApprovalPin(Pin.Password);
        return null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Next()) DialogResult = true;
    }
}
