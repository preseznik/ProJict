using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using ProJict.App.Services;
using ProJict.Core.Tools;

namespace ProJict.App.Views;

/// <summary>
/// Settings › Tools › Sign in…: a tool's website in ProJict's own browser (Edge WebView2), with a browser profile of the
/// tool's own on this computer. You sign in there as you usually do; ProJict never sees the password, and the sign-in
/// stays in that profile for agents using the tool. "Done" notes when you signed in.
/// </summary>
public partial class BrowserSignInWindow : Window
{
    private readonly ToolService _tools;
    private readonly ExternalTool _tool;

    public BrowserSignInWindow(ToolService tools, ExternalTool tool)
    {
        InitializeComponent();
        _tools = tools;
        _tool = tool;
        Title = $"Sign in to {tool.Name}";
        Headline.Text = $"Sign in to {tool.Name} as you usually do";
        Hint.Text = "ProJict never sees your password. The sign-in is kept in a browser of its own for " + tool.Name +
                    ", on this computer only, for agents using it. If a Google sign-in won't work here, use the site's own email sign-in.";
        Loaded += async (_, _) => await OpenAsync();
    }

    /// <summary>The tool's own profile, and its website (or a blank page when it has none yet).</summary>
    private async Task OpenAsync()
    {
        try
        {
            Web.CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = _tools.ProfileFolder(_tool) };
            await Web.EnsureCoreWebView2Async();
            Web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            if (_tool.Website is { } site) Web.CoreWebView2.Navigate(site);
            else Show($"{_tool.Name} has no website yet: add it in the Tools window first.");
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Show(ex is WebView2RuntimeNotFoundException
                ? "Signing in needs Microsoft Edge WebView2, which isn't on this computer (it comes with Windows 11; for Windows 10 get it from microsoft.com)."
                : $"ProJict's browser didn't start: {ex.Message}");
        }
    }

    private void Show(string problem)
    {
        Problem.Text = problem;
        Problem.Visibility = Visibility.Visible;
        DoneButton.IsEnabled = false;
    }

    /// <summary>The web page in this window (for tests: what it shows).</summary>
    internal WebView2 Browser => Web;

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        _tools.SignedIn(_tool);
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        // Lets go of the profile, so Sign out can remove it.
        Web.Dispose();
        base.OnClosed(e);
    }
}
