using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ZeroStrap.UI.ViewModels.Shell
{
    /// <summary>A section plus, for Configure, which tab to open.</summary>
    public readonly record struct ShellTarget(ShellSection Section, int Tab = 0);

    public enum ShellSection
    {
        Overview,
        Configure,
        Appearance,
        System
    }

    public partial class ShellViewModel : ObservableObject
    {
        [ObservableProperty]
        private ShellSection _currentSection = ShellSection.Overview;

        [ObservableProperty]
        private string _searchText = string.Empty;

        public event EventHandler? RequestSaveNoticeEvent;
        public event EventHandler? RequestCloseWindowEvent;

        public IRelayCommand SaveSettingsCommand => new RelayCommand(SaveSettings);
        public IRelayCommand CloseWindowCommand => new RelayCommand(() => RequestCloseWindowEvent?.Invoke(this, EventArgs.Empty));
        public IRelayCommand OpenAboutCommand => new RelayCommand(OpenAbout);

        private void SaveSettings()
        {
            App.FastFlags.Save();
            App.Settings.Save();
            App.State.Save();

            foreach (var task in App.PendingSettingTasks.Values)
                task.Execute();

            App.PendingSettingTasks.Clear();

            UI.Utility.ActivityLog.Record(UI.Utility.ActivityType.Configuration, "Configuration saved", "Settings and Fast Flags written to disk");

            RequestSaveNoticeEvent?.Invoke(this, EventArgs.Empty);
        }

        private void OpenAbout()
        {
            new UI.Elements.About.MainWindow().ShowDialog();
        }
    }
}
