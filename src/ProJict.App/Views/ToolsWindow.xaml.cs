using System.Windows;
using ProJict.App.ViewModels;

namespace ProJict.App.Views;

/// <summary>Settings › Tools: the outside tools agents may use, their keys, projects and budgets.</summary>
public partial class ToolsWindow : Window
{
    public ToolsWindow(MainViewModel main)
    {
        InitializeComponent();
        ViewModel = new ToolsViewModel(main);
        DataContext = ViewModel;
        // A key typed but not saved isn't lost: picking another tool or closing the window keeps it too.
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ToolsViewModel.Selected)) KeepTypedKey();
        };
        Closing += (_, _) => KeepTypedKey();
    }

    public ToolsViewModel ViewModel { get; }

    /// <summary>The tool a key being typed is for (the box stays the same while you pick another tool).</summary>
    private ToolEditorViewModel? _keyFor;

    private void KeyBox_PasswordChanged(object sender, RoutedEventArgs e) => _keyFor = KeyBox.Password.Length > 0 ? _keyFor ?? ViewModel.Selected : null;

    private void KeyBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        KeepTypedKey();
        e.Handled = true;
    }

    private void SaveKey_Click(object sender, RoutedEventArgs e) => KeepTypedKey();

    /// <summary>
    /// The key goes from the password box straight to the tool it was typed for (with Save key, Enter, another tool or
    /// Close); it's never shown or kept in the window. A tool switched away from its API meanwhile gets none.
    /// </summary>
    private void KeepTypedKey()
    {
        var tool = _keyFor;
        if (tool is { IsApi: true } && KeyBox.Password.Trim().Length > 0) tool.SaveKey(KeyBox.Password);
        KeyBox.Clear();
        _keyFor = null;
    }

    /// <summary>A workflow file exported from ComfyUI (Workflow › Export (API)) goes into the tool's library.</summary>
    private void AddWorkflow_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is not { } tool) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "ComfyUI workflow (API format)|*.json", Title = $"Add a workflow to {tool.Name}", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var file in dialog.FileNames) tool.AddWorkflowFile(file);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
