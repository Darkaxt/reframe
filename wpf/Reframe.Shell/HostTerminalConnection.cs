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

    public HostTerminalConnection(HostView view, TerminalControl control)
    {
        this.view = view;
        view.Snapshot += (data, cols, rows) => control.Dispatcher.InvokeAsync(async () =>
        {
            // As reframe's renderer does: a terminal of the snapshot's size, reset, then the repaint.
            await control.ResizeAsync((uint)rows, (uint)cols, CancellationToken.None);
            Output("\u001bc" + data);
            // Back to the control's own size; that resize goes to the host.
            control.TriggerResize(control.RenderSize);
        });
        view.Data += data => control.Dispatcher.InvokeAsync(() => Output(data));
        view.Exit += code => control.Dispatcher.InvokeAsync(() => Output($"\r\n[the session ended with {code}]\r\n"));
    }

    public TerminalModes Modes { get; } = new();

    void Output(string data)
    {
        Modes.Feed(data);
        TerminalOutput?.Invoke(this, new TerminalOutputEventArgs(data));
    }

    public event EventHandler<TerminalOutputEventArgs>? TerminalOutput;

    public void Start() => _ = view.OpenAsync();

    /// <summary>What the control types (keys, its own mouse reports).</summary>
    public void WriteInput(string data) => _ = view.InputAsync(data);

    /// <summary>Text pasted, as xterm's paste sends it (CR line breaks, bracketed while the program asked for it).</summary>
    public void Paste(string text)
    {
        if (text.Length > 0) _ = view.InputAsync(PasteText.Prepare(text, Modes.BracketedPaste));
    }

    public void Resize(uint rows, uint columns) => _ = view.ResizeAsync((int)columns, (int)rows);

    public void Close() => view.Close();
}
