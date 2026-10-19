using System.Collections.Generic;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using PadShift.App.Infrastructure;
using PadShift.Core.Profile;

namespace PadShift.App.ViewModels;

/// <summary>One row of the macro sequence. The file format only stores the concrete element, so the
/// editor infers the kind when it loads an existing sequence.</summary>
public enum MacroStepKind
{
    Keyboard,
    MouseButton,
    MouseMove,
    Wheel,
    GamepadButton,
    Pause,
    Break,

    /// <summary>A macro element this client cannot edit (user command, rumble, stick axis, ...).
    /// It is kept read-only so opening and saving a macro never loses it.</summary>
    PassThrough,
}

/// <summary>How a keyboard/mouse/gamepad step is emitted: dark down/up, or the file's implicit
/// "press and release" form (no <c>action</c> member).</summary>
public enum MacroAction
{
    Auto,
    Down,
    Up,
}

public sealed record MacroActionOption(MacroAction Action, string Label);

public sealed record MacroDirectionOption(string Direction, string Label);

/// <summary>Choices shared by every macro step row.</summary>
public static class MacroStepOptions
{
    public static IReadOnlyList<MacroActionOption> Actions { get; } =
    [
        new(MacroAction.Auto, "自动"),
        new(MacroAction.Down, "按下"),
        new(MacroAction.Up, "抬起"),
    ];

    public static IReadOnlyList<MacroDirectionOption> Directions { get; } =
    [
        new("up", "上"),
        new("down", "下"),
        new("left", "左"),
        new("right", "右"),
    ];

    public static IReadOnlyList<MouseButtonOption> MouseButtonChoices { get; } =
    [
        new(MouseButtons.Left, "鼠标左键"),
        new(MouseButtons.Right, "鼠标右键"),
        new(MouseButtons.Middle, "鼠标中键"),
        new(MouseButtons.X1, "侧键 1"),
        new(MouseButtons.X2, "侧键 2"),
    ];

    public static IReadOnlyList<GamepadTargetOption> PadButtons { get; } =
        GamepadButtons.All.Select(pair => new GamepadTargetOption(pair.Id, pair.Label)).ToList();

    public static string ActionLabel(MacroAction action) => action switch
    {
        MacroAction.Down => "按下",
        MacroAction.Up => "抬起",
        _ => "自动",
    };

    public static MacroActionOption ActionOptionOf(MacroAction action) =>
        Actions.FirstOrDefault(option => option.Action == action) ?? Actions[0];

    public static string DirectionLabel(string direction) =>
        Directions.FirstOrDefault(option => option.Direction == direction)?.Label ?? direction;

    public static string NormalizeDirection(string? direction)
    {
        string text = (direction ?? "").Trim().ToLowerInvariant();
        foreach (MacroDirectionOption option in Directions)
        {
            if (option.Direction == text)
            {
                return option.Direction;
            }
        }

        return "right";
    }
}

/// <summary>
/// One editable macro step. Existing elements are remembered: while the user does not touch a row,
/// <see cref="ToElement"/> returns the original element object, which keeps the JSON of a real
/// profile byte-for-byte stable on an open/save round-trip.
/// </summary>
public sealed class MacroStepViewModel : ObservableObject
{
    public const long MaxPauseMs = 600_000;
    public const long MaxMousePixels = 32_767;
    public const long MaxWheelNotches = 10;

    private MacroElement? _source;
    private readonly JsonElement? _sourceAction;
    private string? _description;
    private bool _dirty;
    private int _index;
    private int _dik;
    private MacroAction _action;
    private long _mouseButtonId = MouseButtons.Left;
    private string _direction = "right";
    private long _amount = 10;
    private long _pauseMs = 100;
    private long _gamepadButtonId = GamepadButtons.A;
    private long _gamepadId = 1;

    private MacroStepViewModel(
        MacroStepKind kind,
        MacroElement? source = null,
        JsonElement? sourceAction = null,
        string? description = null)
    {
        Kind = kind;
        _source = source;
        _sourceAction = sourceAction;
        _description = description;
    }

    // ------------------------------------------------------------------ factories

