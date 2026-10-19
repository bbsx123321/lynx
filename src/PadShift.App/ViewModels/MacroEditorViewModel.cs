using System.Collections.Generic;
using PadShift.App.Infrastructure;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PadShift.Core.Profile;

namespace PadShift.App.ViewModels;

/// <summary>
/// Edits one binding's macro sequence (keyboard/mouse/gamepad/pause/break), its turbo parameters and
/// the live preview. The ViewModel never opens a window: the view supplies captured keys through
/// <see cref="MacroStepViewModel.SetKeyboardKey"/>, so the whole editor runs head-less in tests.
/// </summary>
public sealed class MacroEditorViewModel : ObservableObject
{
    public const int MaxSteps = 200;
    public const long MaxTurboTimes = 9999;
    public const long MaxTurboPauseMs = 60_000;

    private readonly ObservableCollection<MacroStepViewModel> _steps = new();
    private MacroStepViewModel? _selectedStep;
    private string _contextCaption = "";
    private string _previewText = Loc.T("me.empty");
    private IReadOnlyList<string> _errors = Array.Empty<string>();
    private string? _errorText;
    private string? _message;
    private bool _isTurbo;
    private bool _isToggle;
    private long _turboTimes;
    private long _turboPause;
    private bool _turboMacro;

    public MacroEditorViewModel()
    {
        Steps = new ReadOnlyObservableCollection<MacroStepViewModel>(_steps);
        AddKeyboardDownCommand = new RelayCommand(() => AddKeyboard(action: MacroAction.Down));
        AddKeyboardUpCommand = new RelayCommand(() => AddKeyboard(action: MacroAction.Up));
        AddMouseButtonCommand = new RelayCommand(() => AddMouseButton());
        AddMouseMoveCommand = new RelayCommand(() => AddMouseMove());
        AddWheelCommand = new RelayCommand(() => AddWheel());
        AddGamepadButtonCommand = new RelayCommand(() => AddGamepadButton());
        AddPauseCommand = new RelayCommand(() => AddPause());
        AddBreakCommand = new RelayCommand(() => AddBreak());
        RemoveStepCommand = new RelayCommand<MacroStepViewModel>(RemoveStep);
        MoveStepUpCommand = new RelayCommand<MacroStepViewModel>(MoveStepUp);
        MoveStepDownCommand = new RelayCommand<MacroStepViewModel>(MoveStepDown);
        DuplicateStepCommand = new RelayCommand<MacroStepViewModel>(DuplicateStep);
        ClearCommand = new RelayCommand(Clear);
        Refresh();
    }

    // ------------------------------------------------------------------ composition

    public ReadOnlyObservableCollection<MacroStepViewModel> Steps { get; }

    public MacroStepViewModel? SelectedStep
    {
        get => _selectedStep;
        set => SetProperty(ref _selectedStep, value);
    }

    public RelayCommand AddKeyboardDownCommand { get; }

    public RelayCommand AddKeyboardUpCommand { get; }

    public RelayCommand AddMouseButtonCommand { get; }

    public RelayCommand AddMouseMoveCommand { get; }

    public RelayCommand AddWheelCommand { get; }

    public RelayCommand AddGamepadButtonCommand { get; }

    public RelayCommand AddPauseCommand { get; }

    public RelayCommand AddBreakCommand { get; }

    public RelayCommand<MacroStepViewModel> RemoveStepCommand { get; }

    public RelayCommand<MacroStepViewModel> MoveStepUpCommand { get; }

    public RelayCommand<MacroStepViewModel> MoveStepDownCommand { get; }

    public RelayCommand<MacroStepViewModel> DuplicateStepCommand { get; }

    public RelayCommand ClearCommand { get; }

    // ------------------------------------------------------------------ window text

    /// <summary>"A 键（下）· 长按" — written by the binding panel before the dialog opens.</summary>
    public string ContextCaption
    {
        get => _contextCaption;
        private set
        {
            if (SetProperty(ref _contextCaption, value))
            {
                OnPropertyChanged(nameof(Title));
            }
        }
    }

    public string Title => string.IsNullOrWhiteSpace(_contextCaption) ? Loc.T("me.title") : Loc.T("me.titleDash") + _contextCaption;

    public void SetContext(string? caption) => ContextCaption = (caption ?? "").Trim();

    public string PreviewText
    {
        get => _previewText;
        private set => SetProperty(ref _previewText, value);
    }

    public IReadOnlyList<string> Errors => _errors;

    public bool HasErrors => _errors.Count > 0;

