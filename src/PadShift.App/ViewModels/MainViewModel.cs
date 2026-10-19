using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PadShift.App.Infrastructure;
using PadShift.App.Services;
using PadShift.Core.Host;
using PadShift.Core.Profile;

namespace PadShift.App.ViewModels;

/// <summary>
/// The main window's state: device selector, 4 slots, the global remap toggle, the config library
/// and the binding editor. Everything here talks to <see cref="IRemapHost"/> only, so the whole
/// screen can be driven from a test without a window.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly IRemapHost _host;
    private readonly SynchronizationContext? _syncContext;
    private readonly SlotItem[] _slots;
    private readonly RadialMacroPlayer _macroPlayer = new();

    private ControllerItem? _selectedController;
    private SlotItem? _selectedSlot;
    private bool _remapEnabled;

    // 失焦自动停映射：前台进程离开当前配置的 processNames 时自动 DisableRemap。
    private ForegroundWatcher? _foregroundWatcher;
    private string _lastProcessNamesPath = "";
    private HashSet<string> _currentProcessNames = new(StringComparer.OrdinalIgnoreCase);
    private bool _suppressSlotWrite;
    private bool _disposed;
    private string _statusText = Loc.T("m.init");
    private string _currentConfigText = Loc.T("m.confignone");
    private string _currentLayerText = Loc.F("m.currentlayerF", Loc.T("m.mainlayer"));
    private string? _banner;
    private string? _inlineMessage;
    private string _activeLayerName = Loc.T("m.mainlayer");
    private string _apiStatus = "";
    private string? _lastAppliedProfilePath;

    public MainViewModel(IRemapHost host, string profilesRoot)
        : this(host, new ConfigLibraryViewModel(profilesRoot), new GamepadViewModel(), new BindingEditorViewModel())
    {
    }

    public MainViewModel(
        IRemapHost host,
        ConfigLibraryViewModel library,
        GamepadViewModel gamepad,
        BindingEditorViewModel bindingEditor,
        PreferencesViewModel? preferences = null,
        ConfirmationService? confirmation = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        Library = library ?? throw new ArgumentNullException(nameof(library));
        Gamepad = gamepad ?? throw new ArgumentNullException(nameof(gamepad));
        BindingEditor = bindingEditor ?? throw new ArgumentNullException(nameof(bindingEditor));
        Preferences = preferences ?? new PreferencesViewModel(new AppSettings());
        Confirmation = confirmation;
        _syncContext = SynchronizationContext.Current;

        _slots = Enumerable.Range(0, 4).Select(index => new SlotItem(index)).ToArray();
        _selectedSlot = _slots[0];
        _selectedSlot.IsSelected = true;

        RefreshCommand = new RelayCommand(Refresh);
        RefreshDevicesCommand = new RelayCommand(Refresh);
        SelectSlotCommand = new RelayCommand<SlotItem>(SelectSlot);
        ToggleRemapCommand = new RelayCommand(ToggleRemap);
        SaveBindingCommand = new RelayCommand(SaveBinding);
        ClearBindingCommand = new RelayCommand(ClearBinding);

        Gamepad.ControlSelected += OnControlSelected;
        Gamepad.AttachHost(_host);
        _host.Event += OnHostEvent;

        // The pad preselects a control before the panel exists; mirror that selection.
        if (Gamepad.SelectedControl is { } initial)
        {
            BindingEditor.SelectControl(initial);
        }

        Library.Refresh();
        Refresh();
    }

    // ------------------------------------------------------------------ composition

    public ObservableCollection<ControllerItem> Controllers { get; } = new();

    public IReadOnlyList<SlotItem> Slots => _slots;

    public ConfigLibraryViewModel Library { get; }

    public GamepadViewModel Gamepad { get; }

    public BindingEditorViewModel BindingEditor { get; }

    /// <summary>Preferences window state (general / slots / configs / radial / overlay / confirmations).</summary>
    public PreferencesViewModel Preferences { get; }

    /// <summary>Reusable "不再询问" gate; null in head-less tests so no dialog is ever shown.</summary>
    public ConfirmationService? Confirmation { get; }

    /// <summary>The engine façade; exposed for feature dialogs (gyro wizard) built by the view layer.</summary>
    public IRemapHost Host => _host;

    /// <summary>Raised by the radial menu's "打开配置" sector.</summary>
    public event EventHandler? PreferencesRequested;

    /// <summary>Raised when the host watchdog stopped remapping because output was looping.</summary>
    public event EventHandler<string>? OutputLoopDetected;

    public RelayCommand RefreshCommand { get; }

    public RelayCommand RefreshDevicesCommand { get; }

    public RelayCommand<SlotItem> SelectSlotCommand { get; }

    public RelayCommand ToggleRemapCommand { get; }

    public RelayCommand SaveBindingCommand { get; }

    public RelayCommand ClearBindingCommand { get; }

    // ------------------------------------------------------------------ state

    public ControllerItem? SelectedController
    {
        get => _selectedController;
        set
        {
            if (SetProperty(ref _selectedController, value))
            {
                SyncFromHost();
            }
        }
    }

    public SlotItem? SelectedSlot
    {
        get => _selectedSlot;
        set
        {
            if (!SetProperty(ref _selectedSlot, value) || value is null)
            {
                return;
            }

            UpdateSlotSelection();

            if (!_suppressSlotWrite && SelectedController is { } controller)
            {
                try
                {
                    _host.SelectSlot(controller.Id, value.Index);
                }
                catch (Exception ex)
                {
                    SetInlineMessage(Loc.T("m.slotfail") + ex.Message);
                }
            }

            RefreshStatus();
        }
    }

    /// <summary>Global remap switch. Writing it calls <see cref="IRemapHost.EnableRemap"/> / <see cref="IRemapHost.DisableRemap"/>.</summary>
    public bool RemapEnabled
    {
        get => _remapEnabled;
        set
        {
            EnsureFocusLossWatcher();
            if (_remapEnabled == value)
            {
                return;
            }

            if (!value && _remapEnabled && Confirmation is not null &&
                !Confirmation.Confirm(
                    ConfirmationKeys.DisableRemap,
                    Loc.T("m.disableTitle"),
                    Loc.T("m.disableMsg"),
                    Loc.T("m.disableOk")))
            {
                SetInlineMessage(Loc.T("m.disableCancel"));
                OnPropertyChanged(nameof(RemapEnabled));
                return;
            }

            if (!SetProperty(ref _remapEnabled, value))
            {
                return;
            }

            try
            {
                if (value)
                {
                    if (!TryEnableRemap(out string? message))
                    {
                        SetInlineMessage(message);
                        _remapEnabled = false;
                        OnPropertyChanged(nameof(RemapEnabled));
                    }
                    else
                    {
                        SetInlineMessage(message);
                    }
                }
                else
                {
                    _host.DisableRemap(SelectedController?.Id);
                    SetInlineMessage(null);
                }
            }
            catch (ObjectDisposedException ex)
            {
                SetInlineMessage((value ? Loc.T("m.enableFailPfx") : Loc.T("m.disableFailPfx")) +
                                 Loc.T("m.servicestopped") +
                                 "（" + ex.Message + "）");
                LogRemapToggle((value ? "enable" : "disable") + " failed: host disposed: " + ex.Message);
                _remapEnabled = false;
                OnPropertyChanged(nameof(RemapEnabled));
            }
            catch (Exception ex)
            {
                SetInlineMessage((value ? Loc.T("m.enableFailPfx") : Loc.T("m.disableFailPfx")) + ex.Message);
                LogRemapToggle((value ? "enable" : "disable") + " threw: " + ex);
                _remapEnabled = TryGet(() => _host.RemapEnabled, false);
                OnPropertyChanged(nameof(RemapEnabled));
            }

            RefreshStatus();
        }
    }

    /// <summary>
    /// Enables remapping for the selected controller. A config must be live on the current slot;
    /// when none is, the last-used config, the config currently selected in the library, or (as a
    /// last resort) the first readable config is applied first and the status line says so. When
    /// there is nothing to apply the toggle stays off and <paramref name="message"/> explains why.
    /// </summary>
    private bool TryEnableRemap(out string? message)
    {
        message = null;

        if (SelectedController is not { } controller)
        {
            message = Loc.T("m.nocontroller");
            LogRemapToggle("enable failed: no controller selected.");
            return false;
        }

        Guid controllerId = controller.Id;
        int slot = SelectedSlot?.Index ?? 0;

        bool hasConfig;
        try
        {
            hasConfig = _host.GetAppliedConfigs(controllerId, slot).Count > 0;
        }
        catch (Exception ex)
        {
            message = Loc.F("m.slotReadFailF", ex.Message);
            LogRemapToggle("enable failed: reading applied configs threw: " + ex);
            return false;
        }

        string? autoAppliedName = null;
        bool nothingToApply = false;
        if (!hasConfig)
        {
            string? path = ResolveProfileForToggle();
            if (path is not null)
            {
                ApplyConfigResult applied = ApplyConfigBeforeEnable(controllerId, path, slot);
                if (!applied.Success)
                {
                    message = Loc.F("m.autoapplyFailF", System.IO.Path.GetFileNameWithoutExtension(path), ExplainError(applied.Error));
                    LogRemapToggle("enable failed: auto-apply '" + path + "' failed: " + applied.Error);
                    return false;
                }

                autoAppliedName = System.IO.Path.GetFileNameWithoutExtension(path);
                _lastAppliedProfilePath = path;
                LogRemapToggle("auto-applied '" + path + "' to slot " + slot + " before enabling.");
            }
            else
            {
                // Nothing to apply automatically: let the host answer. Its failure (usually
                // "no profile applied") is turned into the actionable message below.
                nothingToApply = true;
                LogRemapToggle("no config applied to slot " + slot + " and no config selected; asking the host.");
            }
        }

        EnableRemapResult result = _host.EnableRemap(controllerId);
        if (!result.Success)
        {
            message = Loc.T("m.enableFailPfx") + ExplainError(result.Error);
            if (nothingToApply && Library.Games.Count == 0)
            {
                message += Loc.T("m.libempty2");
            }

            LogRemapToggle("enable failed: " + result.Error);
            return false;
        }

        if (autoAppliedName is not null)
        {
            message = Loc.F("m.autoappliedF", autoAppliedName, slot + 1);
            LogRemapToggle("enabled remap after applying '" + autoAppliedName + "'.");
        }

        return true;
    }

    /// <summary>
    /// Last used config → config selected in the library (single click loads it into the binding
    /// panel) → first readable config. Null when the library has no usable config at all.
    /// </summary>
    private string? ResolveProfileForToggle()
    {
        if (!string.IsNullOrWhiteSpace(_lastAppliedProfilePath) && File.Exists(_lastAppliedProfilePath))
        {
            return _lastAppliedProfilePath;
        }

        string? selected = BindingEditor.ProfilePath;
        if (!string.IsNullOrWhiteSpace(selected) && File.Exists(selected))
        {
            return selected;
        }

        foreach (ProfileGroupViewModel game in Library.Games)
        {
            foreach (ProfileItem item in game.Profiles)
            {
                if (item.IsValid)
                {
                    return item.Path;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Applies a config from the synchronous toggle path without deadlocking the UI thread: the
    /// host call runs on the pool and is bounded, so a wedged host surfaces as a message instead
    /// of a frozen window.
    /// </summary>
    private ApplyConfigResult ApplyConfigBeforeEnable(Guid controllerId, string path, int slot)
    {
        try
        {
            Task<ApplyConfigResult> task = Task.Run(() => _host.ApplyConfigAsync(controllerId, path, slot));
            if (!task.Wait(TimeSpan.FromSeconds(5)))
            {
                return new ApplyConfigResult(false, Loc.T("m.servicetimeout"), slot);
            }

            return task.Result;
        }
        catch (Exception ex)
        {
            Exception actual = ex is AggregateException aggregate && aggregate.InnerException is not null
                ? aggregate.InnerException
                : ex;
            return new ApplyConfigResult(false, actual.Message, slot);
        }
    }

    /// <summary>Translates the host's terse English error codes into an actionable Chinese reason.</summary>
    private string ExplainError(string? error)
    {
        string raw = (error ?? "").Trim();
        string lower = raw.ToLowerInvariant();
        string reason;

        if (lower.Contains("no profile applied"))
        {
            reason = Loc.T("m.slotempty");
        }
        else if (lower.Contains("unknown controller"))
        {
            reason = Loc.T("m.disconnected");
        }
        else if (lower.Contains("host disposed") || raw.Contains(Loc.T("m.nodevices")))
        {
            reason = Loc.T("m.servicestopped");
        }
        else if (raw.Length == 0)
        {
            reason = Loc.T("m.unknownerr");
        }
        else
        {
            reason = raw;
        }

        string? outputProblem = TryGet(() => _host.OutputProblem, null);
        if (!string.IsNullOrWhiteSpace(outputProblem))
        {
            reason += "  " + outputProblem;
        }

        return reason;
    }

    private void EnsureFocusLossWatcher()
    {
        _foregroundWatcher ??= new ForegroundWatcher(OnForegroundProcess);
    }

    /// <summary>当前配置的关联进程集合（缓存于配置路径，避免每秒读盘）。</summary>
    private HashSet<string> CurrentProcessNames()
    {
        string? path = BindingEditor.ProfilePath;
        if (string.IsNullOrEmpty(path) || path == _lastProcessNamesPath)
        {
            return _currentProcessNames;
        }

        _lastProcessNamesPath = path;
        _currentProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            List<string>? names = ProfileIo.Load(path).Config?.ProcessNames;
            if (names is not null)
            {
                foreach (string name in names)
                {
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        _currentProcessNames.Add(name.Trim());
                    }
                }
            }
        }
        catch (Exception)
        {
            // 读不到就当没有关联进程：不触发失焦停映射。
        }

        return _currentProcessNames;
    }

    private void OnForegroundProcess(string foregroundName)
    {
        try
        {
            if (!RemapEnabled || !Preferences.Settings.TurnRemapOffOnFocusLoss)
            {
                return;
            }

            HashSet<string> names = CurrentProcessNames();
            if (names.Count > 0 && !names.Contains(foregroundName))
            {
                if (ToggleRemapCommand.CanExecute(null))
                {
                    ToggleRemapCommand.Execute(null);
                    SetInlineMessage(Loc.F("m.autodisabled", foregroundName));
                    LogRemapToggle("auto-disabled on focus loss -> " + foregroundName);
                }
            }
        }
        catch (Exception)
        {
            // 失焦监测绝不影响主流程。
        }
    }

    private static void LogRemapToggle(string message)
    {
        string line = Loc.T("m.togglePrefix") + message;
        System.Diagnostics.Debug.WriteLine(line);
        Console.Error.WriteLine(line);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string CurrentConfigText
    {
        get => _currentConfigText;
        private set => SetProperty(ref _currentConfigText, value);
    }

    public string CurrentLayerText
    {
        get => _currentLayerText;
        private set => SetProperty(ref _currentLayerText, value);
    }

    /// <summary>Non-blocking banner (missing ViGEmBus, API failure, ...). Empty hides it.</summary>
    public string? Banner
    {
        get => _banner;
        private set
        {
            if (SetProperty(ref _banner, value))
            {
                OnPropertyChanged(nameof(HasBanner));
            }
        }
    }

    public bool HasBanner => !string.IsNullOrWhiteSpace(_banner);

    public string? InlineMessage
    {
        get => _inlineMessage;
        private set
        {
            if (SetProperty(ref _inlineMessage, value))
            {
                OnPropertyChanged(nameof(HasInlineMessage));
            }
        }
    }

    public bool HasInlineMessage => !string.IsNullOrWhiteSpace(_inlineMessage);

    public string ApiStatus
    {
        get => _apiStatus;
        private set => SetProperty(ref _apiStatus, value);
    }

    public void SetBanner(string? text) => Banner = text;

    public void SetApiStatus(string text) => ApiStatus = text;

    public void SetInlineMessage(string? text) => InlineMessage = text;

    // ------------------------------------------------------------------ commands

    private void SelectSlot(SlotItem? slot)
    {
        if (slot is not null)
        {
            SelectedSlot = slot;
        }
    }

    private void ToggleRemap() => RemapEnabled = !RemapEnabled;

    private void SaveBinding()
    {
        BindingEditor.Save();
        SetInlineMessage(BindingEditor.Message);
        RefreshStatus();
    }

    private void ClearBinding()
    {
        BindingEditor.ClearBinding();
        SetInlineMessage(BindingEditor.Message);
        RefreshStatus();
    }

    // ------------------------------------------------------------------ host sync

    /// <summary>Re-reads controllers, slot and remap state from the host.</summary>
    public void Refresh()
    {
        try
        {
            IReadOnlyList<HostController> controllers = _host.Controllers;

            var live = new HashSet<Guid>(controllers.Select(controller => controller.Id));
            for (int index = Controllers.Count - 1; index >= 0; index--)
            {
                if (!live.Contains(Controllers[index].Id))
                {
                    Controllers.RemoveAt(index);
                }
            }

            foreach (HostController controller in controllers)
            {
                if (Controllers.Any(item => item.Id == controller.Id))
                {
                    continue;
                }

                Controllers.Add(new ControllerItem(controller));
            }

            if (SelectedController is null || !Controllers.Any(item => item.Id == SelectedController.Id))
            {
                _selectedController = Controllers.FirstOrDefault();
                OnPropertyChanged(nameof(SelectedController));
            }
        }
        catch (Exception ex)
        {
            SetInlineMessage(Loc.T("m.readpadsfail") + ex.Message);
        }

        SyncFromHost();
    }

    /// <summary>Loads a profile into the binding panel without touching the host (single click).</summary>
    public void PreviewProfile(ProfileItem? item)
    {
        if (item is null)
        {
            return;
        }

        if (!item.IsValid)
        {
            SetInlineMessage(item.LoadError ?? Loc.T("m.unreadable"));
            return;
        }

        BindingEditor.LoadProfile(item.Path);
    }

    /// <summary>
    /// Loads <paramref name="item"/> into the binding panel and applies it to the current slot of
    /// the selected controller. The profile stays editable even when no controller is connected.
    /// </summary>
    public async Task<bool> ApplyProfileAsync(ProfileItem? item)
    {
        if (item is null)
        {
            return false;
        }

        if (!BindingEditor.LoadProfile(item.Path))
        {
            SetInlineMessage(Loc.T("m.loadfail") + (BindingEditor.Message ?? item.Path));
            return false;
        }

        if (SelectedController is not { } controller)
        {
            SetInlineMessage(Loc.T("m.appliedNoPad"));
            return false;
        }

        int slot = SelectedSlot?.Index ?? 0;
        try
        {
            ApplyConfigResult result = await _host.ApplyConfigAsync(controller.Id, item.Path, slot).ConfigureAwait(true);
            if (!result.Success)
            {
                SetInlineMessage(Loc.T("m.applyfailPfx") + (result.Error ?? Loc.T("m.unknown")));
                return false;
            }

            SetInlineMessage(null);
            _activeLayerName = Loc.T("m.mainlayer");
            _lastAppliedProfilePath = item.Path;
            SyncFromHost();
            return true;
        }
        catch (Exception ex)
        {
            SetInlineMessage(Loc.T("m.applyfailPfx") + ex.Message);
            return false;
        }
    }

    // ------------------------------------------------------------------ import / export / presets

    /// <summary>Copies a foreign <c>.rewasd</c> into <c>ProfilesRoot\&lt;name&gt;\Controller\</c>.</summary>
    public bool ImportConfig(string sourceFile, string? name = null)
    {
        string requestedName = name ?? SuggestImportName(sourceFile) ?? System.IO.Path.GetFileNameWithoutExtension(sourceFile);
        if (Confirmation is not null && !string.IsNullOrWhiteSpace(requestedName))
        {
            bool exists;
            try
            {
                exists = Directory.Exists(System.IO.Path.Combine(Library.RootPath, requestedName));
            }
            catch (Exception)
            {
                exists = false;
            }

            if (exists &&
                !Confirmation.Confirm(
                    ConfirmationKeys.ImportDuplicate,
                    Loc.T("conf.importdup"),
                    Loc.F("m.importdupmsg", requestedName),
                    Loc.T("m.importok")))
            {
                SetInlineMessage(Loc.T("m.importcancel"));
                return false;
            }
        }

        ImportResult result = ProfileTransfer.Import(sourceFile, Library.RootPath, name ?? SuggestImportName(sourceFile));
        if (!result.Success)
        {
            SetInlineMessage(Loc.T("m.importfail") + (result.Error ?? Loc.T("m.unknown")));
            return false;
        }

        Library.Refresh();
        SetInlineMessage(Loc.F("m.importedF", result.Name ?? sourceFile));
        return true;
    }

    /// <summary>
    /// the v2.6 protocol exports are often named <c>Config.rewasd</c>; in that case the profile's app name is a
    /// much better folder name than "Config". Returns null to keep the file name.
    /// </summary>
    private static string? SuggestImportName(string sourceFile)
    {
        string baseName = System.IO.Path.GetFileNameWithoutExtension(sourceFile);
        if (!string.Equals(baseName, "Config", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            string? appName = ProfileIo.Load(sourceFile).Config?.AppName;
            return string.IsNullOrWhiteSpace(appName) ? null : appName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Writes the currently loaded profile (with unsaved edits) to the chosen file.</summary>
    public bool ExportConfig(string destinationFile)
    {
        if (BindingEditor.Profile is not { } profile)
        {
            SetInlineMessage(Loc.T("m.loadfirstExport2"));
            return false;
        }

        ExportResult result = ProfileTransfer.Export(profile, destinationFile);
        if (!result.Success)
        {
            SetInlineMessage(Loc.T("m.exportfail") + (result.Error ?? "未知错误"));
            return false;
        }

        SetInlineMessage(Loc.T("m.exportedF") + System.IO.Path.GetFileName(destinationFile));
        return true;
    }

    /// <summary>Copies the currently loaded profile into the sibling <c>Presets</c> folder.</summary>
    public bool SaveCurrentAsPreset(string? name = null)
    {
        if (BindingEditor.Profile is not { } profile)
        {
            SetInlineMessage(Loc.T("m.loadfirstPreset2"));
            return false;
        }

        PresetResult result = ProfileTransfer.SaveAsPreset(profile, Library.PresetsRoot, name);
        if (!result.Success)
        {
            SetInlineMessage(Loc.T("m.presetfail") + (result.Error ?? "未知错误"));
            return false;
        }

        Library.Refresh();
        SetInlineMessage(Loc.T("m.presetedF") + (result.Name ?? ""));
        return true;
    }

    /// <summary>
    /// Deletes the profile currently open in the binding panel (with a confirmation), prunes the
    /// now-empty Controller/game folders and unloads the editor. Refuses paths outside the library.
    /// </summary>
    public bool DeleteCurrentProfile()
    {
        string? path = BindingEditor.ProfilePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            SetInlineMessage(Loc.T("m.loadfirstDelete2"));
            return false;
        }

        if (!IsInsideLibrary(path))
        {
            SetInlineMessage(Loc.T("m.outsidelib2"));
            return false;
        }

        if (Confirmation is not null &&
            !Confirmation.Confirm(
                ConfirmationKeys.DeleteProfile,
                Loc.T("conf.delete"),
                Loc.F("m.deleteconfirmF", System.IO.Path.GetFileName(path)),
                Loc.T("conf.delete")))
        {
            SetInlineMessage(Loc.T("m.deletecancel"));
            return false;
        }

        try
        {
            File.Delete(path);

            string? controllerFolder = System.IO.Path.GetDirectoryName(path);
            TryDeleteEmptyFolder(controllerFolder);
            string? gameFolder = controllerFolder is null ? null : System.IO.Path.GetDirectoryName(controllerFolder);
            TryDeleteEmptyFolder(gameFolder);

            BindingEditor.UnloadProfile();
            Library.Refresh();
            SetInlineMessage(Loc.T("m.deletedF") + System.IO.Path.GetFileName(path));
            return true;
        }
        catch (Exception ex)
        {
            SetInlineMessage(Loc.T("m.deletefail") + ex.Message);
            return false;
        }
    }

    // ------------------------------------------------------------------ radial menu host operations

    /// <summary>Selects a 0-based slot from the radial menu.</summary>
    public bool SwitchSlotFromRadial(int slotIndex)
    {
        if (slotIndex < 0 || slotIndex >= _slots.Length)
        {
            return false;
        }

        SelectedSlot = _slots[slotIndex];
        return true;
    }

    /// <summary>
    /// Switches the engine's active layer through <see cref="IRemapHost.SetLayer"/>. Unlike the old
    /// client-side-only path this changes what the engine actually remaps; an undefined layer keeps
    /// the previous one and reports「该配置没有第 N 层」.
    /// </summary>
    public bool SwitchLayerFromRadial(int layer)
    {
        if (SelectedController is not { } controller)
        {
            SetInlineMessage(Loc.T("m.disconF"));
            return false;
        }

        if (!_host.SetLayer(controller.Id, layer))
        {
            SetInlineMessage($"该配置没有第 {layer} 层");
            return false;
        }

        BindingEditor.SetLayer(layer);
        RefreshStatus();
        return true;
    }

    public bool ToggleRemapFromRadial()
    {
        RemapEnabled = !RemapEnabled;
        return true;
    }

    public bool SetRemapEnabledFromRadial(bool enabled)
    {
        RemapEnabled = enabled;
        return RemapEnabled == enabled;
    }

    /// <summary>Plays a radial "执行宏" key sequence through the client-side SendInput player.</summary>
    public bool RunMacroFromRadial(string macro)
    {
        if (_macroPlayer.TryPlay(macro, out string? error))
        {
            SetInlineMessage("已执行宏: " + macro);
            return true;
        }

        SetInlineMessage("宏执行失败: " + (error ?? "未知错误"));
        return false;
    }

    public void RequestPreferences() => PreferencesRequested?.Invoke(this, EventArgs.Empty);

    private bool IsInsideLibrary(string path)
    {
        try
        {
            string full = System.IO.Path.GetFullPath(path);
            foreach (string root in new[] { Library.RootPath, Library.PresetsRoot })
            {
                if (string.IsNullOrWhiteSpace(root))
                {
                    continue;
                }

                string prefix = System.IO.Path.GetFullPath(root)
                    .TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
                if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception)
        {
            return false;
        }

        return false;
    }

    private static void TryDeleteEmptyFolder(string? folder)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(folder) &&
                Directory.Exists(folder) &&
                !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }
        }
        catch (Exception)
        {
            // Best effort: leaving an empty folder behind is harmless.
        }
    }

    private void SyncFromHost()
    {
        // The diagram follows the device selector; polling itself is driven by the window.
        Gamepad.ControllerId = SelectedController?.Id;

        if (SelectedController is { } controller)
        {
            int slot = TryGet(() => _host.GetCurrentSlot(controller.Id), 0);
            _suppressSlotWrite = true;
            try
            {
                SelectedSlot = _slots[Math.Clamp(slot, 0, _slots.Length - 1)];
            }
            finally
            {
                _suppressSlotWrite = false;
            }

            _remapEnabled = TryGet(() => _host.GetRemapState(controller.Id), RemapState.NothingApplied) == RemapState.RemapOn;
            OnPropertyChanged(nameof(RemapEnabled));
        }
        else
        {
            _activeLayerName = Loc.T("m.mainlayer");
            _remapEnabled = false;
            OnPropertyChanged(nameof(RemapEnabled));
        }

        UpdateSlotSelection();
        RefreshStatus();
    }

    private void UpdateSlotSelection()
    {
        foreach (SlotItem slot in _slots)
        {
            slot.IsSelected = ReferenceEquals(slot, _selectedSlot);
        }
    }

    private void RefreshStatus()
    {
        if (SelectedController is not { } controller)
        {
            StatusText = "未检测到手柄 — 连接手柄后点击“刷新”";
            CurrentConfigText = Loc.T("m.confignone");
            CurrentLayerText = Loc.T("m.currentnone");
            return;
        }

        int slot = SelectedSlot?.Index ?? 0;
        string config = Loc.T("m.notapplied");
        try
        {
            IReadOnlyList<string> applied = _host.GetAppliedConfigs(controller.Id, slot);
            if (applied.Count > 0)
            {
                config = System.IO.Path.GetFileNameWithoutExtension(applied[^1]);
            }
        }
        catch (Exception)
        {
            // A broken host must not break the status line.
        }

        CurrentConfigText = Loc.F("m.configF", config);
        CurrentLayerText = Loc.F("m.currentlayerF", _activeLayerName);
        StatusText = $"{controller.Name} · Slot {slot + 1} · {(_remapEnabled ? Loc.T("m.remapon") : Loc.T("m.remapoff"))}";
    }

    private void OnControlSelected(object? sender, GamepadControlViewModel control) =>
        BindingEditor.SelectControl(control);

    private void OnHostEvent(object? sender, HostEvent hostEvent) => Post(() => ApplyHostEvent(hostEvent));

    private void ApplyHostEvent(HostEvent hostEvent)
    {
        ControllerItem? selected = SelectedController;
        bool forSelected = selected is null || Matches(selected.Id, hostEvent);

        switch (hostEvent)
        {
            case ControllerConnectedEvent:
            case ControllerDisconnectedEvent:
                Refresh();
                break;

            case RemapStateChangedEvent remap when forSelected:
                _remapEnabled = remap.State == RemapState.RemapOn;
                OnPropertyChanged(nameof(RemapEnabled));
                RefreshStatus();
                break;

            case SlotChangedEvent slot when forSelected:
                _suppressSlotWrite = true;
                try
                {
                    if (slot.Slot >= 0 && slot.Slot < _slots.Length)
                    {
                        SelectedSlot = _slots[slot.Slot];
                    }
                }
                finally
                {
                    _suppressSlotWrite = false;
                }

                RefreshStatus();
                break;

            case ShiftChangedEvent shift when forSelected:
                _activeLayerName = string.IsNullOrWhiteSpace(shift.Name) ? Loc.F("m.layerF", shift.Shift) : shift.Name;
                RefreshStatus();
                break;

            case ConfigAppliedEvent when forSelected:
                _activeLayerName = Loc.T("m.mainlayer");
                RefreshStatus();
                break;

            case OutputLoopDetectedEvent loop:
                _remapEnabled = false;
                OnPropertyChanged(nameof(RemapEnabled));
                string message = "检测到输出环路，已自动停用映射";
                SetInlineMessage(message);
                SetBanner(message);
                RefreshStatus();
                OutputLoopDetected?.Invoke(this, message);
                break;
        }
    }

    private static bool Matches(Guid controllerId, HostEvent hostEvent) => hostEvent switch
    {
        RemapStateChangedEvent remap => remap.ControllerId == controllerId,
        SlotChangedEvent slot => slot.ControllerId == controllerId,
        ShiftChangedEvent shift => shift.ControllerId == controllerId,
        ConfigAppliedEvent applied => applied.ControllerId == controllerId,
        _ => true,
    };

    private void Post(Action action)
    {
        SynchronizationContext? context = _syncContext;
        if (context is null || SynchronizationContext.Current == context)
        {
            action();
        }
        else
        {
            context.Post(_ => action(), null);
        }
    }

    private static T TryGet<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _foregroundWatcher?.Dispose();
        Gamepad.StopLive();
        Gamepad.ControlSelected -= OnControlSelected;
        _host.Event -= OnHostEvent;
    }
}
