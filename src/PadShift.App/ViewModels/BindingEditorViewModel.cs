using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PadShift.App.Infrastructure;
using PadShift.Core.Profile;
using ProfileActivator = PadShift.Core.Profile.Activator;

namespace PadShift.App.ViewModels;

/// <summary>
/// Right-hand binding panel. It edits the in-memory <see cref="GamepadProfile"/> loaded by
/// <see cref="ConfigLibraryViewModel"/>; <see cref="Save"/> writes the whole file back through
/// <see cref="ProfileIo.Save"/>, keeping unknown fields alive via the model's JsonExtensionData.
/// </summary>
/// <remarks>
/// The panel never touches a window: the key-capture dialog lives in the view and calls
/// <see cref="SetKeyboardTarget"/> with the already converted DIK scan code, so the whole editor is
/// testable head-less.
/// </remarks>
public sealed class BindingEditorViewModel : ObservableObject
{
    private const int MaxLayer = 12;

    private readonly ObservableCollection<LayerOption> _layers = new();
    private GamepadProfile? _profile;
    private string? _profilePath;
    private long _deviceId = 1;
    private long _virtualPadId = 1;
    private bool _suppressReload;
    private GamepadControlViewModel? _selectedControl;
    private LayerOption _selectedLayer = LayerOption.Main;
    private ActivatorOption _selectedActivator;
    private TargetOption _selectedTarget;
    private MouseButtonOption _selectedMouseButton;
    private GamepadTargetOption _selectedGamepadTarget;
    private int _keyboardDik;
    private string _keyboardKeyName = "";
    private bool _isTurbo;
    private bool _isToggle;
    private long _turboTimes;
    private long _turboPause;
    private bool _turboMacro;
    private List<MacroElement>? _macroDraft;
    private List<MacroElement>? _loadedMacros;
    private LayerOption _jumpToLayer = LayerOption.Main;
    private string _currentBindingText = Loc.T("b.noprofile");
    private string? _message;
    private bool _isDirty;

    public BindingEditorViewModel()
    {
        Activators = new ObservableCollection<ActivatorOption>(
        [
            new ActivatorOption(ActivatorType.Single, Loc.T("b.tap")),
            new ActivatorOption(ActivatorType.Long, Loc.T("b.hold")),
            new ActivatorOption(ActivatorType.Double, Loc.T("b.double")),
            new ActivatorOption(ActivatorType.Triple, Loc.T("b.triple")),
            new ActivatorOption(ActivatorType.Release, Loc.T("b.release")),
            new ActivatorOption(ActivatorType.Start, Loc.T("b.start")),
        ]);

        Targets = new ObservableCollection<TargetOption>(
        [
            new TargetOption(BindingTargetKind.Keyboard, Loc.T("b.kb")),
            new TargetOption(BindingTargetKind.Mouse, Loc.T("b.mouse")),
            new TargetOption(BindingTargetKind.Gamepad, Loc.T("b.pad")),
        ]);

        MouseButtonOptions = new ObservableCollection<MouseButtonOption>(
        [
            new MouseButtonOption(MouseButtons.Left, Loc.T("b.ml")),
            new MouseButtonOption(MouseButtons.Right, Loc.T("b.mr")),
            new MouseButtonOption(MouseButtons.Middle, Loc.T("b.mm")),
            new MouseButtonOption(MouseButtons.X1, Loc.T("b.x1")),
            new MouseButtonOption(MouseButtons.X2, Loc.T("b.x2")),
        ]);

        var gamepadTargets = new List<GamepadTargetOption>();
        foreach ((long id, string label) in GamepadButtons.All)
        {
            gamepadTargets.Add(new GamepadTargetOption(id, label));
        }

        GamepadTargetOptions = new ObservableCollection<GamepadTargetOption>(gamepadTargets);

        _selectedActivator = Activators[0];
        _selectedTarget = Targets[0];
        _selectedMouseButton = MouseButtonOptions[0];
        _selectedGamepadTarget = GamepadTargetOptions[0];

        _layers.Add(LayerOption.Main);
        MacroEditor = new MacroEditorViewModel();
        SaveCommand = new RelayCommand(() => Save());
        ClearCommand = new RelayCommand(() => ClearBinding());
    }

    // ------------------------------------------------------------------ choices

    public ObservableCollection<ActivatorOption> Activators { get; }

    public ObservableCollection<TargetOption> Targets { get; }

