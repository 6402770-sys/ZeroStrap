using System.Windows;
using System.Windows.Interop;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Wpf.Ui.Mvvm.Contracts;
using Wpf.Ui.Mvvm.Services;

namespace Bloxstrap.UI.Elements.Base
{
    public abstract class WpfUiWindow : UiWindow
    {
        private readonly IThemeService _themeService = new ThemeService();

        public WpfUiWindow()
        {
            ApplyTheme();

            // ZeroBootstrap: re-assert the "None" backdrop after WPF-UI's theme engine has run,
            // which force-applies Mica to the first window of the process at Loaded.
            Loaded += (_, _) =>
            {
                if (WindowBackdropType != BackgroundType.None)
                    return;

                Wpf.Ui.Appearance.Background.Remove(this);
                BindThemeBackground();
            };
        }

        /// <summary>
        /// WPF-UI's Background.Remove(hwnd) looks the brush up in Window.Resources only, misses it and
        /// assigns a hard-coded #202020 local value, hiding the BLACKOUT palette. Re-bind to the
        /// app-level theme brush after every place WPF-UI may have done that.
        /// </summary>
        private void BindThemeBackground()
        {
            if (WindowBackdropType == BackgroundType.None)
                SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
        }

        private static void ApplyMonochromeAccent(bool dark)
        {
            static System.Windows.Media.Color Grey(byte v) => System.Windows.Media.Color.FromRgb(v, v, v);

            // ZeroBootstrap: explicit accent ramp. IThemeService.SetAccent always derives the ramp with
            // light-theme maths (darker steps), which turns #D4D4D4 into a muddy #BBBBBB on dark.
            // Order: system, primary, secondary (most used: primary buttons, toggles, nav pill), tertiary (hover).
            if (dark)
                Accent.Apply(Grey(0xD4), Grey(0xDC), Grey(0xE8), Grey(0xF4));
            else
                Accent.Apply(Grey(0x2A), Grey(0x26), Grey(0x1E), Grey(0x3A));
        }

        public void ApplyTheme()
        {
            const int customThemeIndex = 2; // index for CustomTheme merged dictionary

            bool dark = App.Settings.Prop.Theme.GetFinal() == Enums.Theme.Dark;

            _themeService.SetTheme(dark ? ThemeType.Dark : ThemeType.Light);

            // ZeroBootstrap: monochrome accent. Light grey on dark; near-black on light so accent-filled
            // controls (primary buttons, toggles) keep readable on-accent text.
            ApplyMonochromeAccent(dark);

            // there doesn't seem to be a way to query the name for merged dictionaries
            var dict = new ResourceDictionary { Source = new Uri($"pack://application:,,,/UI/Style/{Enum.GetName(App.Settings.Prop.Theme.GetFinal())}.xaml") };
            Application.Current.Resources.MergedDictionaries[customThemeIndex] = dict;

#if QA_BUILD
            this.BorderBrush = System.Windows.Media.Brushes.Red;
            this.BorderThickness = new Thickness(4);
#endif
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            if (App.Settings.Prop.WPFSoftwareRender || App.LaunchSettings.NoGPUFlag.Active)
            {
                if (PresentationSource.FromVisual(this) is HwndSource hwndSource)
                    hwndSource.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
            }

            base.OnSourceInitialized(e);

            // UiWindow.OnSourceInitialized applies the "None" backdrop via Background.Remove
            BindThemeBackground();
        }
    }
}
