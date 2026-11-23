using System.Text.RegularExpressions;

namespace Reframe.Shell.Core;

/// <summary>A process of the process table: what <see cref="BackgroundWork.Describe"/> reads. The command line is
/// null when it was not read (the shell reads it only for a claude's descendants).</summary>
public sealed record ProcessEntry(int Pid, int ParentPid, string Exe, string? CommandLine, DateTime Started);

/// <summary>What a "background" thread is running: its claude ended its turn while commands it started (tool shells,
/// bash -c …) keep running. Names the work from the process table, for the tile header and the sidebar row.</summary>
public static class BackgroundWork
{
    const int MaxLabel = 80;

    static readonly HashSet<string> ToolShells = new(StringComparer.OrdinalIgnoreCase)
        { "bash.exe", "sh.exe", "cmd.exe", "pwsh.exe", "powershell.exe" };

    /// <summary>Processes that only host the work: shells and console hosts.</summary>
    static readonly HashSet<string> Plumbing = new(StringComparer.OrdinalIgnoreCase)
        { "bash.exe", "sh.exe", "cmd.exe", "conhost.exe", "OpenConsole.exe" };

    /// <summary>Interpreters named with their script: "python gemini_image.py".</summary>
    static readonly HashSet<string> Interpreters = new(StringComparer.OrdinalIgnoreCase)
        { "python.exe", "pythonw.exe", "python3.exe", "node.exe", "pwsh.exe", "powershell.exe" };

    static readonly Regex OwnerTask = new("--owner-task(?:=|\\s+)(?:\"([^\"]*)\"|(\\S+))", RegexOptions.CultureInvariant);
    static readonly Regex Eval = new("eval '((?:[^']|'\\\\'')*)'", RegexOptions.CultureInvariant);

    /// <summary>The label of the work a claude (<paramref name="claudePid"/>) runs in its tool shells, and since when
    /// (the oldest shell's start); null when it runs none.</summary>
    public static (string Label, DateTime Since)? Describe(int claudePid, IReadOnlyList<ProcessEntry> processes)
    {
        var claude = processes.FirstOrDefault(p => p.Pid == claudePid);
        if (claude is null) return null;
        var children = processes.Where(p => p.Pid != p.ParentPid).ToLookup(p => p.ParentPid);
        var shells = children[claudePid]
            .Where(p => ToolShells.Contains(p.Exe) && p.Started >= claude.Started)
            .OrderBy(p => p.Started).ToList();
        if (shells.Count == 0) return null;
        var labels = shells.Select(shell => Label(shell, children)).Distinct().ToList();
        var label = labels.Count > 1 ? $"{labels[0]} (+{labels.Count - 1} more)" : labels[0];
        return (Truncate(label), shells[0].Started);
    }

    /// <summary>The header's and row's text: "Background: python gemini_image.py · 14 min".</summary>
    public static string Text(string label, DateTime since, DateTime now)
    {
        var minutes = (int)Math.Max(0, (now - since).TotalMinutes);
        var age = minutes switch
        {
            < 1 => "just now",
            < 60 => $"{minutes} min",
            _ when minutes % 60 == 0 => $"{minutes / 60} h",
            _ => $"{minutes / 60} h {minutes % 60} min",
        };
        return $"Background: {label} · {age}";
    }

    static string Label(ProcessEntry shell, ILookup<int, ProcessEntry> children)
    {
        // The descendants, with their depth under the shell (guarding against pid reuse loops).
        var found = new List<(ProcessEntry Process, int Depth)>();
        var seen = new HashSet<int> { shell.Pid };
        var queue = new Queue<(ProcessEntry, int)>([(shell, 0)]);
        while (queue.Count > 0)
        {
            var (process, depth) = queue.Dequeue();
            foreach (var child in children[process.Pid])
                if (seen.Add(child.Pid))
                {
                    found.Add((child, depth + 1));
                    queue.Enqueue((child, depth + 1));
                }
        }
        // A gated workload names itself: coordinator-client … --owner-task "…".
        foreach (var (process, _) in found)
            if (process.Exe.Equals("coordinator-client.exe", StringComparison.OrdinalIgnoreCase)
                && process.CommandLine is { } line && OwnerTask.Match(line) is { Success: true } match)
            {
                var task = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                if (task.Trim() is { Length: > 0 } trimmed) return trimmed;
            }
        var work = found.Where(f => !Plumbing.Contains(f.Process.Exe))
            .OrderByDescending(f => f.Depth).ThenBy(f => f.Process.Started).Select(f => f.Process).FirstOrDefault();
        return work is null ? ShellCommand(shell.CommandLine) : Program(work);
    }

    /// <summary>"python gemini_image.py", "gh run", or the exe's name.</summary>
    static string Program(ProcessEntry process)
    {
        var name = Path.GetFileNameWithoutExtension(process.Exe);
        var args = Arguments(process.CommandLine).Skip(1).ToList();
        if (Interpreters.Contains(process.Exe))
        {
            var isPowerShell = name.Contains("powershell", StringComparison.OrdinalIgnoreCase) || name.Equals("pwsh", StringComparison.OrdinalIgnoreCase);
            if (isPowerShell)
            {
                var file = args.FindIndex(a => a.Equals("-File", StringComparison.OrdinalIgnoreCase) || a.Equals("-f", StringComparison.OrdinalIgnoreCase));
                return file >= 0 && file + 1 < args.Count ? $"{name} {FileName(args[file + 1])}" : name;
            }
            var script = args.FirstOrDefault(a => !a.StartsWith('-'));
            return script is null ? name : $"{name} {FileName(script)}";
        }
        var first = args.FirstOrDefault(a => !a.StartsWith('-') && !a.StartsWith('/'));
        return first is null || first.Contains('\\') || first.Contains('/') ? name : $"{name} {first}";
    }

    static string FileName(string path)
    {
        var slash = path.LastIndexOfAny(['\\', '/']);
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    /// <summary>The command of Claude Code's <c>bash -c "source … &amp;&amp; eval '&lt;command&gt;' …"</c>, else
    /// "a shell command".</summary>
    static string ShellCommand(string? line)
    {
        if (line is not null && Eval.Match(line) is { Success: true } match)
        {
            var command = match.Groups[1].Value.Replace("'\\''", "'");
            var firstLine = command.Split('\n')[0].Trim();
            if (firstLine.Length > 0) return firstLine;
        }
        return "a shell command";
    }

    /// <summary>A Windows command line's arguments (double quotes group, and are dropped).</summary>
    static IEnumerable<string> Arguments(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) yield break;
        var current = new System.Text.StringBuilder();
        bool quoted = false, any = false;
        foreach (var c in line)
        {
            if (c == '"') { quoted = !quoted; any = true; }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any) yield return current.ToString();
                current.Clear();
                any = false;
            }
            else { current.Append(c); any = true; }
        }
        if (any) yield return current.ToString();
    }

    static string Truncate(string label) => label.Length > MaxLabel ? label[..(MaxLabel - 1)] + "…" : label;
}
