using System.Windows;
using CommunityToolkit.Mvvm.Input;
using ProJict.Core.Agents;

namespace ProJict.App.ViewModels;

/// <summary>
/// An agent's usage in the Agents view: a ring by its name (on its card and in All agents) that fills as its usage is
/// used up, from what it reported with report_usage or what you set (right-click › Usage…).
/// </summary>
public sealed partial class AgentActivityViewModel
{
    internal AgentUsage? UsageOf(string agent) => _state.Settings.UsageOf(agent);

    /// <summary>Asks how much an agent has used ("62%", "$14 of $50"); tests answer for you. Null when cancelled.</summary>
    public Func<string, string?> AskUsageText { get; set; } = prompt =>
        Views.InputDialog.Ask(Application.Current.MainWindow!, "Usage", prompt, "");

    /// <summary>You set (or clear) how much an agent has used.</summary>
    internal void AskUsage(string agent)
    {
        if (AskUsageText($"How much of {agent}'s usage is used? For example 62%, 62% of the weekly limit, or $14 of $50. Leave it empty to clear it.") is not { } text)
            return;
        if (text.Trim().Length == 0)
        {
            _state.Settings.SetUsage(agent, null);
            _state.StatusText = $"Cleared {agent}'s usage.";
        }
        else if (AgentUsage.Parse(text, _state.UserName, DateTime.Now) is { } usage)
        {
            _state.Settings.SetUsage(agent, usage);
            _state.StatusText = $"{agent}: {usage.Describe(DateTime.Now)}.";
        }
        else
        {
            _state.StatusText = $"“{text.Trim()}” isn't a share (62%) or money spent ($14 of $50).";
            return;
        }
        _state.MarkSettingsDirty();
        RefreshUsage();
    }

    /// <summary>An agent's usage changed: its ring follows, on its card and in All agents.</summary>
    public void RefreshUsage()
    {
        foreach (var card in Live) card.RefreshUsage();
        foreach (var row in AllAgents) row.RefreshUsage();
    }
}

/// <summary>What an agent's usage ring shows.</summary>
public sealed class UsageRingView(AgentUsage? usage)
{
    public bool HasUsage => usage?.Fraction != null;
    public double Fraction => usage?.Fraction ?? 0;
    /// <summary>"Low", "Half", "Most", "NearlyAll": the ring's colour.</summary>
    public string Level => usage?.Level.ToString() ?? "";
    /// <summary>Over a day old: shown faded.</summary>
    public bool IsStale => usage?.IsStale(DateTime.Now) == true;
    public string? ToolTip => usage is { } u ? u.Describe(DateTime.Now) + "\nRight-click › Usage… to change it." : null;
}

public sealed partial class AgentCardViewModel
{
    public UsageRingView Usage => new(_owner.UsageOf(Name));
    public void RefreshUsage() => OnPropertyChanged(nameof(Usage));
    [RelayCommand] private void SetUsage() => _owner.AskUsage(Name);
}

public sealed partial class AgentListRow
{
    public UsageRingView Usage => new(_owner.UsageOf(Name));
    public void RefreshUsage() => OnPropertyChanged(nameof(Usage));
    [RelayCommand] private void SetUsage() => _owner.AskUsage(Name);
}