    public static MacroStepViewModel Keyboard(MacroAction action = MacroAction.Down, int dik = 0, string? description = null) =>
        new(MacroStepKind.Keyboard, description: description) { _action = action, _dik = Math.Clamp(dik, 0, 255) };

    public static MacroStepViewModel MouseButton(long buttonId = MouseButtons.Left, MacroAction action = MacroAction.Auto) =>
        new(MacroStepKind.MouseButton) { _mouseButtonId = Math.Clamp(buttonId, 1, 5), _action = action };

    public static MacroStepViewModel MouseMove(string direction = "right", long pixels = 10) =>
        new(MacroStepKind.MouseMove) { _direction = MacroStepOptions.NormalizeDirection(direction), _amount = ClampAmount(pixels) };

    public static MacroStepViewModel Wheel(string direction = "down", long notches = 1) =>
        new(MacroStepKind.Wheel) { _direction = MacroStepOptions.NormalizeDirection(direction), _amount = Math.Clamp(notches, 1, MaxWheelNotches) };

    public static MacroStepViewModel GamepadButton(long buttonId = GamepadButtons.A, MacroAction action = MacroAction.Auto, long padId = 1) =>
        new(MacroStepKind.GamepadButton) { _gamepadButtonId = Math.Clamp(buttonId, 0, 2001), _action = action, _gamepadId = Math.Clamp(padId, 1, 8) };

    public static MacroStepViewModel Pause(long milliseconds = 100) =>
        new(MacroStepKind.Pause) { _pauseMs = Math.Clamp(milliseconds, 0, MaxPauseMs) };

    public static MacroStepViewModel Break() => new(MacroStepKind.Break);

    /// <summary>Infers the editable step for a file element; unknown elements become pass-through rows.</summary>
    public static MacroStepViewModel? FromElement(MacroElement? element)
    {
        if (element is null)
        {
            return null;
        }

        if (element.Pause is { } pause)
        {
            var step = new MacroStepViewModel(MacroStepKind.Pause, element) { _pauseMs = Math.Clamp(pause.Value, 0, MaxPauseMs) };
            return step;
        }

        if (element.Break is { } breaker && breaker.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            return new MacroStepViewModel(MacroStepKind.Break, element);
        }

        if (element.Keyboard is { } keyboard)
        {
            var step = new MacroStepViewModel(MacroStepKind.Keyboard, element, keyboard.Action, keyboard.Description)
            {
                _dik = (int)Math.Clamp(keyboard.ButtonId, 0, 255),
            };
            step._action = ParseAction(keyboard.Action);
            return step;
        }

        if (element.Mouse is { } mouse)
        {
            if (mouse.ButtonId is { } buttonId)
            {
                var step = new MacroStepViewModel(MacroStepKind.MouseButton, element, mouse.Action)
                {
                    _mouseButtonId = Math.Clamp(buttonId, 1, 5),
                };
                step._action = ParseAction(mouse.Action);
                return step;
            }

            if (mouse.Direction is { ValueKind: JsonValueKind.String } direction)
            {
                var step = new MacroStepViewModel(MacroStepKind.MouseMove, element)
                {
                    _direction = MacroStepOptions.NormalizeDirection(direction.GetString()),
                    _amount = ClampAmount(mouse.Value ?? 10),
                };
                return step;
            }

            if (mouse.Wheel is { ValueKind: JsonValueKind.String } wheel)
            {
                var step = new MacroStepViewModel(MacroStepKind.Wheel, element)
                {
                    _direction = MacroStepOptions.NormalizeDirection(wheel.GetString()),
                    _amount = Math.Clamp(mouse.Value ?? 1, 1, MaxWheelNotches),
                };
                return step;
            }
        }

        if (element.Gamepad is { } gamepad && gamepad.Absolute != true)
        {
            var step = new MacroStepViewModel(MacroStepKind.GamepadButton, element, gamepad.Action)
            {
                _gamepadButtonId = Math.Clamp(gamepad.ButtonId, 0, 2001),
                _gamepadId = Math.Clamp(gamepad.Id, 1, 8),
            };
            step._action = ParseAction(gamepad.Action);
            return step;
        }

        return new MacroStepViewModel(MacroStepKind.PassThrough, element);
    }

