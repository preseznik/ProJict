using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace ProJict.App.Views;

/// <summary>Asked once, before the first start: where the data folder lives, and the PIN for approvals if you set it now (#8793323e).</summary>
public partial class FirstRunWindow : Window
{
    private FirstRunWindow(string suggested)
    {
        InitializeComponent();
        Folder.Text = suggested;
    }

    /// <summary>Returns the chosen folder and the PIN (null when left empty), or null when the window was closed.</summary>
    public static (string Folder, string? Pin)? Ask(string suggested)
    {
        var window = new FirstRunWindow(suggested);
        return window.ShowDialog() == true ? (window.Folder.Text.Trim(), window.ChosenPin) : null;
    }

    /// <summary>The PIN typed (twice), or null when it's left for later.</summary>
    internal string? ChosenPin => Pin.Password.Length == 0 && Repeat.Password.Length == 0 ? null : Pin.Password;

    /// <summary>Why the PIN can't be taken, or null (also when it's left empty).</summary>
    internal string? PinProblemText() =>
        ChosenPin == null ? null
        : ProJict.Core.Agents.ApprovalPin.Problem(Pin.Password) ?? (Pin.Password != Repeat.Password ? "The two PINs aren't the same." : null);

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Folder for your ProJict data" };
        var parent = Path.GetDirectoryName(Folder.Text.Trim().TrimEnd('\\'));
        if (Directory.Exists(Folder.Text)) dialog.InitialDirectory = Folder.Text;
        else if (Directory.Exists(parent)) dialog.InitialDirectory = parent;
        if (dialog.ShowDialog(this) == true) Folder.Text = dialog.FolderName;
    }

    private void Folder_TextChanged(object sender, TextChangedEventArgs e)
    {
        var hasData = false;
        try { hasData = File.Exists(Path.Combine(Folder.Text.Trim(), "workspace.json")); }
        catch (ArgumentException) { }
        Existing.Visibility = hasData ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (PinProblemText() is { } problem)
        {
            PinProblem.Text = problem;
            PinProblem.Visibility = Visibility.Visible;
            Pin.Clear();
            Repeat.Clear();
            Pin.Focus();
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetFullPath(Folder.Text.Trim()));
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Cannot use this folder", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
