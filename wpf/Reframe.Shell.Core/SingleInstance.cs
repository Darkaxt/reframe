using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Reframe.Shell.Core;

/// <summary>
/// One shell per profile (as reframe's requestSingleInstanceLock, src/main/index.ts): the first holds a named mutex of
/// the profile and listens on a pipe of the profile; a second launch finds the mutex, asks the first to come forward
/// over the pipe and exits. Both names are per user session (<c>Local\</c>) and derived from the profile's full path.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    const string Activate = "activate";
    /// <summary>reframe.exe --quit (stop-managed.ps1): the running shell quits as its tray's Quit does, its sessions kept
    /// running in the host.</summary>
    const string Quit = "quit";
    readonly Mutex mutex;
    readonly string pipeName;
    readonly Action activate;
    readonly Action? quit;
    readonly CancellationTokenSource stop = new();
    readonly Task listening;

    SingleInstance(Mutex mutex, string pipeName, Action activate, Action? quit)
    {
        this.mutex = mutex;
        this.pipeName = pipeName;
        this.activate = activate;
        this.quit = quit;
        listening = Task.Run(ListenAsync);
    }

    /// <summary>A name of the profile: the hash of its full path, case-insensitive (Windows paths).</summary>
    public static string Key(string profileDir) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(profileDir).TrimEnd('\\').ToLowerInvariant())))[..16].ToLowerInvariant();

    public static string MutexName(string profileDir) => $@"Local\Reframe.Shell.{Key(profileDir)}";
    public static string PipeName(string profileDir) => $"Reframe.Shell.{Key(profileDir)}.activate";

    /// <summary>This process's instance for the profile, or null when another one runs (then <see cref="ActivateFirstAsync"/>).
    /// <paramref name="activate"/> runs (on a pool thread) when a second launch asks for the window, <paramref name="quit"/>
    /// when a <c>--quit</c> start asks the shell to quit.</summary>
    public static SingleInstance? TryAcquire(string profileDir, Action activate, Action? quit = null)
    {
        // Not owned, only held open: the name exists exactly as long as one instance has it.
        var mutex = new Mutex(false, MutexName(profileDir), out var created);
        if (created) return new SingleInstance(mutex, PipeName(profileDir), activate, quit);
        mutex.Dispose();
        return null;
    }

    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);
    const int ASFW_ANY = -1;

    /// <summary>Asks the profile's running instance to bring its window forward (and lets it take the foreground,
    /// which a launch the user just made may hand on). False when none answered in time.</summary>
    public static Task<bool> ActivateFirstAsync(string profileDir, TimeSpan timeout)
    {
        AllowSetForegroundWindow(ASFW_ANY);
        return SendAsync(profileDir, Activate, timeout);
    }

    /// <summary>Whether a shell runs on the profile (its mutex exists).</summary>
    public static bool Running(string profileDir)
    {
        if (!Mutex.TryOpenExisting(MutexName(profileDir), out var existing)) return false;
        existing.Dispose();
        return true;
    }

    /// <summary>Asks the profile's running instance to quit; false when none answered in time.</summary>
    public static Task<bool> RequestQuitAsync(string profileDir, TimeSpan timeout) => SendAsync(profileDir, Quit, timeout);

    static async Task<bool> SendAsync(string profileDir, string message, TimeSpan timeout)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", PipeName(profileDir), PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var cancel = new CancellationTokenSource(timeout);
            await pipe.ConnectAsync(cancel.Token);
            var line = Encoding.UTF8.GetBytes(message + "\n");
            await pipe.WriteAsync(line, cancel.Token);
            await pipe.FlushAsync(cancel.Token);
            return true;
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    async Task ListenAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop.Token);
                using var reader = new StreamReader(pipe, Encoding.UTF8);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var line = await reader.ReadLineAsync(timeout.Token);
                if (line == Activate) activate();
                else if (line == Quit) quit?.Invoke();
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception e) when (e is IOException or OperationCanceledException or UnauthorizedAccessException)
            {
                // A client that went away or said nothing: wait for the next.
            }
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        try { listening.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        mutex.Dispose();
        stop.Dispose();
    }
}