    // ------------------------------------------------------------------ identity

    public MacroStepKind Kind { get; }

    public bool IsEditable => Kind != MacroStepKind.PassThrough;

    /// <summary>1-based position in the sequence; maintained by <see cref="MacroEditorViewModel"/>.</summary>
    public int Index
    {
        get => _index;
        internal set
        {
            if (SetProperty(ref _index, value))
            {
                OnPropertyChanged(nameof(Describe));
            }
        }
    }

    public string KindLabel => Kind switch
    {
        MacroStepKind.Keyboard => "键盘",
        MacroStepKind.MouseButton => "鼠标按键",
        MacroStepKind.MouseMove => "鼠标移动",
        MacroStepKind.Wheel => "滚轮",
        MacroStepKind.GamepadButton => "手柄按键",
        MacroStepKind.Pause => "暂停",
        MacroStepKind.Break => "分隔",
        _ => "原样保留",
    };

    /// <summary>Human-readable one-line description used by the live preview.</summary>
    public string Describe => Kind switch
    {
        MacroStepKind.Keyboard => $"键盘 {MacroStepOptions.ActionLabel(Action)} {KeyDisplay}",
        MacroStepKind.MouseButton => $"{SelectedMouseButtonOption.Label} {MacroStepOptions.ActionLabel(Action)}",
        MacroStepKind.MouseMove => $"鼠标移动 {MacroStepOptions.DirectionLabel(Direction)} {Amount} 像素",
        MacroStepKind.Wheel => $"滚轮 {MacroStepOptions.DirectionLabel(Direction)} {Amount} 格",
        MacroStepKind.GamepadButton => $"手柄 {SelectedPadButtonOption.Label} {MacroStepOptions.ActionLabel(Action)}",
        MacroStepKind.Pause => $"暂停 {PauseMs} ms",
        MacroStepKind.Break => "分隔（Break）",
        _ => PassThroughLabel,
    };

    private string PassThroughLabel
    {
        get
        {
            if (_source?.UserCommand is not null)
            {
                return "用户命令（原样保留）";
            }

            if (_source?.Scroll is not null)
            {
                return "触控/滚轮滑条（原样保留）";
            }

            if (_source?.Extra is { Count: > 0 } extra && extra.ContainsKey("rumble"))
            {
                return "震动（原样保留）";
            }

            return "其他元素（原样保留）";
        }
    }

    // ------------------------------------------------------------------ editable values

    public IReadOnlyList<MacroActionOption> ActionOptions => MacroStepOptions.Actions;

    public MacroActionOption SelectedActionOption
    {
        get => MacroStepOptions.ActionOptionOf(_action);
        set
        {
            if (value is not null)
            {
                Action = value.Action;
            }
        }
    }

    public MacroAction Action
    {
        get => _action;
        set
        {
            if (SetProperty(ref _action, value))
            {
                OnPropertyChanged(nameof(SelectedActionOption));
                OnPropertyChanged(nameof(Describe));
                MarkDirty();
            }
        }
    }

    /// <summary>DIK scan code of a keyboard step.</summary>
    public int Dik
    {
        get => _dik;
        set
        {
            if (SetProperty(ref _dik, Math.Clamp(value, 0, 255)))
            {
                OnPropertyChanged(nameof(KeyDisplay));
                OnPropertyChanged(nameof(Describe));
                MarkDirty();
            }
        }
    }

    public string KeyDisplay => _dik > 0 ? DikNames.FromDik(_dik) : "未设置";

    /// <summary>Applies a captured key (DIK + display name taken from the capture dialog).</summary>
    public void SetKeyboardKey(int dik, string? description = null)
    {
        if (dik <= 0)
        {
            return;
        }

        _dik = Math.Clamp(dik, 1, 255);
        if (!string.IsNullOrWhiteSpace(description))
        {
            _description = description!.Trim();
        }

        OnPropertyChanged(nameof(Dik));
        OnPropertyChanged(nameof(KeyDisplay));
        OnPropertyChanged(nameof(Describe));
        MarkDirty();
    }

    public IReadOnlyList<MouseButtonOption> MouseButtonOptions => MacroStepOptions.MouseButtonChoices;

