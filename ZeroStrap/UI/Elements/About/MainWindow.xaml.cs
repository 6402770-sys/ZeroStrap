using ZeroStrap.UI.Elements.Shell.Pages;

namespace ZeroStrap.UI.Elements.About
{
    /// <summary>
    /// Standalone host for the shell's About page, used by the launch menu and the legacy settings window.
    /// </summary>
    public partial class MainWindow
    {
        public MainWindow()
        {
            InitializeComponent();

            App.Logger.WriteLine("MainWindow", "Initializing about window");

            RootFrame.Navigate(new AboutPage());
        }
    }
}
