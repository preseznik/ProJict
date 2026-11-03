using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProJict.Core.Agents;

namespace ProJict.App.ViewModels;

/// <summary>How warm you set a room (the thermostat on its wall). Only for fun: the agents never hear of it.</summary>
public enum OfficeTemperature { Comfy, Hot, Cold }

/// <summary>
/// Fun in the office that never reaches the agents: poke a robot (Shift+click, or right-click › Poke) and it hops and
/// giggles, grumbles when it's busy, mumbles when it was asleep and snaps after a few pokes in a row; turn the thermostat
/// and the robots sweat or shiver. Each room has its own: the office, and each project's studios.
/// </summary>
public sealed partial class OfficeViewModel
{
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ThermostatText), nameof(ThermostatToolTip), nameof(ThermostatDial), nameof(RoomTint))]
    private OfficeTemperature _temperature;

    public string ThermostatText => Temperature switch { OfficeTemperature.Hot => "29°", OfficeTemperature.Cold => "14°", _ => "21°" };
    public string ThermostatToolTip => Temperature switch
    {
        OfficeTemperature.Hot => "Thermostat: 29 °C, they're sweating. Click to cool it right down",
        OfficeTemperature.Cold => "Thermostat: 14 °C, they're shivering. Click to set it back to comfy",
        _ => "Thermostat: a comfy 21 °C. Click to turn it up (just for fun: the agents don't know)",
    };
    public Brush ThermostatDial => Temperature switch
    {
        OfficeTemperature.Hot => Brushes.OrangeRed,
        OfficeTemperature.Cold => Brushes.DeepSkyBlue,
        _ => Brushes.MediumSeaGreen,
    };
    /// <summary>A warm or cold tint over the room.</summary>
    public Brush RoomTint => Temperature switch
    {
        OfficeTemperature.Hot => Tint(0x18, 0xF9, 0x73, 0x16),
        OfficeTemperature.Cold => Tint(0x1C, 0x60, 0xA5, 0xFA),
        _ => Brushes.Transparent,
    };

    private static Brush Tint(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>Each room's thermostat, while ProJict runs: the office's, and each studio's (by the studio).</summary>
    private readonly Dictionary<object, OfficeTemperature> _temperatureOf = [];
    private static readonly object OfficeRoom = new();
    private object _room = OfficeRoom;

    /// <summary>The room shown is now the office or a studio: its thermostat shows, set as you left it there.</summary>
    private void EnterRoom(object room)
    {
        if (room == _room) return;
        _room = room;
        Temperature = _temperatureOf.GetValueOrDefault(room);
    }

    [RelayCommand]
    private void TurnThermostat()
    {
        Temperature = Temperature switch
        {
            OfficeTemperature.Comfy => OfficeTemperature.Hot,
            OfficeTemperature.Hot => OfficeTemperature.Cold,
            _ => OfficeTemperature.Comfy,
        };
        _temperatureOf[_room] = Temperature;
    }

    partial void OnTemperatureChanged(OfficeTemperature value)
    {
        foreach (var agent in Agents) agent.Feel(value);
    }
}

public sealed partial class OfficeAgentViewModel
{
    /// <summary>What it says when poked, for a moment; null otherwise.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasReaction))] private string? _reaction;
    public bool HasReaction => Reaction != null;
    /// <summary>The thermostat's turned up: sweat on its head.</summary>
    [ObservableProperty] private bool _sweating;
    /// <summary>Turned down: it shivers.</summary>
    [ObservableProperty] private bool _shivering;

    /// <summary>Poked: the view makes it hop (a big hop when it giggles, a small shake when it's cross).</summary>
    public event Action<bool>? Poked;

    /// <summary>How long a reaction shows, and how close together pokes make it cross.</summary>
    private static readonly TimeSpan ReactionFor = TimeSpan.FromSeconds(2.5), PokesWithin = TimeSpan.FromSeconds(6);
    private const int PokesToSnap = 3;
    private readonly List<DateTime> _pokes = [];
    private DispatcherTimer? _reactionTimer;
    private int _turn;

    private static readonly string[] Giggles = ["Hehe!", "That tickles!", "Boop!", "Hi there!", "Hee hee"];
    private static readonly string[] Grumbles = ["Hey, I'm working!", "Hmph.", "Not now…", "I was in the middle of something"];

    internal void Feel(OfficeTemperature temperature)
    {
        Sweating = temperature == OfficeTemperature.Hot;
        Shivering = temperature == OfficeTemperature.Cold;
    }

    /// <summary>Shift+click, or right-click › Poke: it reacts by what it's doing (nothing reaches the agent).</summary>
    [RelayCommand]
    public void Poke() => Poke(DateTime.Now);

    internal void Poke(DateTime now)
    {
        _pokes.RemoveAll(p => now - p > PokesWithin);
        _pokes.Add(now);
        var cross = _pokes.Count >= PokesToSnap || Activity is OfficeActivity.Building or OfficeActivity.Managing or OfficeActivity.Reading;
        Reaction = _pokes.Count >= PokesToSnap ? "OK, OK, stop poking!"
            : Activity == OfficeActivity.Resting ? "Zzz… huh? Oh, it's you."
            : cross ? Grumbles[_turn++ % Grumbles.Length]
            : Giggles[_turn++ % Giggles.Length];
        Poked?.Invoke(!cross);
        _reactionTimer ??= new DispatcherTimer(ReactionFor, DispatcherPriority.Background, (_, _) =>
        {
            _reactionTimer!.Stop();
            Reaction = null;
        }, Dispatcher.CurrentDispatcher);
        _reactionTimer.Stop();
        _reactionTimer.Start();
    }
}
