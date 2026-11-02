using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ProJict.Core.Model;

namespace ProJict.App.Views;

/// <summary>
/// Plays an audio or video file in place (Chat, discussions, items' files and linked media). The file is opened only
/// when played, one player plays at a time, and leaving the view closes the file again. What Windows can't play says
/// so and keeps the button to open it in its own app.
/// </summary>
public partial class InlinePlayer : UserControl
{
    public static readonly DependencyProperty PathProperty =
        DependencyProperty.Register(nameof(Path), typeof(string), typeof(InlinePlayer), new PropertyMetadata(null, (d, _) => ((InlinePlayer)d).Reset()));
    public static readonly DependencyProperty KindProperty =
        DependencyProperty.Register(nameof(Kind), typeof(MediaKind), typeof(InlinePlayer), new PropertyMetadata(MediaKind.Audio, (d, _) => ((InlinePlayer)d).Reset()));
    public static readonly DependencyProperty FileNameProperty =
        DependencyProperty.Register(nameof(FileName), typeof(string), typeof(InlinePlayer), new PropertyMetadata(null, (d, _) => ((InlinePlayer)d).Reset()));
    public static readonly DependencyProperty PosterProperty =
        DependencyProperty.Register(nameof(Poster), typeof(ImageSource), typeof(InlinePlayer), new PropertyMetadata(null, (d, _) => ((InlinePlayer)d).Reset()));
    public static readonly DependencyProperty OpenCommandProperty =
        DependencyProperty.Register(nameof(OpenCommand), typeof(ICommand), typeof(InlinePlayer));

