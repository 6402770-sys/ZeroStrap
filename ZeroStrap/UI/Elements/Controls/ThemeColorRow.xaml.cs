using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using ZeroStrap.UI.Theming;

namespace ZeroStrap.UI.Elements.Controls
{
    /// <summary>One editable colour in the theme editor: label, swatch, hex value and an optional contrast note.</summary>
    public sealed class ColorRowVm : INotifyPropertyChanged
    {
        private string _hex;
        private string? _note;
        private ContrastLevel _level;

        public ColorRowVm(string key, string label, string group, string hex, Func<IReadOnlyList<string>> quickColors)
        {
            Key = key;
            Label = label;
            Group = group;
            _hex = hex;
            QuickColors = quickColors;
        }

        public string Key { get; }
        public string Label { get; }
        public string Group { get; }
        public Func<IReadOnlyList<string>> QuickColors { get; }

        public string Hex => _hex;

        public Brush Swatch =>
            ColorMath.TryParseHex(_hex, out var rgb) ? new SolidColorBrush(Color.FromRgb(rgb.R, rgb.G, rgb.B)) : Brushes.Transparent;

        public string? Note => _note;
        public ContrastLevel Level => _level;

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>Raised when the user (not the editor itself) changed the value.</summary>
        public event Action<ColorRowVm>? UserChanged;

        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        /// <summary>Sets the value without raising <see cref="UserChanged"/>.</summary>
        public void SetSilently(string hex)
        {
            if (_hex == hex)
                return;

            _hex = hex;
            Raise(nameof(Hex));
            Raise(nameof(Swatch));
        }

        public void SetFromUser(string hex)
        {
            if (!ColorMath.TryParseHex(hex, out var rgb))
                return;

            string normal = rgb.ToHex();
            if (_hex == normal)
                return;

            SetSilently(normal);
            UserChanged?.Invoke(this);
        }

        /// <summary>Shows or clears the contrast note for this row.</summary>
        public void SetContrast(double? ratio)
        {
            string? note = ratio is { } r ? ContrastNote.Message(r) : null;
            var level = ratio is { } r2 ? ContrastNote.Classify(r2) : ContrastLevel.Ok;

            if (note == _note && level == _level)
                return;

            _note = note;
            _level = level;
            Raise(nameof(Note));
            Raise(nameof(Level));
        }
    }

    public partial class ThemeColorRow : UserControl
    {
        private ColorRowVm? _vm;
        private DateTime _closedAt = DateTime.MinValue;

        public ThemeColorRow()
        {
            InitializeComponent();

            Picker.ColorChanged += hex => _vm?.SetFromUser(hex);
            Picker.CloseRequested += () => PickerPopup.IsOpen = false;
            DataContextChanged += (_, _) => Attach(DataContext as ColorRowVm);
            Unloaded += (_, _) => PickerPopup.IsOpen = false;
        }

        private void Attach(ColorRowVm? vm)
        {
            if (_vm is not null)
                _vm.PropertyChanged -= Vm_PropertyChanged;

            _vm = vm;

            if (_vm is null)
                return;

            _vm.PropertyChanged += Vm_PropertyChanged;
            ShowValue();
        }

        private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ColorRowVm.Hex))
                ShowValue();
        }

        private void ShowValue()
        {
            if (_vm is null)
                return;

            if (!HexBox.IsKeyboardFocusWithin)
                HexBox.Text = _vm.Hex;

            if (Picker.Color != _vm.Hex)
                Picker.Color = _vm.Hex;
        }

        // ---------------------------------------------------------------- popup

        private void SwatchButton_Click(object sender, RoutedEventArgs e)
        {
            // A click that closed the popup (click outside) must not immediately reopen it.
            if ((DateTime.UtcNow - _closedAt).TotalMilliseconds < 250 || _vm is null)
                return;

            Picker.QuickColors = _vm.QuickColors();
            Picker.Color = _vm.Hex;
            PickerPopup.IsOpen = true;
        }

        private void PickerPopup_Opened(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(Picker.FocusArea), System.Windows.Threading.DispatcherPriority.Input);

        private void PickerPopup_Closed(object? sender, EventArgs e)
        {
            _closedAt = DateTime.UtcNow;
            SwatchButton.Focus();
        }

        // ---------------------------------------------------------------- hex field

        private void HexBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => HexBox.SelectAll();

        private void HexBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (!HexBox.IsKeyboardFocusWithin)
                CommitHex();
        }

        private void HexBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                CommitHex();
                HexBox.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _vm is not null && HexBox.Text != _vm.Hex)
            {
                HexBox.Text = _vm.Hex;
                HexBox.SelectAll();
                e.Handled = true;
            }
        }

        /// <summary>Applies a valid colour and reverts the field when the text is not a #RRGGBB value.</summary>
        private void CommitHex()
        {
            if (_vm is null)
                return;

            if (ColorMath.TryParseLooseHex(HexBox.Text, out var rgb))
                _vm.SetFromUser(rgb.ToHex());

            HexBox.Text = _vm.Hex;
        }
    }
}