    public ObservableCollection<MouseButtonOption> MouseButtonOptions { get; }

    public ObservableCollection<GamepadTargetOption> GamepadTargetOptions { get; }

    public ObservableCollection<LayerOption> Layers => _layers;

    /// <summary>Macro sequence editor shared with <c>MacroEditorWindow</c>.</summary>
    public MacroEditorViewModel MacroEditor { get; }

    public RelayCommand SaveCommand { get; }

    public RelayCommand ClearCommand { get; }

    // ------------------------------------------------------------------ state

    public string? ProfilePath => _profilePath;

    public bool HasProfile => _profile is not null;

    public string ProfileName => _profilePath is null ? Loc.T("b.noprofileParen") : System.IO.Path.GetFileName(_profilePath);

    /// <summary>The parsed profile currently being edited (used by export / save-as-preset).</summary>
    public GamepadProfile? Profile => _profile;

    public string? ProfileAppName => _profile?.Config?.AppName;

    /// <summary>Steps of the sequence that would be written: the macro draft, or the file's current macro.</summary>
    public int MacroStepCount => _macroDraft?.Count ?? _loadedMacros?.Count ?? 0;

    public string MacroButtonText => Loc.F("b.macrobtnF", MacroStepCount);

    public GamepadControlViewModel? SelectedControl => _selectedControl;

    public string SelectedControlCaption => _selectedControl?.Caption ?? Loc.T("b.nocontrol");