    public string? Path { get => (string?)GetValue(PathProperty); set => SetValue(PathProperty, value); }
    public MediaKind Kind { get => (MediaKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public string? FileName { get => (string?)GetValue(FileNameProperty); set => SetValue(FileNameProperty, value); }
    /// <summary>A frame from the video, shown until it plays.</summary>
    public ImageSource? Poster { get => (ImageSource?)GetValue(PosterProperty); set => SetValue(PosterProperty, value); }
    /// <summary>Opens the file in its own app.</summary>
    public ICommand? OpenCommand { get => (ICommand?)GetValue(OpenCommandProperty); set => SetValue(OpenCommandProperty, value); }

    /// <summary>The one playing now: another one starting pauses it.</summary>
    private static InlinePlayer? _playing;
    /// <summary>The volume and mute you chose, for every player until ProJict closes.</summary>
    private static double _volume = 0.8;
    private static bool _muted;
    /// <summary>Players start muted (tests, so nothing is heard).</summary>
    public static bool StartMuted { get; set; }

    private readonly DispatcherTimer _clock;
    private bool _opened, _syncing;
    private bool IsVideo => Kind == MediaKind.Video;

    public InlinePlayer()
    {
        InitializeComponent();
        _clock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _clock.Tick += (_, _) => SyncPosition();
        _syncing = true;
        VolumeSlider.Value = _volume;
        _syncing = false;
        Unloaded += (_, _) => Close();
        Reset();
    }

    public bool IsPlaying { get; private set; }
    public TimeSpan Position => Media.Position;
    public TimeSpan? Duration => Media.NaturalDuration.HasTimeSpan ? Media.NaturalDuration.TimeSpan : null;
    /// <summary>Why it can't play here, or null.</summary>
    public string? Problem { get; private set; }

    public void Play()
    {
        if (Path is not { } path || !File.Exists(path))
        {
            Fail("The file isn't there any more.");
            return;
        }
        if (_playing != null && _playing != this) _playing.Pause();
        _playing = this;
        if (Media.Source == null)
        {
            Media.Volume = _volume;
            Media.IsMuted = _muted || StartMuted;
            Media.Source = new Uri(path);
        }
        Media.Play();
        IsPlaying = true;
        _clock.Start();
        Sync();
    }

    public void Pause()
    {
        if (!IsPlaying) return;
        Media.Pause();
        IsPlaying = false;
        _clock.Stop();
        Sync();
    }

    /// <summary>Lets go of the file (the view closed or scrolled it away): it starts again from the start next time.</summary>
    private void Close()
    {
        if (Media.Source == null) return;
        _clock.Stop();
        Media.Stop();
        Media.Close();
        Media.Source = null;
        _opened = false;
        IsPlaying = false;
        if (_playing == this) _playing = null;
        Sync();
    }

    /// <summary>Another file, kind or name: back to the start, nothing loaded.</summary>
    private void Reset()
    {
        if (Media == null) return;
        Close();
        Problem = null;
        ProblemText.Visibility = Visibility.Collapsed;
        Controls.Visibility = Visibility.Visible;
        Screen.Visibility = Visibility.Visible;
        Frame.Width = IsVideo ? 340 : 300;
        // Audio keeps the (empty) screen in the layout: a collapsed MediaElement doesn't play.
        Screen.Height = IsVideo ? double.NaN : 0;
        Screen.MinHeight = IsVideo ? 120 : 0;
        Screen.Margin = IsVideo ? new Thickness(-2, -1, -2, 6) : new Thickness(0);
        Screen.Background = IsVideo ? Brushes.Black : Brushes.Transparent;
        Screen.IsHitTestVisible = IsVideo;
        PosterImage.Source = Poster;
        NameText.Text = FileName ?? System.IO.Path.GetFileName(Path ?? "");
        NameText.ToolTip = Path;
        Sync();
    }

    private void Sync()
    {
        PlayButton.Content = IsPlaying ? "" : "";
        PlayButton.ToolTip = IsPlaying ? "Pause" : "Play";
        System.Windows.Automation.AutomationProperties.SetName(PlayButton, IsPlaying ? "Pause" : "Play");
        BigPlay.Visibility = IsVideo && !IsPlaying ? Visibility.Visible : Visibility.Collapsed;
        PosterImage.Visibility = _opened ? Visibility.Collapsed : Visibility.Visible;
        var muted = Media.Source != null ? Media.IsMuted : _muted;
        MuteButton.Content = muted ? "" : "";
        MuteButton.ToolTip = muted ? "Unmute" : "Mute";
        SeekSlider.IsEnabled = _opened;
        SyncPosition();
    }

    private void SyncPosition()
    {
        _syncing = true;
        if (Duration is { } length)
        {
            SeekSlider.Maximum = Math.Max(0.1, length.TotalSeconds);
            SeekSlider.Value = Math.Min(SeekSlider.Maximum, Media.Position.TotalSeconds);
            TimeText.Text = $"{Time(Media.Position)} / {Time(length)}";
        }
        else
        {
            SeekSlider.Value = 0;
            TimeText.Text = "";
        }
        _syncing = false;
    }

    private static string Time(TimeSpan t) => t.TotalHours >= 1
        ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
        : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    private void Fail(string problem)
    {
        Close();
        Problem = problem;
        ProblemText.Text = problem;
        ProblemText.Visibility = Visibility.Visible;
        // No picture to show: the name, the reason and the button to open it are what's left.
        Screen.Visibility = Visibility.Collapsed;
    }

    private void Media_MediaOpened(object sender, RoutedEventArgs e)
    {
        _opened = true;
        Sync();
    }

    private void Media_MediaEnded(object sender, RoutedEventArgs e)
    {
        Media.Stop();
        IsPlaying = false;
        _clock.Stop();
        Sync();
    }

    private void Media_MediaFailed(object? sender, ExceptionRoutedEventArgs e) =>
        Fail("Windows can't play this file here: open it in its own app with the button above.");

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsPlaying) Pause();
        else Play();
    }

    private void Screen_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        PlayButton_Click(sender, e);
        e.Handled = true;
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        Pause();
        if (OpenCommand is { } open && open.CanExecute(null)) open.Execute(null);
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        _muted = !(Media.Source != null ? Media.IsMuted : _muted);
        Media.IsMuted = _muted;
        Sync();
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing) return;
        _volume = e.NewValue;
        Media.Volume = _volume;
    }

    private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing || !_opened) return;
        Media.Position = TimeSpan.FromSeconds(e.NewValue);
        SyncPosition();
    }
}

/// <summary>A file in a list: audio and video play in place (Player); other files show as their chip (Chip).</summary>
public sealed class PlayableTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Chip { get; set; }
    public DataTemplate? Player { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is ViewModels.IPlayable { Plays: true } ? Player : Chip;
}
