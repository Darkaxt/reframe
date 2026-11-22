using Microsoft.Terminal.Wpf;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>
/// Feeds a TerminalControl from a host view. The control never spawns anything: its input and resizes go to the
/// session in the host, and the host's snapshot and output come back. Close only detaches the view. The output also
/// goes through <see cref="Modes"/>, which the control does not report (bracketed paste, mouse tracking).
/// </summary>
sealed class HostTerminalConnection : ITerminalConnection
{
    readonly HostView view;
    /// <summary>Set by <see cref="Close"/>: the tile is going, and its native terminal may already be destroyed.</summary>
    volatile bool closed;

    public HostTerminalConnection(HostView view, TerminalControl control)
    {
        this.view = view;
        // A snapshot or output queued on the dispatcher can run after the tile was disposed (many threads opening and
        // closing at once): calling into a destroyed native terminal is an access violation that ends the process, so a
        // closed or unloaded control is left alone, and so is one whose terminal window is not built yet or was destroyed
        // (its HwndHost has no handle then).
        bool Gone() => closed || !control.IsLoaded || System.Windows.PresentationSource.FromVisual(control) is null
            || control.FindName("termContainer") is not System.Windows.Interop.HwndHost { Handle: var handle } || handle == IntPtr.Zero;
        view.Snapshot += (data, cols, rows) => control.Dispatcher.InvokeAsync(async () =>
        {
            if (Gone()) return;
            // As reframe's renderer does: a terminal of the snapshot's size, reset, then the repaint.
            await control.ResizeAsync((uint)rows, (uint)cols, CancellationToken.None);
            if (Gone()) return;
            Output("\u001bc" + data);
            Unpin(control);
            // Back to the control's own size; that resize goes to the host. A hidden one (a single window's other
            // threads) keeps the snapshot's size: its stale size would shrink the session; it is fitted when shown.
            if (control.IsVisible && !Gone() && control.RenderSize is { Width: > 0, Height: > 0 }) control.TriggerResize(control.RenderSize);
        });
        view.Data += data => control.Dispatcher.InvokeAsync(() =>
        {
            if (!Gone()) Output(data);
        });
        view.Exit += code => control.Dispatcher.InvokeAsync(() =>
        {
            if (!Gone()) Output($"\r\n[the session ended with {code}]\r\n");
        });
    }

    public TerminalModes Modes { get; } = new();

    /// <summary>
    /// ResizeAsync also sets a margin on the control's grid that keeps its terminal window at the size it resized to,
    /// and with AutoResize nothing takes it off again: a snapshot of a smaller terminal (the session last shown in a
    /// quarter tile) kept the terminal window that small in a whole tile, and the window's own resizes kept the session
    /// at that size. Without the margin the terminal window fills the control and fits the session to it.
    /// </summary>
    public static void Unpin(TerminalControl control)
    {
        if (control.FindName("terminalGrid") is System.Windows.FrameworkElement grid && grid.Margin != default)
            grid.Margin = default;
    }

    void Output(string data)
    {
        Modes.Feed(data);
        TerminalOutput?.Invoke(this, new TerminalOutputEventArgs(data));
    }

    public event EventHandler<TerminalOutputEventArgs>? TerminalOutput;

    public void Start() => _ = view.OpenAsync();

    /// <summary>What the control types (keys, its own mouse reports).</summary>
    public void WriteInput(string data) => Send(data);

    /// <summary>Text pasted, as xterm's paste sends it (CR line breaks, bracketed while the program asked for it).</summary>
    public void Paste(string text)
    {
        if (text.Length > 0) Send(PasteText.Prepare(text, Modes.BracketedPaste));
    }

    /// <summary>The user's input; it waits while Reframe types a message of its own into the thread, and Reframe's
    /// messages wait while the user writes (<see cref="InputGuard"/>).</summary>
    void Send(string data)
    {
        InputGuard.NoteUser(view.SessionId, data);
        if (!InputGuard.Defer(view.SessionId, () => view.InputAsync(data))) _ = view.InputAsync(data);
    }

    (uint Rows, uint Columns)? sent;

    /// <summary>The size to the session, unless it is the one sent last (a refit of an unchanged tile).</summary>
    public void Resize(uint rows, uint columns)
    {
        if (sent == (rows, columns)) return;
        sent = (rows, columns);
        _ = view.ResizeAsync((int)columns, (int)rows);
    }

    public void Close()
    {
        closed = true;
        view.Close();
    }
}
