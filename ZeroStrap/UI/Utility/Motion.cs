using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ZeroStrap.UI.Utility
{
    /// <summary>
    /// Entrance motion for the Obsidian shell. Elements tagged with <c>Motion.Enter="n"</c> fade in and
    /// rise 12px when they load, staggered by their order <c>n</c>. Honours the "Reduce motion" setting.
    /// </summary>
    public static class Motion
    {
        private const int StaggerMs = 45;

        public static bool Enabled => !(App.Settings.Loaded && App.Settings.Prop.ReduceMotion);

        public static readonly DependencyProperty EnterProperty = DependencyProperty.RegisterAttached(
            "Enter", typeof(int), typeof(Motion), new PropertyMetadata(-1, OnEnterChanged));

        public static int GetEnter(DependencyObject d) => (int)d.GetValue(EnterProperty);
        public static void SetEnter(DependencyObject d, int value) => d.SetValue(EnterProperty, value);

        private static void OnEnterChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not FrameworkElement element || (int)e.NewValue < 0)
                return;

            element.Loaded -= Element_Loaded;
            element.Loaded += Element_Loaded;
        }

        private static void Element_Loaded(object sender, RoutedEventArgs e)
        {
            var element = (FrameworkElement)sender;
            element.Loaded -= Element_Loaded;
            FadeRise(element, GetEnter(element) * StaggerMs);
        }

        /// <summary>Fade from 0 and rise from <paramref name="offset"/> px below, after <paramref name="delayMs"/>.</summary>
        public static void FadeRise(UIElement element, int delayMs = 0, double offset = 12, int durationMs = 380)
        {
            if (!Enabled)
                return;

            var translate = new TranslateTransform(0, offset);
            element.RenderTransform = translate;
            element.Opacity = 0;

            var begin = TimeSpan.FromMilliseconds(delayMs);
            var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };

            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(durationMs * 0.7)) { BeginTime = begin, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            var rise = new DoubleAnimation(offset, 0, TimeSpan.FromMilliseconds(durationMs)) { BeginTime = begin, EasingFunction = ease };

            // Release the animated values when done so later layout/opacity changes are not pinned.
            fade.Completed += (_, _) => { element.BeginAnimation(UIElement.OpacityProperty, null); element.Opacity = 1; };
            rise.Completed += (_, _) => { translate.BeginAnimation(TranslateTransform.YProperty, null); translate.Y = 0; };

            element.BeginAnimation(UIElement.OpacityProperty, fade);
            translate.BeginAnimation(TranslateTransform.YProperty, rise);
        }

        /// <summary>Crossfade-in used for page changes inside the shell frame.</summary>
        public static void PageEnter(UIElement element) => FadeRise(element, 0, 10, 340);
    }
}
