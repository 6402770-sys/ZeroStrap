using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Bloxstrap.FastFlags
{
    /// <summary>
    /// Runs Fast Flag shortcuts inside one window (the Settings window). Window-scoped on purpose:
    /// no global keyboard hook, nothing is intercepted while the user types, and the handler is
    /// removed when the window closes. A shortcut edits the pending configuration only.
    /// </summary>
    public sealed class FlagBindingHost
    {
        private readonly Window _window;
        private readonly Action<string> _notify;

        private FlagBindingHost(Window window, Action<string> notify)
        {
            _window = window;
            _notify = notify;

            _window.PreviewKeyDown += Window_PreviewKeyDown;
            _window.Closed += Window_Closed;
        }

        public static FlagBindingHost Attach(Window window, Action<string> notify) => new(window, notify);

        private void Window_Closed(object? sender, EventArgs e)
        {
            _window.PreviewKeyDown -= Window_PreviewKeyDown;
            _window.Closed -= Window_Closed;
        }

        /// <summary>"Ctrl+Shift+K" style description of a key event; null for a lone modifier press.</summary>
        public static string? DescribeGesture(KeyEventArgs e)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None)
                return null;

            var parts = new List<string>();
            var modifiers = Keyboard.Modifiers;

            if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");

            // D0-D9 are the number row; NormalizeGesture wants the digit itself
            string name = key is >= Key.D0 and <= Key.D9 ? ((int)(key - Key.D0)).ToString() : key.ToString();
            parts.Add(name);

            return String.Join("+", parts);
        }

        private static bool IsTyping(object? focused) =>
            focused is TextBoxBase or PasswordBox
            || focused is ComboBox { IsEditable: true }
            || (focused is DependencyObject d && FindAncestor<DataGridCell>(d) is { IsEditing: true });

        private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
        {
            while (node is not null)
            {
                if (node is T match)
                    return match;

                node = System.Windows.Media.VisualTreeHelper.GetParent(node);
            }

            return null;
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var workspace = FastFlagWorkspace.Instance;
            var state = workspace.State.Prop;

            if (!state.BindingsEnabled || state.Bindings.Count == 0 || e.IsRepeat)
                return;

            if (IsTyping(Keyboard.FocusedElement))
                return;

            string? gesture = FlagBindingEngine.NormalizeGesture(DescribeGesture(e));

            if (gesture is null)
                return;

            var binding = FlagBindingEngine.Find(state.Bindings, gesture);

            if (binding is null || FlagBindingEngine.Validate(binding) is not null)
                return;

            e.Handled = true;

            string? current = workspace.Store.Get(binding.FlagName);
            string? next = FlagBindingEngine.NextValue(binding, current);

            workspace.Store.Set(binding.FlagName, next);
            workspace.NotifyFlagsChanged();

            _notify(String.Format(Strings.FastFlags_Binding_Applied, binding.FlagName, next ?? Strings.FastFlags_Binding_NotSet));
        }
    }
}
