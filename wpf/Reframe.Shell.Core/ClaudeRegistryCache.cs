using System.Diagnostics;

namespace Reframe.Shell.Core;

/// <summary>
/// Claude Code's session registry (<c>&lt;home&gt;/sessions/*.json</c>) kept in memory: read in full once, then only the
/// files a <see cref="FileSystemWatcher"/> reports changed (300 ms after the last change), and each listed process
/// followed until it exits, so a claude that ends leaves the list without the registry being read again. A watcher
/// error (its buffer overflowed, the folder went away) reads the registry in full once. <see cref="Changed"/> is
/// raised on a pool thread when the claudes running changed. The entries and their liveness follow
/// <see cref="ClaudeRegistry"/>'s rules.
/// </summary>
public sealed class ClaudeRegistryCache : IDisposable
{
    readonly string home, dir;
    readonly object gate = new(), flushing = new();
    /// <summary>The live entries, by registry file.</summary>
    readonly Dictionary<string, RunningClaude> entries = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The listed processes followed to their exit.</summary>
    readonly Dictionary<int, Process> watched = [];
    /// <summary>Listed processes whose exit cannot be followed: asked about whenever the list is read.</summary>
    readonly HashSet<int> unwatched = [];
    readonly HashSet<string> pending = new(StringComparer.OrdinalIgnoreCase);
    readonly Timer debounce;
    FileSystemWatcher? watcher;
    bool rescan = true, disposed;

    /// <summary>The claudes running changed (a pool thread).</summary>
    public event Action? Changed;

    public ClaudeRegistryCache(string home)
    {
        this.home = home;
        dir = Path.Combine(home, "sessions");
        debounce = new Timer(_ => Flush(notify: true));
    }

    /// <summary>The registry's entries whose process runs (<see cref="ClaudeRegistry.Running"/>), from memory. The first
    /// call reads the registry; without a sessions folder yet, it is read each time until one appears.</summary>
    public List<RunningClaude> Running()
    {
        if (!Watching()) return ClaudeRegistry.Running(home);
        Flush(notify: false);
        List<RunningClaude> list;
        HashSet<int> ask;
        lock (gate)
        {
            list = [.. entries.Values];
            ask = [.. unwatched];
        }
        return ask.Count == 0 ? list : list.Where(e => !ask.Contains(e.Pid) || ClaudeRegistry.Alive(e.Pid)).ToList();
    }

    bool Watching()
    {
        lock (gate)
        {
            if (disposed) return false;
            if (watcher is not null) return true;
            try
            {
                if (!Directory.Exists(dir)) return false;
                var w = new FileSystemWatcher(dir, "*.json")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };
                w.Created += (_, e) => Queue(e.FullPath);
                w.Changed += (_, e) => Queue(e.FullPath);
                w.Deleted += (_, e) => Queue(e.FullPath);
                w.Renamed += (_, e) =>
                {
                    Queue(e.OldFullPath);
                    Queue(e.FullPath);
                };
                w.Error += (_, _) =>
                {
                    lock (gate) rescan = true;
                    Arm();
                };
                w.EnableRaisingEvents = true;
                watcher = w;
                rescan = true;
                return true;
            }
            catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return false;
            }
        }
    }

    void Queue(string file)
    {
        lock (gate) pending.Add(file);
        Arm();
    }

    void Arm()
    {
        try
        {
            debounce.Change(300, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Reads what changed: the whole registry after an error (or at first), else the files reported.</summary>
    void Flush(bool notify)
    {
        bool changed = false;
        lock (flushing)
        {
            string[] files;
            bool full;
            lock (gate)
            {
                if (disposed) return;
                files = [.. pending];
                pending.Clear();
                full = rescan;
                rescan = false;
            }
            if (full) changed = Rescan();
            else
                foreach (var file in files)
                    changed |= Update(file);
        }
        if (changed && notify) Changed?.Invoke();
    }

    bool Rescan()
    {
        string[] files;
        try
        {
            files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json") : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            files = [];
        }
        var changed = false;
        var present = files.ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (gate)
            foreach (var gone in entries.Keys.Where(f => !present.Contains(f)).ToList())
                changed |= Apply(entries, gone, null, fileExists: false);
        foreach (var file in files) changed |= Update(file);
        return changed;
    }

    /// <summary>One registry file read again: listed while its process runs, dropped when it is gone.</summary>
    bool Update(string file)
    {
        var entry = ClaudeRegistry.ReadEntry(file);
        var exists = entry is not null || File.Exists(file);
        // An entry whose process has ended is not listed (ClaudeRegistry.Running's rule).
        if (entry is not null && !Follow(entry.Pid)) (entry, exists) = (null, false);
        lock (gate) return Apply(entries, file, entry, exists);
    }

    /// <summary>Follows a process to its exit; false when it does not run (<see cref="ClaudeRegistry.Alive"/>).</summary>
    bool Follow(int pid)
    {
        lock (gate)
            if (watched.ContainsKey(pid) || unwatched.Contains(pid)) return true;
        Process process;
        try
        {
            process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                process.Dispose();
                return false;
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
        try
        {
            process.Exited += (_, _) => Exited(pid, process);
            process.EnableRaisingEvents = true;
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            process.Dispose();
            lock (gate) unwatched.Add(pid);
            return true;
        }
        lock (gate)
        {
            if (disposed || !watched.TryAdd(pid, process)) process.Dispose();
        }
        return true;
    }

    void Exited(int pid, Process process)
    {
        bool changed;
        lock (gate)
        {
            if (disposed) return;
            if (watched.TryGetValue(pid, out var current) && ReferenceEquals(current, process)) watched.Remove(pid);
            changed = RemovePid(entries, pid);
        }
        process.Dispose();
        if (changed) Changed?.Invoke();
    }

    /// <summary>A registry file's entry put in <paramref name="entries"/>: set when read, removed when the file is gone,
    /// kept as it was when the file exists but could not be read (being written). True when the entries changed.</summary>
    public static bool Apply(IDictionary<string, RunningClaude> entries, string file, RunningClaude? entry, bool fileExists)
    {
        if (entry is not null)
        {
            if (entries.TryGetValue(file, out var old) && old == entry) return false;
            entries[file] = entry;
            return true;
        }
        return !fileExists && entries.Remove(file);
    }

    /// <summary>The entries of an exited process removed; true when there were any.</summary>
    public static bool RemovePid(IDictionary<string, RunningClaude> entries, int pid)
    {
        var gone = entries.Where(e => e.Value.Pid == pid).Select(e => e.Key).ToList();
        foreach (var file in gone) entries.Remove(file);
        return gone.Count > 0;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            watcher?.Dispose();
            watcher = null;
            foreach (var process in watched.Values) process.Dispose();
            watched.Clear();
        }
        debounce.Dispose();
    }
}
