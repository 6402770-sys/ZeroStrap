using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

using ZeroStrap.UI.Elements.Base;
using ZeroStrap.UI.Theming;
using ZeroStrap.UI.Utility;

namespace ZeroStrap.UI.Elements.Shell.Pages
{
    public partial class AppearancePage : Page
    {
        private Settings.Pages.AppearancePage? _launcherPage;
        private bool _refreshing;

        public AppearancePage()
        {
            InitializeComponent();
            Loaded += (_, _) => Refresh();
        }

        public void Refresh()
        {
            _refreshing = true;

            string themeId = ThemeManager.ActiveId;
            ThemeCardDark.IsChecked = themeId == ThemeManager.ObsidianId;
            ThemeCardLight.IsChecked = themeId == ThemeManager.LightId;
            ThemeCardDefault.IsChecked = themeId == ThemeManager.SystemId;

            AnimationsToggle.IsChecked = !App.Settings.Prop.ReduceMotion;

            CustomThemeRun.Text = string.IsNullOrEmpty(App.Settings.Prop.SelectedCustomTheme)
                ? "none"
                : App.Settings.Prop.SelectedCustomTheme;

            _refreshing = false;
        }

        private void ThemeCard_Checked(object sender, RoutedEventArgs e)
        {
            if (_refreshing || sender is not RadioButton { Tag: string id })
                return;

            if (ThemeManager.ActiveId == id)
                return;

            try
            {
                ThemeManager.SetActive(id);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("AppearancePage::ThemeCard_Checked", ex);
            }

            ActivityLog.Record(ActivityType.Theme, "Theme changed", $"Switched to {ThemeManager.ActiveDisplayName} theme");
        }

        private void AnimationsToggle_Click(object sender, RoutedEventArgs e)
        {
            // Saved with the rest of the settings (Ctrl + S); takes effect immediately in this session.
            App.Settings.Prop.ReduceMotion = AnimationsToggle.IsChecked != true;
        }

        private void AppearanceHost_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is Frame { Content: null } frame)
            {
                _launcherPage = new Settings.Pages.AppearancePage();
                frame.Navigate(_launcherPage);
            }
        }

        /// <summary>Scrolls to the custom theme manager inside the launcher settings and highlights it.</summary>
        private void ManageCustomThemes_Click(object sender, RoutedEventArgs e)
        {
            FrameworkElement target = LauncherCard;

            // The themes list lives in the "Style" expander, which only opens once the Custom style is
            // picked; target the expander itself so its Style selector is what the user lands on.
            DependencyObject? node = _launcherPage?.FindName("CustomThemesListBox") as DependencyObject;
            while (node is not null && node is not Wpf.Ui.Controls.CardExpander)
                node = LogicalTreeHelper.GetParent(node);

            if (node is FrameworkElement { IsVisible: true } expander)
                target = expander;

            if (target is Wpf.Ui.Controls.CardExpander { IsExpanded: false })
                CustomThemeHint.Visibility = Visibility.Visible;

            var offset = target.TransformToAncestor(PageScroll).Transform(new Point(0, 0)).Y;
            AnimateScroll(PageScroll.VerticalOffset + offset - 24);
            Flash(target);
        }

        private void AnimateScroll(double to)
        {
            to = Math.Max(0, Math.Min(to, PageScroll.ScrollableHeight));

            if (!Motion.Enabled)
            {
                PageScroll.ScrollToVerticalOffset(to);
                return;
            }

            double from = PageScroll.VerticalOffset;
            var start = DateTime.Now;
            const double durationMs = 420;

            EventHandler? tick = null;
            tick = (_, _) =>
            {
                double t = Math.Min(1, (DateTime.Now - start).TotalMilliseconds / durationMs);
                double eased = 1 - Math.Pow(1 - t, 4);
                PageScroll.ScrollToVerticalOffset(from + (to - from) * eased);

                if (t >= 1)
                    CompositionTarget.Rendering -= tick;
            };
            CompositionTarget.Rendering += tick;
        }

        private static void Flash(FrameworkElement element)
        {
            var pulse = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromMilliseconds(380) };
            pulse.KeyFrames.Add(new EasingDoubleKeyFrame(0.45, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(160))));
            pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(520)), new CubicEase { EasingMode = EasingMode.EaseOut }));
            pulse.Completed += (_, _) => element.BeginAnimation(OpacityProperty, null);
            element.BeginAnimation(OpacityProperty, pulse);
        }
    }
}
