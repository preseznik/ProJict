using System.Windows;

namespace ProJict.App.Services;

/// <summary>"Are you sure?" before something you can't take back: Yes or No. Off-screen tests answer for you.</summary>
public static class Confirm
{
    /// <summary>Asks <paramref name="question"/> under <paramref name="title"/>; true for Yes.</summary>
    public static Func<string, string, bool> Ask { get; set; } = (question, title) =>
        MessageBox.Show(Application.Current.MainWindow!, question, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
}
