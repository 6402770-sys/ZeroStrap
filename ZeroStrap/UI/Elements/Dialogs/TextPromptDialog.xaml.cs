using System.Windows;

namespace ZeroStrap.UI.Elements.Dialogs
{
    /// <summary>Single-line text input (preset names).</summary>
    public partial class TextPromptDialog
    {
        public string? Result { get; private set; }

        public TextPromptDialog(string title, string prompt, string initial = "")
        {
            InitializeComponent();

            Title = title;
            Caption.Content = title;
            PromptText.Text = prompt;
            InputTextBox.Text = initial;
            OkButton.IsEnabled = !String.IsNullOrWhiteSpace(initial);

            Loaded += (_, _) =>
            {
                InputTextBox.Focus();
                InputTextBox.SelectAll();
            };
        }

        public static string? Ask(Window? owner, string title, string prompt, string initial = "")
        {
            var dialog = new TextPromptDialog(title, prompt, initial) { Owner = owner };
            dialog.ShowDialog();
            return dialog.Result;
        }

        private void InputTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
            OkButton.IsEnabled = !String.IsNullOrWhiteSpace(InputTextBox.Text);

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            Result = InputTextBox.Text.Trim();
            Close();
        }
    }
}
