using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

using ZeroStrap.Models.Theming;
using ZeroStrap.UI.Theming;
using ZeroStrap.UI.Utility;

namespace ZeroStrap.UI.Elements.Controls
{
    /// <summary>
    /// The shell's image or video backdrop. Follows the active theme (rebuilding only when its background changed),
    /// pauses while the window is minimized or hidden, shows only a still with reduced motion, and releases the media
    /// whenever it is switched away. Falls back to the plain theme background (and posts a one-time Theme Studio
    /// notice) when the media is missing or cannot be played.
    /// </summary>
    public partial class BackgroundHost : UserControl
    {
        public static readonly DependencyProperty IsActiveProperty =
            DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(BackgroundHost), new PropertyMetadata(false));

        /// <summary>True while a background is shown, so the content area can let it through.</summary>
        public bool IsActive
        {
            get => (bool)GetValue(IsActiveProperty);
            private set => SetValue(IsActiveProperty, value);
        }

        private const int MaxImageDecodeWidth = 2560;

        private ThemeBackground? _background;
        private string? _scrimColour;
        private string _themeId = "";
        private string? _mediaPath;
        private bool _failed;

        private MediaElement? _video;
        private bool _playing;
        private bool _loggedLoop;
        private string _playbackReason = "";

        private Window? _window;
        private bool _subscribed;
        private int _generation;

        public BackgroundHost()
        {
            InitializeComponent();

            Loaded += (_, _) => Attach();
            Unloaded += (_, _) => Detach();
            SizeChanged += (_, _) => UpdateBox();
            IsVisibleChanged += (_, _) => UpdatePlayback();
        }

        /// <summary>True while a video element exists (for diagnostics and tests).</summary>
        public bool HasVideo => _video is not null;

        public bool IsPlaying => _playing;

        // ---------------------------------------------------------------- lifecycle

        private void Attach()
        {
            if (_subscribed)
                return;

            _subscribed = true;

            ThemeManager.ThemeChanged += ThemeManager_ThemeChanged;
            Motion.Changed += Motion_Changed;
            SystemParameters.StaticPropertyChanged += SystemParameters_StaticPropertyChanged;

            _window = Window.GetWindow(this);
            if (_window is not null)
            {
                _window.StateChanged += Window_StateChanged;
                _window.IsVisibleChanged += Window_IsVisibleChanged;
            }

            // Fresh start: nothing is built yet.
            _background = null;
            _scrimColour = null;
            Apply(ThemeManager.ActiveDefinition);
        }

        private void Detach()
        {
            if (!_subscribed)
                return;

            _subscribed = false;

            ThemeManager.ThemeChanged -= ThemeManager_ThemeChanged;
            Motion.Changed -= Motion_Changed;
            SystemParameters.StaticPropertyChanged -= SystemParameters_StaticPropertyChanged;

            if (_window is not null)
            {
                _window.StateChanged -= Window_StateChanged;
                _window.IsVisibleChanged -= Window_IsVisibleChanged;
                _window = null;
            }

            Teardown();
            _background = null;
            _scrimColour = null;
        }

        private void ThemeManager_ThemeChanged(ThemeDefinition definition)
        {
            if (Dispatcher.CheckAccess())
                Apply(definition);
            else
                Dispatcher.BeginInvoke(new Action(() => Apply(definition)));
        }

        private void Motion_Changed() => OnMotionChanged();

