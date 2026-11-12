using System.Windows;
using Microsoft.Terminal.Wpf;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>
/// Feeds a TerminalControl from a host view. The control never spawns anything: its input and resizes go to the
/// session in the host, and the host's snapshot and output come back. Close only detaches the view.
/// </summary>
sealed class HostTerminalConnection : ITerminalConnection
{
    readonly HostView view;
    readonly TerminalControl control;

    public HostTerminalConnection(HostView view, TerminalControl control)
    {
        this.view = view;
        this.control = control;
        view.Snapshot += (data, cols, rows) => control.Dispatcher.InvokeAsync(async () =>
        {
            // As reframe's renderer does: a terminal of the snapshot's size, reset, then the repaint.
            await control.ResizeAsync((uint)rows, (uint)cols, CancellationToken.None);
            TerminalOutput?.Invoke(this, new TerminalOutputEventArgs("\u001bc" + data));
            // Back to the control's own size; that resize goes to the host.
            control.TriggerResize(control.RenderSize);
        });
        view.Data += data => control.Dispatcher.InvokeAsync(() => TerminalOutput?.Invoke(this, new TerminalOutputEventArgs(data)));
        view.Exit += code => control.Dispatcher.InvokeAsync(() =>
            TerminalOutput?.Invoke(this, new TerminalOutputEventArgs($"\r\n[the session ended with {code}]\r\n")));
    }

    public event EventHandler<TerminalOutputEventArgs>? TerminalOutput;

    public void Start() => _ = view.OpenAsync();

    public void WriteInput(string data)
    {
        // As in reframe: selecting does not copy; Enter with a selection copies it (and deselects) instead of sending.
        if (data == "\r")
        {
            var selected = control.GetSelectedText();
            if (!string.IsNullOrEmpty(selected))
            {
                Clipboard.SetText(selected);
                return;
            }
        }
        _ = view.InputAsync(data);
    }

    public void Paste()
    {
        if (Clipboard.ContainsText()) _ = view.InputAsync(Clipboard.GetText());
    }

    public void Resize(uint rows, uint columns) => _ = view.ResizeAsync((int)columns, (int)rows);

    public void Close() => view.Close();
}
