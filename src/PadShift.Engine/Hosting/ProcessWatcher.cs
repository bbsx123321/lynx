using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PadShift.Engine.Hosting;

/// <summary>One process as reported by an <see cref="IProcessSource"/>.</summary>
/// <param name="Pid">Windows process id (identity for the start/stop diff).</param>
/// <param name="Name">Executable name; may or may not include the extension.</param>
/// <param name="ParentFolder">
/// Name of the directory that contains the executable, when it can be read without
/// elevation (for example <c>Fortnite</c> for <c>...\Fortnite\FortniteClient-Win64-Shipping.exe</c>).
/// <see langword="null"/> when the path is not accessible; never throws out of the source.
/// </param>
public sealed record ProcessEntry(int Pid, string Name, string? ParentFolder);

/// <summary>
/// Process enumeration seam. The production implementation is <see cref="SystemProcessSource"/>;
/// tests inject a fake so that no real process is needed.
/// </summary>
public interface IProcessSource
{
    /// <summary>
    /// Return every process visible to the current user. Implementations must not throw:
    /// access-denied processes are skipped instead.
    /// </summary>
    IReadOnlyList<ProcessEntry> GetProcesses();
}

/// <summary>A process that appeared since the previous poll.</summary>
public sealed record ProcessStartedEventArgs(string Name, int Pid, string? ParentFolder);

/// <summary>A process that disappeared since the previous poll.</summary>
public sealed record ProcessStoppedEventArgs(string Name, int Pid);

/// <summary>
/// Polls <see cref="IProcessSource"/> and raises start/stop transitions. Deliberately does not
/// use WMI (which needs DCOM/service access) and never requests elevation: it only calls
/// <see cref="Process.GetProcesses()"/> through <see cref="SystemProcessSource"/> and ignores
/// processes whose details are access-denied. No exception ever escapes the watcher.
/// </summary>
public sealed class ProcessWatcher : IDisposable
{
    private readonly object _gate = new();
    private readonly IProcessSource _source;
    private readonly int _pollMs;
    private readonly Dictionary<int, string> _known = new();

    private bool _seeded;
    private bool _started;
    private bool _disposed;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public ProcessWatcher(IProcessSource source, int pollMs = 2000)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _pollMs = Math.Clamp(pollMs, 10, 3_600_000);
    }

    /// <summary>Effective poll period in milliseconds (clamped to 10 ms .. 1 h).</summary>
    public int PollMs => _pollMs;

    /// <summary>Raised for every (pid, name) pair that was not present in the previous snapshot.</summary>
    public event EventHandler<ProcessStartedEventArgs>? ProcessStarted;

    /// <summary>Raised for every (pid, name) pair that is gone from the current snapshot.</summary>
    public event EventHandler<ProcessStoppedEventArgs>? ProcessStopped;

    /// <summary>
    /// Seed the baseline and start polling on a background timer. The first snapshot never
    /// raises events (it only records what is already running); call <see cref="PollOnce"/>
    /// manually if deterministic transitions are needed.
    /// </summary>
    public void Start()
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                return;
            }

            SeedLocked();
            _started = true;
            _cts = new CancellationTokenSource();
            cts = _cts;
        }

        _loop = Task.Run(() => RunAsync(cts.Token));
    }

    /// <summary>Stop the background timer. Safe to call when not started; a later Start re-seeds.</summary>
    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_gate)
        {
            _started = false;
            cts = _cts;
            _cts = null;
            loop = _loop;
            _loop = null;
        }

        try
        {
            cts?.Cancel();
            loop?.Wait(TimeSpan.FromMilliseconds(500));
        }
        catch (Exception)
        {
            // Cancellation/aggregate exceptions during shutdown are expected.
        }

        cts?.Dispose();
    }

    /// <summary>
    /// Take one snapshot and raise the start/stop diff. The very first call only seeds the
    /// baseline (no events). Returns true when at least one event was raised. Never throws.
    /// </summary>
    public bool PollOnce()
    {
        if (_disposed)
        {
            return false;
        }

        Dictionary<int, ProcessEntry>? current;
        try
        {
            current = Snapshot();
        }
        catch (Exception)
        {
            // A broken source must never take the watcher (or the host) down.
            return false;
        }

        if (current is null)
        {
            // A failed snapshot is not "everything exited": leave the baseline untouched.
            return false;
        }

        var stopped = new List<ProcessStoppedEventArgs>();
        var started = new List<ProcessStartedEventArgs>();
        lock (_gate)
        {
            if (!_seeded)
            {
                _seeded = true;
                _known.Clear();
                foreach (KeyValuePair<int, ProcessEntry> pair in current)
                {
                    _known[pair.Key] = pair.Value.Name;
                }

                return false;
            }

            foreach (KeyValuePair<int, string> pair in _known)
            {
                if (!current.ContainsKey(pair.Key))
                {
                    stopped.Add(new ProcessStoppedEventArgs(pair.Value, pair.Key));
                }
            }

            foreach (KeyValuePair<int, ProcessEntry> pair in current)
            {
                if (_known.TryGetValue(pair.Key, out string? oldName))
                {
                    if (string.Equals(oldName, pair.Value.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // PID reuse: the identity is (pid, name), so this is a stop + start.
                    stopped.Add(new ProcessStoppedEventArgs(oldName, pair.Key));
                }

                started.Add(new ProcessStartedEventArgs(
                    pair.Value.Name,
                    pair.Key,
                    pair.Value.ParentFolder));
            }

            _known.Clear();
            foreach (KeyValuePair<int, ProcessEntry> pair in current)
            {
                _known[pair.Key] = pair.Value.Name;
            }
        }

        Raise(ProcessStopped, this, stopped);
        Raise(ProcessStarted, this, started);
        return stopped.Count > 0 || started.Count > 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }

    // --------------------------------------------------------------- internals

    private async Task RunAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_pollMs));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                try
                {
                    PollOnce();
                }
                catch (Exception)
                {
                    // PollOnce already swallows source failures; keep the timer alive regardless.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Read and normalise one snapshot. Returns <see langword="null"/> when the source itself
    /// failed so that callers can distinguish "no processes" from "could not look".
    /// </summary>
    private Dictionary<int, ProcessEntry>? Snapshot()
    {
        IReadOnlyList<ProcessEntry> entries;
        try
        {
            entries = _source.GetProcesses() ?? Array.Empty<ProcessEntry>();
        }
        catch (Exception)
        {
            return null;
        }

        var map = new Dictionary<int, ProcessEntry>();
        foreach (ProcessEntry entry in entries)
        {
            if (entry is null || entry.Pid <= 0)
            {
                continue;
            }

            string name = ProcessNameUtility.Normalize(entry.Name);
            if (name.Length == 0)
            {
                continue;
            }

            map[entry.Pid] = entry with { Name = name };
        }

        return map;
    }

    /// <summary>Seed the baseline without raising events; caller must hold <see cref="_gate"/>.</summary>
    private void SeedLocked()
    {
        if (_seeded)
        {
            return;
        }

        Dictionary<int, ProcessEntry>? snapshot;
        try
        {
            snapshot = Snapshot();
        }
        catch (Exception)
        {
            return;
        }

        if (snapshot is null)
        {
            // Stay unseeded so the next poll retries instead of treating everything as new.
            return;
        }

        _seeded = true;
        _known.Clear();
        foreach (KeyValuePair<int, ProcessEntry> pair in snapshot)
        {
            _known[pair.Key] = pair.Value.Name;
        }
    }

    /// <summary>Invoke every subscriber separately so one throwing handler cannot break the rest.</summary>
    private static void Raise<T>(EventHandler<T>? handler, object sender, List<T> args)
    {
        if (handler is null || args.Count == 0)
        {
            return;
        }

        foreach (EventHandler<T> subscriber in handler.GetInvocationList())
        {
            foreach (T arg in args)
            {
                try
                {
                    subscriber(sender, arg);
                }
                catch (Exception)
                {
                    // watchers must never throw; the host logs its own failures.
                }
            }
        }
    }
}

