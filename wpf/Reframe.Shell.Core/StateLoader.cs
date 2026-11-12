using System.Text;

namespace Reframe.Shell.Core;

/// <summary>Where the loaded state came from (as StateStore's LoadSource).</summary>
public enum StateSource { File, Backup, Default, Newer }

/// <summary>
/// The loaded state. <c>Rejected</c>: the file could not be used, and why (reframe sets such a file aside; the shell,
/// read-only for now, leaves it). <c>Revision</c>: the host's revision of the file as read, null when read directly.
/// </summary>
public sealed record StateLoadResult(AppState State, StateSource Source, string? Rejected = null, long? Revision = null);

/// <summary>
/// Loads state.json as StateStore.load does (the file, else its .bak, else the defaults; a newer reframe's file
/// gives the defaults and must not be written), through the host's <c>state</c> service when it offers one, else
/// straight from the files. Read-only: nothing is written or moved.
/// </summary>
public static class StateLoader
{
    /// <summary>Reads a file's text: null when it is not there.</summary>
    public delegate Task<(string? Content, long? Revision)> ReadText(string file);

    public static Task<StateLoadResult> LoadFileAsync(string file) => LoadAsync(file, ReadDirect);

    /// <summary>Through the host's <c>state.read</c> when it serves <c>state</c>; else from the files directly.</summary>
    public static Task<StateLoadResult> LoadAsync(HostServices host, string file) =>
        host.Offers("state") ? LoadAsync(file, async f => await host.ReadStateAsync(f)) : LoadFileAsync(file);

    public static async Task<StateLoadResult> LoadAsync(string file, ReadText read)
    {
        var (content, revision) = await read(file);
        var fromFile = content is null ? null : StateParser.Parse(content);
        if (fromFile?.Kind == StateParseKind.State) return new(fromFile.State!, StateSource.File, Revision: revision);
        if (fromFile?.Kind == StateParseKind.Newer) return new(AppState.CreateDefault(), StateSource.Newer, Revision: revision);
        var rejected = fromFile?.Reason;
        var (backupContent, _) = await read(file + ".bak");
        var fromBackup = backupContent is null ? null : StateParser.Parse(backupContent);
        return fromBackup?.Kind switch
        {
            StateParseKind.Newer => new(AppState.CreateDefault(), StateSource.Newer, rejected, revision),
            StateParseKind.State => new(fromBackup.State!, StateSource.Backup, rejected, revision),
            _ => new(AppState.CreateDefault(), StateSource.Default, rejected, revision),
        };
    }

    /// <summary>The text as Node's readFileSync(file, 'utf8') gives it: a byte order mark is kept (and rejected).</summary>
    static async Task<(string?, long?)> ReadDirect(string file)
    {
        try
        {
            return (new UTF8Encoding(false).GetString(await File.ReadAllBytesAsync(file)), null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return (null, null);
        }
    }
}
