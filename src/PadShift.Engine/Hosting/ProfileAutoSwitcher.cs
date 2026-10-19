using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using PadShift.Core.Host;
using PadShift.Core.Profile;

namespace PadShift.Engine.Hosting;

/// <summary>
/// the reference tool 的「每游戏进程关联/自动检测」：把每个 profile 的 <c>config.processNames</c> 建成
/// 「进程名 → profile 路径」索引，监听 <see cref="ProcessWatcher"/> 的启动/退出事件，
/// 在游戏启动时把匹配的 profile 应用到当前槽位，在游戏退出时恢复到用户手动应用过的
/// 「默认」profile。
/// </summary>
/// <remarks>
/// <para>匹配规则（全部大小写不敏感，进程名先去掉路径并补齐 <c>.exe</c>）：</para>
/// <list type="number">
/// <item><description>只有一个 profile 匹配 → 应用到每个已连接控制器的当前槽位。</description></item>
/// <item><description>多个 profile 匹配 → 只接受目录名等于进程父目录名（exe 所在目录名）的那一个；
/// 仍然不是唯一命中就记录日志并跳过。</description></item>
/// <item><description>没有匹配 → 什么都不做。</description></item>
/// <item><description>进程退出且没有其它自动切换仍在生效 → 恢复该控制器的默认 profile
/// （默认 profile = 最近一次非自动切换的 <see cref="IRemapHost.ApplyConfigAsync"/>）。</description></item>
/// </list>
/// <para>
/// 索引是惰性构建并按 <see cref="ProfileLibrary.Revision"/> 失效；profile 解析失败只会跳过
/// 该文件，不会让 watcher 或宿主崩溃。
/// </para>
/// </remarks>
public sealed class ProfileAutoSwitcher : IDisposable
{
    private sealed record DefaultProfile(string Path, int Slot);

    private readonly object _gate = new();
    private readonly IRemapHost _host;
    private readonly ProfileLibrary _library;
    private readonly Action<string>? _log;
    private readonly Dictionary<Guid, DefaultProfile> _defaults = new();
    private readonly HashSet<int> _activeAutoPids = new();

    private Dictionary<string, List<string>>? _index;
    private int _indexedRevision = -1;
    private ProcessWatcher? _watcher;
    private int _suppressDefaultTracking;
    private bool _disposed;

    public ProfileAutoSwitcher(IRemapHost host, ProfileLibrary library, Action<string>? log = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _log = log;
        _host.Event += OnHostEvent;
    }

    /// <summary>Raised after an auto-switched profile was applied successfully.</summary>
    public event EventHandler<AutoSwitchAppliedEvent>? Applied;

