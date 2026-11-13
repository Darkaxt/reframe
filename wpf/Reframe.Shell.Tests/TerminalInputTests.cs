using Reframe.Shell.Core;
using Xunit;

namespace Reframe.Shell.Tests;

/// <summary>Ported from src/renderer/terminal-keys.test.ts, paste.test.ts and shortcuts.test.ts.</summary>
public class TerminalInputTests
{
    // ---------- terminal-keys ----------

    [Fact]
    public void Shift_Enter_types_ESC_CR()
    {
        Assert.Equal("\u001b\r", TerminalKeys.Newline);
        Assert.Equal(EnterAction.Newline, TerminalKeys.EnterKey(shift: true, ctrl: false, alt: false, hasSelection: false));
    }

    [Fact]
    public void Enter_or_Shift_Enter_with_a_selection_copies_and_sends_nothing()
    {
        foreach (var shift in new[] { false, true })
            Assert.Equal(EnterAction.Copy, TerminalKeys.EnterKey(shift, ctrl: false, alt: false, hasSelection: true));
    }

    [Fact]
    public void Plain_Enter_and_modified_Enter_are_left_to_the_terminal()
    {
        Assert.Null(TerminalKeys.EnterKey(shift: false, ctrl: false, alt: false, hasSelection: false));
        Assert.Null(TerminalKeys.EnterKey(shift: true, ctrl: true, alt: false, hasSelection: true));
        Assert.Null(TerminalKeys.EnterKey(shift: true, ctrl: false, alt: true, hasSelection: true));
    }

    [Fact]
    public void Copy_notice_shows_the_text_on_one_line_cut_at_40()
    {
        Assert.Equal("hello world", TerminalKeys.CopyPreview("  hello\n  world "));
        Assert.Equal(new string('x', 40) + "…", TerminalKeys.CopyPreview(new string('x', 50)));
    }

    [Fact]
    public void Select_hint_is_for_a_left_drag_without_Shift_in_a_terminal_that_reads_the_mouse()
    {
        (double, double)? start = (0, 0);
        Assert.True(TerminalKeys.DragWithoutShift(start, 10, 0, true, false, true));
        Assert.False(TerminalKeys.DragWithoutShift(start, 10, 0, true, false, false));
        Assert.False(TerminalKeys.DragWithoutShift(start, 10, 0, true, true, true));
        Assert.False(TerminalKeys.DragWithoutShift(start, 10, 0, false, false, true));
        Assert.False(TerminalKeys.DragWithoutShift(start, 2, 0, true, false, true));
        Assert.False(TerminalKeys.DragWithoutShift(null, 10, 0, true, false, true));
    }

    [Fact]
    public void Ctrl_C_copies_only_with_a_selection_or_Shift_and_Ctrl_V_pastes()
    {
        Assert.Equal("copy", TerminalKeys.ClipboardKey('c', ctrl: true, shift: false, alt: false, hasSelection: true));
        Assert.Equal("copy", TerminalKeys.ClipboardKey('C', ctrl: true, shift: true, alt: false, hasSelection: false));
        Assert.Null(TerminalKeys.ClipboardKey('c', ctrl: true, shift: false, alt: false, hasSelection: false)); // interrupts claude
        Assert.Equal("paste", TerminalKeys.ClipboardKey('v', ctrl: true, shift: false, alt: false, hasSelection: false));
        Assert.Null(TerminalKeys.ClipboardKey('v', ctrl: true, shift: false, alt: true, hasSelection: false));
        Assert.Null(TerminalKeys.ClipboardKey('v', ctrl: false, shift: false, alt: false, hasSelection: false));
    }

    // ---------- paste ----------

    [Fact]
    public void FormatPaths_quotes_Windows_paths_only_where_cmd_and_PowerShell_need_it() =>
        Assert.Equal(@"C:\shots\a.png ""C:\My Pictures\b.png"" ", PasteText.FormatPaths([@"C:\shots\a.png", @"C:\My Pictures\b.png"], windows: true));

