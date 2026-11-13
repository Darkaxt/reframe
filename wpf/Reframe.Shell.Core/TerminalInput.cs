using System.Text;
using System.Text.RegularExpressions;

namespace Reframe.Shell.Core;

/// <summary>What Enter does in a terminal tile (src/renderer/terminal-keys.ts).</summary>
public enum EnterAction { Copy, Newline }

/// <summary>
/// Enter in a terminal tile, as in the Windows console: with text selected it copies the selection (nothing reaches the
/// session) and deselects; Shift+Enter without a selection types a newline into Claude Code's prompt. ESC CR (Meta+Enter)
/// is what Claude Code reads as a newline. The WPF shell handles the key down (WPF's PreviewKeyDown), and a handled key
/// down never becomes a WM_CHAR, so there is no keypress to swallow as in xterm.
/// </summary>
public static class TerminalKeys
{
    public const string Newline = "\u001b\r";

    public static EnterAction? EnterKey(bool shift, bool ctrl, bool alt, bool hasSelection)
    {
        if (ctrl || alt) return null;
        return hasSelection ? EnterAction.Copy : shift ? EnterAction.Newline : null;
    }

    /// <summary>The copied text as the copy notice shows it: on one line, its first <paramref name="max"/> characters.</summary>
    public static string CopyPreview(string text, int max = 40)
    {
        var line = Regex.Replace(text, @"\s+", " ").Trim();
        return line.Length > max ? line[..max] + "…" : line;
    }

    /// <summary>Whether a mouse drag in a terminal that reads the mouse would have selected text, if Shift had been held.</summary>
    public static bool DragWithoutShift((double X, double Y)? start, double x, double y, bool leftButton, bool shift, bool mouseTracking)
    {
        if (start is not { } s || !mouseTracking || shift || !leftButton) return false;
        return Math.Sqrt((x - s.X) * (x - s.X) + (y - s.Y) * (y - s.Y)) > 4;
    }

    /// <summary>Ctrl+C copies when text is selected or with Shift (otherwise it interrupts claude), Ctrl+V pastes.</summary>
    public static string? ClipboardKey(char key, bool ctrl, bool shift, bool alt, bool hasSelection)
    {
        if (!ctrl || alt) return null;
        return char.ToLowerInvariant(key) switch
        {
            'c' when shift || hasSelection => "copy",
            'v' => "paste",
            _ => null,
        };
    }
}

/// <summary>What reframe types into a session for files and images, the way a terminal would (src/renderer/paste.ts).</summary>
public static class PasteText
{
    /// <summary>A session in WSL: its distribution and where Windows drives are mounted.</summary>
    public sealed record Wsl(string Distro, string MountRoot);

    /// <summary>File paths as one would type them: quoted where needed, separated by spaces, with a space after. For a
    /// session in WSL they are its Linux paths (C:\x → /mnt/c/x), quoted for its shell.</summary>
    public static string FormatPaths(IReadOnlyList<string> paths, bool windows, Wsl? wsl = null)
    {
        if (wsl is not null)
            return FormatPaths(paths.Select(p => WslPaths.LinuxPathOf(p, wsl.Distro, wsl.MountRoot) ?? p).ToList(), false);
        Func<string, string> quote = windows
            ? p => Regex.IsMatch(p, "[\\s&()^'\"`,;=!%]") ? $"\"{p}\"" : p
            : p => Regex.IsMatch(p, @"^[\w@%+=:,./-]+$") ? p : $"'{p.Replace("'", "'\\''")}'";
        return paths.Count > 0 ? string.Join(' ', paths.Select(quote)) + " " : "";
    }

    /// <summary>The key Claude Code takes to paste the clipboard's image: Alt+V on Windows, Ctrl+V elsewhere.</summary>
    public static string ImagePasteKey(bool windows) => windows ? "\u001bv" : "\u0016";

    /// <summary>Whether reframe saves the clipboard's image to a file and types its path instead of sending Claude Code
    /// its image-paste key: in WSL Claude Code cannot reach the clipboard; on Windows it reads it slowly (PowerShell).</summary>
    public static bool PastesImageAsFile(bool windows, bool wsl) => wsl || windows;

    /// <summary>Text as xterm's paste sends it: line breaks as CR, and inside ESC[200~ … ESC[201~ while the program asked
    /// for bracketed paste (xterm's prepareTextForTerminal and bracketTextForPaste).</summary>
    public static string Prepare(string text, bool bracketed)
    {
        var prepared = Regex.Replace(text, "\r?\n", "\r");
        return bracketed ? $"\u001b[200~{prepared}\u001b[201~" : prepared;
    }
}

