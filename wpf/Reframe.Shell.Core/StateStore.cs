using System.Text;
using System.Text.Json.Nodes;

namespace Reframe.Shell.Core;

/// <summary>The host's <c>state</c> service (docs/ptyhost-protocol.md): read and compare-and-set write.</summary>
public interface IStateFiles
{
    Task<(string? Content, long Revision)> ReadAsync(string file);
    /// <summary>The new revision; <see cref="HostServiceException"/> with code <c>conflict</c> when <paramref name="revision"/> is stale.</summary>
    Task<long> WriteAsync(string file, string content, long revision, bool backup);
}

/// <summary>
/// Loads and saves state.json as src/main/state/store.ts's StateStore does: through the host's <c>state</c> service
/// while it serves one (CAS revisions: read first when the revision is not known, one retry with the newest state on a
/// conflict), else here (temp file, flushed, renamed over the file; the previous good file kept as <c>.bak</c>). A file
/// that cannot be used is set aside as <c>state.json.rejected-&lt;time&gt;</c> (five kept); one from a newer reframe
/// makes the store read-only. Saves are written one at a time, always the newest; <see cref="SaveSoon"/> coalesces,
/// <see cref="FlushAsync"/> waits (and with a deadline gives up on the host and writes here).
/// Unknown fields are kept (StateJson.Write with keepUnknown).
/// </summary>
public sealed class StateStore(string file, Func<IStateFiles?> host, Action<string, string>? log = null)
{
    public const int KeepRejected = 5;
    readonly object gate = new();
    string? unsaved, newest;
    bool writing, abandoned;
    Task written = Task.CompletedTask;
    (long Revision, bool Good)? remote;
    Timer? timer;
    Func<string>? soon;

    public string File { get; } = file;
    public string BackupFile => File + ".bak";
    /// <summary>The file is a newer reframe's (or the store was made read-only): nothing is written.</summary>
    public bool ReadOnly { get; set; }

    public StateStore(string file, IStateFiles? host, Action<string, string>? log = null) : this(file, () => host, log) { }

    IStateFiles? Served() => abandoned ? null : host();

    /// <summary>Loads (StateLoader: file, else .bak, else defaults); an unusable file is set aside first.</summary>
    public async Task<StateLoadResult> LoadAsync()
    {
        var served = Served();
        var result = served is null
            ? await StateLoader.LoadFileAsync(File)
            : await StateLoader.LoadAsync(File, async f => { var (c, r) = await served.ReadAsync(f); return (c, r); });
        if (result.Source == StateSource.Newer) ReadOnly = true;
        if (result.Rejected is not null && result.Source != StateSource.Newer)
        {
            SetAside();
            remote = null;
        }
        else if (result.Revision is { } revision) remote = (revision, result.Source == StateSource.File);
        return result;
    }

    /// <summary>Writes the state now (cancels a pending <see cref="SaveSoon"/>); done once <see cref="Settle"/> is.</summary>
    public void Save(AppState state, IReadOnlySet<string>? exclude = null) => SaveText(Serialize(state, exclude));

    /// <summary>The state as reframe writes it (JSON.stringify(state, null, 2)), unknown fields kept, without the
    /// sessions in <paramref name="exclude"/> (ones the shell lists but reframe's state does not have).</summary>
    public static string Serialize(AppState state, IReadOnlySet<string>? exclude = null)
    {
        if (exclude is { Count: > 0 } && state.Windows.Any(w => exclude.Any(id => LayoutActions.WindowHasSession(w, id))))
        {
            // Their tiles go too: a window of reframe's never shows a session its state does not have.
            var copy = new AppState
            {
                Version = state.Version, Sessions = state.Sessions, RecentFolders = state.RecentFolders, Preferences = state.Preferences,
                PinnedChats = state.PinnedChats, ExpandedProjects = state.ExpandedProjects, FolderWorkspaces = state.FolderWorkspaces,
                Archives = state.Archives, WslDistros = state.WslDistros, HiddenThreads = state.HiddenThreads,
                Windows = [.. state.Windows.Select(w => exclude.Aggregate(w, LayoutActions.DropSession))],
            };
            foreach (var (key, value) in state.Extra) copy.Extra[key] = value;
            state = copy;
        }
        var json = StateJson.Write(state);
        if (exclude is { Count: > 0 } && json["sessions"] is JsonObject sessions)
            foreach (var id in exclude) sessions.Remove(id);
        return StateJson.Format(json);
    }

    void SaveText(string text)
    {
        CancelSoon();
        if (ReadOnly) return;
        lock (gate)
        {
            newest = unsaved = text;
            if (writing) return;
            writing = true;
            written = Task.Run(DrainAsync);
        }
    }

    /// <summary>Coalesces frequent saves into one write after <paramref name="delayMs"/>.</summary>
    public void SaveSoon(AppState state, IReadOnlySet<string>? exclude = null, int delayMs = 500)
    {
        lock (gate)
        {
            soon = () => Serialize(state, exclude);
            timer ??= new Timer(_ =>
            {
                Func<string>? next;
                lock (gate)
                {
                    next = soon;
                    soon = null;
                    timer?.Dispose();
                    timer = null;
                }
                if (next is not null) SaveText(next());
            }, null, delayMs, Timeout.Infinite);
        }
    }

