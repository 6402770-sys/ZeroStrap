using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

using ZeroStrap.Enums;

namespace ZeroStrap.UI.Utility
{
    /// <summary>
    /// Entrance motion for the Obsidian shell. Elements tagged with <c>Motion.Enter="n"</c> fade in and
    /// rise 12px when they load, staggered by their order <c>n</c>. Honours the "Reduce motion" setting.
    /// </summary>
    public static class Motion
    {
        private const int StaggerMs = 45;

        /// <summary>See <see cref="MotionRules.IsEnabled"/>: "Smooth animations" and, optionally, the Windows animation setting.</summary>
        public static bool Enabled
        {
            get
            {
                bool loaded = App.Settings.Loaded;
                return MotionRules.IsEnabled(
                    loaded && App.Settings.Prop.ReduceMotion,
                    !loaded || App.Settings.Prop.FollowWindowsAnimations,
                    SystemParameters.ClientAreaAnimation);
            }
        }

        /// <summary>Raised after a motion preference changed (Theme Studio toggles), so long-running visuals can react.</summary>
        public static event Action? Changed;

        public static void NotifyChanged() => Changed?.Invoke();

        /// <summary>Subtle shortens travel and duration; Standard is the shipped feel.</summary>
        public static MotionIntensity Intensity => App.Settings.Loaded ? App.Settings.Prop.MotionIntensity : MotionIntensity.Standard;

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
            FadeRise(element, MotionRules.Duration(GetEnter(element) * StaggerMs, Intensity));
        }

        /// <summary>Fade from 0 and rise from <paramref name="offset"/> px below, after <paramref name="delayMs"/>.</summary>
        public static void FadeRise(UIElement element, int delayMs = 0, double offset = 12, int durationMs = 380)
        {
            if (!Enabled)
                return;

            var intensity = Intensity;
            offset = MotionRules.Travel(offset, intensity);
            durationMs = MotionRules.Duration(durationMs, intensity);

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

        /// <summary>True while a <see cref="WorkspaceSwap"/> is running; callers ignore input until it clears.</summary>
        public static bool IsSwapping { get; private set; }

        /// <summary>
        /// Swaps a section's content for a full-section workspace (or back). The outgoing element fades, slides
        /// 28px away and shrinks slightly; the incoming one follows 110ms later and settles into place.
        /// Both ignore hit testing while the swap runs. Visibility is swapped instantly when motion is reduced.
        /// </summary>
        public static void WorkspaceSwap(FrameworkElement outgoing, FrameworkElement incoming, bool forward, Action? completed = null)
        {
            if (IsSwapping)
                return;

            if (!Enabled)
            {
                incoming.Visibility = Visibility.Visible;
                outgoing.Visibility = Visibility.Collapsed;
                completed?.Invoke();
                return;
            }

            var intensity = Intensity;
            double travelOut = MotionRules.Travel(28, intensity);
            double travelIn = MotionRules.Travel(36, intensity);

            IsSwapping = true;
            outgoing.IsHitTestVisible = false;
            incoming.IsHitTestVisible = false;
            outgoing.RenderTransformOrigin = new Point(0.5, 0.5);
            incoming.RenderTransformOrigin = new Point(0.5, 0.5);

            var outScale = new ScaleTransform(1, 1);
            var outMove = new TranslateTransform(0, 0);
            outgoing.RenderTransform = new TransformGroup { Children = { outScale, outMove } };

            double inFrom = forward ? travelIn : -travelIn;
            var inScale = new ScaleTransform(0.985, 0.985);
            var inMove = new TranslateTransform(inFrom, 0);
            incoming.RenderTransform = new TransformGroup { Children = { inScale, inMove } };

            // The incoming element is laid out right away (invisible) so the first frames of the slide are not
            // spent building it; it stays at opacity 0 until its own 110ms delay has passed.
            incoming.Opacity = 0;
            incoming.Visibility = Visibility.Visible;

            var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };
            var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
            var outDuration = TimeSpan.FromMilliseconds(MotionRules.Duration(200, intensity));
            var inDuration = TimeSpan.FromMilliseconds(MotionRules.Duration(340, intensity));
            var inBegin = TimeSpan.FromMilliseconds(MotionRules.Duration(110, intensity));

            outgoing.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, outDuration) { EasingFunction = easeIn, FillBehavior = FillBehavior.HoldEnd });
            outMove.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, forward ? -travelOut : travelOut, outDuration) { EasingFunction = easeIn, FillBehavior = FillBehavior.HoldEnd });
            outScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.985, outDuration) { EasingFunction = easeIn, FillBehavior = FillBehavior.HoldEnd });
            outScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.985, outDuration) { EasingFunction = easeIn, FillBehavior = FillBehavior.HoldEnd });

            var inFade = new DoubleAnimation(0, 1, inDuration) { BeginTime = inBegin, EasingFunction = easeOut, FillBehavior = FillBehavior.HoldEnd };
            // the handler must be attached before BeginAnimation: the clock is created from a copy of the timeline
            inFade.Completed += (_, _) => FinishWorkspaceSwap(outgoing, incoming, outMove, outScale, inMove, inScale, completed);
            incoming.BeginAnimation(UIElement.OpacityProperty, inFade);
            inMove.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(inFrom, 0, inDuration) { BeginTime = inBegin, EasingFunction = easeOut, FillBehavior = FillBehavior.HoldEnd });
            inScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.985, 1, inDuration) { BeginTime = inBegin, EasingFunction = easeOut, FillBehavior = FillBehavior.HoldEnd });
            inScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.985, 1, inDuration) { BeginTime = inBegin, EasingFunction = easeOut, FillBehavior = FillBehavior.HoldEnd });

        }

        private static void FinishWorkspaceSwap(FrameworkElement outgoing, FrameworkElement incoming,
            TranslateTransform outMove, ScaleTransform outScale, TranslateTransform inMove, ScaleTransform inScale, Action? completed)
        {
            // Release every animated value, then restore a clean state.
            outgoing.BeginAnimation(UIElement.OpacityProperty, null);
            incoming.BeginAnimation(UIElement.OpacityProperty, null);
            outMove.BeginAnimation(TranslateTransform.XProperty, null);
            outScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            outScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            inMove.BeginAnimation(TranslateTransform.XProperty, null);
            inScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            inScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

            outgoing.Visibility = Visibility.Collapsed;
            outgoing.RenderTransform = Transform.Identity;
            incoming.RenderTransform = Transform.Identity;
            outgoing.Opacity = 1;
            incoming.Opacity = 1;
            outgoing.IsHitTestVisible = true;
            incoming.IsHitTestVisible = true;

            IsSwapping = false;
            completed?.Invoke();
        }

        /// <summary>Crossfade-in used for page changes inside the shell frame.</summary>
        public static void PageEnter(UIElement element) => FadeRise(element, 0, 10, 340);
    }
}