    public MouseButtonOption SelectedMouseButtonOption
    {
        get => MacroStepOptions.MouseButtonChoices.FirstOrDefault(option => option.ButtonId == _mouseButtonId)
               ?? MacroStepOptions.MouseButtonChoices[0];
        set
        {
            if (value is not null)
            {
                MouseButtonId = value.ButtonId;
            }
        }
    }

    public long MouseButtonId
    {
        get => _mouseButtonId;
        set
        {
            if (SetProperty(ref _mouseButtonId, Math.Clamp(value, 1, 5)))
            {
                OnPropertyChanged(nameof(SelectedMouseButtonOption));
                OnPropertyChanged(nameof(Describe));
                MarkDirty();
            }
        }
    }

    public IReadOnlyList<MacroDirectionOption> DirectionOptions => MacroStepOptions.Directions;

    public MacroDirectionOption SelectedDirectionOption
    {
        get => MacroStepOptions.Directions.FirstOrDefault(option => option.Direction == _direction)
               ?? MacroStepOptions.Directions[3];
        set
        {
            if (value is not null)
            {
                Direction = value.Direction;
            }
        }
    }

    public string Direction
    {
        get => _direction;
        set
        {
            if (SetProperty(ref _direction, MacroStepOptions.NormalizeDirection(value)))
            {
                OnPropertyChanged(nameof(SelectedDirectionOption));
                OnPropertyChanged(nameof(Describe));
                MarkDirty();
            }
        }
    }

    /// <summary>Mouse-move distance in pixels or wheel notches, depending on <see cref="Kind"/>.</summary>
    public long Amount
    {
        get => _amount;
        set
        {
            if (SetProperty(ref _amount, Math.Clamp(value, 0, MaxMousePixels)))
            {
                OnPropertyChanged(nameof(Describe));
                MarkDirty();
            }
        }
    }

    public long PauseMs
    {
        get => _pauseMs;
        set
        {
            if (SetProperty(ref _pauseMs, Math.Clamp(value, 0, MaxPauseMs)))
            {
                OnPropertyChanged(nameof(Describe));
                MarkDirty();
            }
        }
    }

    public IReadOnlyList<GamepadTargetOption> PadButtonOptions => MacroStepOptions.PadButtons;

    public GamepadTargetOption SelectedPadButtonOption
    {
        get => MacroStepOptions.PadButtons.FirstOrDefault(option => option.ButtonId == _gamepadButtonId)
               ?? MacroStepOptions.PadButtons[0];
        set
        {
            if (value is not null)
            {
                GamepadButtonId = value.ButtonId;
            }
        }
    }

    public long GamepadButtonId
    {
        get => _gamepadButtonId;
        set
        {
            if (SetProperty(ref _gamepadButtonId, Math.Clamp(value, 0, 2001)))
            {
                OnPropertyChanged(nameof(SelectedPadButtonOption));
                OnPropertyChanged(nameof(Describe));
                MarkDirty();
            }
        }
    }

    public long GamepadId
    {
        get => _gamepadId;
        set
        {
            if (SetProperty(ref _gamepadId, Math.Clamp(value, 1, 8)))
            {
                MarkDirty();
            }
        }
    }

    // ------------------------------------------------------------------ serialization

