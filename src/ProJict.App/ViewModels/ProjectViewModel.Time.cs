using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using ProJict.Core.Features;
using ProJict.Core.Model;

namespace ProJict.App.ViewModels;

/// <summary>
/// The project's Time tab: how long was spent on it (items in progress, and time logged by hand), by people and agents,
/// week by week, per person and agent, and the items that took longest; with the tokens and cost agents reported.
/// Worked out when the tab is shown.
/// </summary>
public sealed partial class ProjectViewModel
{
    private ProjectTime? _time;

    /// <summary>The Time tab is the one showing: it's worked out again.</summary>
    public bool IsTimeShown
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            if (value) RefreshTime();
        }
    }

    private ProjectTime Time => _time ??= WorkLog.Of(Project, DateTime.Now);

    public bool HasTime => Time.Total > TimeSpan.Zero || Time.Tokens > 0;

    public IReadOnlyList<StatTile> TimeTiles => new List<StatTile>
    {
        new(WorkLog.Format(Time.Total), "in all"),
        new(WorkLog.Format(Time.ThisWeek), "this week"),
        new(Time.Tokens > 0 ? WorkLog.FormatTokens(Time.Tokens) : "–", "tokens", "As agents reported them (complete_item or log_work)"),
        new(Time.Cost > 0 ? WorkLog.FormatCost(Time.Cost) : "–", "cost", "As agents reported it"),
    };

    /// <summary>The last twelve weeks, people and agents stacked.</summary>
    public IReadOnlyList<ChartBar> TimeBars
    {
        get
        {
            var max = Math.Max(1, Time.Weeks.Max(w => (w.People + w.Agents).TotalMinutes));
            GridLength Star(double minutes) => new(Math.Max(0, minutes), GridUnitType.Star);
            return Time.Weeks.Select(w => new ChartBar(Star(w.People.TotalMinutes), Star(w.Agents.TotalMinutes), Star(max - (w.People + w.Agents).TotalMinutes),
                w.Start.ToString("d MMM", CultureInfo.CurrentCulture),
                $"Week of {w.Start.ToString("d MMMM", CultureInfo.CurrentCulture)}: people {WorkLog.Format(w.People)}, AI agents {WorkLog.Format(w.Agents)}")).ToList();
        }
    }

    public IReadOnlyList<TimeWorkerRow> TimeWorkers => Time.Workers.Select(w => new TimeWorkerRow(w)).ToList();

    public IReadOnlyList<TimeItemRow> TimeLongest => Time.Longest.Select(i => new TimeItemRow(i, this)).ToList();

    public string TimeHint =>
        "Time counts while an item is in progress, for whoever put it there (an agent's check-out too), plus time logged by hand (right-click an " +
        "item › Log time…). Agents report tokens and cost when they finish an item.";

    public void RefreshTime()
    {
        _time = null;
        foreach (var name in new[] { nameof(HasTime), nameof(TimeTiles), nameof(TimeBars), nameof(TimeWorkers), nameof(TimeLongest) }) OnPropertyChanged(name);
    }

    /// <summary>Shows an item from the Time tab on the board.</summary>
    internal void ShowOnBoard(TodoItem item)
    {
        if (Project.Find(item.Id) == null) return;
        SelectedTab = 0;
        Reveal(item);
    }
}

/// <summary>A person or agent in the Time tab: items finished, time, tokens and cost.</summary>
public sealed class TimeWorkerRow(WorkerTime worker)
{
    public string Name => worker.Name;
    public string Kind => worker.Agent ? "AI agent" : "person";
    public int ItemsDone => worker.ItemsDone;
    public string Time => WorkLog.Format(worker.Time);
    public string Tokens => worker.Tokens > 0 ? WorkLog.FormatTokens(worker.Tokens) : "–";
    public string Cost => worker.Cost > 0 ? WorkLog.FormatCost(worker.Cost) : "–";
}

/// <summary>An item that took long, in the Time tab; a click shows it on the board.</summary>
public sealed partial class TimeItemRow(ItemTime item, ProjectViewModel owner)
{
    public string Text => item.Item.DisplayText().Replace("\r\n", " ").Replace('\n', ' ');
    public string Category => item.Category;
    public string Time => WorkLog.Format(item.Time);
    public string Usage => string.Join(" · ", new[]
    {
        item.Tokens > 0 ? WorkLog.FormatTokens(item.Tokens) + " tokens" : null,
        item.Cost > 0 ? WorkLog.FormatCost(item.Cost) : null,
    }.Where(x => x != null));
    public bool IsDone => item.Item.IsDone;

    [RelayCommand] private void Show() => owner.ShowOnBoard(item.Item);
}
