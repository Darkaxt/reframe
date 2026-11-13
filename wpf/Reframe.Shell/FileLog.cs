using System.Diagnostics;
using System.IO;
using System.Text;

namespace Reframe.Shell;

/// <summary>The shell's Trace output in the profile's logs\main.log, in the lines reframe's Electron log wrote
/// (<c>2026-11-13T14:42:53.299Z INFO  message</c>), so the same file keeps telling what happened. Only the profile's
/// running shell writes it; a log grown past 5 MB at start is kept as main.old.log.</summary>
sealed class FileLog : TraceListener
{
    const long MaxBytes = 5 * 1024 * 1024;
    readonly StreamWriter writer;
    readonly object gate = new();

    FileLog(StreamWriter writer) => this.writer = writer;

    public static void Start(string profileDir, string version)
    {
        try
        {
            var dir = Path.Combine(profileDir, "logs");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "main.log");
            if (File.Exists(path) && new FileInfo(path).Length > MaxBytes) File.Move(path, Path.Combine(dir, "main.old.log"), true);
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            Trace.Listeners.Add(new FileLog(new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true }));
            Trace.WriteLine($"reframe starting {{\"version\":\"{version}\",\"shell\":\"wpf\",\"pid\":{Environment.ProcessId}}}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // No log is no reason not to start.
        }
    }

    public override void Write(string? message) => WriteLine(message);

    public override void WriteLine(string? message)
    {
        lock (gate)
        {
            try
            {
                writer.WriteLine($"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} INFO  {message}");
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) lock (gate) writer.Dispose();
        base.Dispose(disposing);
    }
}
