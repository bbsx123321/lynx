using System;
using System.Collections.Generic;
using PadShift.Core;

namespace PadShift.Engine.Hosting;

/// <summary>
/// 手柄组合键长按检测。每个控制器一个实例；<see cref="Detect"/> 只读 <see cref="ControllerState"/>
/// 并按调用方给的单调毫秒时钟推进，不分配、不加锁、不读系统时间，适合放在轮询热路径上。
/// </summary>
/// <remarks>
/// 规则：任意时刻只有「第一个所有按钮都按住」的热键处于激活态；它连续按住 <see cref="SlotHotkey.HoldMs"/>
/// 后返回一次槽位号（1..4），之后保持按住不会重复返回，全部按钮松开后才允许下一次触发。
/// 另一个热键部分/全部按住时不会打断当前热键，也不会让自己提前计时。
/// </remarks>
public sealed class SlotHotkeyDetector
{
    private readonly int[] _slots;
    private readonly int[][] _buttons;
    private readonly int[] _holdMs;
    private readonly long[] _startedAt;
    private readonly bool[] _fired;

    public SlotHotkeyDetector(IReadOnlyList<SlotHotkey>? hotkeys)
    {
        if (hotkeys is null || hotkeys.Count == 0)
        {
            _slots = Array.Empty<int>();
            _buttons = Array.Empty<int[]>();
            _holdMs = Array.Empty<int>();
            _startedAt = Array.Empty<long>();
            _fired = Array.Empty<bool>();
            return;
        }

        _slots = new int[hotkeys.Count];
        _buttons = new int[hotkeys.Count][];
        _holdMs = new int[hotkeys.Count];
        _startedAt = new long[hotkeys.Count];
        _fired = new bool[hotkeys.Count];

        for (int i = 0; i < hotkeys.Count; i++)
        {
            SlotHotkey hotkey = hotkeys[i]
                ?? throw new ArgumentException("SlotHotkeys must not contain null entries.", nameof(hotkeys));
            _slots[i] = hotkey.Slot;
            _buttons[i] = hotkey.Buttons;
            _holdMs[i] = hotkey.HoldMs;
            _startedAt[i] = -1;
        }
    }

    /// <summary>没有配置任何热键时 <see cref="Detect"/> 可以走最短路径。</summary>
    public bool IsEmpty => _slots.Length == 0;

    /// <summary>
    /// 采样一次输入状态。返回本 tick 刚触发切换的目标槽位（1..4），没有触发时返回 -1。
    /// 同一段连续按住只会返回一次。
    /// </summary>
    public int Detect(ControllerState state, long nowMs)
    {
        int count = _slots.Length;
        if (count == 0 || state is null)
        {
            return -1;
        }

        int active = -1;
        for (int i = 0; i < count; i++)
        {
            if (AllHeld(_buttons[i], state))
            {
                active = i;
                break;
            }
        }

        if (active < 0)
        {
            // 全部松开：清空所有热键的进度（含已触发标记），下次按下重新计时。
            for (int i = 0; i < count; i++)
            {
                _startedAt[i] = -1;
                _fired[i] = false;
            }

            return -1;
        }

        // 有热键处于按住状态时，其它热键既不推进计时也不允许触发。
        for (int i = 0; i < count; i++)
        {
            if (i == active)
            {
                continue;
            }

            _startedAt[i] = -1;
            _fired[i] = false;
        }

        if (_startedAt[active] < 0)
        {
            _startedAt[active] = nowMs;
        }

        if (!_fired[active] && nowMs - _startedAt[active] >= _holdMs[active])
        {
            _fired[active] = true;
            return _slots[active];
        }

        return -1;
    }

    private static bool AllHeld(int[] buttons, ControllerState state)
    {
        bool[] pressed = state.Buttons;
        for (int i = 0; i < buttons.Length; i++)
        {
            int button = buttons[i];
            if ((uint)button >= (uint)pressed.Length || !pressed[button])
            {
                return false;
            }
        }

        return true;
    }
}
