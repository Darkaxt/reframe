using System.Windows.Threading;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>Keeps the session list in step with the transcripts on disk. The host lists a Claude Code folder again only
/// once told it changed (<c>transcripts.watch</c>), so a transcript another client deleted (or started) stayed listed
/// (or unlisted) until the host restarted.</summary>
sealed partial class ShellController
{
    DispatcherTimer? transcriptsTimer;

    async Task WatchTranscriptsAsync(HostServices services)
    {
        if (!services.Offers("transcripts")) return;
        services.TranscriptsChanged += () => Ui.InvokeAsync(() =>
        {
            // A burst of file changes (an archive or delete removes many) lists once.
            transcriptsTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(700), DispatcherPriority.Background, (_, _) =>
            {
                transcriptsTimer?.Stop();
                ListChanged();
            }, Ui);
            transcriptsTimer.Stop();
            transcriptsTimer.Start();
        });
        try
        {
            await services.TranscriptsWatchAsync(ClaudeRegistry.Home());
        }
        catch (HostServiceException e)
        {
            System.Diagnostics.Trace.WriteLine($"transcripts: not watched: {e.Message}");
        }
    }
}
