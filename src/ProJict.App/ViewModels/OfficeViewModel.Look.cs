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
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(RoomPicture), nameof(ShowsClassicRoom))] private bool _isDrawnLook;
    /// <summary>Today's room, drawn in shapes: walls, window, clock, boards, plant.</summary>
    public bool ShowsClassicRoom => !IsDrawnLook;

    private bool? _dark;
    public ImageSource? RoomPicture => IsDrawnLook ? PictureOf(RoomName, _dark ??= Views.MermaidDiagram.IsDark()) : null;
    /// <summary>"office", "game-studio" or "film-studio": the room's pictures in Assets/Office.</summary>
    public string RoomName => !IsStudio ? "office" : IsFilm ? "film-studio" : "game-studio";

    partial void OnStudioNameChanged(string? value) => OnPropertyChanged(nameof(RoomPicture));
    partial void OnIsFilmChanged(bool value) => OnPropertyChanged(nameof(RoomPicture));

    /// <summary>The theme changed (checked as the office updates): the room follows it.</summary>
    private void SyncTheme()
    {
        var dark = Views.MermaidDiagram.IsDark();
        if (dark == _dark) return;
        _dark = dark;
        if (IsDrawnLook) OnPropertyChanged(nameof(RoomPicture));
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
