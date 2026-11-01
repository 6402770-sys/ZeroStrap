using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

using ZeroStrap.UI.Theming;

namespace ZeroStrap.UI.Elements.Controls
{
    /// <summary>
    /// Colour picker panel (saturation/value square, hue strip, hex field, quick swatches). Shown inside a Popup by
    /// the theme editor. <see cref="ColorChanged"/> fires continuously while the user drags or types a valid colour.
    /// </summary>
    public partial class ZeroColorPicker : UserControl
    {
        public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
            nameof(Color), typeof(string), typeof(ZeroColorPicker),
            new FrameworkPropertyMetadata("#000000", (d, e) => ((ZeroColorPicker)d).OnColorPropertyChanged((string)e.NewValue)));

        public static readonly DependencyProperty QuickColorsProperty = DependencyProperty.Register(
            nameof(QuickColors), typeof(IEnumerable<string>), typeof(ZeroColorPicker),
            new PropertyMetadata(null, (d, _) => ((ZeroColorPicker)d).BuildQuickSwatches()));

        private double _h, _s, _v = 1;
        private bool _updating;
        private bool _dragSv, _dragHue;

        /// <summary>Raised with the new "#RRGGBB" value whenever the user changes the colour.</summary>
        public event Action<string>? ColorChanged;

        /// <summary>Raised when the user presses Escape; the host closes the popup.</summary>
        public event Action? CloseRequested;

        public ZeroColorPicker()
        {
            InitializeComponent();
            SyncFromColor(Color);
            Refresh(updateHex: true);
        }

        /// <summary>Current colour as "#RRGGBB".</summary>
        public string Color
        {
            get => (string)GetValue(ColorProperty);
            set => SetValue(ColorProperty, value);
        }

        public IEnumerable<string>? QuickColors
        {
            get => (IEnumerable<string>?)GetValue(QuickColorsProperty);
            set => SetValue(QuickColorsProperty, value);
        }

        /// <summary>Moves keyboard focus to the saturation/value square so the arrow keys work straight away.</summary>
        public void FocusArea() => Keyboard.Focus(SvArea);

        // ---------------------------------------------------------------- state

        private Rgb CurrentRgb => ColorMath.FromHsv(_h, _s, _v);

        private static SolidColorBrush BrushOf(Rgb rgb) => new(System.Windows.Media.Color.FromRgb(rgb.R, rgb.G, rgb.B));

        private void OnColorPropertyChanged(string value)
        {
            if (_updating)
                return;

            SyncFromColor(value);
            Refresh(updateHex: true);
        }

        /// <summary>Takes the HSV from a colour, keeping the current hue when the colour has none (greys, black).</summary>
        private void SyncFromColor(string? hex)
        {
            if (!ColorMath.TryParseHex(hex, out var rgb) || rgb == CurrentRgb)
                return;

            var (h, s, v) = ColorMath.ToHsv(rgb);
            if (s > 1e-6 && v > 1e-6)
                _h = h;

            _s = s;
            _v = v;
        }

        private void Refresh(bool updateHex)
        {
            var rgb = CurrentRgb;

            HueLayer.Fill = BrushOf(ColorMath.FromHsv(_h, 1, 1));
            CurrentSwatch.Background = BrushOf(rgb);

            if (updateHex && !HexBox.IsKeyboardFocusWithin)
                HexBox.Text = rgb.ToHex();

            PositionThumbs();
        }

        private void PositionThumbs()
        {
            if (SvArea.ActualWidth > 0)
            {
                Canvas.SetLeft(SvThumb, _s * SvArea.ActualWidth - SvThumb.Width / 2);
                Canvas.SetTop(SvThumb, (1 - _v) * SvArea.ActualHeight - SvThumb.Height / 2);
            }

            if (HueArea.ActualWidth > 0)
                Canvas.SetLeft(HueThumb, _h * (HueArea.ActualWidth - HueThumb.Width));
        }

        /// <summary>Applies a user edit: keeps the property in sync without re-deriving HSV, then notifies.</summary>
        private void Commit(bool updateHex = true)
        {
            string hex = CurrentRgb.ToHex();

            _updating = true;
            try { Color = hex; }
            finally { _updating = false; }

            Refresh(updateHex);
            ColorChanged?.Invoke(hex);
        }

        // ---------------------------------------------------------------- saturation / value

        private void SvArea_SizeChanged(object sender, SizeChangedEventArgs e) => PositionThumbs();

        private void SetSvFromPoint(Point p)
        {
            if (SvArea.ActualWidth <= 0 || SvArea.ActualHeight <= 0)
                return;

            _s = Math.Clamp(p.X / SvArea.ActualWidth, 0, 1);
            _v = 1 - Math.Clamp(p.Y / SvArea.ActualHeight, 0, 1);
            Commit();
        }

