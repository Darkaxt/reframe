using System.Text.RegularExpressions;

namespace Reframe.Shell.Core;

/// <summary>A key press as src/renderer/shortcuts.ts sees it: the physical key (<c>Code</c>, as KeyboardEvent.code) and
/// the character it makes (<c>Key</c>).</summary>
public readonly record struct KeyPress(string Code, string Key, bool Ctrl, bool Meta, bool Shift, bool Alt);

/// <summary>reframe's keyboard shortcuts (src/renderer/shortcuts.ts): Ctrl+Shift letters and arrows, Ctrl+1…9, Ctrl+,
/// (settings), F11, Ctrl+Tab and the text size keys (Ctrl with +, - and 0, as in Windows Terminal). Claude Code takes
/// many plain Ctrl keys, so reframe catches only these before the terminal sees them.</summary>
public abstract record Shortcut
{
    public sealed record FocusTile(int Index) : Shortcut;
    public sealed record FocusDirection(string Direction) : Shortcut;
    public sealed record CycleTile(int Step) : Shortcut;
    public sealed record TextSize(TextSizeChange Change) : Shortcut;
    /// <summary>newSession, newWindow, nextWaiting, find, zoom, toggleCollapse, toggleSidebar, toggleViewMode,
    /// toggleFullscreen, openSettings.</summary>
    public sealed record Named(string Kind) : Shortcut;
}

public enum TextSizeChange { Bigger, Smaller, Reset }

public static class Shortcuts
{
    static readonly Dictionary<string, string> Arrows = new() { ["ArrowLeft"] = "left", ["ArrowRight"] = "right", ["ArrowUp"] = "up", ["ArrowDown"] = "down" };
    static readonly Dictionary<string, string> Letters = new()
    {
        ["KeyT"] = "newSession", ["KeyN"] = "newWindow", ["KeyM"] = "toggleCollapse", ["KeyL"] = "toggleSidebar",
        ["Space"] = "nextWaiting", ["KeyF"] = "find", ["KeyZ"] = "zoom", ["KeyG"] = "toggleViewMode",
    };
    // By character, not key: they sit elsewhere on other layouts (+ has a key of its own on a German keyboard).
    static readonly Dictionary<string, TextSizeChange> TextSizes = new()
    {
        ["+"] = TextSizeChange.Bigger, ["="] = TextSizeChange.Bigger, ["-"] = TextSizeChange.Smaller, ["0"] = TextSizeChange.Reset,
    };

    /// <summary>The shortcut a key press means, if any.</summary>
    public static Shortcut? For(KeyPress k, bool mac = false)
    {
        if (k.Code == "F11" && !k.Ctrl && !k.Meta && !k.Shift && !k.Alt) return new Shortcut.Named("toggleFullscreen");
        if (k.Code == "Tab" && k.Ctrl && !k.Meta && !k.Alt) return new Shortcut.CycleTile(k.Shift ? -1 : 1);
        var mod = mac ? k.Meta && !k.Ctrl : k.Ctrl && !k.Meta;
        if (!mod || k.Alt) return null;
        // Shift only for + (Shift and = on US keyboards); Ctrl+Shift+- is Ctrl+_, Claude Code's undo.
        if (TextSizes.TryGetValue(k.Key, out var change) && (!k.Shift || k.Key == "+")) return new Shortcut.TextSize(change);
        if (k.Code == "Comma" && !k.Shift) return new Shortcut.Named("openSettings");
        var digit = Regex.Match(k.Code, "^Digit([1-9])$");
        if (digit.Success && !k.Shift) return new Shortcut.FocusTile(int.Parse(digit.Groups[1].Value) - 1);
        if (!k.Shift) return null;
        if (Arrows.TryGetValue(k.Code, out var direction)) return new Shortcut.FocusDirection(direction);
        return Letters.TryGetValue(k.Code, out var kind) ? new Shortcut.Named(kind) : null;
    }

    /// <summary>The font size a text size change leads to, within the range.</summary>
    public static int FontSizeAfter(int size, TextSizeChange change) => change == TextSizeChange.Reset
        ? StateValues.FontSizeNormal
        : Math.Clamp(size + (change == TextSizeChange.Bigger ? 1 : -1), StateValues.FontSizeMin, StateValues.FontSizeMax);

    /// <summary>The state after a change, or null when the size stays (at the end of its range already).</summary>
    public static FontSizeState? ChangeFontSize(FontSizeState state, TextSizeChange change)
    {
        var size = FontSizeAfter(state.Size, change);
        return size == state.Size ? null : new FontSizeState(size, size);
    }

    /// <summary>The state once a saved size comes back: taken, unless a later change of this window is still on its way.</summary>
    public static FontSizeState EchoFontSize(FontSizeState state, int saved) =>
        state.Awaited is { } awaited && saved != awaited ? state : new FontSizeState(saved, null);

    /// <summary>A size in CSS px (reframe's preferences.fontSize) as the patched terminal control takes it
    /// (TerminalSetFont): points at 96 dpi (13 px = 9.75 pt) and the cell height in px. xterm draws 13 px with
    /// lineHeight 1.1 in 16 px rows; the rows keep that ratio at other sizes. Cell width is the font's own advance.</summary>
    public static (float Points, int CellHeight) TerminalFont(int px) =>
        (px * 0.75f, (int)Math.Max(1, Math.Round(px * 16 / 13.0, MidpointRounding.AwayFromZero)));
}

/// <summary>The terminals' font size as a window keeps it: changed at once, with the saved size coming back later.</summary>
public sealed record FontSizeState(int Size, int? Awaited);
