using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ZeroStrap.UI.ViewModels.Shell
{
    public enum ShellSection
    {
        Overview,
        Play,
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

            RequestSaveNoticeEvent?.Invoke(this, EventArgs.Empty);
        }

        private void OpenAbout()
        {
            new UI.Elements.About.MainWindow().ShowDialog();
        }
    }
}