    void CancelSoon()
    {
        lock (gate)
        {
            timer?.Dispose();
            timer = null;
            soon = null;
        }
    }

    public Task Settle() { lock (gate) return written; }

    /// <summary>Resolves once the newest state (a coalesced one too) is on disk. With a deadline, a host that has not
    /// written it by then is given up on: the newest state is written here, and so is everything after.</summary>
    public async Task FlushAsync(int? deadlineMs = null)
    {
        Func<string>? next;
        lock (gate) next = soon;
        if (next is not null) SaveText(next());
        Task done;
        lock (gate)
        {
            if (!writing || abandoned) return;
            done = written;
        }
        if (deadlineMs is null || await Task.WhenAny(done, Task.Delay(deadlineMs.Value)) == done)
        {
            await done;
            return;
        }
        log?.Invoke("warn", "the pty host did not save the state in time; saving here");
        string? last;
        lock (gate)
        {
            abandoned = true;
            unsaved = null;
            last = newest;
        }
        if (last is not null) SaveHere(last);
    }

    async Task DrainAsync()
    {
        for (; ; )
        {
            string? text;
            lock (gate)
            {
                text = unsaved;
                unsaved = null;
                if (text is null)
                {
                    writing = false;
                    return;
                }
            }
            try
            {
                if (Served() is { } served)
                {
                    try
                    {
                        await SaveThereAsync(served, text);
                        continue;
                    }
                    catch (Exception e)
                    {
                        log?.Invoke("warn", $"the pty host could not save the state; saving here: {e.Message}");
                    }
                }
                bool latest;
                lock (gate) latest = unsaved is null && ReferenceEquals(text, newest);
                if (latest) SaveHere(text);
            }
            catch (Exception e)
            {
                log?.Invoke("error", $"could not save the state: {e.Message}");
            }
        }
    }

    async Task SaveThereAsync(IStateFiles served, string text)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (remote is null)
            {
                var (content, revision) = await served.ReadAsync(File);
                remote = (revision, content is not null && StateParser.Parse(content).Kind == StateParseKind.State);
            }
            try
            {
                var next = await served.WriteAsync(File, text, remote.Value.Revision, remote.Value.Good);
                remote = (next, true);
                return;
            }
            catch (Exception e)
            {
                remote = null;
                if (attempt > 0 || e is not HostServiceException { Code: "conflict" } || Served() is null) throw;
                lock (gate)
                {
                    if (unsaved is not null)
                    {
                        text = unsaved;
                        unsaved = null;
                    }
                }
                log?.Invoke("info", "the state file changed since the pty host last wrote it; reading it again");
            }
        }
    }

    void SaveHere(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(File))!);
        var temp = $"{File}.{Environment.ProcessId}.tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(new UTF8Encoding(false).GetBytes(text));
            stream.Flush(true);
        }
        if (IsGood()) System.IO.File.Copy(File, BackupFile, true);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                System.IO.File.Move(temp, File, true);
                break;
            }
            catch (Exception e) when (attempt < 4 && e is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(20 * (attempt + 1));
            }
        }
        remote = null;
    }

    bool IsGood()
    {
        try
        {
            return System.IO.File.Exists(File) && StateParser.Parse(new UTF8Encoding(false).GetString(System.IO.File.ReadAllBytes(File))).Kind == StateParseKind.State;
        }
        catch (IOException) { return false; }
    }

    /// <summary>Moves the unusable file out of the way as <c>state.json.rejected-yyyyMMdd-HHmmss[-nnn]</c> (UTC, as
    /// store.ts names it); beyond <see cref="KeepRejected"/> such files the oldest go. Its new name.</summary>
    public string SetAside(DateTime? now = null)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(File))!;
        var prefix = Path.GetFileName(File) + ".rejected-";
        var stamp = (now ?? DateTime.UtcNow).ToUniversalTime().ToString("yyyyMMdd-HHmmss");
        var same = Directory.GetFiles(dir, prefix + stamp + "*").Select(Path.GetFileName).ToList();
        var highest = same.Select(n => System.Text.RegularExpressions.Regex.Match(n!, @"-(\d{3})$")).Select(m => m.Success ? int.Parse(m.Groups[1].Value) : 1).DefaultIfEmpty(0).Max();
        var target = Path.Combine(dir, prefix + stamp + (same.Count > 0 ? $"-{highest + 1:000}" : ""));
        try
        {
            System.IO.File.Move(File, target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return File;
        }
        foreach (var old in Directory.GetFiles(dir, prefix + "*").Select(Path.GetFileName).Order(StringComparer.Ordinal).SkipLast(KeepRejected))
            System.IO.File.Delete(Path.Combine(dir, old!));
        return target;
    }
}

/// <summary>The host's <c>state</c> service as <see cref="IStateFiles"/>.</summary>
public sealed class HostStateFiles(HostServices services) : IStateFiles
{
    public Task<(string? Content, long Revision)> ReadAsync(string file) => services.ReadStateRevisionAsync(file);
    public Task<long> WriteAsync(string file, string content, long revision, bool backup) => services.WriteStateAsync(file, content, revision, backup);
}
