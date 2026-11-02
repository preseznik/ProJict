using System.Windows;
using Microsoft.Win32;
using ProJict.App.ViewModels;

namespace ProJict.App.Views;

/// <summary>ProJict's API: turning it on, where it listens, and the tokens that may use it.</summary>
public partial class ApiWindow : Window
{
    private readonly ApiViewModel _vm;

    public ApiWindow(MainViewModel main)
    {
        InitializeComponent();
        _vm = new ApiViewModel(main);
        DataContext = _vm;
        Closed += (_, _) => _vm.Detach();
    }

    public ApiViewModel ViewModel => _vm;

    private void CopySetup_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.CopySetup()) MessageBox.Show(this, "The clipboard is busy; select the text and press Ctrl+C.", "ProJict", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void CopySecret_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.CopySecret()) MessageBox.Show(this, "The clipboard is busy; select the token and press Ctrl+C.", "ProJict", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void SaveCertificate_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { FileName = $"ProJict-{Environment.MachineName}.crt", Filter = "Certificate (*.crt)|*.crt", Title = "Save ProJict's certificate" };
        if (dialog.ShowDialog(this) != true) return;
        try { _vm.SaveCertificate(dialog.FileName); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "ProJict", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
