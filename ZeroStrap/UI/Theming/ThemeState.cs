using System.ComponentModel;

namespace ZeroStrap.UI.Theming
{
    /// <summary>
    /// Bindable theme flags that styles cannot express with plain resources (a style trigger needs a binding source).
    /// Written by <see cref="ThemeManager"/>; read from XAML through <c>{x:Static theming:ThemeState.Instance}</c>.
    /// </summary>
    public sealed class ThemeState : INotifyPropertyChanged
    {
        private bool _subtleButtons;

        public static ThemeState Instance { get; } = new();

        private ThemeState() { }

        /// <summary>True when the active theme's Components.ButtonStyle is Subtle.</summary>
        public bool SubtleButtons
        {
            get => _subtleButtons;
            set
            {
                if (_subtleButtons == value)
                    return;

                _subtleButtons = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SubtleButtons)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
