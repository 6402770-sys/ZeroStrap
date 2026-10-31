using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using ZeroStrap.FastFlagCollections;

namespace ZeroStrap.UI.Elements.Dialogs
{
    /// <summary>Asks for the number of logical processors before Pure FPS is prepared. Result is null when cancelled.</summary>
    public partial class LogicalProcessorsDialog
    {
        private const int MinimumValue = 2;

        public int? Result { get; private set; }

        public LogicalProcessorsDialog(int? detected, bool reliable, string flagName)
        {
            InitializeComponent();

            _flagName = flagName;

            if (reliable && detected is int count)
            {
                DetectedText.Text = $"Detected {count} logical processors.";
                CountBox.Text = count.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                DetectedText.Text = "Couldn't detect this reliably. Enter it manually.";
            }

            DataObject.AddPastingHandler(CountBox, CountBox_Pasting);
            UpdateState();

            Loaded += (_, _) =>
            {
                CountBox.Focus();
                CountBox.SelectAll();
            };
        }

        private readonly string _flagName;

        /// <summary>Shows the dialog; null if the user cancelled.</summary>
        public static int? Ask(Window? owner, string flagName)
        {
            var (count, reliable) = LogicalProcessorDetector.Detect();
            var dialog = new LogicalProcessorsDialog(count, reliable, flagName) { Owner = owner };
            dialog.ShowDialog();
            return dialog.Result;
        }

        private bool TryGetValue(out int value)
        {
            value = 0;
            return Int32.TryParse(CountBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= MinimumValue;
        }

        private void UpdateState()
        {
            bool valid = TryGetValue(out int value);

            ContinueButton.IsEnabled = valid;
            ResultText.Text = valid
                ? $"{_flagName} = {(value - 1).ToString(CultureInfo.InvariantCulture)}"
                : $"Enter {MinimumValue} or more.";
        }

        private static bool AllDigits(string text) => text.All(c => c is >= '0' and <= '9');

        private void CountBox_PreviewTextInput(object sender, TextCompositionEventArgs e) => e.Handled = !AllDigits(e.Text);

        private void CountBox_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText) || e.DataObject.GetData(DataFormats.UnicodeText) is not string text || !AllDigits(text))
                e.CancelCommand();
        }

        private void CountBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (ContinueButton is not null)
                UpdateState();
        }

        private void ContinueButton_Click(object sender, RoutedEventArgs e)
        {
            if (!TryGetValue(out int value))
                return;

            Result = value;
            Close();
        }
    }
}
