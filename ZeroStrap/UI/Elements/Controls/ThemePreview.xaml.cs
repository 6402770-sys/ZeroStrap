using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using WpfFontFamily = System.Windows.Media.FontFamily;

using ZeroStrap.Models.Theming;
using ZeroStrap.UI.Theming;

namespace ZeroStrap.UI.Elements.Controls
{
    /// <summary>
    /// Miniature app window rendered from a theme (colours, typography, radius, button and surface style) with local
    /// brushes and values, so it is independent of the application theme and updates instantly while editing.
    /// </summary>
    public partial class ThemePreview : UserControl
    {
        // Design values from the XAML, captured once so every Apply starts from the same base.
        private readonly Dictionary<TextBlock, double> _baseSizes = new();
        private readonly Dictionary<Border, CornerRadius> _baseRadii = new();

        public ThemePreview()
        {
            InitializeComponent();

            foreach (var text in new[] { BrandText, NavSelectedText, Nav2Text, Nav3Text, CardTitle, CardCaption, CardMuted, PrimaryText, SecondaryText })
                _baseSizes[text] = text.FontSize;

            foreach (var border in new[] { Root, Sidebar, Mark, NavSelected, Card, PrimaryButton, SecondaryButton })
                _baseRadii[border] = border.CornerRadius;

            Apply(new ThemeDefinition());
        }

        public void Apply(ThemeDefinition theme)
        {
            ApplyColors(theme.Colors ?? new ThemeColors(), theme.Components?.ButtonStyle ?? ThemeButtonStyle.Solid);
            ApplyTypography(theme.Typography ?? new ThemeTypography());
            ApplyComponents(theme.Components ?? new ThemeComponents());
            ApplyBackground(theme);
        }

        private string? _backdropPath;