    /// <summary>
    /// Current process-name index snapshot (key = normalised process name, value = profile paths).
    /// Building it loads every profile lazily; used by tests and diagnostics.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Index
    {
        get
        {
            EnsureIndex();
            lock (_gate)
            {
                var copy = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, List<string>> pair in _index ?? new Dictionary<string, List<string>>())
                {
                    copy[pair.Key] = pair.Value.ToArray();
                }

                return copy;
            }
        }
    }

    /// <summary>Subscribe to a watcher (replacing any previous subscription).</summary>
    public void Attach(ProcessWatcher watcher)
    {
        ArgumentNullException.ThrowIfNull(watcher);
        ProcessWatcher? previous;
        lock (_gate)
        {
            if (ReferenceEquals(_watcher, watcher))
            {
                return;
            }

            previous = _watcher;
            _watcher = watcher;
        }

        if (previous is not null)
        {
            previous.ProcessStarted -= OnProcessStartedEvent;
            previous.ProcessStopped -= OnProcessStoppedEvent;
        }

        watcher.ProcessStarted += OnProcessStartedEvent;
        watcher.ProcessStopped += OnProcessStoppedEvent;
    }

    /// <summary>Stop listening to the current watcher.</summary>
    public void Detach()
    {
        ProcessWatcher? watcher;
        lock (_gate)
        {
            watcher = _watcher;
            _watcher = null;
        }

        if (watcher is null)
        {
            return;
        }

        watcher.ProcessStarted -= OnProcessStartedEvent;
        watcher.ProcessStopped -= OnProcessStoppedEvent;
    }

    /// <summary>
    /// Resolve a process to exactly one profile path. A single match wins; an ambiguous match is
    /// broken by comparing the profile's folder name with <paramref name="parentFolder"/>
    /// (the folder that contains the process executable). Returns false when nothing or more
    /// than one candidate remains.
    /// </summary>
    public bool TryResolveProfile(string? processName, string? parentFolder, out string? profilePath)
    {
        profilePath = null;
        string key = ProcessNameUtility.Normalize(processName);
        if (key.Length == 0)
        {
            return false;
        }

        EnsureIndex();

        string[] candidates;
        lock (_gate)
        {
            if (_index is null || !_index.TryGetValue(key, out List<string>? matches) || matches.Count == 0)
            {
                return false;
            }

            candidates = matches.ToArray();
        }

        if (candidates.Length == 1)
        {
            profilePath = candidates[0];
            return true;
        }

        if (string.IsNullOrWhiteSpace(parentFolder))
        {
            Log($"auto-switch: {key} matches {candidates.Length} profiles ({string.Join(", ", candidates)}); " +
                "no process folder is available to break the tie, skipping.");
            return false;
        }

        var folderMatches = new List<string>(1);
        foreach (string candidate in candidates)
        {
            if (string.Equals(ProfileFolderName(candidate), parentFolder.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                folderMatches.Add(candidate);
            }
        }

        if (folderMatches.Count == 1)
        {
            profilePath = folderMatches[0];
            return true;
        }

        Log($"auto-switch: {key} is ambiguous in folder '{parentFolder}' " +
            $"({string.Join(", ", candidates)}); skipping.");
        return false;
    }

    /// <summary>Handle a process start. Safe to call directly (tests) or from the watcher.</summary>
    public void OnProcessStarted(string processName, int pid, string? parentFolder = null)
    {
        if (_disposed)
        {
            return;
        }

        string key = ProcessNameUtility.Normalize(processName);
        if (key.Length == 0)
        {
            return;
        }

        if (!_host.RemapEnabled)
        {
            Log($"auto-switch: {key} started, but remap is off; not applying a profile.");
            return;
        }

        if (!TryResolveProfile(key, parentFolder, out string? profilePath) || profilePath is null)
        {
            return;
        }

        IReadOnlyList<HostController> controllers;
        try
        {
            controllers = _host.Controllers;
        }
        catch (Exception exception)
        {
            Log($"auto-switch: cannot enumerate controllers: {exception.Message}");
            return;
        }

        if (controllers.Count == 0)
        {
            Log($"auto-switch: {key} matched {profilePath}, but no controller is connected.");
            return;
        }

        foreach (HostController controller in controllers)
        {
            int slot = CurrentSlotOf(controller.Id);
            ApplyAuto(controller.Id, profilePath, key, slot, pid);
        }
    }

    /// <summary>Handle a process stop; restores the remembered default when nothing is left.</summary>
    public void OnProcessStopped(string processName, int pid)
    {
        if (_disposed)
        {
            return;
        }

        bool wasTracked;
        bool anyLeft;
        lock (_gate)
        {
            wasTracked = _activeAutoPids.Remove(pid);
            anyLeft = _activeAutoPids.Count > 0;
        }

        if (!wasTracked)
        {
            return;
        }

        if (anyLeft)
        {
            Log($"auto-switch: {processName} stopped, but another auto-switched process is still running.");
            return;
        }

        RestoreDefaults();
    }

    /// <summary>True when a manual (non-auto-switched) profile has been remembered for this controller.</summary>
    public bool TryGetDefaultProfile(Guid controllerId, out string? profilePath, out int slot)
    {
        lock (_gate)
        {
            if (_defaults.TryGetValue(controllerId, out DefaultProfile? profile))
            {
                profilePath = profile.Path;
                slot = profile.Slot;
                return true;
            }
        }

        profilePath = null;
        slot = 0;
        return false;
    }

    /// <summary>Drop the cached process → profile index; the next lookup rebuilds it.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _index = null;
            _indexedRevision = -1;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Detach();
        _host.Event -= OnHostEvent;
    }

    // --------------------------------------------------------------- internals

    private void OnProcessStartedEvent(object? sender, ProcessStartedEventArgs e) =>
        OnProcessStarted(e.Name, e.Pid, e.ParentFolder);

    private void OnProcessStoppedEvent(object? sender, ProcessStoppedEventArgs e) =>
        OnProcessStopped(e.Name, e.Pid);

    /// <summary>Remember every non-auto <c>ConfigAppliedEvent</c> as the controller's default.</summary>
    private void OnHostEvent(object? sender, HostEvent hostEvent)
    {
        if (_disposed || hostEvent is not ConfigAppliedEvent applied)
        {
            return;
        }

        if (Volatile.Read(ref _suppressDefaultTracking) > 0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(applied.ConfigPath))
        {
            return;
        }

        lock (_gate)
        {
            _defaults[applied.ControllerId] = new DefaultProfile(applied.ConfigPath, applied.Slot);
        }
    }

    private void ApplyAuto(Guid controllerId, string profilePath, string processName, int slot, int pid)
    {
        ApplyConfigResult result;
        Interlocked.Increment(ref _suppressDefaultTracking);
        try
        {
            result = _host.ApplyConfigAsync(controllerId, profilePath, slot).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Log($"auto-switch: applying '{profilePath}' to {controllerId} failed: {exception.Message}");
            return;
        }
        finally
        {
            Interlocked.Decrement(ref _suppressDefaultTracking);
        }

        if (!result.Success)
        {
            Log($"auto-switch: applying '{profilePath}' to {controllerId} was rejected: {result.Error}");
            return;
        }

        lock (_gate)
        {
            _activeAutoPids.Add(pid);
        }

        RaiseApplied(new AutoSwitchAppliedEvent(
            controllerId,
            profilePath,
            processName,
            DateTimeOffset.UtcNow));
    }

    private void RestoreDefaults()
    {
        KeyValuePair<Guid, DefaultProfile>[] targets;
        lock (_gate)
        {
            if (_defaults.Count == 0)
            {
                return;
            }

            targets = new KeyValuePair<Guid, DefaultProfile>[_defaults.Count];
            int index = 0;
            foreach (KeyValuePair<Guid, DefaultProfile> pair in _defaults)
            {
                targets[index++] = pair;
            }
        }

        if (!_host.RemapEnabled)
        {
            Log("auto-switch: last game exited, but remap is off; keeping the current slot untouched.");
            return;
        }

        foreach (KeyValuePair<Guid, DefaultProfile> target in targets)
        {
            ApplyConfigResult result;
            Interlocked.Increment(ref _suppressDefaultTracking);
            try
            {
                result = _host
                    .ApplyConfigAsync(target.Key, target.Value.Path, target.Value.Slot)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception exception)
            {
                Log($"auto-switch: restoring default '{target.Value.Path}' failed: {exception.Message}");
                continue;
            }
            finally
            {
                Interlocked.Decrement(ref _suppressDefaultTracking);
            }

            if (!result.Success)
            {
                Log($"auto-switch: restoring default '{target.Value.Path}' was rejected: {result.Error}");
            }
        }
    }

    private int CurrentSlotOf(Guid controllerId)
    {
        try
        {
            return _host.GetCurrentSlot(controllerId);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>Lazily build (and cache) the process → profile index for the current library revision.</summary>
    private void EnsureIndex()
    {
        int revision = _library.Revision;
        lock (_gate)
        {
            if (_index is not null && _indexedRevision == revision)
            {
                return;
            }
        }

        var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var namesByPath = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        foreach (string path in _library.Profiles)
        {
            if (!namesByPath.TryGetValue(path, out string[]? names))
            {
                names = LoadProcessNames(path);
                namesByPath[path] = names;
            }

            foreach (string name in names)
            {
                if (!index.TryGetValue(name, out List<string>? profiles))
                {
                    profiles = new List<string>(1);
                    index[name] = profiles;
                }

                if (!profiles.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    profiles.Add(path);
                }
            }
        }

        lock (_gate)
        {
            // A concurrent refresh may have produced a newer revision while we were loading;
            // storing the old revision guarantees the next lookup rebuilds.
            _index = index;
            _indexedRevision = revision;
        }
    }

    private string[] LoadProcessNames(string profilePath)
    {
        try
        {
            GamepadProfile profile = ProfileIo.Load(profilePath);
            List<string>? configured = profile.Config?.ProcessNames;
            if (configured is null || configured.Count == 0)
            {
                return Array.Empty<string>();
            }

            var names = new List<string>(configured.Count);
            foreach (string candidate in configured)
            {
                string name = ProcessNameUtility.Normalize(candidate);
                if (name.Length > 0 && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    names.Add(name);
                }
            }

            return names.ToArray();
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or System.Security.SecurityException
                                              or JsonException
                                              or NotSupportedException
                                              or ArgumentException)
        {
            Log($"auto-switch: cannot read process names from '{profilePath}': {exception.Message}");
            return Array.Empty<string>();
        }
    }

    /// <summary>Folder that directly contains the profile (the reference tool stores one folder per game).</summary>
    private static string ProfileFolderName(string profilePath)
    {
        try
        {
            return Path.GetFileName(Path.GetDirectoryName(profilePath)) ?? string.Empty;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    private void RaiseApplied(AutoSwitchAppliedEvent appliedEvent)
    {
        EventHandler<AutoSwitchAppliedEvent>? handler = Applied;
        if (handler is null)
        {
            return;
        }

        foreach (EventHandler<AutoSwitchAppliedEvent> subscriber in handler.GetInvocationList())
        {
            try
            {
                subscriber(this, appliedEvent);
            }
            catch (Exception)
            {
                // A broken listener must not stop the process watcher.
            }
        }
    }

    private void Log(string message)
    {
        Action<string>? log = _log;
        if (log is null)
        {
            return;
        }

        try
        {
            log(message);
        }
        catch (Exception)
        {
            // Logging must never break switching.
        }
    }
}
