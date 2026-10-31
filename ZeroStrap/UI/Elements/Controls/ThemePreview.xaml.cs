using System.Windows.Controls;
using System.Windows.Media;

using ZeroStrap.Models.Theming;
using ZeroStrap.UI.Theming;

namespace ZeroStrap.UI.Elements.Controls
{
    /// <summary>Miniature app window rendered from a theme's colours with local brushes.</summary>
    public partial class ThemePreview : UserControl
    {
        public ThemePreview()
        {
            InitializeComponent();
            Apply(new ThemeColors());
        }

        public void Apply(ThemeColors c)
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

            Fill(PrimaryButton, c.Accent);
            Ink(PrimaryText, c.OnAccent);
            Fill(SecondaryButton, c.SurfaceElevated);
            Stroke(SecondaryButton, c.Border);
            Ink(SecondaryText, c.TextPrimary);

            Dot(DotSuccess, c.Success);
            Dot(DotWarning, c.Warning);
            Dot(DotDanger, c.Danger);
            Dot(DotInfo, c.Info);
        }
    }
}
