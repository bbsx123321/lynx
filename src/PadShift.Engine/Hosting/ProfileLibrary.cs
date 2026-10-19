using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace PadShift.Engine.Hosting;

/// <summary>
/// Scans a folder for <c>.rewasd</c> profiles and resolves names to absolute paths.
/// Scanning is done at construction / explicitly via <see cref="Refresh"/>; the tick loop
/// never touches the file system.
/// </summary>
public sealed class ProfileLibrary
{
    private readonly object _gate = new();
    private string _root;
    private string[] _profiles = Array.Empty<string>();
    private int _revision;

    public ProfileLibrary(string? root)
    {
        _root = Normalize(root);
        Refresh();
    }

    public string Root
    {
        get
        {
            lock (_gate)
            {
                return _root;
            }
        }
    }

    /// <summary>Absolute paths of every profile found during the last scan (ordinal-ignore-case sorted).</summary>
    public IReadOnlyList<string> Profiles
    {
        get
        {
            lock (_gate)
            {
                return _profiles;
            }
        }
    }

    /// <summary>
    /// Monotonically increasing counter bumped by every <see cref="Refresh"/>. Caches that are
    /// derived from <see cref="Profiles"/> (for example the process -> profile index) compare
    /// this value and rebuild when it changes.
    /// </summary>
    public int Revision => Volatile.Read(ref _revision);

    /// <summary>Rescan <see cref="Root"/>; missing/unreadable folders yield an empty library.</summary>
    public void Refresh()
    {
        string root;
        lock (_gate)
        {
            root = _root;
        }

        var found = new List<string>();
        try
        {
            if (Directory.Exists(root))
            {
                foreach (string file in Directory.EnumerateFiles(root, "*.rewasd", SearchOption.AllDirectories))
                {
                    found.Add(Path.GetFullPath(file));
                }
            }
        }
        catch (IOException)
        {
            // Unreadable library: keep what we found.
        }
        catch (UnauthorizedAccessException)
        {
        }

        found.Sort(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            _profiles = found.ToArray();
        }

        Interlocked.Increment(ref _revision);
    }

    /// <summary>Point the library at another folder and rescan.</summary>
    public void SetRoot(string? root)
    {
        lock (_gate)
        {
            _root = Normalize(root);
        }

        Refresh();
    }

    /// <summary>
    /// Resolve a user-supplied path or bare profile name to an absolute existing file.
    /// Returns null when the profile does not exist (ApplyConfig must not invent paths).
    /// </summary>
    public string? Resolve(string? pathOrName)
    {
        if (string.IsNullOrWhiteSpace(pathOrName))
        {
            return null;
        }

        try
        {
            if (File.Exists(pathOrName))
            {
                return Path.GetFullPath(pathOrName);
            }

            string name = Path.GetFileName(pathOrName);
            if (name.Length == 0)
            {
                return null;
            }

            string withExtension = name.EndsWith(".rewasd", StringComparison.OrdinalIgnoreCase) ? name : name + ".rewasd";

            lock (_gate)
            {
                foreach (string candidate in _profiles)
                {
                    if (string.Equals(Path.GetFileName(candidate), name, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(Path.GetFileName(candidate), withExtension, StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate;
                    }
                }

                string inRoot = Path.Combine(_root, withExtension);
                if (File.Exists(inRoot))
                {
                    return Path.GetFullPath(inRoot);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (ArgumentException)
        {
        }
        catch (NotSupportedException)
        {
        }

        return null;
    }

    private static string Normalize(string? root) =>
        string.IsNullOrWhiteSpace(root) ? HostOptions.DefaultProfilesRoot() : root!;
}
