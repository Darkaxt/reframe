using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Reframe.Shell.Tests;

/// <summary>The WPF shell's design tokens (Reframe.Shell/Theme/Tokens.xaml) match the Electron UI they copy:
/// src/renderer/styles.css and the title bar overlay of src/main/windows.ts.</summary>
public class TokenTests
{
    static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "src", "renderer", "styles.css"))) return dir.FullName;
        throw new InvalidOperationException("repository root (src/renderer/styles.css) not found");
    }

    static readonly string Root = RepoRoot();
    static readonly string Css = File.ReadAllText(Path.Combine(Root, "src", "renderer", "styles.css"));

    /// <summary>Tokens.xaml's resources by key: element name and text.</summary>
    static readonly Dictionary<string, (string Kind, string Value)> Tokens = XDocument
        .Load(Path.Combine(Root, "wpf", "Reframe.Shell", "Theme", "Tokens.xaml"))
        .Root!.Elements()
        .Where(e => e.Attribute(X + "Key") is not null)
        .ToDictionary(e => e.Attribute(X + "Key")!.Value, e => (e.Name.LocalName, e.Value.Trim()));

    /// <summary>The declarations of the first rule whose selector is exactly <paramref name="selector"/>.</summary>
    static Dictionary<string, string> Rule(string selector)
    {
        var match = Regex.Match(Css, @"(?:^|\n)" + Regex.Escape(selector) + @"\s*\{([^}]*)\}");
        Assert.True(match.Success, $"no rule {selector} in styles.css");
        return Regex.Matches(match.Groups[1].Value, @"([-\w]+)\s*:\s*([^;]+);")
            .GroupBy(m => m.Groups[1].Value)
            .ToDictionary(g => g.Key, g => g.Last().Groups[2].Value.Trim());
    }

    static double Px(string value)
    {
        // "270px", "var(--sidebar-width, 270px)", "6px" (one-value padding).
        var match = Regex.Match(value, @"(\d+(?:\.\d+)?)px\)?$");
        Assert.True(match.Success, $"not a px length: {value}");
        return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    double Token(string key)
    {
        Assert.True(Tokens.TryGetValue(key, out var token), $"Tokens.xaml has no {key}");
        return double.Parse(token.Value, CultureInfo.InvariantCulture);
    }

    [Fact]
    public void ColoursMatchTheRootCustomProperties()
    {
        var root = Rule(":root");
        var colours = root.Where(p => p.Key.StartsWith("--") && p.Value.StartsWith('#'))
            .ToDictionary(p => p.Key[2..], p => p.Value.ToLowerInvariant());
        // The ones the parity plan lists must be among them.
        foreach (var name in new[] { "bg", "surface", "terminal", "border", "border-strong", "text", "muted", "accent",
                     "accent-text", "ok", "warn", "danger", "orange" })
            Assert.Contains(name, colours.Keys);
        foreach (var (name, css) in colours)
        {
            Assert.True(Tokens.TryGetValue(name, out var token), $"Tokens.xaml has no colour {name}");
            Assert.Equal("Color", token.Kind);
            Assert.Equal(css, token.Value.ToLowerInvariant());
            Assert.True(Tokens.ContainsKey(name + "Brush"), $"Tokens.xaml has no brush {name}Brush");
        }
        var xamlColours = Tokens.Where(t => t.Value.Kind == "Color").Select(t => t.Key);
        Assert.Empty(xamlColours.Except(colours.Keys));
    }

    [Fact]
    public void RadiusAndFontSizesMatch()
    {
        var root = Rule(":root");
        Assert.Equal(Px(root["--radius"]), Token("radius"));
        Assert.Equal(Token("radius"), double.Parse(Tokens["radiusCorner"].Value, CultureInfo.InvariantCulture));
        Assert.Equal(double.Parse(Regex.Match(root["font"], @"(\d+)px").Groups[1].Value), Token("FontSize.Base"));

        var sizes = Regex.Matches(Css, @"font-size:\s*(\d+)px").GroupBy(m => double.Parse(m.Groups[1].Value))
            .ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(sizes.MaxBy(s => s.Value).Key, Token("FontSize.Small")); // 12px dominates
        foreach (var key in new[] { "FontSize.Small", "FontSize.Smaller", "FontSize.Tiny" })
            Assert.Contains(Token(key), sizes.Keys);
        Assert.Equal(11, Token("FontSize.Smaller"));
        Assert.Equal(10, Token("FontSize.Tiny"));
    }

    [Theory]
    [InlineData("Toolbar.Height", ".toolbar", "height")]
    [InlineData("TileHeader.Height", ".tile-header", "height")]
    [InlineData("GroupHeader.Height", ".group-header", "height")]
    [InlineData("ToolbarIcon.Size", ".toolbar-icon", "width")]
    [InlineData("IconButton.Size", ".icon-button", "width")]
    [InlineData("FolderWorkspace.MinHeight", ".folder-workspace", "min-height")]
    [InlineData("Sidebar.Width", ".sidebar", "width")]
    [InlineData("SidebarRow.Padding", ".sidebar-row", "padding")]
    [InlineData("Dock.Width", ".dock", "width")]
    [InlineData("StatusDot.Size", ".status-dot", "width")]
    [InlineData("QuotaBar.Height", ".quota-bar", "height")]
    public void SizesMatchTheirCssRule(string key, string selector, string property)
    {
        var rule = Rule(selector);
        Assert.True(rule.ContainsKey(property), $"{selector} has no {property}");
        Assert.Equal(Px(rule[property]), Token(key));
    }

    [Fact]
    public void TitleBarHeightMatchesTheOverlay()
    {
        var windows = File.ReadAllText(Path.Combine(Root, "src", "main", "windows.ts"));
        var match = Regex.Match(windows, @"TITLE_BAR_OVERLAY\s*=\s*\{[^}]*height:\s*(\d+)");
        Assert.True(match.Success, "TITLE_BAR_OVERLAY not found in windows.ts");
        Assert.Equal(double.Parse(match.Groups[1].Value), Token("TitleBar.Height"));
        // And its colours are tokens.
        Assert.Contains("color: '" + Tokens["surface"].Value, windows);
        Assert.Contains("symbolColor: '" + Tokens["text"].Value, windows);
    }
}
