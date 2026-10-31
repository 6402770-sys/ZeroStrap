using System.Windows;

namespace ZeroStrap.UI.Elements.Dialogs
{
    /// <summary>Confirms replacing the whole Fast Flag configuration with a collection. Cancel is the default.</summary>
    public partial class ReplaceFlagsDialog
    {
        /// <summary>Flags that live in this page's Engine settings.</summary>
        public static readonly IReadOnlyList<string> EngineFlags = new[]
        {
            "FIntDebugForceMSAASamples",
            "DFIntTextureQualityOverride",
            "DFFlagTextureQualityOverrideEnabled",
            "DFFlagDisableDPIScale"
        };

        public bool Confirmed { get; private set; }

        /// <param name="resolvedLine">Shown for Pure FPS, e.g. "DFIntRuntimeConcurrency = 15".</param>
        public ReplaceFlagsDialog(int currentCount, string name, int count, bool touchesEngineSettings, string? resolvedLine)
        {
            InitializeComponent();

            Title = "Replace your Fast Flags?";

            BodyText.Text = $"Your current configuration ({Plural(currentCount)}) will be replaced by {name} ({Plural(count)}). Flags that aren't in {name} are removed.";
            EngineText.Visibility = touchesEngineSettings ? Visibility.Visible : Visibility.Collapsed;

            if (resolvedLine is not null)
            {
                ResolvedText.Text = resolvedLine;
                ResolvedText.Visibility = Visibility.Visible;
            }

            Loaded += (_, _) => CancelButton.Focus();
        }

        public static string Plural(int count) => count == 1 ? "1 flag" : $"{count} flags";

        public static bool Ask(Window? owner, int currentCount, string name, int count, bool touchesEngineSettings, string? resolvedLine)
        {
            var dialog = new ReplaceFlagsDialog(currentCount, name, count, touchesEngineSettings, resolvedLine) { Owner = owner };
            dialog.ShowDialog();
            return dialog.Confirmed;
        }

        private void ReplaceButton_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = true;
            Close();
        }
    }
}
