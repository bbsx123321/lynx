using System.Collections.ObjectModel;
using PadShift.App.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PadShift.App.Services;
using PadShift.Core.Profile;

namespace PadShift.App.ViewModels;

/// <summary>
/// Scans the profiles root for <c>**\*.rewasd</c> and groups the files by their top-level folder,
/// which is how a the reference tool library is organised: one folder per game, one file per config.
/// When the sibling <c>Presets</c> folder exists (the the reference tool layout) its files are shown as the
/// 「预设」 group and can be applied with the same double-click as a game config.
/// </summary>
public sealed class ConfigLibraryViewModel : ObservableObject
{
    private string _rootPath;
    private string _presetsRoot;
    private string? _message;

    public ConfigLibraryViewModel(string? rootPath, string? presetsRoot = null)
    {
        _rootPath = (rootPath ?? "").Trim();
        _presetsRoot = string.IsNullOrWhiteSpace(presetsRoot)
            ? ProfileTransfer.DerivePresetsRoot(_rootPath)
            : presetsRoot!.Trim();
        RefreshCommand = new RelayCommand(Refresh);
    }

    public ObservableCollection<ProfileGroupViewModel> Games { get; } = new();

    public RelayCommand RefreshCommand { get; }

    public string RootPath
    {
        get => _rootPath;
        private set => SetProperty(ref _rootPath, value);
    }

    /// <summary>Global preset folder (<c>ProfilesRoot\..\Presets</c>); empty when it cannot be derived.</summary>
    public string PresetsRoot
    {
        get => _presetsRoot;
        private set => SetProperty(ref _presetsRoot, value);
    }

    public string? Message
    {
        get => _message;
        private set
        {
            if (SetProperty(ref _message, value))
            {
                OnPropertyChanged(nameof(HasMessage));
            }
        }
    }

    public bool HasMessage => !string.IsNullOrWhiteSpace(_message);

    public bool IsEmpty => Games.Count == 0;

    public string CountText => Loc.F("lib.countF", Games.Count, Games.Sum(game => game.Profiles.Count));

    /// <summary>Rescans the folder. Never throws: an unreadable folder is reported in <see cref="Message"/>.</summary>
    public void Refresh()
    {
        Games.Clear();
        Message = null;

        try
        {
            if (string.IsNullOrWhiteSpace(_rootPath))
            {
                Message = Loc.T("lib.noroot");
            }
            else if (!Directory.Exists(_rootPath))
            {
                Message = Loc.F("lib.nodirF", _rootPath);
            }
            else
            {
                var groups = new Dictionary<string, ProfileGroupViewModel>(StringComparer.OrdinalIgnoreCase);
                var files = new List<string>(Directory.EnumerateFiles(_rootPath, "*.rewasd", SearchOption.AllDirectories));
                files.Sort(StringComparer.OrdinalIgnoreCase);

                foreach (string file in files)
                {
                    string game = TopFolderOf(file);
                    if (!groups.TryGetValue(game, out ProfileGroupViewModel? group))
                    {
                        group = new ProfileGroupViewModel(game);
                        groups[game] = group;
                    }

                    group.Profiles.Add(CreateItem(file, game));
                }

                foreach (ProfileGroupViewModel group in groups.Values.OrderBy(
                             candidate => candidate.Name,
                             StringComparer.OrdinalIgnoreCase))
                {
                    Games.Add(group);
                }

                AddPresets();

                if (Games.Count == 0)
                {
                    Message = Loc.T("lib.empty");
                }
            }
        }
        catch (Exception ex)
        {
            Message = Loc.T("lib.scanfail") + ex.Message;
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CountText));
    }

    /// <summary>Adds the preset group first when the sibling <c>Presets</c> folder exists and has files.</summary>
    private void AddPresets()
    {
        if (string.IsNullOrWhiteSpace(_presetsRoot) || !Directory.Exists(_presetsRoot))
        {
            return;
        }

        var group = new ProfileGroupViewModel(ProfileTransfer.PresetGroupName, isPreset: true);
        var files = new List<string>(Directory.EnumerateFiles(_presetsRoot, "*.rewasd", SearchOption.AllDirectories));
        files.Sort(StringComparer.OrdinalIgnoreCase);

        foreach (string file in files)
        {
            group.Profiles.Add(CreateItem(file, group.Name));
        }

        if (group.Profiles.Count > 0)
        {
            Games.Insert(0, group);
        }
    }

    /// <summary>First path segment under the root, or <c>(根目录)</c> for files sitting directly in it.</summary>
    private string TopFolderOf(string file)
    {
        string relative = System.IO.Path.GetRelativePath(_rootPath, file);
        int separator = relative.IndexOf(System.IO.Path.DirectorySeparatorChar);
        return separator > 0 ? relative[..separator] : Loc.T("lib.root");
    }

    private static ProfileItem CreateItem(string file, string game)
    {
        // 游戏封面遵循 the reference tool 约定：游戏目录下的 IcoGame.png。
        string? icon = null;
        try
        {
            string? dir = System.IO.Path.GetDirectoryName(file);
            if (dir is not null)
            {
                string candidate = System.IO.Path.Combine(dir, "IcoGame.png");
                icon = System.IO.File.Exists(candidate) ? candidate : null;
            }
        }
        catch (Exception)
        {
            // 封面纯属装饰，读不到就隐藏。
        }

        try
        {
            GamepadProfile profile = ProfileIo.Load(file);
            ProfileConfig? config = profile.Config;
            string? description = config?.Description is { Length: > 0 } text ? FirstLine(text, 80) : null;
            return new ProfileItem(file, game, config?.AppName, null, icon, description);
        }
        catch (Exception ex)
        {
            return new ProfileItem(file, game, null, Loc.T("lib.readfail") + ex.Message, icon, null);
        }
    }

    private static readonly char[] NewLineChar = { '\n', '\r' };

    private static string FirstLine(string text, int maxLength)
    {
        string line = text.Split(NewLineChar)[0].Trim();
        return line.Length <= maxLength ? line : line[..maxLength] + "…";
    }
}