    [Fact]
    public void FormatPaths_quotes_other_paths_for_a_POSIX_shell() =>
        Assert.Equal("/home/me/a.png '/home/me/it'\\''s here.png' ", PasteText.FormatPaths(["/home/me/a.png", "/home/me/it's here.png"], windows: false));

    [Fact]
    public void FormatPaths_types_Linux_paths_into_a_session_in_WSL() =>
        Assert.Equal("'/mnt/c/My Pictures/b.png' /home/me/a.txt ",
            PasteText.FormatPaths([@"C:\My Pictures\b.png", @"\\wsl.localhost\Ubuntu\home\me\a.txt"], windows: true, new PasteText.Wsl("Ubuntu", "/mnt/")));

    [Fact]
    public void FormatPaths_types_nothing_for_no_files() => Assert.Equal("", PasteText.FormatPaths([], windows: true));

    [Fact]
    public void ImagePasteKey_is_Alt_V_on_Windows_and_Ctrl_V_elsewhere()
    {
        Assert.Equal("\u001bv", PasteText.ImagePasteKey(windows: true));
        Assert.Equal("\u0016", PasteText.ImagePasteKey(windows: false));
    }

    [Fact]
    public void PastesImageAsFile_on_Windows_and_in_WSL_only()
    {
        Assert.True(PasteText.PastesImageAsFile(true, false));
        Assert.True(PasteText.PastesImageAsFile(true, true));
        Assert.True(PasteText.PastesImageAsFile(false, true));
        Assert.False(PasteText.PastesImageAsFile(false, false));
    }

    [Fact]
    public void Paste_sends_CR_line_breaks_and_brackets_while_asked_to()
    {
        Assert.Equal("a\rb\rc", PasteText.Prepare("a\r\nb\nc", bracketed: false));
        Assert.Equal("\u001b[200~a\rb\u001b[201~", PasteText.Prepare("a\nb", bracketed: true));
    }

    [Fact]
    public void Wsl_paths_as_programs_in_the_distribution_reach_them()
    {
        Assert.Equal("/mnt/d/x/y", WslPaths.LinuxPathOf(@"D:\x\y", "Ubuntu", "/mnt/"));
        Assert.Equal("/home/a", WslPaths.LinuxPathOf(@"\\wsl$\ubuntu\home\a", "Ubuntu", "/mnt/"));
        Assert.Null(WslPaths.LinuxPathOf(@"\\wsl.localhost\Debian\home\a", "Ubuntu", "/mnt/"));
        Assert.Equal(("Ubuntu", "/"), WslPaths.Parse(@"\\wsl.localhost\Ubuntu"));
        Assert.Null(WslPaths.Parse(@"C:\x"));
    }

    // ---------- terminal modes ----------

    [Fact]
    public void Modes_follow_DECSET_even_split_between_writes_and_reset_on_RIS()
    {
        var modes = new TerminalModes();
        modes.Feed("hello\u001b[?20");
        Assert.False(modes.BracketedPaste);
        modes.Feed("04h world");
        Assert.True(modes.BracketedPaste);
        modes.Feed("\u001b[?1000;1006h");
        Assert.True(modes.MouseTracking);
        modes.Feed("\u001b[?1000l");
        Assert.False(modes.MouseTracking);
        modes.Feed("\u001b[?1002h\u001b");
        modes.Feed("c");
        Assert.False(modes.BracketedPaste);
        Assert.False(modes.MouseTracking);
    }

    // ---------- drops ----------