/// <summary>Folders in WSL distributions as Windows sees them and as Linux does (src/shared/wsl.ts).</summary>
public static class WslPaths
{
    public static readonly string[] Shares = [@"\\wsl.localhost", @"\\wsl$"];
    public const string DefaultMountRoot = "/mnt/";

    public static (string Distro, string Path)? Parse(string windowsPath, string? share = null)
    {
        var normalized = windowsPath.Replace('/', '\\');
        foreach (var root in (share is null ? Shares : Shares.Prepend(share)))
        {
            var prefix = root.TrimEnd('\\', '/') + "\\";
            if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var parts = normalized[prefix.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;
            return (parts[0], "/" + string.Join('/', parts.Skip(1)));
        }
        return null;
    }

    /// <summary>A Windows path as programs in <paramref name="distro"/> reach it, or null for another distribution's folders.</summary>
    public static string? LinuxPathOf(string windowsPath, string distro, string mountRoot, string? share = null)
    {
        var drive = Regex.Match(windowsPath, @"^([A-Za-z]):(?:[\\/](.*))?$");
        if (drive.Success)
        {
            var rest = drive.Groups[2].Value.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            return $"{mountRoot.TrimEnd('/')}/{char.ToLowerInvariant(drive.Groups[1].Value[0])}{string.Concat(rest.Select(p => "/" + p))}";
        }
        return Parse(windowsPath, share) is { } own && string.Equals(own.Distro, distro, StringComparison.OrdinalIgnoreCase) ? own.Path : null;
    }
}

/// <summary>
/// The terminal modes the shell needs and the WPF terminal control does not report: bracketed paste (DECSET 2004) and
/// whether the program reads the mouse (1000, 1002, 1003). Fed the session's output as it is written to the control;
/// a sequence split between two writes is kept until its end comes. RIS (ESC c, as the snapshot starts) resets them.
/// </summary>
public sealed class TerminalModes
{
    static readonly Regex Sequence = new(@"\u001b(?:c|\[\?([0-9;]*)([hl]))", RegexOptions.Compiled);
    readonly HashSet<int> mouse = [];
    string tail = "";

    public bool BracketedPaste { get; private set; }
    public bool MouseTracking => mouse.Count > 0;

    public void Feed(string data)
    {
        var text = tail + data;
        tail = "";
        var consumed = 0;
        foreach (Match m in Sequence.Matches(text))
        {
            consumed = m.Index + m.Length;
            if (m.Value == "\u001bc")
            {
                BracketedPaste = false;
                mouse.Clear();
                continue;
            }
            var on = m.Groups[2].Value == "h";
            foreach (var part in m.Groups[1].Value.Split(';'))
            {
                if (!int.TryParse(part, out var mode)) continue;
                if (mode == 2004) BracketedPaste = on;
                else if (mode is 1000 or 1002 or 1003)
                {
                    if (on) mouse.Add(mode);
                    else mouse.Remove(mode);
                }
            }
        }
        // An escape near the end that may be the start of a sequence waits for the next write.
        var esc = text.LastIndexOf('\u001b');
        if (esc >= consumed && esc >= text.Length - 16 && Regex.IsMatch(text[esc..], @"^\u001b(\[(\?[0-9;]*)?)?$")) tail = text[esc..];
    }
}

/// <summary>A file for a session (a drop that exists only in memory, the clipboard's image), saved under reframe's drops
/// folder (%TEMP%\reframe-drops\&lt;uuid&gt;\name, as src/main/app.ts saveDroppedFile; the host's cleanup removes old ones).</summary>
public static class Drops
{
    public const long MaxBytes = 200L * 1024 * 1024;

    public static string Dir => Path.Combine(Path.GetTempPath(), "reframe-drops");

    /// <summary>A plain file name: no folders, nothing Windows refuses in names, at most 120 characters.</summary>
    public static string SafeName(string name)
    {
        var baseName = name.Split('/', '\\').Last();
        var cleaned = new StringBuilder();
        foreach (var c in baseName) cleaned.Append(c < ' ' || "<>:\"/\\|?*".Contains(c) ? '_' : c);
        var safe = cleaned.ToString();
        if (safe.Length > 120) safe = safe[..120];
        return safe.Length > 0 ? safe : "dropped";
    }

    public static string? Save(string name, byte[] data, string? root = null)
    {
        if (data.LongLength > MaxBytes) return null;
        var dir = Path.Combine(root ?? Dir, Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, SafeName(name));
        File.WriteAllBytes(file, data);
        return file;
    }
}
