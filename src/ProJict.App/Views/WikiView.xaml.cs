using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using ProJict.App.Services;
using ProJict.App.ViewModels;

namespace ProJict.App.Views;

public partial class WikiView : UserControl
{
    private WikiViewModel? _wiki;
    private WikiPageViewModel? _page;

    public WikiView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as WikiViewModel);
    }

    private void Attach(WikiViewModel? wiki)
    {
        if (_wiki != null) _wiki.PropertyChanged -= OnWikiChanged;
        _wiki = wiki;
        if (_wiki != null) _wiki.PropertyChanged += OnWikiChanged;
        AttachPage(_wiki?.Page);
    }

    private void OnWikiChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WikiViewModel.Page)) AttachPage(_wiki?.Page);
    }

    private void AttachPage(WikiPageViewModel? page)
    {
        if (_page != null)
        {
            _page.PropertyChanged -= OnPageChanged;
            _page.InsertRequested -= Insert;
        }
        _page = page;
        if (_page == null) return;
        _page.PropertyChanged += OnPageChanged;
        _page.InsertRequested += Insert;
        if (_page.IsEditing) FocusEditor(selectTitle: _page.EditText.Length == 0 || _page.Model.Version == 1);
    }

    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WikiPageViewModel.IsEditing) && _page is { IsEditing: true }) FocusEditor(selectTitle: false);
    }

    /// <summary>Into the editor once it shows; a new page's title first, so it gets a name.</summary>
    private void FocusEditor(bool selectTitle) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
    {
        if (selectTitle && _page?.Model.Title.StartsWith("New page", StringComparison.Ordinal) == true)
        {
            TitleBox.Focus();
            TitleBox.SelectAll();
            return;
        }
        Editor.Focus();
        if (Editor.CaretIndex == 0) Editor.CaretIndex = Editor.Text.Length;
    });

    /// <summary>Text (a link to a file) goes where the caret is, as typing would, so Ctrl+Z in the box takes it out again.</summary>
    private void Insert(string text)
    {
        if (!Editor.IsVisible)
        {
            Editor.Text = Editor.Text.TrimEnd() + "\n\n" + text + "\n";
            return;
        }
        var at = Editor.SelectionStart;
        var before = at > 0 && Editor.Text[at - 1] != '\n' ? "\n" : "";
        Editor.SelectedText = before + text + "\n";
        Editor.CaretIndex = at + before.Length + text.Length + 1;
        Editor.Focus();
    }

    private void Editor_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_page != null) _page.Caret = Editor.CaretIndex;
    }

    private void TitleBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        // Enter takes the name and goes on to the text.
        TitleBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        if (_page is { IsEditing: true }) Editor.Focus();
        else Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.DataContext = button.DataContext;
        menu.IsOpen = true;
    }

    /// <summary>A double-click on the page opens it for editing.</summary>
    private void Reader_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkContentElement { Parent: System.Windows.Documents.Hyperlink }) return;
        _page?.StartEditing();
    }

    // ───────────── Pictures and files into the editor ─────────────

    private void Editor_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void Editor_PreviewDrop(object sender, DragEventArgs e)
    {
        if (_page == null || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        e.Handled = true;
        // Where it was dropped.
        var at = Editor.GetCharacterIndexFromPoint(e.GetPosition(Editor), snapToText: true);
        if (at >= 0) Editor.CaretIndex = Math.Min(Editor.Text.Length, at + 1);
        _ = _page.AddFilesAsync(files, insert: true);
    }

    private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_page == null || e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control || !ClipboardAttachments.HasAttachment()) return;
        var (files, png) = ClipboardAttachments.Read();
        if (files.Count == 0 && png == null) return;
        e.Handled = true;
        _ = _page.AddFilesAsync(files, insert: true, png);
    }
}