        private void SystemParameters_StaticPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation))
                Dispatcher.BeginInvoke(new Action(OnMotionChanged));
        }

        private void Window_StateChanged(object? sender, EventArgs e) => UpdatePlayback();

        private void Window_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdatePlayback();

        // ---------------------------------------------------------------- applying a theme

        private static ThemeDefinition Effective(ThemeDefinition definition) =>
            ThemeGuard.IsUnreadable(definition) ? BuiltinThemes.Obsidian() : definition;

        /// <summary>Applies the theme's background: nothing, a parameter update, or a full rebuild, as needed.</summary>
        private void Apply(ThemeDefinition definition)
        {
            definition = Effective(definition);

            var next = ThemeMedia.HasBackground(definition.Background) ? definition.Background : null;
            string scrim = definition.Colors.Background;

            var change = ThemeMedia.Compare(_background, _scrimColour, next, scrim);

            _themeId = definition.Kind == ThemeKind.Custom ? definition.Id : "";

            if (change == BackgroundChange.None)
                return;

            _background = next is null ? null : Copy(next);
            _scrimColour = scrim;

            if (change == BackgroundChange.Parameters)
            {
                ApplyParameters();
                return;
            }

            Teardown();
            _failed = false;

            if (_background is null)
                return;

            Build(definition);
        }

        private static ThemeBackground Copy(ThemeBackground b) => new()
        {
            Type = b.Type,
            Media = b.Media,
            Opacity = b.Opacity,
            Blur = b.Blur,
            Speed = b.Speed
        };

        private void Build(ThemeDefinition definition)
        {
            var background = _background!;
            int generation = ++_generation;

            string? path = ThemeManager.Store.GetMediaPath(definition);
            if (path is null)
            {
                Fail(background.Type, $"Background media \"{background.Media}\" was not found or is not allowed.");
                return;
            }

            _mediaPath = path;
            ApplyParameters();

            if (background.Type == ThemeBackgroundType.Image)
            {
                _ = LoadStillAsync(path, generation, isPoster: false);
                return;
            }

            // Video: the poster paints first (and is the whole picture with reduced motion).
            string poster = Path.Combine(Path.GetDirectoryName(path)!, ThemeMedia.PosterName(Path.GetFileName(path)));
            bool hasPoster = File.Exists(poster);

            if (hasPoster)
                _ = LoadStillAsync(poster, generation, isPoster: true);
            else
                _ = CreatePosterLazilyAsync(path, poster, generation);

            // With reduced motion and a poster there is nothing to play, so no video is opened at all.
            if (Motion.Enabled || !hasPoster)
                EnsureVideo(path);

            ShowActive();
        }

        /// <summary>Themes imported from a file arrive without a poster; make one in the background.</summary>
        private async Task CreatePosterLazilyAsync(string videoPath, string posterPath, int generation)
        {
            try
            {
                // let the first frames of the app settle before decoding a second time
                await Task.Delay(1500);
                if (generation != _generation)
                    return;

                if (await MediaProbe.CapturePosterAsync(videoPath, posterPath, msg => Log(msg)) && generation == _generation)
                    await LoadStillAsync(posterPath, generation, isPoster: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Log("Poster creation failed: " + ex.Message);
            }
        }

        private async Task LoadStillAsync(string path, int generation, bool isPoster)
        {
            BitmapSource? bitmap;
            try
            {
                bitmap = await Task.Run(() => LoadFrozen(path));
            }
            catch (Exception ex) when (ex is NotSupportedException or IOException or InvalidOperationException or UnauthorizedAccessException or FileFormatException or ArgumentException)
            {
                bitmap = null;
                Log($"Could not load {Path.GetFileName(path)}: {ex.Message}");
            }

            if (generation != _generation || _background is null)
                return;

            if (bitmap is null)
            {
                // A poster that will not load is not fatal; an image that will not load is.
                if (!isPoster)
                    Fail(ThemeBackgroundType.Image, "The background image could not be decoded.");

                return;
            }

            Still.Source = bitmap;
            Still.Visibility = Visibility.Visible;
            ShowActive();
        }

        private static BitmapSource LoadFrozen(string path)
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.CacheOption = BitmapCacheOption.OnLoad; // do not keep the file open (it may be deleted later)
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = MaxImageDecodeWidth;
            image.EndInit();
            image.Freeze();
            return image;
        }

        private void ShowActive()
        {
            IsActive = true;
            Visibility = Visibility.Visible;
            UpdateBox();
        }

        // ---------------------------------------------------------------- video

        private void EnsureVideo(string path)
        {
            if (_video is not null)
                return;

            var video = new MediaElement
            {
                LoadedBehavior = MediaState.Manual,
                UnloadedBehavior = MediaState.Manual,
                IsMuted = true,
                Volume = 0,
                Stretch = Stretch.UniformToFill,
                ScrubbingEnabled = false,
                SpeedRatio = Speed()
            };

            video.MediaOpened += Video_MediaOpened;
            video.MediaFailed += Video_MediaFailed;
            video.MediaEnded += Video_MediaEnded;

            _video = video;
            MediaHost.Children.Add(video);

            video.Source = new Uri(path);
            UpdatePlayback();
        }

        private void ReleaseVideo()
        {
            var video = _video;
            if (video is null)
                return;

            _video = null;
            _playing = false;

            video.MediaOpened -= Video_MediaOpened;
            video.MediaFailed -= Video_MediaFailed;
            video.MediaEnded -= Video_MediaEnded;

            try
            {
                video.Stop();
                video.Close();
            }
            catch (InvalidOperationException)
            {
            }

            video.Source = null;
            MediaHost.Children.Remove(video);
        }

        private void Video_MediaOpened(object sender, RoutedEventArgs e)
        {
            if (!ReferenceEquals(sender, _video))
                return;

            Log("Background video opened.");

            // Show the first frame even when playback is not wanted right now (reduced motion without a poster).
            if (!_playing)
                _video.Pause();

            UpdatePlayback();
        }

        private void Video_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
        {
            if (!ReferenceEquals(sender, _video))
                return;

            Fail(ThemeBackgroundType.Video, "Background video failed: " + e.ErrorException?.Message);
        }

        private void Video_MediaEnded(object sender, RoutedEventArgs e)
        {
            if (!ReferenceEquals(sender, _video))
                return;

            _video.Position = TimeSpan.Zero;
            if (_playing)
                _video.Play();

            if (!_loggedLoop)
            {
                _loggedLoop = true;
                Log("Background video looped (further loops are not logged).");
            }
        }

        private double Speed() => Math.Clamp(_background?.Speed ?? 1.0, ThemeValidator.MinSpeed, ThemeValidator.MaxSpeed);

        // ---------------------------------------------------------------- playback gating

        private bool WindowAllowsPlayback => _window is null || (_window.IsVisible && _window.WindowState != System.Windows.WindowState.Minimized);

        private void OnMotionChanged()
        {
            if (!_subscribed || _background is null || _background.Type != ThemeBackgroundType.Video || _mediaPath is null)
                return;

            if (!Motion.Enabled)
            {
                // Reduced motion: keep only the poster. Without a poster the paused element is the still frame.
                string poster = Path.Combine(Path.GetDirectoryName(_mediaPath)!, ThemeMedia.PosterName(Path.GetFileName(_mediaPath)));
                if (File.Exists(poster))
                {
                    ReleaseVideo();
                    if (Still.Source is null)
                        _ = LoadStillAsync(poster, _generation, isPoster: true);

                    Log("Reduced motion: showing the poster only.");
                    return;
                }
            }
            else
            {
                EnsureVideo(_mediaPath);
            }

            UpdatePlayback();
        }

        /// <summary>Plays only while a video exists, motion is on, the window is visible and not minimized.</summary>
        private void UpdatePlayback()
        {
            var video = _video;
            if (video is null)
                return;

            bool want = IsVisible && WindowAllowsPlayback && Motion.Enabled;
            string reason = want ? "playing"
                : !Motion.Enabled ? "reduced motion"
                : _window is { WindowState: System.Windows.WindowState.Minimized } ? "window minimized"
                : "window hidden";

            if (want == _playing && reason == _playbackReason)
                return;

            _playbackReason = reason;

            try
            {
                if (want && !_playing)
                {
                    video.SpeedRatio = Speed();
                    video.Play();
                    _playing = true;
                    Log("Background video playing.");
                }
                else if (!want && _playing)
                {
                    video.Pause();
                    _playing = false;
                    Log($"Background video paused ({reason}).");
                }
            }
            catch (InvalidOperationException ex)
            {
                Log("Playback state change failed: " + ex.Message);
            }
        }

        // ---------------------------------------------------------------- layout and parameters

        private void ApplyParameters()
        {
            var b = _background;
            if (b is null)
                return;

            var colour = ColorMath.TryParseHex(_scrimColour, out var rgb) ? Color.FromRgb(rgb.R, rgb.G, rgb.B) : Colors.Black;
            var brush = new SolidColorBrush(colour);
            brush.Freeze();

            Scrim.Fill = brush;
            Scrim.Opacity = Math.Clamp(b.Opacity, ThemeValidator.MinScrim, ThemeValidator.MaxScrim);

            if (_video is not null)
                _video.SpeedRatio = Speed();

            UpdateBox();
        }

        /// <summary>
        /// Sizes the media box. With blur the box overscans by the radius so the blurred edges never show the window
        /// behind; from a radius of <see cref="ThemeMedia.HalfResolutionBlur"/> the box is half size and scaled up 2x so
        /// the blur shader works on a quarter of the pixels.
        /// </summary>
        private void UpdateBox()
        {
            var b = _background;
            if (b is null || ActualWidth <= 0 || ActualHeight <= 0)
                return;

            int blur = Math.Clamp(b.Blur, ThemeValidator.MinBlur, ThemeValidator.MaxBlur);
            bool half = ThemeMedia.IsHalfResolution(b);
            double pad = blur;
            double w = ActualWidth + 2 * pad, h = ActualHeight + 2 * pad;

            MediaBox.Margin = new Thickness(-pad, -pad, 0, 0);

            if (half)
            {
                MediaBox.Width = w / 2;
                MediaBox.Height = h / 2;
                MediaBox.RenderTransform = new ScaleTransform(2, 2);
            }
            else
            {
                MediaBox.Width = w;
                MediaBox.Height = h;
                MediaBox.RenderTransform = Transform.Identity;
            }

            MediaBox.Effect = blur > 0
                ? new BlurEffect { Radius = half ? blur / 2.0 : blur, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance }
                : null;
        }

        // ---------------------------------------------------------------- teardown and failure

        private void Teardown()
        {
            _generation++;

            ReleaseVideo();

            Still.Source = null;
            Still.Visibility = Visibility.Collapsed;
            MediaBox.Effect = null;
            MediaBox.RenderTransform = Transform.Identity;
            Scrim.Fill = null;

            _mediaPath = null;
            _playbackReason = "";
            _loggedLoop = false;

            IsActive = false;
            Visibility = Visibility.Collapsed;
        }

        private void Fail(ThemeBackgroundType type, string detail)
        {
            if (_failed)
                return;

            _failed = true;
            Log(detail + " Falling back to the plain background.");

            Teardown();

            if (_themeId.Length > 0)
                BackgroundNoticeBoard.Default.Post(_themeId, BackgroundNoticeBoard.FailureMessage(type));
        }

        private static void Log(string message) => App.Logger.WriteLine("BackgroundHost", message);
    }
}
