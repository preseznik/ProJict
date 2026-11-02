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
    }

    public ToolsViewModel ViewModel { get; }

    /// <summary>The key goes from the password box straight to the tool; it's never shown or kept in the window.</summary>
    private void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is not { } tool || KeyBox.Password.Trim().Length == 0) return;
        tool.SaveKey(KeyBox.Password);
        KeyBox.Clear();
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
