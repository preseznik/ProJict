using System.Globalization;
using System.Windows;

namespace ProJict.App.Views;

/// <summary>What the reservation dialog asks for: the machine, from when, until when (null: until you free it), and what for.</summary>
public sealed record ReservationRequest(string Machine, DateTime From, DateTime? To, string? For);

/// <summary>
/// A reservation of a shared machine (#d27b8c7b), like a meeting in Teams or Outlook: which machine, starts and ends
/// (date and time), all day, and what for. New, a booked slot to edit (or cancel), or what you hold now (its end: a time,
/// or until you free it).
/// </summary>
public partial class ReservationDialog : Window
{
    public enum Kind { New, Booking, Hold }

    private readonly Kind _kind;
    private readonly Func<ReservationRequest, string?> _save;
    private readonly Action? _remove;
    private DateTime? _lastStart;

    /// <param name="save">Does it; returns why it couldn't, or null.</param>
    /// <param name="remove">Cancels the booking (Booking only).</param>
    public ReservationDialog(Kind kind, IEnumerable<string> machines, string? machine, DateTime from, DateTime? to, string? purpose,
        Func<ReservationRequest, string?> save, Action? remove = null)
    {
        InitializeComponent();
        _kind = kind;
        _save = save;
        _remove = remove;
        foreach (var m in machines) MachineBox.Items.Add(m);
        MachineBox.Text = machine ?? (MachineBox.Items.Count > 0 ? (string)MachineBox.Items[0] : "");

        var culture = CultureInfo.CurrentCulture;
        for (var t = TimeSpan.Zero; t < TimeSpan.FromDays(1); t += TimeSpan.FromMinutes(30))
        {
            var label = DateTime.Today.Add(t).ToString("t", culture);
            StartTime.Items.Add(label);
            EndTime.Items.Add(label);
        }
        StartDate.SelectedDate = from.Date;
        StartTime.Text = from.ToString("t", culture);
        var end = to ?? from.AddHours(1);
        EndDate.SelectedDate = end.Date;
        EndTime.Text = end.ToString("t", culture);
        Purpose.Text = purpose ?? "";
        _lastStart = from.Date;

        switch (kind)
        {
            case Kind.New:
                Heading.Text = Title = "New reservation";
                break;
            case Kind.Booking:
                Heading.Text = Title = "Edit reservation";
                RemoveButton.Visibility = Visibility.Visible;
                break;
            case Kind.Hold:
                Heading.Text = Title = "Your reservation";
                MachineBox.IsEnabled = StartDate.IsEnabled = StartTime.IsEnabled = false;
                AllDay.Visibility = Visibility.Collapsed;
                UntilFreed.Visibility = Visibility.Visible;
                UntilFreed.IsChecked = to == null;
                Hint.Text = "It's yours now: change when it ends, or keep it until you free it.";
                UntilFreed_Click(this, new RoutedEventArgs());
                break;
        }
        // A whole day (or days) from midnight to midnight shows as all day.
        if (kind != Kind.Hold && to is { } t2 && from == from.Date && t2 == t2.Date && t2 > from)
        {
            AllDay.IsChecked = true;
            EndDate.SelectedDate = t2.AddDays(-1);
            AllDay_Click(this, new RoutedEventArgs());
        }
    }

    private void AllDay_Click(object sender, RoutedEventArgs e)
    {
        var allDay = AllDay.IsChecked == true;
        StartTime.IsEnabled = EndTime.IsEnabled = !allDay;
        if (allDay && EndDate.SelectedDate < StartDate.SelectedDate) EndDate.SelectedDate = StartDate.SelectedDate;
    }

    private void UntilFreed_Click(object sender, RoutedEventArgs e) =>
        EndDate.IsEnabled = EndTime.IsEnabled = UntilFreed.IsChecked != true;

    /// <summary>Moving the start moves the end along, as Outlook does.</summary>
    private void StartDate_SelectedDateChanged(object? sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (StartDate.SelectedDate is not { } start || _lastStart is not { } before || EndDate.SelectedDate is not { } end) return;
        EndDate.SelectedDate = end.AddDays((start - before).TotalDays);
        _lastStart = start;
    }

    /// <summary>"14:30", "14.30", "1430", "2:30 PM".</summary>
    private static TimeSpan? ParseTime(string text)
    {
        var t = text.Trim().Replace('.', ':');
        if (t.Length is 3 or 4 && t.All(char.IsDigit)) t = t[..^2] + ":" + t[^2..];
        if (TimeSpan.TryParse(t, CultureInfo.InvariantCulture, out var span) && span >= TimeSpan.Zero && span < TimeSpan.FromDays(1)) return span;
        return DateTime.TryParse(t, CultureInfo.CurrentCulture, DateTimeStyles.NoCurrentDateDefault, out var at) ? at.TimeOfDay : null;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var machine = MachineBox.Text.Trim();
        if (machine.Length == 0)
        {
            Show("Pick the machine, or type its name.");
            return;
        }
        if (StartDate.SelectedDate is not { } startDay)
        {
            Show("Pick the day it starts.");
            return;
        }
        var allDay = AllDay.IsChecked == true;
        DateTime from;
        if (allDay) from = startDay;
        else if (ParseTime(StartTime.Text) is { } st) from = startDay + st;
        else
        {
            Show("Write the start time like 14:30.");
            return;
        }
        DateTime? to = null;
        if (!(_kind == Kind.Hold && UntilFreed.IsChecked == true))
        {
            if (EndDate.SelectedDate is not { } endDay)
            {
                Show("Pick the day it ends.");
                return;
            }
            if (allDay) to = endDay.AddDays(1);
            else if (ParseTime(EndTime.Text) is { } et) to = endDay + et;
            else
            {
                Show("Write the end time like 16:00.");
                return;
            }
            if (to <= from && _kind != Kind.Hold)
            {
                Show("It ends after it starts.");
                return;
            }
        }
        if (_save(new ReservationRequest(machine, from, to, string.IsNullOrWhiteSpace(Purpose.Text) ? null : Purpose.Text.Trim())) is { } problem)
        {
            Show(problem);
            return;
        }
        DialogResult = true;
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        _remove?.Invoke();
        DialogResult = true;
    }

    private void Show(string problem)
    {
        Problem.Text = problem;
        Problem.Visibility = Visibility.Visible;
    }
}