    [Fact]
    public void Drops_keep_a_plain_file_name_and_refuse_huge_data()
    {
        Assert.Equal("a_b_.png", Drops.SafeName("a<b>.png"));
        Assert.Equal("evil.png", Drops.SafeName(@"..\..\evil.png"));
        Assert.Equal("dropped", Drops.SafeName(""));
        Assert.Equal(120, Drops.SafeName(new string('n', 300)).Length);
        var root = Path.Combine(Path.GetTempPath(), "reframe-drops-test-" + Guid.NewGuid());
        try
        {
            var file = Drops.Save("clip:board.png", [1, 2, 3], root)!;
            Assert.Equal("clip_board.png", Path.GetFileName(file));
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(file));
            Assert.StartsWith(root, file);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // ---------- shortcuts ----------

    static KeyPress Press(string code, bool ctrl = false, bool meta = false, bool shift = false, bool alt = false, string? key = null) =>
        new(code, key ?? System.Text.RegularExpressions.Regex.Replace(code, "^(Key|Digit)", "").ToLowerInvariant(), ctrl, meta, shift, alt);

    [Fact]
    public void Knows_the_Ctrl_Shift_shortcuts_and_Ctrl_1_to_9()
    {
        Assert.Equal(new Shortcut.Named("newSession"), Shortcuts.For(Press("KeyT", ctrl: true, shift: true)));
        Assert.Equal(new Shortcut.Named("newWindow"), Shortcuts.For(Press("KeyN", ctrl: true, shift: true)));
        Assert.Equal(new Shortcut.Named("toggleCollapse"), Shortcuts.For(Press("KeyM", ctrl: true, shift: true)));
        Assert.Equal(new Shortcut.Named("toggleSidebar"), Shortcuts.For(Press("KeyL", ctrl: true, shift: true)));
        Assert.Equal(new Shortcut.Named("nextWaiting"), Shortcuts.For(Press("Space", ctrl: true, shift: true, key: " ")));
        Assert.Equal(new Shortcut.Named("nextWaiting"), Shortcuts.For(Press("Space", meta: true, shift: true, key: " "), mac: true));
        Assert.Null(Shortcuts.For(Press("Space", ctrl: true, key: " ")));
        Assert.Equal(new Shortcut.Named("find"), Shortcuts.For(Press("KeyF", ctrl: true, shift: true)));
        Assert.Equal(new Shortcut.Named("zoom"), Shortcuts.For(Press("KeyZ", ctrl: true, shift: true)));
        Assert.Equal(new Shortcut.Named("toggleViewMode"), Shortcuts.For(Press("KeyG", ctrl: true, shift: true)));
        Assert.Equal(new Shortcut.CycleTile(1), Shortcuts.For(Press("Tab", ctrl: true)));
        Assert.Equal(new Shortcut.CycleTile(-1), Shortcuts.For(Press("Tab", ctrl: true, shift: true)));
        Assert.Equal(new Shortcut.FocusDirection("left"), Shortcuts.For(Press("ArrowLeft", ctrl: true, shift: true)));
        Assert.Equal(new Shortcut.FocusTile(2), Shortcuts.For(Press("Digit3", ctrl: true)));
        Assert.Equal(new Shortcut.Named("toggleFullscreen"), Shortcuts.For(Press("F11")));
        Assert.Equal(new Shortcut.Named("openSettings"), Shortcuts.For(Press("Comma", ctrl: true, key: ",")));
        Assert.Equal(new Shortcut.Named("openSettings"), Shortcuts.For(Press("Comma", meta: true, key: ","), mac: true));
        Assert.Null(Shortcuts.For(Press("F11", shift: true)));
    }

    [Fact]
    public void Leaves_everything_else_to_the_terminal()
    {
        Assert.Null(Shortcuts.For(Press("KeyT", ctrl: true)));
        Assert.Null(Shortcuts.For(Press("KeyV", ctrl: true, shift: true)));
        Assert.Null(Shortcuts.For(Press("KeyT", ctrl: true, shift: true, alt: true)));
        Assert.Null(Shortcuts.For(Press("KeyT", shift: true)));
    }

    [Fact]
    public void Uses_Cmd_instead_of_Ctrl_on_macOS()
    {
        Assert.Equal(new Shortcut.Named("newSession"), Shortcuts.For(Press("KeyT", meta: true, shift: true), mac: true));
        Assert.Null(Shortcuts.For(Press("KeyT", ctrl: true, shift: true), mac: true));
        Assert.Null(Shortcuts.For(Press("Digit1", meta: true)));
    }

    [Fact]
    public void Changes_the_text_size_with_Ctrl_and_plus_equals_minus_and_0_on_any_layout()
    {
        var bigger = new Shortcut.TextSize(TextSizeChange.Bigger);
        var smaller = new Shortcut.TextSize(TextSizeChange.Smaller);
        Assert.Equal(bigger, Shortcuts.For(Press("Equal", ctrl: true, key: "=")));
        Assert.Equal(bigger, Shortcuts.For(Press("Equal", ctrl: true, shift: true, key: "+"))); // US
        Assert.Equal(bigger, Shortcuts.For(Press("BracketRight", ctrl: true, key: "+"))); // German
        Assert.Equal(bigger, Shortcuts.For(Press("NumpadAdd", ctrl: true, key: "+")));
        Assert.Equal(smaller, Shortcuts.For(Press("Minus", ctrl: true, key: "-")));
        Assert.Equal(smaller, Shortcuts.For(Press("Slash", ctrl: true, key: "-"))); // German
        Assert.Equal(new Shortcut.TextSize(TextSizeChange.Reset), Shortcuts.For(Press("Digit0", ctrl: true)));
        Assert.Equal(bigger, Shortcuts.For(Press("Equal", meta: true, key: "="), mac: true));
        Assert.Null(Shortcuts.For(Press("Minus", ctrl: true, shift: true, key: "_")));
        Assert.Null(Shortcuts.For(Press("Digit0", ctrl: true, shift: true, key: "=")));
        Assert.Null(Shortcuts.For(Press("Minus", key: "-")));
    }

    [Fact]
    public void Font_size_changes_at_once_and_takes_a_saved_size_back_only_once_nothing_of_its_own_is_on_its_way()
    {
        var state = new FontSizeState(13, null);
        state = Shortcuts.ChangeFontSize(state, TextSizeChange.Bigger)!;
        state = Shortcuts.ChangeFontSize(state, TextSizeChange.Bigger)!;
        Assert.Equal(new FontSizeState(15, 15), state);
        Assert.Same(state, Shortcuts.EchoFontSize(state, 14));
        state = Shortcuts.ChangeFontSize(state, TextSizeChange.Smaller)!;
        Assert.Equal(14, state.Size);
        Assert.Equal(new FontSizeState(14, null), Shortcuts.EchoFontSize(Shortcuts.EchoFontSize(state, 15), 14));
        Assert.Equal(new FontSizeState(20, null), Shortcuts.EchoFontSize(new FontSizeState(14, null), 20));
        Assert.Null(Shortcuts.ChangeFontSize(new FontSizeState(32, null), TextSizeChange.Bigger));
        Assert.Null(Shortcuts.ChangeFontSize(new FontSizeState(13, null), TextSizeChange.Reset));
    }

    [Fact]
    public void FontSizeAfter_steps_by_one_within_8_to_32_and_goes_back_to_13()
    {
        Assert.Equal(14, Shortcuts.FontSizeAfter(13, TextSizeChange.Bigger));
        Assert.Equal(12, Shortcuts.FontSizeAfter(13, TextSizeChange.Smaller));
        Assert.Equal(32, Shortcuts.FontSizeAfter(32, TextSizeChange.Bigger));
        Assert.Equal(8, Shortcuts.FontSizeAfter(8, TextSizeChange.Smaller));
        Assert.Equal(13, Shortcuts.FontSizeAfter(20, TextSizeChange.Reset));
    }

    [Fact]
    public void Terminal_font_matches_xterm_rows()
    {
        Assert.Equal((9.75f, 16), Shortcuts.TerminalFont(13));
        Assert.Equal((6f, 10), Shortcuts.TerminalFont(8));
        Assert.Equal((24f, 39), Shortcuts.TerminalFont(32));
    }
}