/// <summary>Event name normalisation shared by the watcher and the auto-switcher.</summary>
internal static class ProcessNameUtility
{
    /// <summary>
    /// Reduce a process name to a comparable key: strip any directory part, trim quotes, and
    /// add the implicit <c>.exe</c> when the name has no extension (the v2.6 protocol profiles normally
    /// store <c>Game.exe</c> while <see cref="Process.ProcessName"/> omits the extension).
    /// </summary>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        string value = name.Trim().Trim('"').Trim();
        try
        {
            value = Path.GetFileName(value);
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }

        if (value.Length == 0)
        {
            return string.Empty;
        }

        if (!Path.HasExtension(value))
        {
            value += ".exe";
        }

        return value;
    }
}

/// <summary>
/// Production <see cref="IProcessSource"/>: plain <see cref="Process.GetProcesses()"/>.
/// Access-denied processes (and processes that exit mid-enumeration) are skipped. No WMI,
/// no handle elevation, no <c>MainModule</c> requirement — the parent folder is best-effort.
/// </summary>
public sealed class SystemProcessSource : IProcessSource
{
    public IReadOnlyList<ProcessEntry> GetProcesses()
    {
        var list = new List<ProcessEntry>();
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception)
        {
            // Enumeration can fail on locked-down sessions; an empty snapshot is valid.
            return list;
        }

        foreach (Process process in processes)
        {
            try
            {
                int pid = process.Id;
                string name = process.ProcessName;
                string? folder = null;

                try
                {
                    string? file = process.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(file))
                    {
                        folder = Path.GetFileName(Path.GetDirectoryName(file));
                    }
                }
                catch (Exception exception) when (exception is Win32Exception
                                                       or InvalidOperationException
                                                       or NotSupportedException
                                                       or UnauthorizedAccessException
                                                       or IOException
                                                       or ArgumentException)
                {
                    // Access denied (protected/elevated process): name-only entry.
                }

                if (!string.IsNullOrWhiteSpace(name))
                {
                    list.Add(new ProcessEntry(pid, name, folder));
                }
            }
            catch (Exception exception) when (exception is Win32Exception
                                                   or InvalidOperationException
                                                   or NotSupportedException)
            {
                // The process exited between GetProcesses() and the property reads.
            }
            finally
            {
                process.Dispose();
            }
        }

        return list;
    }
}
