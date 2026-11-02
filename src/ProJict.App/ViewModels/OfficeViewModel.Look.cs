using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ProJict.App.ViewModels;

/// <summary>
/// The office's look: Classic (today's, drawn by ProJict) or Drawn (a picture of the room, made with ComfyUI, behind the
/// desks and robots). The picture is the office's, the game studio's or the film studio's, light or dark with ProJict's
/// theme (the dark one is the same room at night, so nothing moves when the theme changes).
/// </summary>
public sealed partial class OfficeViewModel
{
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(RoomPicture), nameof(ShowsClassicRoom), nameof(IsDarkRoom))] private bool _isDrawnLook;
    /// <summary>Today's room, drawn in shapes: walls, window, clock, boards, plant.</summary>
    public bool ShowsClassicRoom => !IsDrawnLook;

    private bool? _dark;
    public ImageSource? RoomPicture => IsDrawnLook ? PictureOf(RoomName, _dark ??= Views.MermaidDiagram.IsDark()) : null;
    /// <summary>"office", "game-studio" or "film-studio": the room's pictures in Assets/Office.</summary>
    public string RoomName => !IsStudio ? "office" : IsFilm ? "film-studio" : "game-studio";
    /// <summary>The drawn room at night (the dark theme): the furniture's pictures get the same night tint as the room.</summary>
    public bool IsDarkRoom => IsDrawnLook && (_dark ??= Views.MermaidDiagram.IsDark());

    partial void OnStudioNameChanged(string? value)
    {
        OnPropertyChanged(nameof(RoomPicture));
        ArrangeDesks();
    }

    partial void OnIsFilmChanged(bool value) => OnPropertyChanged(nameof(RoomPicture));
    partial void OnIsDrawnLookChanged(bool value) => ArrangeDesks();

    /// <summary>
    /// The drawn office has a wide, shallow floor (the classic room's back desks would stand on its back wall): its desks
    /// stand in two rows on either side of the cooler, the back row's robots between the front row's, so none hides
    /// another. Its seat (where the robot sits) and how far down the floor (y) for the left side's desks, back to front
    /// as <see cref="DeskDepths"/>; the right side's are the same, mirrored.
    /// </summary>
    private static readonly (double SeatX, double Y)[] DrawnOfficeDesks = [(305, 400), (185, 405), (262, 466), (115, 468)];

    /// <summary>Where talkers stand around the cooler in the drawn office: close in, between the desks on either side.</summary>
    private static readonly (double X, double Y)[] DrawnOfficeCoolerSpots =
        [.. new (double X, double Y)[] { (-62, 6), (62, 6), (-38, 30), (38, 30), (-92, 26), (92, 26), (-30, -26), (30, -26) }
            .Select(o => StandingAt(CoolerX + o.X, CoolerY + o.Y))];

    /// <summary>Where talkers stand around the cooler in this room.</summary>
    private (double X, double Y)[] TalkSpots => IsDrawnLook && !IsStudio ? DrawnOfficeCoolerSpots : CoolerSpots;

    /// <summary>The studio's lobby in the drawn studios: a step forward, onto their floor (the bench it stands by is the classic room's).</summary>
    private static readonly (double X, double Y)[] DrawnLobbySpots =
        [.. Enumerable.Range(0, 5).Select(i => StandingAt(344 + 28 * i, 350)), .. Enumerable.Range(0, 4).Select(i => StandingAt(358 + 28 * i, 372))];

    /// <summary>Where agents not hired yet wait in this studio.</summary>
    private (double X, double Y)[] Lobby => IsDrawnLook ? DrawnLobbySpots : LobbySpots;

    /// <summary>Stands the desks where this room has them: against the walls, or in the drawn office out on its floor.</summary>
    private void ArrangeDesks()
    {
        if (Desks == null) return;
        var perSide = DeskDepths.Length;
        for (var i = 0; i < Desks.Count; i++)
        {
            var desk = Desks[i];
            if (!IsDrawnLook || IsStudio)
            {
                desk.StandAt(WallPoint(desk.Left, desk.Depth));
                continue;
            }
            var (seatX, y) = DrawnOfficeDesks[i % perSide];
            var scale = ScaleAt(y);
            var x = seatX - 89 * scale;
            desk.StandAt((desk.Left ? x : Width - x, y));
        }
    }

    /// <summary>The theme changed (checked as the office updates): the room follows it.</summary>
    private void SyncTheme()
    {
        var dark = Views.MermaidDiagram.IsDark();
        if (dark == _dark) return;
        _dark = dark;
        if (!IsDrawnLook) return;
        OnPropertyChanged(nameof(RoomPicture));
        OnPropertyChanged(nameof(IsDarkRoom));
    }

    private static readonly Dictionary<string, ImageSource?> Pictures = [];

    private static ImageSource? PictureOf(string room, bool dark)
    {
        var name = $"{room}-{(dark ? "dark" : "light")}";
        if (Pictures.TryGetValue(name, out var picture)) return picture;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri($"pack://application:,,,/ProJict;component/Assets/Office/{name}.jpg");
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            picture = image;
        }
        catch (Exception)
        {
            picture = null; // not built in: the classic room's floor shows through
        }
        Pictures[name] = picture;
        return picture;
    }
}
