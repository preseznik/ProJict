using CommunityToolkit.Mvvm.Input;

namespace ProJict.App.ViewModels;

/// <summary>An item an agent asks you to test (#09a108b5): a "to test" chip, which opens the To test tab.</summary>
public sealed partial class ItemRowViewModel
{
    public bool HasTest => Owner.HasOpenTest(Item.Id);
    public string TestToolTip => Owner.TestToolTip(Item.Id);

    [RelayCommand] private void ShowTest() => Owner.RevealTests(Item.Id);

    internal void RefreshTest()
    {
        OnPropertyChanged(nameof(HasTest));
        OnPropertyChanged(nameof(TestToolTip));
    }
}
