using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Reframe.Shell.Core;

/// <summary>A part of an icon: an SVG path, a rectangle or a circle (in the icon's viewBox units).</summary>
public abstract record IconPart;
public sealed record IconPath(string D) : IconPart;
public sealed record IconRect(double X, double Y, double Width, double Height, double Rx) : IconPart;
public sealed record IconCircle(double Cx, double Cy, double R) : IconPart;

/// <summary>An icon: stroked (1.6 wide, round caps and joins, in a 16 viewBox, as components/icons.tsx draws them) or
/// filled (the provider marks of ProviderIcon.tsx, in a 24 viewBox).</summary>
public sealed record IconDefinition(string Name, double ViewBox, bool Filled, IReadOnlyList<IconPart> Parts);

/// <summary>
/// The Electron UI's icons with the same geometry: path data copied verbatim from src/renderer/components/icons.tsx and
/// ProviderIcon.tsx (brand paths: LobeHub lobe-icons, MIT, see docs/provider-icons.md). IconTests checks that every path
/// is still in those files.
/// </summary>
public static class Icons
{
    static IconDefinition Stroked(string name, params IconPart[] parts) => new(name, 16, false, parts);
    static IconDefinition Filled(string name, double viewBox, params IconPart[] parts) => new(name, viewBox, true, parts);

    public static readonly IconDefinition Folder = Stroked("folder", new IconPath("M1.5 4V3h5l1.5 2h6.5v8H1.5V4Z"));
    public static readonly IconDefinition Collapse = Stroked("collapse", new IconPath("M10 3.5 5.5 8l4.5 4.5"));
    public static readonly IconDefinition Close = Stroked("close", new IconPath("m4 4 8 8M12 4l-8 8"));
    public static readonly IconDefinition Window = Stroked("window", new IconRect(2.5, 3, 11, 10, 1.5), new IconPath("M2.5 6h11"));
    public static readonly IconDefinition Info = Stroked("info", new IconCircle(8, 8, 6), new IconPath("M8 7.2v4M8 4.8v.01"));
    public static readonly IconDefinition Gear = Stroked("gear", new IconCircle(8, 8, 2.2),
        new IconPath("M8 1.6v1.8M8 12.6v1.8M1.6 8h1.8M12.6 8h1.8M3.5 3.5l1.3 1.3M11.2 11.2l1.3 1.3M3.5 12.5l1.3-1.3M11.2 4.8l1.3-1.3"),
        new IconCircle(8, 8, 4.4));
    public static readonly IconDefinition Plus = Stroked("plus", new IconPath("M8 3v10M3 8h10"));
    public static readonly IconDefinition List = Stroked("list", new IconPath("M3 4h10M3 8h10M3 12h10"));
    public static readonly IconDefinition Zoom = Stroked("zoom", new IconPath("M9.5 2.5h4v4M6.5 13.5h-4v-4M13.5 2.5 9 7M2.5 13.5 7 9"));
    public static readonly IconDefinition Unzoom = Stroked("unzoom", new IconPath("M13 7H9V3M3 9h4v4M9 7l4.5-4.5M7 9l-4.5 4.5"));
    public static readonly IconDefinition ExitFullscreen = Stroked("exit-fullscreen", new IconPath("M6 2.5V6H2.5M10 2.5V6h3.5M6 13.5V10H2.5M10 13.5V10h3.5"));
    public static readonly IconDefinition ChevronUp = Stroked("chevron-up", new IconPath("M3.5 10 8 5.5l4.5 4.5"));
    public static readonly IconDefinition ChevronDown = Stroked("chevron-down", new IconPath("M3.5 6 8 10.5 12.5 6"));
    /// <summary>MoreIcon: filled dots (r 1.3) at x 4, 8, 12 on y 8.</summary>
    public static readonly IconDefinition More = Filled("more", 16, [.. new[] { 4.0, 8, 12 }.Select(x => (IconPart)new IconCircle(x, 8, 1.3))]);
    public static readonly IconDefinition Grid = Stroked("grid", new IconRect(2, 2, 5, 5, 1), new IconRect(9, 2, 5, 5, 1),
        new IconRect(2, 9, 5, 5, 1), new IconRect(9, 9, 5, 5, 1));
    /// <summary>GripIcon: filled dots (r 1.1) at x 6, 10 and y 5, 8, 11.</summary>
    public static readonly IconDefinition Grip = Filled("grip", 16,
        [.. new[] { 5.0, 8, 11 }.SelectMany(y => new[] { 6.0, 10 }.Select(x => (IconPart)new IconCircle(x, y, 1.1)))]);

