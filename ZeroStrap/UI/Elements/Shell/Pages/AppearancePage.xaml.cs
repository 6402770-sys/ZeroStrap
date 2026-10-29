using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using ZeroStrap.Enums;
using ZeroStrap.UI.Elements.Base;

namespace ZeroStrap.UI.Elements.Shell.Pages
{
    public partial class AppearancePage : Page
    {
        public AppearancePage()
        {
            InitializeComponent();
            Loaded += (_, _) => Refresh();
        }

        private void Refresh()
        {
            var theme = App.Settings.Prop.Theme;
            UpdateSelection(theme);

            CustomThemeText.Text = string.IsNullOrEmpty(App.Settings.Prop.SelectedCustomTheme)
                ? "None"
                : App.Settings.Prop.SelectedCustomTheme;
        }

        private void UpdateSelection(Theme theme)
        {
            var accent = (SolidColorBrush)FindResource("TextFillColorPrimaryBrush");
            var neutral = (SolidColorBrush)FindResource("CardStrokeColorDefaultBrush");

            ThemeCardDefault.BorderBrush = theme == Theme.Default ? accent : neutral;
            ThemeCardLight.BorderBrush = theme == Theme.Light ? accent : neutral;
            ThemeCardDark.BorderBrush = theme == Theme.Dark ? accent : neutral;

            ThemeCardDefault.BorderThickness = new Thickness(theme == Theme.Default ? 2 : 1);
            ThemeCardLight.BorderThickness = new Thickness(theme == Theme.Light ? 2 : 1);
            ThemeCardDark.BorderThickness = new Thickness(theme == Theme.Dark ? 2 : 1);

            ActiveThemeHint.Text = theme switch
            {
                Theme.Light => "Light is active. Themed brushes update immediately.",
                Theme.Dark => "Obsidian (Dark) is active. Themed brushes update immediately.",
                _ => "Following Windows theme."
            };
        }

        private void ThemeCard_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border { Tag: string tag })
                return;

            if (!Enum.TryParse<Theme>(tag, out var theme))
                return;

            App.Settings.Prop.Theme = theme;
            UpdateSelection(theme);

            try
            {
                if (Window.GetWindow(this) is WpfUiWindow w)
                    w.ApplyTheme();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("AppearancePage::ThemeCard_Click", ex);
            }
        }

        private void OpenAdvanced_Click(object sender, RoutedEventArgs e)
        {
            // Jump to the embedded advanced panel.
            if (Parent is DependencyObject)
                AppearanceHost?.Navigate(new Settings.Pages.AppearancePage());
        }

        private void AppearanceHost_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is Frame { Content: null } frame)
                frame.Navigate(new Settings.Pages.AppearancePage());
        }
    }
}
