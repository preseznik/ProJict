using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ProJict.Core.Model;

namespace ProJict.App.ViewModels;

/// <summary>
/// The room's things in the drawn look, made with ComfyUI like the desks: the thermostat on a wall of each room, and in
/// the studios the bench where unhired agents wait (behind the lobby, with its sign) and the milestone board where the
/// room's picture has space for it. The classic room keeps its own drawings where they always were.
/// </summary>
public sealed partial class OfficeViewModel
{
    /// <summary>
    /// Where the thermostat hangs: by the classic room's door, or on a free bit of the drawn room's wall (its picture
    /// fills the room's height from the left, so these are the picture's places): under the office's clock, under the
    /// game studio's left speaker, between the film studio's green screen and its posters.
    /// </summary>
    public double ThermostatX => !IsDrawnLook ? 228 : !IsStudio ? 303 : IsFilm ? 467 : 195;
    public double ThermostatY => !IsDrawnLook ? 204 : !IsStudio ? 238 : IsFilm ? 210 : 203;
    /// <summary>The milestone board: over the game studio's storyboard wall, or on the film studio's free wall right of its posters.</summary>
    public double MilestoneX => IsDrawnLook && IsFilm ? 700 : 484;
    public double MilestoneY => IsDrawnLook && IsFilm ? 112 : 102;

    /// <summary>The drawn studio's bench: against the back wall, just behind where the lobby's agents stand, as wide as they are.</summary>
    public double BenchWidth => BenchBox.Width;
    public double BenchHeight => BenchWidth * BenchAspect;
    public double BenchX => BenchBox.X;
    public double BenchY => BenchBox.Bottom - BenchHeight;
    /// <summary>The LOBBY sign on the wall above it, clear of the names over the waiting agents.</summary>
    public double LobbySignX => BenchX + BenchWidth / 2 - 19;
    public double LobbySignY => BenchY - 36;

    private const double BenchAspect = 437.0 / 975;

    private (double X, double Bottom, double Width) BenchBox
    {
        get
        {
            var spots = Lobby;
            var back = spots.Min(s => s.Y);
            var row = spots.Where(s => s.Y == back).ToList();
            // A spot is where a robot's middle is; its feet are a little lower.
            var feet = back + 24 * ScaleAt(back);
            var scale = ScaleAt(feet);
            var left = row.Min(s => s.X) - 26 * scale;
            var right = row.Max(s => s.X) + 26 * scale;
            return (left, feet - 5 * scale, right - left);
        }
    }

    private void PlaceProps()
    {
        foreach (var name in new[] { nameof(ThermostatX), nameof(ThermostatY), nameof(MilestoneX), nameof(MilestoneY), nameof(BenchWidth),
                     nameof(BenchHeight), nameof(BenchX), nameof(BenchY), nameof(LobbySignX), nameof(LobbySignY) })
            OnPropertyChanged(name);
    }

    partial void OnIsDrawnLookChanged(bool oldValue, bool newValue) => PlaceProps();
    partial void OnIsFilmChanged(bool oldValue, bool newValue) => PlaceProps();
    partial void OnStudioNameChanged(string? oldValue, string? newValue) => PlaceProps();

    private static readonly Dictionary<string, ImageSource?> PropPictures = [];

    /// <summary>A prop's picture in Assets/Office/Props (null if it isn't built in).</summary>
    internal static ImageSource? PropPicture(string name)
    {
        if (PropPictures.TryGetValue(name, out var picture)) return picture;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri($"pack://application:,,,/ProJict;component/Assets/Office/Props/{name}.png");
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            picture = image;
        }
        catch (Exception)
        {
            picture = null;
        }
        PropPictures[name] = picture;
        return picture;
    }
}

public sealed partial class OfficeDeskViewModel
{
    /// <summary>
    /// In the drawn studios, the station's thing standing on its desk (a trophy for the lead, a clapperboard for the
    /// director…), instead of the classic room's board on the wall above it, which would float over the desks in front.
    /// </summary>
    public ImageSource? Prop => Station is { } station && PropNames.TryGetValue(station, out var name) ? OfficeViewModel.PropPicture(name) : null;

    private static readonly Dictionary<StudioStation, string> PropNames = new()
    {
        [StudioStation.Lead] = "lead", [StudioStation.Design] = "design", [StudioStation.Level] = "level", [StudioStation.Code] = "code",
        [StudioStation.Art] = "art", [StudioStation.Sound] = "sound", [StudioStation.QA] = "qa", [StudioStation.Production] = "production",
        [StudioStation.Director] = "director", [StudioStation.Writer] = "writer", [StudioStation.Capture] = "capture", [StudioStation.Editing] = "editing",
        [StudioStation.Mixing] = "mixing", [StudioStation.Graphics] = "graphics", [StudioStation.Publishing] = "publishing", [StudioStation.Producing] = "producing",
        [StudioStation.UI] = "ui", [StudioStation.Cinematics] = "cinematics",
    };

    partial void OnStationChanged(StudioStation? oldValue, StudioStation? newValue) => OnPropertyChanged(nameof(Prop));
}

/// <summary>
/// A station's name plate in the studio, on the front of its desk. The desks stand close (five a side), and the monitor
/// or thing on the next desk in front would cover the name of the one behind it: the plate is level with that desk, drawn
/// after it, so a robot sitting there still comes first.
/// </summary>
public sealed class OfficePlateViewModel : ObservableObject
{
    private readonly IReadOnlyList<OfficeDeskViewModel> _desks;

    public OfficePlateViewModel(OfficeDeskViewModel desk, IReadOnlyList<OfficeDeskViewModel> desks)
    {
        Desk = desk;
        _desks = desks;
        // The desks move when the look changes.
        foreach (var other in desks)
            other.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(OfficeDeskViewModel.ZIndex)) OnPropertyChanged(nameof(ZIndex));
            };
    }

    public OfficeDeskViewModel Desk { get; }
    public int ZIndex => _desks.Where(d => d.Left == Desk.Left && d.Y > Desk.Y).MinBy(d => d.Y)?.ZIndex ?? Desk.ZIndex;
}