    public string CurrentBindingText
    {
        get => _currentBindingText;
        private set => SetProperty(ref _currentBindingText, value);
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

    public bool IsDirty
    {
        get => _isDirty;
        private set => SetProperty(ref _isDirty, value);
    }

    public LayerOption SelectedLayer
    {
        get => _selectedLayer;
        set
        {
            if (value is null || !SetProperty(ref _selectedLayer, value) || _suppressReload)
            {
                return;
            }

            ReloadFromProfile();
        }
    }

    public ActivatorOption SelectedActivator
    {
        get => _selectedActivator;
        set
        {
            if (value is null || !SetProperty(ref _selectedActivator, value) || _suppressReload)
            {
                return;
            }

            ReloadFromProfile();
        }
    }

    /// <summary>Output kind. Changing it does not reload: it is the user's choice for the next save.</summary>
    public TargetOption SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (value is not null)
            {
                SetProperty(ref _selectedTarget, value);
            }
        }
    }

    public MouseButtonOption SelectedMouseButton
    {
        get => _selectedMouseButton;
        set
        {
            if (value is not null)
            {
                SetProperty(ref _selectedMouseButton, value);
                IsDirty = true;
            }
        }
    }

    public GamepadTargetOption SelectedGamepadTarget
    {
        get => _selectedGamepadTarget;
        set
        {
            if (value is not null)
            {
                SetProperty(ref _selectedGamepadTarget, value);
                IsDirty = true;
            }
        }
    }

    public LayerOption JumpToLayer
    {
        get => _jumpToLayer;
        set
        {
            if (value is not null)
            {
                SetProperty(ref _jumpToLayer, value);
                IsDirty = true;
            }
        }
    }

    public int KeyboardDik => _keyboardDik;

    public string KeyboardKeyText => _keyboardDik > 0 ? _keyboardKeyName : Loc.T("b.notcaptured");

    public bool IsTurbo
    {
        get => _isTurbo;
        set
        {
            if (!SetProperty(ref _isTurbo, value))
            {
                return;
            }

            if (value && _isToggle)
            {
                _isToggle = false;
                OnPropertyChanged(nameof(IsToggle));
            }

            IsDirty = true;
        }
    }

    public bool IsToggle
    {
        get => _isToggle;
        set
        {
            if (!SetProperty(ref _isToggle, value))
            {
                return;
            }

            if (value && _isTurbo)
            {
                _isTurbo = false;
                OnPropertyChanged(nameof(IsTurbo));
            }

            IsDirty = true;
        }
    }

    /// <summary>Turbo repeat count written to <c>activator.params.times</c>; 0 means "not written".</summary>
    public long TurboTimes
    {
        get => _turboTimes;
        set
        {
            long clamped = Math.Clamp(value, 0, MacroEditorViewModel.MaxTurboTimes);
            if (SetProperty(ref _turboTimes, clamped))
            {
                IsDirty = true;
            }

            if (clamped != value)
            {
                OnPropertyChanged(nameof(TurboTimes));
            }
        }
    }

    /// <summary>Turbo interval written to <c>activator.params.pause</c>; 0 means "not written".</summary>
    public long TurboPause
    {
        get => _turboPause;
        set
        {
            long clamped = Math.Clamp(value, 0, MacroEditorViewModel.MaxTurboPauseMs);
            if (SetProperty(ref _turboPause, clamped))
            {
                IsDirty = true;
            }

            if (clamped != value)
            {
                OnPropertyChanged(nameof(TurboPause));
            }
        }
    }

    /// <summary>True = <c>activator.params.macro</c>, i.e. times/pause repeat the whole sequence.</summary>
    public bool TurboMacro
    {
        get => _turboMacro;
        set
        {
            if (SetProperty(ref _turboMacro, value))
            {
                IsDirty = true;
            }
        }
    }

    // ------------------------------------------------------------------ macro editor

    /// <summary>
    /// Fills the macro dialog with the selected control+activator's sequence and turbo settings.
    /// A mapping that is not bound yet starts from an empty sequence; the panel keeps its
    /// single-target draft until the user actually applies a macro.
    /// </summary>
    public MacroEditorViewModel BeginMacroEditor()
    {
        List<MacroElement> sequence = _macroDraft ?? _loadedMacros ?? new List<MacroElement>();
        MacroEditor.SetContext($"{SelectedControlCaption} · {_selectedActivator.Label}");
        MacroEditor.Load(sequence, _isTurbo, _isToggle, _turboTimes, _turboPause, _turboMacro);
        return MacroEditor;
    }

    /// <summary>Takes the edited sequence/turbo settings into the panel's draft (persisted by Save).</summary>
    public bool ApplyMacroEditor()
    {
        IReadOnlyList<string> errors = MacroEditor.Validate();
        if (errors.Count > 0)
        {
            Message = string.Join("；", errors);
            return false;
        }

        _macroDraft = MacroEditor.BuildElements();
        _isTurbo = MacroEditor.IsTurbo;
        _isToggle = MacroEditor.IsToggle;
        _turboTimes = MacroEditor.TurboTimes;
        _turboPause = MacroEditor.TurboPause;
        _turboMacro = MacroEditor.TurboMacro;

        OnPropertyChanged(nameof(IsTurbo));
        OnPropertyChanged(nameof(IsToggle));
        OnPropertyChanged(nameof(TurboTimes));
        OnPropertyChanged(nameof(TurboPause));
        OnPropertyChanged(nameof(TurboMacro));
        NotifyMacroCount();

        IsDirty = true;
        Message = Loc.F("b.macroupdF", _macroDraft.Count);
        return true;
    }

    // ------------------------------------------------------------------ loading

    /// <summary>Loads a profile from disk; failures are reported through <see cref="Message"/>.</summary>
    public bool LoadProfile(string path)
    {
        try
        {
            GamepadProfile profile = ProfileIo.Load(path);
            LoadProfile(profile, path);
            return true;
        }
        catch (Exception ex)
        {
            _profile = null;
            _profilePath = null;
            _macroDraft = null;
            _loadedMacros = null;
            Message = Loc.T("b.writefail") + ex.Message;
            CurrentBindingText = Loc.T("b.noprofile");
            NotifyMacroCount();
            OnPropertyChanged(nameof(HasProfile));
            OnPropertyChanged(nameof(ProfileName));
            OnPropertyChanged(nameof(ProfilePath));
            return false;
        }
    }

    /// <summary>Attaches an already parsed profile (used by tests and after an in-memory edit).</summary>
    public void LoadProfile(GamepadProfile profile, string path)
    {
        ArgumentNullException.ThrowIfNull(profile);

        _profile = profile;
        _profilePath = path;
        _deviceId = ResolveDeviceId(profile);
        _virtualPadId = ResolveVirtualPadId(profile);
        _macroDraft = null;
        _loadedMacros = null;
        NotifyMacroCount();
        RebuildLayers();

        Message = null;
        IsDirty = false;
        OnPropertyChanged(nameof(HasProfile));
        OnPropertyChanged(nameof(ProfileName));
        OnPropertyChanged(nameof(ProfilePath));
        ReloadFromProfile();
    }

    /// <summary>Detaches the profile (after the file was deleted from the library).</summary>
    public void UnloadProfile()
    {
        _profile = null;
        _profilePath = null;
        _macroDraft = null;
        _loadedMacros = null;
        _selectedLayer = LayerOption.Main;
        _jumpToLayer = LayerOption.Main;
        _layers.Clear();
        _layers.Add(LayerOption.Main);
        IsDirty = false;
        CurrentBindingText = Loc.T("b.noprofile");
        Message = null;
        NotifyMacroCount();
        OnPropertyChanged(nameof(HasProfile));
        OnPropertyChanged(nameof(ProfileName));
        OnPropertyChanged(nameof(ProfilePath));
        OnPropertyChanged(nameof(SelectedLayer));
        OnPropertyChanged(nameof(JumpToLayer));
        ResetTargetFields();
    }

    /// <summary>Points the panel at a control; reloads that control's current mapping.</summary>
    public void SelectControl(GamepadControlViewModel? control)
    {
        _selectedControl = control;
        OnPropertyChanged(nameof(SelectedControl));
        OnPropertyChanged(nameof(SelectedControlCaption));
        ReloadFromProfile();
    }

    public void SetLayer(int layer)
    {
        LayerOption? option = _layers.FirstOrDefault(candidate => candidate.Layer == layer);
        if (option is not null)
        {
            SelectedLayer = option;
        }
    }

    public void SetActivator(ActivatorType type)
    {
        ActivatorOption? option = Activators.FirstOrDefault(candidate => candidate.Type == type);
        if (option is not null)
        {
            SelectedActivator = option;
        }
    }

    public void SetTarget(BindingTargetKind kind)
    {
        TargetOption? option = Targets.FirstOrDefault(candidate => candidate.Kind == kind);
        if (option is not null)
        {
            SelectedTarget = option;
        }
    }

    /// <summary>Applies a captured key. <paramref name="dik"/> is the DIK scan code stored in the profile.</summary>
    public void SetKeyboardTarget(int dik, string? name = null)
    {
        if (dik <= 0)
        {
            return;
        }

        _keyboardDik = dik;
        _keyboardKeyName = string.IsNullOrWhiteSpace(name) ? DikNames.FromDik(dik) : name!;
        SetTarget(BindingTargetKind.Keyboard);
        IsDirty = true;
        OnPropertyChanged(nameof(KeyboardDik));
        OnPropertyChanged(nameof(KeyboardKeyText));
    }

    public void SetMouseTarget(int buttonId)
    {
        MouseButtonOption? option = MouseButtonOptions.FirstOrDefault(candidate => candidate.ButtonId == buttonId);
        if (option is not null)
        {
            SelectedMouseButton = option;
            SetTarget(BindingTargetKind.Mouse);
        }
    }

    public void SetGamepadTarget(long buttonId)
    {
        GamepadTargetOption? option = GamepadTargetOptions.FirstOrDefault(candidate => candidate.ButtonId == buttonId);
        if (option is not null)
        {
            SelectedGamepadTarget = option;
            SetTarget(BindingTargetKind.Gamepad);
        }
    }

    // ------------------------------------------------------------------ writing

    /// <summary>
    /// Writes the current editor state into the in-memory profile and returns the affected rule.
    /// Returns null (with <see cref="Message"/> set) when there is nothing to write.
    /// </summary>
    public MappingRule? Commit()
    {
        if (_profile is null)
        {
            Message = Loc.T("b.loadfirst");
            return null;
        }

        if (_selectedControl is null)
        {
            Message = Loc.T("b.pickcontrol");
            return null;
        }

        if (_selectedTarget.Kind == BindingTargetKind.Keyboard && _keyboardDik <= 0)
        {
            Message = Loc.T("b.capturefirst");
            return null;
        }

        try
        {
            long maskId = EnsureMask(_selectedControl.ButtonId, _selectedControl.Caption);
            int layer = _selectedLayer.Layer;

            _profile.Mappings ??= new List<MappingRule>();
            MappingRule? rule = FindMappingForMask(maskId, layer, _selectedActivator.Type);
            if (rule is null)
            {
                rule = new MappingRule
                {
                    Condition = new MappingCondition
                    {
                        ShiftId = layer == 0 ? null : layer,
                        Mask = new ConditionMask
                        {
                            Id = maskId,
                            Activator = new ProfileActivator { Type = _selectedActivator.Type },
                        },
                    },
                };
                _profile.Mappings.Add(rule);
            }

            // JSON can carry null for any reference member, so tolerate it instead of trusting the
            // property initialisers.
            MappingCondition condition = rule.Condition;
            if (condition is null)
            {
                condition = new MappingCondition();
                rule.Condition = condition;
            }

            condition.Mask ??= new ConditionMask { Id = maskId };
            condition.Mask.Id = maskId;
            condition.ShiftId = layer == 0 ? null : layer;

            ProfileActivator? activator = condition.Mask.Activator;
            if (activator is null)
            {
                activator = new ProfileActivator();
                condition.Mask.Activator = activator;
            }

            activator.Type = _selectedActivator.Type;
            activator.Mode = ResolveMode();
            WriteActivatorParams(activator);

            rule.Macros = ResolveMacroSequence();
            rule.JumpToLayer = _jumpToLayer.Layer > 0
                ? JsonSerializer.SerializeToElement(new { layer = (long)_jumpToLayer.Layer, delay = 0 })
                : null;

            IsDirty = true;
            Message = Loc.T("b.inmemorySave");
            CurrentBindingText = DescribeRule(rule);
            return rule;
        }
        catch (Exception ex)
        {
            Message = Loc.T("b.writefail") + ex.Message;
            return null;
        }
    }

    /// <summary>Commits and persists to the loaded file.</summary>
    public bool Save()
    {
        MappingRule? rule = Commit();
        return rule is not null && Persist();
    }

    /// <summary>Removes every mapping of the selected control on the selected layer.</summary>
    public bool ClearBinding()
    {
        if (_profile is null)
        {
            Message = Loc.T("b.loadfirst");
            return false;
        }

        if (_selectedControl is null)
        {
            Message = Loc.T("b.pickcontrol");
            return false;
        }

        long? maskId = FindMaskId(_selectedControl.ButtonId);
        if (maskId is null)
        {
            Message = Loc.T("b.nobinding");
            return false;
        }

        int layer = _selectedLayer.Layer;
        List<MappingRule>? mappings = _profile.Mappings;
        int removed = mappings?.RemoveAll(rule =>
            rule?.Condition?.Mask?.Id == maskId && (int)(rule.Condition.ShiftId ?? 0) == layer) ?? 0;

        // The mask is only meaningful while a mapping references it.
        bool stillReferenced = mappings?.Any(rule => rule?.Condition?.Mask?.Id == maskId) == true;
        if (!stillReferenced)
        {
            _profile.Masks?.RemoveAll(mask => mask is not null && mask.Id == maskId);
        }

        CurrentBindingText = Loc.T("b.unbound");
        Message = removed > 0 ? Loc.T("b.clearedF") : Loc.T("b.nobindingLayer");
        IsDirty = removed > 0;
        return removed > 0 && Persist();
    }

    private bool Persist()
    {
        if (_profile is null || string.IsNullOrWhiteSpace(_profilePath))
        {
            Message = Loc.T("b.nosave");
            return false;
        }

        try
        {
            ProfileIo.Save(_profilePath, _profile);
            IsDirty = false;
            Message = Loc.F("b.savedF", System.IO.Path.GetFileName(_profilePath));
            return true;
        }
        catch (Exception ex)
        {
            Message = Loc.T("b.savefail") + ex.Message;
            return false;
        }
    }

    // ------------------------------------------------------------------ internals

    /// <summary>
    /// The sequence written to <c>mapping.macros</c>: an applied macro draft wins, then a multi-step
    /// macro already present in the profile, then the panel's single-target output.
    /// </summary>
    private List<MacroElement> ResolveMacroSequence()
    {
        if (_macroDraft is { Count: > 0 } draft)
        {
            return draft;
        }

        if (_loadedMacros is { Count: > 1 } loaded)
        {
            return loaded;
        }

        return new List<MacroElement> { CreateMacroElement() };
    }

    /// <summary>Mirrors the turbo text fields into <c>activator.params</c> (0 = leave the key out).</summary>
    private void WriteActivatorParams(ProfileActivator activator)
    {
        if (activator.Params is null && _turboTimes <= 0 && _turboPause <= 0 && !_turboMacro)
        {
            return;
        }

        ActivatorParams parameters = activator.Params ?? new ActivatorParams();
        parameters.Times = _turboTimes > 0 ? Math.Clamp(_turboTimes, 1, MacroEditorViewModel.MaxTurboTimes) : null;
        parameters.Pause = _turboPause > 0 ? Math.Clamp(_turboPause, 10, MacroEditorViewModel.MaxTurboPauseMs) : null;
        parameters.Macro = _turboMacro ? true : null;
        activator.Params = HasAnyParams(parameters) ? parameters : null;
    }

    private static bool HasAnyParams(ActivatorParams parameters) =>
        parameters.Expert is not null ||
        parameters.Times is not null ||
        parameters.Pause is not null ||
        parameters.Delay is not null ||
        parameters.Macro is not null ||
        parameters.LongWaitTime is not null ||
        parameters.SingleWaitTime is not null ||
        parameters.DoubleWaitTime is not null ||
        parameters.Extra is { Count: > 0 };

    private void NotifyMacroCount()
    {
        OnPropertyChanged(nameof(MacroStepCount));
        OnPropertyChanged(nameof(MacroButtonText));
    }

    private void ReloadFromProfile()
    {
        if (_profile is null)
        {
            CurrentBindingText = Loc.T("b.noprofile");
            return;
        }

        if (_selectedControl is null)
        {
            CurrentBindingText = Loc.T("b.pickpad");
            return;
        }

        MappingRule? rule = FindMapping(_selectedControl.ButtonId, _selectedLayer.Layer, _selectedActivator.Type);
        if (rule is null)
        {
            ResetTargetFields();
            CurrentBindingText = Loc.F("b.unboundF", _selectedActivator.Label);
            return;
        }

        ApplyRuleToEditors(rule);
        CurrentBindingText = DescribeRule(rule);
    }

    private void ResetTargetFields()
    {
        _suppressReload = true;
        try
        {
            _keyboardDik = 0;
            _keyboardKeyName = "";
            _isTurbo = false;
            _isToggle = false;
            _turboTimes = 0;
            _turboPause = 0;
            _turboMacro = false;
            _macroDraft = null;
            _loadedMacros = null;
            _jumpToLayer = _layers.Count > 0 ? _layers[0] : LayerOption.Main;
            OnPropertyChanged(nameof(KeyboardDik));
            OnPropertyChanged(nameof(KeyboardKeyText));
            OnPropertyChanged(nameof(IsTurbo));
            OnPropertyChanged(nameof(IsToggle));
            OnPropertyChanged(nameof(TurboTimes));
            OnPropertyChanged(nameof(TurboPause));
            OnPropertyChanged(nameof(TurboMacro));
            OnPropertyChanged(nameof(JumpToLayer));
            NotifyMacroCount();
        }
        finally
        {
            _suppressReload = false;
        }
    }

    private void ApplyRuleToEditors(MappingRule rule)
    {
        _suppressReload = true;
        try
        {
            ProfileActivator? activator = rule.Condition?.Mask?.Activator;
            if (activator is not null)
            {
                ActivatorOption? option = Activators.FirstOrDefault(candidate => candidate.Type == activator.Type);
                if (option is not null)
                {
                    _selectedActivator = option;
                    OnPropertyChanged(nameof(SelectedActivator));
                }

                _isTurbo = activator.Mode == ActivationMode.Turbo;
                _isToggle = activator.Mode == ActivationMode.Toggle;
                _turboTimes = activator.Params?.Times ?? 0;
                _turboPause = activator.Params?.Pause ?? 0;
                _turboMacro = activator.Params?.Macro == true;
                OnPropertyChanged(nameof(IsTurbo));
                OnPropertyChanged(nameof(IsToggle));
                OnPropertyChanged(nameof(TurboTimes));
                OnPropertyChanged(nameof(TurboPause));
                OnPropertyChanged(nameof(TurboMacro));
            }

            _loadedMacros = rule.Macros;
            _macroDraft = null;
            NotifyMacroCount();

            int jumpLayer = ReadJumpLayer(rule.JumpToLayer);
            _jumpToLayer = _layers.FirstOrDefault(candidate => candidate.Layer == jumpLayer) ?? LayerOption.Main;
            OnPropertyChanged(nameof(JumpToLayer));

            MacroElement? macro = rule.Macros?.FirstOrDefault(element => element is not null);
            if (macro?.Keyboard is { } keyboard)
            {
                _selectedTarget = Targets[0];
                _keyboardDik = keyboard.ButtonId is > 0 and < 256 ? (int)keyboard.ButtonId : 0;
                _keyboardKeyName = DikNames.FromDik(_keyboardDik);
            }
            else if (macro?.Mouse is { } mouse)
            {
                _selectedTarget = Targets[1];
                int id = mouse.ButtonId is { } raw ? (int)raw : 0;
                _selectedMouseButton = MouseButtonOptions.FirstOrDefault(option => option.ButtonId == id)
                                       ?? MouseButtonOptions[0];
                OnPropertyChanged(nameof(SelectedMouseButton));
            }
            else if (macro?.Gamepad is { } gamepad)
            {
                _selectedTarget = Targets[2];
                _selectedGamepadTarget = GamepadTargetOptions.FirstOrDefault(option => option.ButtonId == gamepad.ButtonId)
                                         ?? GamepadTargetOptions[0];
                OnPropertyChanged(nameof(SelectedGamepadTarget));
            }

            OnPropertyChanged(nameof(SelectedTarget));
            OnPropertyChanged(nameof(KeyboardDik));
            OnPropertyChanged(nameof(KeyboardKeyText));
        }
        finally
        {
            _suppressReload = false;
        }
    }

    private static int ReadJumpLayer(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } value)
        {
            return 0;
        }

        if (value.TryGetProperty("layer", out JsonElement layer) && layer.TryGetInt32(out int layerNumber))
        {
            return Math.Clamp(layerNumber, 0, MaxLayer);
        }

        return 0;
    }

    private string DescribeRule(MappingRule rule)
    {
        ProfileActivator? activator = rule.Condition?.Mask?.Activator;
        ActivatorOption? option = Activators.FirstOrDefault(candidate => candidate.Type == (activator?.Type ?? ActivatorType.Single));
        var parts = new List<string> { option?.Label ?? Loc.T("b.tap") };

        MacroElement? macro = rule.Macros?.FirstOrDefault(element => element is not null);
        string target;
        if (macro?.Keyboard is { } keyboard)
        {
            target = Loc.T("b.kbPrefix") + DikNames.FromDik((int)keyboard.ButtonId);
        }
        else if (macro?.Mouse is { } mouse)
        {
            target = DescribeMouse(mouse);
        }
        else if (macro?.Gamepad is { } gamepad)
        {
            target = gamepad.Absolute == true
                ? Loc.T("b.axisPrefix") + gamepad.ButtonId
                : Loc.T("b.padPrefix") + GamepadButtons.LabelOf(gamepad.ButtonId);
        }
        else if (rule.Hardware?.Unmap == true)
        {
            target = Loc.T("b.mask");
        }
        else
        {
            target = Loc.T("b.notarget");
        }

        parts.Add("→");
        parts.Add(target);

        if (activator?.Mode == ActivationMode.Turbo)
        {
            parts.Add("· Turbo");
        }
        else if (activator?.Mode == ActivationMode.Toggle)
        {
            parts.Add("· Toggle");
        }

        int jump = ReadJumpLayer(rule.JumpToLayer);
        if (jump > 0)
        {
            parts.Add(Loc.F("b.jumpF", jump));
        }

        if (rule.Macros is { Count: > 1 } macros)
        {
            parts.Add(Loc.F("b.macroTagF", macros.Count));
        }

        return string.Join(' ', parts);
    }

    private string DescribeMouse(MouseOutput mouse)
    {
        if (mouse.ButtonId is { } raw)
        {
            int id = (int)raw;
            return MouseButtonOptions.FirstOrDefault(candidate => candidate.ButtonId == id)?.Label ?? Loc.F("b.mouseBtnF", id);
        }

        if (mouse.Wheel is { ValueKind: JsonValueKind.String } wheel)
        {
            string text = wheel.GetString() ?? string.Empty;
            return text.Equals("up", StringComparison.OrdinalIgnoreCase) ? Loc.T("b.wheelUp") : Loc.T("b.wheelDown");
        }

        if (mouse.Direction is { ValueKind: JsonValueKind.String } direction)
        {
            return Loc.T("b.mouseMovePrefix") + direction.GetString();
        }

        return Loc.T("b.mouseWord");
    }

    private ActivationMode ResolveMode()
    {
        if (_isTurbo)
        {
            return ActivationMode.Turbo;
        }

        return _isToggle ? ActivationMode.Toggle : ActivationMode.HoldUntilRelease;
    }

    private MacroElement CreateMacroElement() => _selectedTarget.Kind switch
    {
        BindingTargetKind.Mouse => new MacroElement
        {
            Mouse = new MouseOutput
            {
                ButtonId = _selectedMouseButton.ButtonId,
            },
        },
        BindingTargetKind.Gamepad => new MacroElement
        {
            Gamepad = new GamepadOutput
            {
                Id = _virtualPadId,
                ButtonId = _selectedGamepadTarget.ButtonId,
                Description = _selectedGamepadTarget.Label,
            },
        },
        _ => new MacroElement
        {
            Keyboard = new KeyboardOutput
            {
                ButtonId = _keyboardDik,
                Description = _keyboardKeyName,
            },
        },
    };

    private long? FindMaskId(long buttonId)
    {
        if (_profile?.Masks is not { } masks)
        {
            return null;
        }

        long? fallback = null;
        foreach (MaskRule? mask in masks)
        {
            if (mask?.Set is not { Count: 1 } set || set[0] is not { } reference)
            {
                continue;
            }

            if (reference.ButtonId != buttonId)
            {
                continue;
            }

            if (reference.DeviceId == _deviceId)
            {
                return mask.Id;
            }

            fallback ??= mask.Id;
        }

        return fallback;
    }

    private MappingRule? FindMapping(long buttonId, int layer, ActivatorType type)
    {
        long? maskId = FindMaskId(buttonId);
        return maskId is null ? null : FindMappingForMask(maskId.Value, layer, type);
    }

    private MappingRule? FindMappingForMask(long maskId, int layer, ActivatorType type)
    {
        foreach (MappingRule? rule in _profile?.Mappings ?? new List<MappingRule>())
        {
            if (rule?.Condition?.Mask is not { } mask || mask.Id != maskId)
            {
                continue;
            }

            if ((int)(rule.Condition.ShiftId ?? 0) != layer)
            {
                continue;
            }

            if ((mask.Activator?.Type ?? ActivatorType.Single) != type)
            {
                continue;
            }

            return rule;
        }

        return null;
    }

    private long EnsureMask(long buttonId, string description)
    {
        long? existing = FindMaskId(buttonId);
        if (existing is { } maskId)
        {
            return maskId;
        }

        _profile!.Masks ??= new List<MaskRule>();
        long next = 1;
        foreach (MaskRule? mask in _profile.Masks)
        {
            if (mask is not null)
            {
                next = Math.Max(next, mask.Id + 1);
            }
        }

        _profile.Masks.Add(new MaskRule
        {
            Id = next,
            Set = new List<ButtonRef>
            {
                new() { DeviceId = _deviceId, ButtonId = buttonId, Description = description },
            },
        });

        return next;
    }

    private void RebuildLayers()
    {
        var options = new List<LayerOption> { LayerOption.Main };
        foreach (ShiftRule? shift in _profile?.Shifts ?? new List<ShiftRule>())
        {
            if (shift is null || shift.Id is <= 0 or > MaxLayer)
            {
                continue;
            }

            string label = string.IsNullOrWhiteSpace(shift.Description)
                ? Loc.F("m.layerF", shift.Id)
                : Loc.F("b.layerDescF", shift.Id, shift.Description);
            options.Add(new LayerOption((int)shift.Id, label));
        }

        _layers.Clear();
        foreach (LayerOption option in options)
        {
            _layers.Add(option);
        }

        _selectedLayer = _layers[0];
        _jumpToLayer = _layers[0];
        OnPropertyChanged(nameof(SelectedLayer));
        OnPropertyChanged(nameof(JumpToLayer));
    }

    private static long ResolveDeviceId(GamepadProfile profile)
    {
        List<HardwareDevice>? hardware = profile.Devices?.Hardware;
        if (hardware is not { Count: > 0 })
        {
            return 1;
        }

        foreach (HardwareDevice? device in hardware)
        {
            if (device is not null && string.Equals(device.Name, "gamepad", StringComparison.OrdinalIgnoreCase))
            {
                return device.Id;
            }
        }

        return hardware[0] is { } first ? first.Id : 1;
    }

    private static long ResolveVirtualPadId(GamepadProfile profile)
    {
        List<VirtualGamepad>? pads = profile.Devices?.Virtual?.Gamepads;
        if (pads is { Count: > 0 } && pads[0] is { } pad)
        {
            return pad.Id > 0 ? pad.Id : 1;
        }

        return 1;
    }
}