        private void SvArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            SvArea.Focus();
            SvArea.CaptureMouse();
            _dragSv = true;
            SetSvFromPoint(e.GetPosition(SvArea));
            e.Handled = true;
        }

        private void SvArea_MouseMove(object sender, MouseEventArgs e)
        {
            if (_dragSv && e.LeftButton == MouseButtonState.Pressed)
                SetSvFromPoint(e.GetPosition(SvArea));
        }

        private void SvArea_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _dragSv = false;
            SvArea.ReleaseMouseCapture();
        }

        private void SvArea_KeyDown(object sender, KeyEventArgs e)
        {
            double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.10 : 0.01;

            switch (e.Key)
            {
                case Key.Left: _s -= step; break;
                case Key.Right: _s += step; break;
                case Key.Up: _v += step; break;
                case Key.Down: _v -= step; break;
                default: return;
            }

            _s = Math.Clamp(_s, 0, 1);
            _v = Math.Clamp(_v, 0, 1);
            Commit();
            e.Handled = true;
        }

        // ---------------------------------------------------------------- hue

        private void HueArea_SizeChanged(object sender, SizeChangedEventArgs e) => PositionThumbs();

        private void SetHueFromPoint(Point p)
        {
            double usable = HueArea.ActualWidth - HueThumb.Width;
            if (usable <= 0)
                return;

            _h = Math.Clamp((p.X - HueThumb.Width / 2) / usable, 0, 1);
            Commit();
        }

        private void HueArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            HueArea.Focus();
            HueArea.CaptureMouse();
            _dragHue = true;
            SetHueFromPoint(e.GetPosition(HueArea));
            e.Handled = true;
        }

        private void HueArea_MouseMove(object sender, MouseEventArgs e)
        {
            if (_dragHue && e.LeftButton == MouseButtonState.Pressed)
                SetHueFromPoint(e.GetPosition(HueArea));
        }

        private void HueArea_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _dragHue = false;
            HueArea.ReleaseMouseCapture();
        }

        private void HueArea_KeyDown(object sender, KeyEventArgs e)
        {
            double step = (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10.0 : 1.0) / 360.0;

            switch (e.Key)
            {
                case Key.Left: case Key.Down: _h -= step; break;
                case Key.Right: case Key.Up: _h += step; break;
                case Key.Home: _h = 0; break;
                case Key.End: _h = 1; break;
                default: return;
            }

            _h = Math.Clamp(_h, 0, 1);
            Commit();
            e.Handled = true;
        }

        // ---------------------------------------------------------------- hex

        private void HexBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => HexBox.SelectAll();

        private void HexBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitHex();

        private void HexBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                CommitHex();
                HexBox.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && HexBox.Text != CurrentRgb.ToHex())
            {
                // First Escape reverts the field; the popup closes on the next one.
                HexBox.Text = CurrentRgb.ToHex();
                HexBox.SelectAll();
                e.Handled = true;
            }
        }

        private void CommitHex()
        {
            if (ColorMath.TryParseLooseHex(HexBox.Text, out var rgb))
            {
                if (rgb != CurrentRgb)
                {
                    SyncFromColor(rgb.ToHex());
                    Commit(updateHex: false);
                }

                HexBox.Text = rgb.ToHex();
            }
            else
            {
                HexBox.Text = CurrentRgb.ToHex();
            }
        }

        // ---------------------------------------------------------------- quick swatches

        private void BuildQuickSwatches()
        {
            QuickHost.Items.Clear();

            var colours = (QuickColors ?? Enumerable.Empty<string>())
                .Where(h => ColorMath.TryParseHex(h, out _))
                .Select(h => ColorMath.Parse(h).ToHex())
                .Distinct()
                .Take(8);

            foreach (string hex in colours)
            {
                var button = new Button
                {
                    Width = 24,
                    Height = 24,
                    Margin = new Thickness(0, 0, 6, 6),
                    Padding = new Thickness(0),
                    Cursor = Cursors.Hand,
                    ToolTip = hex,
                    Template = SwatchTemplate(),
                    Background = BrushOf(ColorMath.Parse(hex))
                };
                button.SetValue(AutomationProperties.NameProperty, "Use " + hex);
                button.Click += (_, _) =>
                {
                    SyncFromColor(hex);
                    Commit();
                };

                QuickHost.Items.Add(button);
            }
        }

        private static ControlTemplate SwatchTemplate()
        {
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetResourceReference(Border.CornerRadiusProperty, "ControlCornerRadius");
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorSecondaryBrush");
            border.SetBinding(Border.BackgroundProperty, new Binding(nameof(Background)) { RelativeSource = RelativeSource.TemplatedParent });
            template.VisualTree = border;
            return template;
        }

        // ---------------------------------------------------------------- keyboard

        private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && !e.Handled && !(HexBox.IsKeyboardFocusWithin && HexBox.Text != CurrentRgb.ToHex()))
            {
                CloseRequested?.Invoke();
                e.Handled = true;
            }
        }
    }
}