    /// <summary>Rebuilds the file element for this step (or returns the untouched source element).</summary>
    public MacroElement ToElement()
    {
        if (_source is not null && !_dirty)
        {
            return _source;
        }

        switch (Kind)
        {
            case MacroStepKind.Keyboard:
                return Finish(new MacroElement
                {
                    Keyboard = new KeyboardOutput
                    {
                        ButtonId = _dik,
                        Description = _description,
                        Action = BuildAction(),
                        Pulsation = _source?.Keyboard?.Pulsation,
                        Extra = CopyExtra(_source?.Keyboard?.Extra),
                    },
                });

            case MacroStepKind.MouseButton:
                return Finish(new MacroElement
                {
                    Mouse = new MouseOutput
                    {
                        ButtonId = _mouseButtonId,
                        Action = BuildAction(),
                        Flick = _source?.Mouse?.Flick,
                        Extra = CopyExtra(_source?.Mouse?.Extra),
                    },
                });

            case MacroStepKind.MouseMove:
                return Finish(new MacroElement
                {
                    Mouse = new MouseOutput
                    {
                        Direction = JsonSerializer.SerializeToElement(_direction),
                        Value = _amount,
                        Flick = _source?.Mouse?.Flick,
                        Extra = CopyExtra(_source?.Mouse?.Extra),
                    },
                });

            case MacroStepKind.Wheel:
                return Finish(new MacroElement
                {
                    Mouse = new MouseOutput
                    {
                        Wheel = JsonSerializer.SerializeToElement(_direction),
                        Value = _amount,
                        Flick = _source?.Mouse?.Flick,
                        Extra = CopyExtra(_source?.Mouse?.Extra),
                    },
                });

            case MacroStepKind.GamepadButton:
                return Finish(new MacroElement
                {
                    Gamepad = new GamepadOutput
                    {
                        Id = _gamepadId,
                        ButtonId = _gamepadButtonId,
                        Description = _description,
                        Action = BuildAction(),
                        Pulsation = _source?.Gamepad?.Pulsation,
                        Extra = CopyExtra(_source?.Gamepad?.Extra),
                    },
                });

            case MacroStepKind.Pause:
                return Finish(new MacroElement
                {
                    Pause = new PauseOutput
                    {
                        Value = _pauseMs,
                        Extra = CopyExtra(_source?.Pause?.Extra),
                    },
                });

            case MacroStepKind.Break:
                return Finish(new MacroElement { Break = JsonSerializer.SerializeToElement(new { }) });

            default:
                return _source ?? new MacroElement();
        }
    }

    /// <summary>Re-validates this row; null means the row is complete.</summary>
    public string? Validate() => Kind switch
    {
        MacroStepKind.Keyboard => _dik is > 0 and < 256 ? null : "请捕获键盘按键（DIK 1–255）",
        MacroStepKind.MouseButton => _mouseButtonId is >= 1 and <= 5 ? null : "鼠标按键编号必须在 1–5",
        MacroStepKind.MouseMove => _amount is > 0 and <= MaxMousePixels ? null : $"鼠标移动需要 1–{MaxMousePixels} 像素",
        MacroStepKind.Wheel => _amount is >= 1 and <= MaxWheelNotches ? null : $"滚轮格数必须在 1–{MaxWheelNotches}",
        MacroStepKind.GamepadButton => _gamepadButtonId > 0 ? null : "请选择手柄按键",
        MacroStepKind.Pause => _pauseMs is >= 0 and <= MaxPauseMs ? null : $"暂停范围 0–{MaxPauseMs} ms",
        MacroStepKind.Break => null,
        _ => _source is not null ? null : "未知步骤",
    };

    /// <summary>Detaches the step from its source element so a duplicate is rebuilt independently.</summary>
    internal void Detach() => _source = null;

    private MacroElement Finish(MacroElement element)
    {
        element.Extra = CopyExtra(_source?.Extra);
        return element;
    }

    private JsonElement? BuildAction() => _action switch
    {
        MacroAction.Down => JsonSerializer.SerializeToElement("down"),
        MacroAction.Up => JsonSerializer.SerializeToElement("up"),
        _ => _sourceAction,
    };

    private void MarkDirty() => _dirty = true;

    private static MacroAction ParseAction(JsonElement? action)
    {
        if (action is { ValueKind: JsonValueKind.String } element)
        {
            string? text = element.GetString();
            if (string.Equals(text, "down", StringComparison.OrdinalIgnoreCase))
            {
                return MacroAction.Down;
            }

            if (string.Equals(text, "up", StringComparison.OrdinalIgnoreCase))
            {
                return MacroAction.Up;
            }
        }

        return MacroAction.Auto;
    }

    private static long ClampAmount(long value) => Math.Clamp(value, 1, MaxMousePixels);

    private static Dictionary<string, JsonElement>? CopyExtra(Dictionary<string, JsonElement>? extra) =>
        extra is { Count: > 0 } ? new Dictionary<string, JsonElement>(extra) : null;
}
