using System.Windows.Controls;
using System.Windows.Input;
using ProJict.App.ViewModels;

namespace ProJict.App.Views;

/// <summary>A person's or agent's profile card (#490e5810); Esc closes it.</summary>
public partial class ProfileCard : UserControl
{
    public ProfileCard()
    {
        InitializeComponent();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || DataContext is not ProfileCardViewModel card) return;
            card.CloseCommand.Execute(null);
            e.Handled = true;
        };
    }
}
