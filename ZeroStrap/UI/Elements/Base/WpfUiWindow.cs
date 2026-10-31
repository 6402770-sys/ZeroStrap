using System.Windows;
using System.Windows.Interop;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using ZeroStrap.UI.Theming;

namespace ZeroStrap.UI.Elements.Base
{
    public abstract class WpfUiWindow : UiWindow
    {
        public WpfUiWindow()
        {
            ApplyTheme();

            // ZeroStrap: re-assert the "None" backdrop after WPF-UI's theme engine has run,
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

        /// <summary>Applies the active interface theme (see <see cref="ThemeManager"/>); app-wide, so it covers every open window.</summary>
        public void ApplyTheme()
        {
            ThemeManager.Apply();

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
