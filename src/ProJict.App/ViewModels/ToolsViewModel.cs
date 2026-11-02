using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProJict.App.Services;
using ProJict.Core.Model;
using ProJict.Core.Tools;

namespace ProJict.App.ViewModels;

/// <summary>
/// Settings › Tools: the outside tools agents may use, listed on the left, the chosen one's details on the right (what
/// it's for, its website, API or browser, its key, how you're signed in, a budget, its projects and the agents that may
/// always have its key). Every change is kept at once.
/// </summary>
public sealed partial class ToolsViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public ToolsViewModel(MainViewModel main)
    {
        _main = main;
        Tools = new ObservableCollection<ToolEditorViewModel>(main.Tools.Tools.Select(t => new ToolEditorViewModel(t, main)));
        Selected = Tools.FirstOrDefault();
    }

    public ObservableCollection<ToolEditorViewModel> Tools { get; }
    [ObservableProperty] private ToolEditorViewModel? _selected;
    public bool HasSelected => Selected != null;
    public bool IsEmpty => Tools.Count == 0;

    partial void OnSelectedChanged(ToolEditorViewModel? value)
    {
        ConfirmRemove = false;
        OnPropertyChanged(nameof(HasSelected));
    }

    [RelayCommand]
    private void AddTool()
    {
        var tool = _main.Tools.Data.Add();
        _main.Tools.Save();
        var editor = new ToolEditorViewModel(tool, _main);
        Tools.Add(editor);
        Selected = editor;
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Remove was clicked once: the button asks to be clicked again.</summary>
    [ObservableProperty] private bool _confirmRemove;
    public string RemoveText => ConfirmRemove ? "Click again to remove it, with its key" : "Remove the tool…";
    partial void OnConfirmRemoveChanged(bool value) => OnPropertyChanged(nameof(RemoveText));

    [RelayCommand]
    private void RemoveTool()
    {
        if (Selected is not { } editor) return;
        if (!ConfirmRemove)
        {
            ConfirmRemove = true;
            return;
        }
        var at = Tools.IndexOf(editor);
        _main.Tools.Remove(editor.Model);
        Tools.Remove(editor);
        Selected = Tools.Count > 0 ? Tools[Math.Min(at, Tools.Count - 1)] : null;
        OnPropertyChanged(nameof(IsEmpty));
        _main.State.StatusText = $"Removed {editor.Model.Name} from the tools.";
    }
}

/// <summary>One tool in the Tools window, edited in place.</summary>
public sealed partial class ToolEditorViewModel(ExternalTool tool, MainViewModel main) : ObservableObject
{
    private ToolService Service => main.Tools;
    public ExternalTool Model => tool;

    private void Changed(params string[] names)
    {
        Service.Save();
        foreach (var name in names) OnPropertyChanged(name);
        OnPropertyChanged(nameof(Subtitle));
    }

    public string Name
    {
        get => tool.Name;
        set
        {
            value = (value ?? "").Trim();
            NameProblem = Service.Data.NameProblem(value, tool);
            OnPropertyChanged(nameof(NameProblem));
            if (NameProblem != null || value == tool.Name) return;
            tool.Name = value;
            Changed(nameof(Name), nameof(AllowedHint));
        }
    }
    public string? NameProblem { get; private set; }

    /// <summary>"API · key set", "API · no key yet", "in the browser".</summary>
    public string Subtitle => tool.Access == ToolAccess.Browser ? "in the browser" : tool.HasKey ? "API · key set" : "API · no key yet";

    public string Purpose
    {
        get => tool.Purpose;
        set
        {
            value = Clip(value) ?? "";
            if (value == tool.Purpose) return;
            tool.Purpose = value;
            Changed(nameof(Purpose));
        }
    }

    public string Website
    {
        get => tool.Website ?? "";
        set
        {
            var text = (value ?? "").Trim();
            var url = ToolData.Website(text);
            WebsiteProblem = text.Length > 0 && url == null ? "That isn't a web address (e.g. https://suno.com)." : null;
            OnPropertyChanged(nameof(WebsiteProblem));
            if (WebsiteProblem != null || url == tool.Website) return;
            tool.Website = url;
            Changed(nameof(Website), nameof(HasWebsite));
        }
    }
    public string? WebsiteProblem { get; private set; }
    public bool HasWebsite => tool.Website != null;

