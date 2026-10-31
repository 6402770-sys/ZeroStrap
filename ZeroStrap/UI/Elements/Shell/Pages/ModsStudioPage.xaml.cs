using System;
using System.Windows;
using System.Windows.Controls;

namespace ZeroStrap.UI.Elements.Shell.Pages
{
    public partial class ModsStudioPage : Page
    {
        /// <summary>Raised when the user asks for the current Configure › Mods page.</summary>
        public event EventHandler? OpenLegacyModsRequested;

        public ModsStudioPage()
        {
            InitializeComponent();
        }

        private void OpenLegacyMods_Click(object sender, RoutedEventArgs e) => OpenLegacyModsRequested?.Invoke(this, EventArgs.Empty);
    }
}
