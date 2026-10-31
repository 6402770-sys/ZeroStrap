using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ZeroStrap.UI.Utility
{
    /// <summary>
    /// Clips a <see cref="Border"/>'s child to the border's rounded inner corners (a Border does not clip
    /// its child to its CornerRadius by itself). Set <c>RoundedClip.Enabled="True"</c> on the border.
    /// </summary>
    public static class RoundedClip
    {
        public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
            "Enabled", typeof(bool), typeof(RoundedClip), new PropertyMetadata(false, OnEnabledChanged));

        public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);
        public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

        private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not Border border)
                return;

            border.Loaded -= Border_Loaded;
            border.SizeChanged -= Border_SizeChanged;

            if ((bool)e.NewValue)
            {
                border.Loaded += Border_Loaded;
                border.SizeChanged += Border_SizeChanged;
                Apply(border);
            }
            else if (border.Child is not null)
            {
                border.Child.Clip = null;
            }
        }

        private static void Border_Loaded(object sender, RoutedEventArgs e) => Apply((Border)sender);

        private static void Border_SizeChanged(object sender, SizeChangedEventArgs e) => Apply((Border)sender);

        private static void Apply(Border border)
        {
            if (border.Child is not { } child)
                return;

            double width = border.ActualWidth - border.BorderThickness.Left - border.BorderThickness.Right - border.Padding.Left - border.Padding.Right;
            double height = border.ActualHeight - border.BorderThickness.Top - border.BorderThickness.Bottom - border.Padding.Top - border.Padding.Bottom;

            if (width <= 0 || height <= 0)
                return;

            double radius = Math.Max(0, border.CornerRadius.TopLeft - border.BorderThickness.Left);
            var clip = new RectangleGeometry(new Rect(0, 0, width, height), radius, radius);
            clip.Freeze();
            child.Clip = clip;
        }
    }
}
