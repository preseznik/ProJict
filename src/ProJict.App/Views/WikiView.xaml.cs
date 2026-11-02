using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using ProJict.App.Services;
using ProJict.App.ViewModels;
using ProJict.Core.Wiki;

namespace ProJict.App.Views;

public partial class WikiView : UserControl
{
    private WikiViewModel? _wiki;
    private WikiPageViewModel? _page;

    public WikiView()
    {
        InitializeComponent();
        // Only while shown: a view that's gone (another view, or the theme changed) lets go of the page.
        Loaded += (_, _) => Attach(DataContext as WikiViewModel);
        Unloaded += (_, _) => Attach(null);
        DataContextChanged += (_, _) =>
        {
            if (IsLoaded) Attach(DataContext as WikiViewModel);
        };
    }

    /// <summary>
    /// Shows a drawn page in a viewer, taking it from one that had it before (a page belongs to one viewer at a time).
    /// The same page drawn again (a diagram ready, the preview after typing) stays where you were scrolled to.
    /// </summary>
    private void Show(FlowDocumentScrollViewer viewer, System.Windows.Documents.FlowDocument? document, bool keepScroll)
    {
        if (ReferenceEquals(viewer.Document, document)) return;
        var scroller = keepScroll ? FindScroller(viewer) : null;
        var offset = scroller?.VerticalOffset ?? 0;
        if (document?.Parent is FlowDocumentScrollViewer other && other != viewer) other.Document = null;
        viewer.Document = document;
        if (scroller != null && offset > 0) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => scroller.ScrollToVerticalOffset(offset));
    }

    private static ScrollViewer? FindScroller(DependencyObject parent)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer scroller) return scroller;
            if (FindScroller(child) is { } found) return found;
        }
        return null;
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
        Show(Reader, page?.Document, keepScroll: false);
        Show(PreviewViewer, page is { IsEditing: true } ? page.Preview : null, keepScroll: false);
        if (_page == null) return;
        _page.PropertyChanged += OnPageChanged;
        _page.InsertRequested += Insert;
        if (_page.IsEditing) FocusEditor(selectTitle: _page.EditText.Length == 0 || _page.Model.Version == 1);
    }

    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_page == null) return;
        switch (e.PropertyName)
        {
            case nameof(WikiPageViewModel.Document):
                Show(Reader, _page.Document, keepScroll: true);
                break;
            case nameof(WikiPageViewModel.Preview) when _page.IsEditing:
                Show(PreviewViewer, _page.Preview, keepScroll: true);
                break;
            case nameof(WikiPageViewModel.IsEditing) when _page.IsEditing:
                Show(PreviewViewer, _page.Preview, keepScroll: false);
                FocusEditor(selectTitle: false);
                break;
        }
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

    private void Diagram_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var menu = DiagramMenu();
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>The Diagram menu: a starter of each kind, put where you're writing (Ctrl+Z in the box takes it out again).</summary>
    internal ContextMenu DiagramMenu()
    {
        var menu = new ContextMenu();
        foreach (var starter in DiagramStarters.All())
        {
            var item = new MenuItem { Header = starter.Name };
            item.Click += (_, _) => Insert(starter.Text);
            menu.Items.Add(item);
        }
        return menu;
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