    public string? ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value))
            {
                OnPropertyChanged(nameof(HasErrors));
            }
        }
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

    /// <summary>True when the sequence is non-empty and every row passes validation.</summary>
    public bool CanSave => _steps.Count > 0 && _errors.Count == 0;

    // ------------------------------------------------------------------ behaviour (turbo)

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

            RefreshValidation();
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

            RefreshValidation();
        }
    }

    /// <summary>0 = the parameter is not written (engine default / unlimited).</summary>
    public long TurboTimes
    {
        get => _turboTimes;
        set
        {
            long clamped = Math.Clamp(value, 0, MaxTurboTimes);
            if (SetProperty(ref _turboTimes, clamped))
            {
                RefreshValidation();
            }

            if (clamped != value)
            {
                OnPropertyChanged(nameof(TurboTimes));
            }
        }
    }

    /// <summary>0 = the parameter is not written (engine default).</summary>
    public long TurboPause
    {
        get => _turboPause;
        set
        {
            long clamped = Math.Clamp(value, 0, MaxTurboPauseMs);
            if (SetProperty(ref _turboPause, clamped))
            {
                RefreshValidation();
            }

            if (clamped != value)
            {
                OnPropertyChanged(nameof(TurboPause));
            }
        }
    }

    /// <summary>Maps to <c>activator.params.macro</c>: the repeat applies to the whole sequence.</summary>
    public bool TurboMacro
    {
        get => _turboMacro;
        set => SetProperty(ref _turboMacro, value);
    }

    // ------------------------------------------------------------------ loading

    /// <summary>Replaces the sequence with elements read from a profile (untouched rows stay verbatim).</summary>
    public void Load(
        IEnumerable<MacroElement>? elements,
        bool isTurbo = false,
        bool isToggle = false,
        long turboTimes = 0,
        long turboPause = 0,
        bool turboMacro = false)
    {
        DetachAll();
        _steps.Clear();

        foreach (MacroElement? element in elements ?? Array.Empty<MacroElement>())
        {
            MacroStepViewModel? step = MacroStepViewModel.FromElement(element);
            if (step is not null)
            {
                AttachLoaded(step);
            }
        }

        _isTurbo = isTurbo;
        _isToggle = isToggle;
        _turboTimes = Math.Clamp(turboTimes, 0, MaxTurboTimes);
        _turboPause = Math.Clamp(turboPause, 0, MaxTurboPauseMs);
        _turboMacro = turboMacro;

        OnPropertyChanged(nameof(IsTurbo));
        OnPropertyChanged(nameof(IsToggle));
        OnPropertyChanged(nameof(TurboTimes));
        OnPropertyChanged(nameof(TurboPause));
        OnPropertyChanged(nameof(TurboMacro));

        Renumber();
        SelectedStep = _steps.Count > 0 ? _steps[0] : null;
        Message = null;
        Refresh();
    }

    public void Clear()
    {
        DetachAll();
        _steps.Clear();
        SelectedStep = null;
        Message = null;
        Renumber();
        Refresh();
    }

    // ------------------------------------------------------------------ editing

    public MacroStepViewModel? AddKeyboard(int dik = 0, MacroAction action = MacroAction.Down, string? description = null) =>
        Attach(MacroStepViewModel.Keyboard(action, dik, description));

    public MacroStepViewModel? AddMouseButton(long buttonId = MouseButtons.Left, MacroAction action = MacroAction.Auto) =>
        Attach(MacroStepViewModel.MouseButton(buttonId, action));

    public MacroStepViewModel? AddMouseMove(string direction = "right", long pixels = 10) =>
        Attach(MacroStepViewModel.MouseMove(direction, pixels));

    public MacroStepViewModel? AddWheel(string direction = "down", long notches = 1) =>
        Attach(MacroStepViewModel.Wheel(direction, notches));

    public MacroStepViewModel? AddGamepadButton(long buttonId = GamepadButtons.A, MacroAction action = MacroAction.Auto, long padId = 1) =>
        Attach(MacroStepViewModel.GamepadButton(buttonId, action, padId));

    public MacroStepViewModel? AddPause(long milliseconds = 100) =>
        Attach(MacroStepViewModel.Pause(milliseconds));

    public MacroStepViewModel? AddBreak() => Attach(MacroStepViewModel.Break());

    public void RemoveStep(MacroStepViewModel? step)
    {
        if (step is null)
        {
            return;
        }

        int index = _steps.IndexOf(step);
        if (index < 0)
        {
            return;
        }

        step.PropertyChanged -= OnStepPropertyChanged;
        _steps.RemoveAt(index);
        Renumber();
        SelectedStep = _steps.Count == 0 ? null : _steps[Math.Min(index, _steps.Count - 1)];
        Message = null;
        Refresh();
    }

    public void MoveStepUp(MacroStepViewModel? step)
    {
        if (step is null)
        {
            return;
        }

        int index = _steps.IndexOf(step);
        if (index <= 0)
        {
            return;
        }

        _steps.Move(index, index - 1);
        Renumber();
        SelectedStep = step;
        Refresh();
    }

    public void MoveStepDown(MacroStepViewModel? step)
    {
        if (step is null)
        {
            return;
        }

        int index = _steps.IndexOf(step);
        if (index < 0 || index >= _steps.Count - 1)
        {
            return;
        }

        _steps.Move(index, index + 1);
        Renumber();
        SelectedStep = step;
        Refresh();
    }

    /// <summary>Inserts a fresh copy after the original; the copy is not tied to the source element.</summary>
    public void DuplicateStep(MacroStepViewModel? step)
    {
        if (step is null)
        {
            return;
        }

        int index = _steps.IndexOf(step);
        if (index < 0)
        {
            return;
        }

        MacroStepViewModel? copy = MacroStepViewModel.FromElement(step.ToElement());
        if (copy is null)
        {
            return;
        }

        if (copy.Kind != MacroStepKind.PassThrough)
        {
            // Pass-through rows are their source element; detaching them would lose the payload.
            copy.Detach();
        }

        Attach(copy, index + 1);
    }

    // ------------------------------------------------------------------ output

    /// <summary>Builds the ordered <see cref="MacroElement"/> list written to <c>mapping.macros</c>.</summary>
    public List<MacroElement> BuildElements()
    {
        var elements = new List<MacroElement>(_steps.Count);
        foreach (MacroStepViewModel step in _steps)
        {
            elements.Add(step.ToElement());
        }

        return elements;
    }

    /// <summary>Sequence + turbo validation; the dialog must not close while this is non-empty.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (_steps.Count == 0)
        {
            errors.Add("宏序列为空：至少需要一步");
        }

        if (_steps.Count > MaxSteps)
        {
            errors.Add($"宏最多 {MaxSteps} 步（当前 {_steps.Count} 步）");
        }

        for (int index = 0; index < _steps.Count; index++)
        {
            string? error = _steps[index].Validate();
            if (error is not null)
            {
                errors.Add($"第 {index + 1} 步：{error}");
            }
        }

        if (_turboTimes is < 0 or > MaxTurboTimes)
        {
            errors.Add($"Turbo 次数范围 0–{MaxTurboTimes}");
        }

        if (_turboPause is < 0 or > MaxTurboPauseMs)
        {
            errors.Add($"Turbo 间隔范围 0–{MaxTurboPauseMs} ms");
        }

        return errors;
    }

    // ------------------------------------------------------------------ internals

    private MacroStepViewModel? Attach(MacroStepViewModel? step, int? index = null)
    {
        if (step is null)
        {
            return null;
        }

        if (_steps.Count >= MaxSteps)
        {
            Message = $"宏最多 {MaxSteps} 步";
            return null;
        }

        step.PropertyChanged += OnStepPropertyChanged;
        if (index is { } insertAt && insertAt >= 0 && insertAt <= _steps.Count)
        {
            _steps.Insert(insertAt, step);
        }
        else
        {
            _steps.Add(step);
        }

        Renumber();
        SelectedStep = step;
        Message = null;
        Refresh();
        return step;
    }

    private void AttachLoaded(MacroStepViewModel step)
    {
        step.PropertyChanged += OnStepPropertyChanged;
        _steps.Add(step);
    }

    private void DetachAll()
    {
        foreach (MacroStepViewModel step in _steps)
        {
            step.PropertyChanged -= OnStepPropertyChanged;
        }
    }

    private void OnStepPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Refresh();

    private void Renumber()
    {
        for (int index = 0; index < _steps.Count; index++)
        {
            _steps[index].Index = index + 1;
        }
    }

    private void Refresh()
    {
        PreviewText = _steps.Count == 0
            ? Loc.T("me.empty")
            : string.Join(" → ", _steps.Select(step => $"{step.Index}. {step.Describe}"));
        RefreshValidation();
    }

    private void RefreshValidation()
    {
        _errors = Validate();
        ErrorText = _errors.Count == 0 ? null : string.Join("；", _errors);
        OnPropertyChanged(nameof(Errors));
        OnPropertyChanged(nameof(CanSave));
    }
}
