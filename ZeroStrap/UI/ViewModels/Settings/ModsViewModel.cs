using System.Windows;
using System.Windows.Input;

using Microsoft.Win32;

using Windows.Win32;
using Windows.Win32.UI.Shell;
using Windows.Win32.Foundation;

using CommunityToolkit.Mvvm.Input;

using ZeroStrap.Models.SettingTasks;
using ZeroStrap.AppData;

namespace ZeroStrap.UI.ViewModels.Settings
{
    public class ModsViewModel : NotifyPropertyChangedViewModel
    {
        private static ModsViewModel? _shared;

        /// <summary>
        /// The instance Mods Studio's Library binds to. It is kept for the whole session so a preset changed but not yet saved
        /// still shows as changed when the page is visited again.
        /// </summary>
        public static ModsViewModel Shared => _shared ??= new ModsViewModel();

        private void OpenModsFolder() => Process.Start("explorer.exe", Paths.Modifications);

        /// <summary>
        /// Records that a preset has just been switched off on disk (Mods Studio did it): both the saved and the shown value become
        /// "off", and any pending change for it is dropped. <paramref name="taskName"/> is <c>BuiltinPreset.TaskName</c>.
        /// </summary>
        public void SetPresetOff(string taskName)
        {
            Models.SettingTasks.Base.BaseTask? task = null;

            switch (taskName)
            {
                case "CursorType":
                    CursorTypeTask.OriginalState = default;
                    task = CursorTypeTask;
                    break;
                case "OldCharacterSounds":
                    OldCharacterSoundsTask.OriginalState = false;
                    task = OldCharacterSoundsTask;
                    break;
                case "OldAvatarBackground":
                    OldAvatarBackgroundTask.OriginalState = false;
                    task = OldAvatarBackgroundTask;
                    break;
                case "EmojiFont":
                    EmojiFontTask.OriginalState = default;
                    task = EmojiFontTask;
                    break;
                case "TextFont":
                    TextFontTask.OriginalState = "";
                    task = TextFontTask;
                    break;
            }

            if (task is not null)
                App.PendingSettingTasks.Remove(task.Name);

            RefreshPresets();
        }

        /// <summary>Makes the bindings read the preset values again (they are plain properties of the tasks).</summary>
        public void RefreshPresets()
        {
            OnPropertyChanged(nameof(CursorTypeTask));
            OnPropertyChanged(nameof(OldCharacterSoundsTask));
            OnPropertyChanged(nameof(OldAvatarBackgroundTask));
            OnPropertyChanged(nameof(EmojiFontTask));
            OnPropertyChanged(nameof(TextFontTask));
            OnPropertyChanged(nameof(ChooseCustomFontVisibility));
            OnPropertyChanged(nameof(DeleteCustomFontVisibility));
            OnPropertyChanged(nameof(CustomFontName));
        }

        /// <summary>File name of the chosen custom font, empty when none.</summary>
        public string CustomFontName => String.IsNullOrEmpty(TextFontTask.NewState) ? "" : Path.GetFileName(TextFontTask.NewState);

        private readonly Dictionary<string, byte[]> FontHeaders = new()
        {
            { "ttf", new byte[4] { 0x00, 0x01, 0x00, 0x00 } },
            { "otf", new byte[4] { 0x4F, 0x54, 0x54, 0x4F } },
            { "ttc", new byte[4] { 0x74, 0x74, 0x63, 0x66 } } 
        };

        private void ManageCustomFont()
        {
            if (!String.IsNullOrEmpty(TextFontTask.NewState))
            {
                TextFontTask.NewState = "";
            }
            else
            {
                var dialog = new OpenFileDialog
                {
                    Filter = $"{Strings.Menu_FontFiles}|*.ttf;*.otf;*.ttc"
                };

                if (dialog.ShowDialog() != true)
                    return;

                string type = dialog.FileName.Substring(dialog.FileName.Length-3, 3).ToLowerInvariant();

                if (!FontHeaders.ContainsKey(type) 
                    || !FontHeaders.Any(x => File.ReadAllBytes(dialog.FileName).Take(4).SequenceEqual(x.Value)))
                {
                    Frontend.ShowMessageBox(Strings.Menu_Mods_Misc_CustomFont_Invalid, MessageBoxImage.Error);
                    return;
                }

                TextFontTask.NewState = dialog.FileName;
            }

            OnPropertyChanged(nameof(ChooseCustomFontVisibility));
            OnPropertyChanged(nameof(DeleteCustomFontVisibility));
            OnPropertyChanged(nameof(CustomFontName));
        }

        public ICommand OpenModsFolderCommand => new RelayCommand(OpenModsFolder);

        public Visibility ChooseCustomFontVisibility => !String.IsNullOrEmpty(TextFontTask.NewState) ? Visibility.Collapsed : Visibility.Visible;

        public Visibility DeleteCustomFontVisibility => !String.IsNullOrEmpty(TextFontTask.NewState) ? Visibility.Visible : Visibility.Collapsed;

        public ICommand ManageCustomFontCommand => new RelayCommand(ManageCustomFont);

        public ICommand OpenCompatSettingsCommand => new RelayCommand(OpenCompatSettings);

        public ModPresetTask OldAvatarBackgroundTask { get; } = new("OldAvatarBackground", @"ExtraContent\places\Mobile.rbxl", "OldAvatarBackground.rbxl");

        public ModPresetTask OldCharacterSoundsTask { get; } = new("OldCharacterSounds", new()
        {
            { @"content\sounds\action_footsteps_plastic.mp3", "Sounds.OldWalk.mp3"  },
            { @"content\sounds\action_jump.mp3",              "Sounds.OldJump.mp3"  },
            { @"content\sounds\action_get_up.mp3",            "Sounds.OldGetUp.mp3" },
            { @"content\sounds\action_falling.mp3",           "Sounds.Empty.mp3"    },
            { @"content\sounds\action_jump_land.mp3",         "Sounds.Empty.mp3"    },
            { @"content\sounds\action_swim.mp3",              "Sounds.Empty.mp3"    },
            { @"content\sounds\impact_water.mp3",             "Sounds.Empty.mp3"    }
        });

        public EmojiModPresetTask EmojiFontTask { get; } = new();

        public EnumModPresetTask<Enums.CursorType> CursorTypeTask { get; } = new("CursorType", new()
        {
            {
                Enums.CursorType.From2006, new()
                {
                    { @"content\textures\Cursors\KeyboardMouse\ArrowCursor.png",    "Cursor.From2006.ArrowCursor.png"    },
                    { @"content\textures\Cursors\KeyboardMouse\ArrowFarCursor.png", "Cursor.From2006.ArrowFarCursor.png" }
                }
            },
            {
                Enums.CursorType.From2013, new()
                {
                    { @"content\textures\Cursors\KeyboardMouse\ArrowCursor.png",    "Cursor.From2013.ArrowCursor.png"    },
                    { @"content\textures\Cursors\KeyboardMouse\ArrowFarCursor.png", "Cursor.From2013.ArrowFarCursor.png" }
                }
            }
        });

        public FontModPresetTask TextFontTask { get; } = new();

        private void OpenCompatSettings()
        {
            string path = new RobloxPlayerData().ExecutablePath;

            if (File.Exists(path))
                PInvoke.SHObjectProperties(HWND.Null, SHOP_TYPE.SHOP_FILEPATH, path, "Compatibility");
            else
                Frontend.ShowMessageBox(Strings.Common_RobloxNotInstalled, MessageBoxImage.Error);

        }
    }
}