        /// <summary>Shows the theme's image (or a video's poster) under its scrim, with the blur scaled to the miniature.</summary>
        private void ApplyBackground(ThemeDefinition theme)
        {
            string? path = null;
            var b = theme.Background;

            if (ThemeMedia.HasBackground(b) && theme.Kind == ThemeKind.Custom)
            {
                try
                {
                    path = ThemeManager.Store.GetMediaPath(theme);
                    if (path is not null && b!.Type == ThemeBackgroundType.Video)
                    {
                        string poster = Path.Combine(Path.GetDirectoryName(path)!, ThemeMedia.PosterName(Path.GetFileName(path)));
                        path = File.Exists(poster) ? poster : null;
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException)
                {
                    path = null;
                }
            }

            if (path is null)
            {
                BackdropLayer.Visibility = Visibility.Collapsed;
                BackdropImage.Source = null;
                _backdropPath = null;
                return;
            }

            if (!string.Equals(path, _backdropPath, StringComparison.OrdinalIgnoreCase))
            {
                _backdropPath = path;

                try
                {
                    var image = new System.Windows.Media.Imaging.BitmapImage();
                    image.BeginInit();
                    image.UriSource = new Uri(path);
                    image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    image.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile;
                    image.DecodePixelWidth = 640;
                    image.EndInit();
                    image.Freeze();
                    BackdropImage.Source = image;
                }
                catch (Exception ex) when (ex is NotSupportedException or IOException or InvalidOperationException or FileFormatException or ArgumentException)
                {
                    BackdropImage.Source = null;
                    BackdropLayer.Visibility = Visibility.Collapsed;
                    return;
                }
            }

            double scale = 0.3; // the miniature is roughly a third of the app window
            int blur = Math.Clamp(b!.Blur, ThemeValidator.MinBlur, ThemeValidator.MaxBlur);
            BackdropImage.Effect = blur > 0 ? new System.Windows.Media.Effects.BlurEffect { Radius = blur * scale } : null;

            BackdropScrim.Fill = ColorMath.TryParseHex(theme.Colors?.Background, out var rgb)
                ? new SolidColorBrush(Color.FromRgb(rgb.R, rgb.G, rgb.B))
                : Brushes.Black;
            BackdropScrim.Opacity = Math.Clamp(b.Opacity, ThemeValidator.MinScrim, ThemeValidator.MaxScrim);
            BackdropLayer.Visibility = Visibility.Visible;
        }

        private void ApplyColors(ThemeColors c, ThemeButtonStyle buttonStyle)
        {
            // Unparsable values are ignored (the previous brush stays) so a half-typed colour never breaks the preview.
            Brush? Paint(string hex) =>
                ColorMath.TryParseHex(hex, out var rgb) ? new SolidColorBrush(Color.FromRgb(rgb.R, rgb.G, rgb.B)) : null;

            void Fill(Border target, string hex) { if (Paint(hex) is { } b) target.Background = b; }
            void Stroke(Border target, string hex) { if (Paint(hex) is { } b) target.BorderBrush = b; }
            void Ink(TextBlock target, string hex) { if (Paint(hex) is { } b) target.Foreground = b; }
            void Dot(System.Windows.Shapes.Shape target, string hex) { if (Paint(hex) is { } b) target.Fill = b; }

            Fill(Root, c.Background);
            Stroke(Root, c.Border);

            Fill(Sidebar, c.BackgroundSecondary);
            Stroke(Sidebar, c.Border);
            Fill(Mark, c.Accent);
            Ink(BrandText, c.TextPrimary);

            Fill(NavSelected, c.SurfaceElevated);
            Ink(NavSelectedText, c.TextPrimary);
            Ink(Nav2Text, c.TextSecondary);
            Ink(Nav3Text, c.TextSecondary);

            Fill(Card, c.Surface);
            Stroke(Card, c.Border);
            Ink(CardTitle, c.TextPrimary);
            Ink(CardCaption, c.TextSecondary);
            Ink(CardMuted, c.TextMuted);

            if (buttonStyle == ThemeButtonStyle.Subtle && ColorMath.TryParseHex(c.Accent, out var accent))
            {
                // Same recipe as the app's Subtle primary button: ~14% accent tint with accent-coloured text.
                PrimaryButton.Background = new SolidColorBrush(Color.FromArgb(36, accent.R, accent.G, accent.B));
                PrimaryText.Foreground = new SolidColorBrush(Color.FromRgb(accent.R, accent.G, accent.B));
            }
            else
            {
                Fill(PrimaryButton, c.Accent);
                Ink(PrimaryText, c.OnAccent);
            }

            Fill(SecondaryButton, c.SurfaceElevated);
            Stroke(SecondaryButton, c.Border);
            Ink(SecondaryText, c.TextPrimary);

            Dot(DotSuccess, c.Success);
            Dot(DotWarning, c.Warning);
            Dot(DotDanger, c.Danger);
            Dot(DotInfo, c.Info);
        }

        private void ApplyTypography(ThemeTypography t)
        {
            double scale = double.IsNaN(t.Scale) ? 1.0 : Math.Clamp(t.Scale, ThemeValidator.MinScale, ThemeValidator.MaxScale);

            TextElement.SetFontFamily(Root, new WpfFontFamily(ThemeTokens.BuildFamily(t.Family)));

            foreach (var (text, size) in _baseSizes)
                text.FontSize = ThemeTokens.ScaleFont(size, scale);

            var body = FontWeight.FromOpenTypeWeight(ThemeTokens.NormaliseWeight(t.BodyWeight));
            var heading = FontWeight.FromOpenTypeWeight(ThemeTokens.NormaliseWeight(t.HeadingWeight));

            foreach (var text in new[] { Nav2Text, Nav3Text, CardCaption, CardMuted, SecondaryText })
                text.FontWeight = body;

            CardTitle.FontWeight = heading;
        }

        private void ApplyComponents(ThemeComponents c)
        {
            int radius = Math.Clamp(c.Radius, ThemeValidator.MinRadius, ThemeValidator.MaxRadius);

            foreach (var (border, design) in _baseRadii)
            {
                double Scale(double v) => ThemeTokens.ScaleRadius(v, radius);
                border.CornerRadius = new CornerRadius(Scale(design.TopLeft), Scale(design.TopRight), Scale(design.BottomRight), Scale(design.BottomLeft));
            }

            // Flat surfaces drop the card outline; the fill alone carries the card.
            Card.BorderThickness = new Thickness(ThemeTokens.SurfaceBorder(c.SurfaceStyle));
        }
    }
}