    public static readonly IconDefinition ChatGpt = Filled("chatgpt", 24, new IconPath(
        "M9.205 8.658v-2.26c0-.19.072-.333.238-.428l4.543-2.616c.619-.357 1.356-.523 2.117-.523 2.854 0 4.662 2.212 4.662 4.566 0 .167 0 .357-.024.547l-4.71-2.759a.797.797 0 00-.856 0l-5.97 3.473zm10.609 8.8V12.06c0-.333-.143-.57-.429-.737l-5.97-3.473 1.95-1.118a.433.433 0 01.476 0l4.543 2.617c1.309.76 2.189 2.378 2.189 3.948 0 1.808-1.07 3.473-2.76 4.163zM7.802 12.703l-1.95-1.142c-.167-.095-.239-.238-.239-.428V5.899c0-2.545 1.95-4.472 4.591-4.472 1 0 1.927.333 2.712.928L8.23 5.067c-.285.166-.428.404-.428.737v6.898zM12 15.128l-2.795-1.57v-3.33L12 8.658l2.795 1.57v3.33L12 15.128zm1.796 7.23c-1 0-1.927-.332-2.712-.927l4.686-2.712c.285-.166.428-.404.428-.737v-6.898l1.974 1.142c.167.095.238.238.238.428v5.233c0 2.545-1.974 4.472-4.614 4.472zm-5.637-5.303l-4.544-2.617c-1.308-.761-2.188-2.378-2.188-3.948A4.482 4.482 0 014.21 6.327v5.423c0 .333.143.571.428.738l5.947 3.449-1.95 1.118a.432.432 0 01-.476 0zm-.262 3.9c-2.688 0-4.662-2.021-4.662-4.519 0-.19.024-.38.047-.57l4.686 2.71c.286.167.571.167.856 0l5.97-3.448v2.26c0 .19-.07.333-.237.428l-4.543 2.616c-.619.357-1.356.523-2.117.523zm5.899 2.83a5.947 5.947 0 005.827-4.756C22.287 18.339 24 15.84 24 13.296c0-1.665-.713-3.282-1.998-4.448.119-.5.19-.999.19-1.498 0-3.401-2.759-5.947-5.946-5.947-.642 0-1.26.095-1.88.31A5.962 5.962 0 0010.205 0a5.947 5.947 0 00-5.827 4.757C1.713 5.447 0 7.945 0 10.49c0 1.666.713 3.283 1.998 4.448-.119.5-.19 1-.19 1.499 0 3.401 2.759 5.946 5.946 5.946.642 0 1.26-.095 1.88-.309a5.96 5.96 0 004.162 1.713z"));
    public static readonly IconDefinition Anthropic = Filled("anthropic", 24, new IconPath(
        "M13.827 3.52h3.603L24 20h-3.603l-6.57-16.48zm-7.258 0h3.767L16.906 20h-3.674l-1.343-3.461H5.017l-1.344 3.46H0L6.57 3.522zm4.132 9.959L8.453 7.687 6.205 13.48H10.7z"));

    public static readonly IReadOnlyList<IconDefinition> All =
        [Folder, Collapse, Close, Window, Info, Gear, Plus, List, Zoom, Unzoom, ExitFullscreen, ChevronUp, ChevronDown, More, Grid, Grip, ChatGpt, Anthropic];

    /// <summary>The WPF shell's own icons (no Electron counterpart, so not in <see cref="All"/>): the per-thread
    /// meters' chip, graphics card and memory module, drawn as Windows' Taskbar Thermals widget draws them.</summary>
    public static readonly IReadOnlyList<IconDefinition> ShellOnly =
    [
        Stroked("cpu", new IconRect(4, 4, 8, 8, 1), new IconRect(6.5, 6.5, 3, 3, 0.5),
            new IconPath("M6 1.5v2M10 1.5v2M6 12.5v2M10 12.5v2M1.5 6h2M1.5 10h2M12.5 6h2M12.5 10h2")),
        Stroked("gpu", new IconRect(1.5, 4, 13, 7.5, 1), new IconCircle(6, 7.75, 2), new IconPath("M10.5 6.5h2M10.5 9h2M3 11.5v2")),
        Stroked("ram", new IconRect(1.5, 4.5, 13, 6, 1), new IconPath("M4.5 6.75v1.5M7 6.75v1.5M9.5 6.75v1.5M12 6.75v1.5M3.5 10.5v2M6 10.5v2M10 10.5v2M12.5 10.5v2")),
    ];

    public static IconDefinition? Find(string name) => All.FirstOrDefault(i => i.Name == name) ?? ShellOnly.FirstOrDefault(i => i.Name == name);
}

/// <summary>SVG path data rewritten for WPF's path markup: one space between every token, so that what SVG allows
/// and WPF's parser does not (".5.5", arc flags written together as in "a1 1 0 00-1 0") reads the same.</summary>
public static partial class SvgPath
{
    [GeneratedRegex(@"\G[+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?")]
    private static partial Regex Number();

    public static string ToWpf(string d)
    {
        var result = new StringBuilder();
        var command = ' ';
        var argument = 0;
        var i = 0;
        while (i < d.Length)
        {
            var c = d[i];
            if (char.IsWhiteSpace(c) || c == ',')
            {
                i++;
                continue;
            }
            if ("MmLlHhVvCcSsQqTtAaZz".Contains(c))
            {
                command = c;
                argument = 0;
                Append(result, c.ToString());
                i++;
                continue;
            }
            // An arc's fourth and fifth arguments are flags: one character each, with or without separators.
            if (command is 'A' or 'a' && argument % 7 is 3 or 4 && c is '0' or '1')
            {
                Append(result, c.ToString());
                argument++;
                i++;
                continue;
            }
            var match = Number().Match(d, i);
            if (!match.Success) throw new FormatException($"unexpected '{c}' at {i} in path data");
            Append(result, double.Parse(match.Value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture));
            argument++;
            i += match.Length;
        }
        return result.ToString();
    }

    static void Append(StringBuilder result, string token)
    {
        if (result.Length > 0) result.Append(' ');
        result.Append(token);
    }
}
