using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>
/// A terminal window dragged onto this window (App.tsx onForeignDrag/onForeignDrop, TakeoverDialog.tsx): where it would
/// land is previewed while it is over the window, and on release the takeover dialog asks which of its sessions move
/// here. Drags from other apps that WPF sees (a Windows Terminal tab torn out over the window offers no data) report
/// the pointer to the watcher.
/// </summary>
public partial class MainWindow
{
    bool foreignPreviewShown;
    long foreignReportedAt;

    void WatchForeignDrags()
    {
        AllowDrop = true;
        PreviewDragOver += (_, e) =>
        {
            if (headerDrag is not null || Environment.TickCount64 - foreignReportedAt < 50) return;
            foreignReportedAt = Environment.TickCount64;
            shell.NoteForeignPointer(WindowId, ScreenPoint(e.GetPosition(this)), e.Data.GetFormats().Length > 0);
        };
        PreviewDragLeave += (_, e) =>
        {
            if (headerDrag is null && !IsInside(e.GetPosition(this))) shell.NoteForeignPointer(WindowId, null, false);
        };
        PreviewDrop += (_, _) =>
        {
            if (headerDrag is null) shell.NoteForeignPointer(WindowId, null, false);
        };
    }

    /// <summary>The surface point of a screen point in DIPs.</summary>
    Point SurfacePoint(Point screen) => surface.PointFromScreen(new Point(screen.X * Scale, screen.Y * Scale));

    Box LayoutArea() => new(0, 0, surface.ActualWidth, surface.ActualHeight);

    /// <summary>The drop preview for a terminal window dragged over this window (null: it left). WPF cannot draw over
    /// the terminals, so they are hidden meanwhile, as during a tile's drag.</summary>
    public void ForeignHover(ScreenPoint? point)
    {
        Box? preview = null;
        if (point is { } p && modal is null)
        {
            var at = SurfacePoint(new Point(p.X, p.Y));
            LayoutGeometry.ForeignTargetAt(places, LayoutArea(), at.X, at.Y, out preview);
        }
        if (preview is { } box)
        {
            Put(dropPreview, box);
            dropPreview.Visibility = Visibility.Visible;
            if (!foreignPreviewShown) SetTerminalsShown(false);
            foreignPreviewShown = true;
        }
        else if (foreignPreviewShown)
        {
            foreignPreviewShown = false;
            dropPreview.Visibility = Visibility.Collapsed;
            SetTerminalsShown(true);
        }
    }

    /// <summary>The takeover dialog for a terminal window dropped at <paramref name="screenPoint"/> (DIPs): the sessions
    /// picked and where they land (taken before the dialog covers the window), or null when cancelled.</summary>
    public Task<(List<string> Ids, DropTarget? Target)?> AskTakeoverAsync(IReadOnlyList<ForeignCandidate> candidates, Point screenPoint)
    {
        ForeignHover(null);
        var at = SurfacePoint(screenPoint);
        var target = LayoutGeometry.ForeignTargetAt(places, LayoutArea(), at.X, at.Y, out _);
        var answer = new TaskCompletionSource<(List<string> Ids, DropTarget? Target)?>();
        CloseModal();
        var dialog = ShowTakeover(candidates);
        dialog.Answered += ids =>
        {
            answer.TrySetResult(ids is null ? null : (ids, target));
            CloseModal();
        };
        modal!.Closed += () => answer.TrySetResult(null);
        return answer.Task;
    }

    TakeoverDialog ShowTakeover(IReadOnlyList<ForeignCandidate> candidates)
    {
        var dialog = new TakeoverDialog(candidates);
        ShowModal(dialog.Frame);
        if (!Capturing) Dispatcher.BeginInvoke(() => Keyboard.Focus(dialog.ConfirmButton), DispatcherPriority.Input);
        return dialog;
    }

    /// <summary>A session taken over lands in this window, at <paramref name="target"/> (else at the end), selected.</summary>
    public void PlaceSession(string sessionId, DropTarget? target)
    {
        Dispatch(w => LayoutActions.AddTile(w, sessionId, target));
        FocusTile(sessionId);
    }

    /// <summary>The "hidden" fixture's takeover state: the dialog for a terminal window dropped on the window with two
    /// sessions, the first preselected (e2e/visual-reference.spec.ts sends the same request).</summary>
    async Task CaptureTakeoverAsync(HwndSource popup, string dir)
    {
        if (fixture?.Takeover is not { Count: > 0 } candidates) return;
        ShowTakeover(candidates);
        await DiagCapture.SettleAsync(Dispatcher, 500);
        DiagCapture.Save(popup, root, dir, "takeover", modal?.Overlay);
        CloseModal();
    }
}