    [RelayCommand]
    private void OpenWebsite()
    {
        if (tool.Website == null) return;
        try { Process.Start(new ProcessStartInfo(tool.Website) { UseShellExecute = true }); }
        catch (Exception) { main.State.StatusText = $"Couldn't open {tool.Website}."; }
    }

    public bool IsApi
    {
        get => tool.Access == ToolAccess.Api;
        set
        {
            if (!value || tool.Access == ToolAccess.Api) return;
            tool.Access = ToolAccess.Api;
            Changed(nameof(IsApi), nameof(IsBrowser));
        }
    }

    public bool IsBrowser
    {
        get => tool.Access == ToolAccess.Browser;
        set
        {
            if (!value || tool.Access == ToolAccess.Browser) return;
            tool.Access = ToolAccess.Browser;
            Changed(nameof(IsApi), nameof(IsBrowser));
        }
    }

    public string KeyText => tool.HasKey
        ? $"A key is set{(tool.KeyEnding != null ? $" (ending …{tool.KeyEnding})" : "")}, protected for your Windows user. Agents get it only when you allow it."
        : "No key yet. Paste it here; it's kept protected for your Windows user, never in the workspace, its mirror or backups.";
    public bool HasKey => tool.HasKey;

    /// <summary>A key typed in the window (from a password box, so it's never shown).</summary>
    public void SaveKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        Service.SetKey(tool, key);
        if (tool.Access != ToolAccess.Api) tool.Access = ToolAccess.Api;
        Changed(nameof(KeyText), nameof(HasKey), nameof(IsApi), nameof(IsBrowser));
        main.State.StatusText = $"Saved the {tool.Name} API key.";
    }

    [RelayCommand]
    private void RemoveKey()
    {
        Service.SetKey(tool, null);
        Changed(nameof(KeyText), nameof(HasKey));
        main.State.StatusText = $"Removed the {tool.Name} API key.";
    }

    public string SignIn
    {
        get => tool.SignIn ?? "";
        set
        {
            var text = Clip(value);
            if (text == tool.SignIn) return;
            tool.SignIn = text;
            Changed(nameof(SignIn));
        }
    }

    public string Budget
    {
        get => tool.Budget ?? "";
        set
        {
            var text = Clip(value);
            if (text == tool.Budget) return;
            tool.Budget = text;
            Changed(nameof(Budget));
        }
    }

    private static string? Clip(string? text)
    {
        text = text?.Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length > ToolData.MaxText ? text[..ToolData.MaxText] : text;
    }

    /// <summary>Your projects, ticked for the ones the tool is set up for.</summary>
    public IReadOnlyList<ToolProjectChoice> Projects =>
        main.State.Projects.Where(p => p.Remote == null && !p.IsInbox).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new ToolProjectChoice(tool, p, Service)).ToList();

    public IReadOnlyList<ToolAllowedAgent> Allowed => tool.AlwaysAllowed.Select(a => new ToolAllowedAgent(a, this)).ToList();
    public bool HasAllowed => tool.AlwaysAllowed.Count > 0;
    public string AllowedHint => HasAllowed
        ? "These agents get the key whenever they ask, without asking you:"
        : $"Agents ask you for the key each time (Allow once), unless you pick “Always” for one when it asks.";

    internal void Disallow(string agent)
    {
        Service.Disallow(tool, agent);
        OnPropertyChanged(nameof(Allowed));
        OnPropertyChanged(nameof(HasAllowed));
        OnPropertyChanged(nameof(AllowedHint));
    }
}

/// <summary>A project in a tool's list: ticked when the tool is set up for it.</summary>
public sealed class ToolProjectChoice(ExternalTool tool, Project project, ToolService service) : ObservableObject
{
    public string Name => project.Name;

    public bool IsChecked
    {
        get => tool.Projects.Contains(project.Id);
        set
        {
            if (value == IsChecked) return;
            if (value) tool.Projects.Add(project.Id);
            else tool.Projects.Remove(project.Id);
            service.Save();
            OnPropertyChanged();
        }
    }
}

/// <summary>An agent that may always have a tool's key, with a way to take that back.</summary>
public sealed partial class ToolAllowedAgent(string agent, ToolEditorViewModel owner)
{
    public string Name => agent;
    [RelayCommand] private void Remove() => owner.Disallow(agent);
}
