using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using static Reframe.Shell.Core.Strings;

namespace Reframe.Shell;

/// <summary>
/// About Reframe (AboutDialog.tsx): the logo, the name, the version (package.json's, which the build stamps into the
/// shell), the tagline, the GitHub links, the credit, the licence (folded) and Close. WPF draws no colour emoji, so the
/// star and the heart are drawn as the Segoe UI Emoji glyphs look.
/// </summary>
sealed class AboutDialog
{
    public const string RepoUrl = "https://github.com/Darkaxt/reframe";
    public const string AuthorUrl = "https://github.com/TheyCallMeJuzou";

    static Brush B(string key) => (Brush)Application.Current.FindResource(key);
    static Style S(string key) => (Style)Application.Current.FindResource(key);

    public Border Frame { get; }
    public Button CloseButton { get; }
    public event Action? CloseRequested;

    /// <summary>The shell's version: package.json's, as the build stamped it.</summary>
    public static string Version =>
        typeof(AboutDialog).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";

    static string License()
    {
        using var stream = typeof(AboutDialog).Assembly.GetManifestResourceStream("LICENSE");
        return stream is null ? "" : new System.IO.StreamReader(stream).ReadToEnd();
    }

    static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    static TextBlock Centered(string text, string style = "Text13") =>
        new() { Text = text, Style = S(style), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };

    static Button Link(string url, UIElement content, string name)
    {
        var link = new Button { Style = S("LinkButton"), Content = content, HorizontalAlignment = HorizontalAlignment.Center, ToolTip = url };
        AutomationProperties.SetName(link, name);
        link.Click += (_, _) => Open(url);
        link.MouseEnter += (_, _) => Underline(content, true);
        link.MouseLeave += (_, _) => Underline(content, false);
        return link;
    }

    static void Underline(UIElement content, bool on)
    {
        foreach (var text in content is Panel panel ? panel.Children.OfType<TextBlock>() : content is TextBlock t ? [t] : [])
            text.TextDecorations = on ? TextDecorations.Underline : null;
    }

    public AboutDialog()
    {
        // .modal.about: align-items center, gap 4, centred text, 420 wide.
        var column = new StackPanel();
        void Add(FrameworkElement e, double top = 0, double bottom = 0)
        {
            e.Margin = new Thickness(e.Margin.Left, (column.Children.Count > 0 ? 4 : 0) + top, e.Margin.Right, bottom);
            column.Children.Add(e);
        }
        Add(Logo(72), 4);
        // .about-name says 20 px, but `.modal h2` (more specific) makes it 15 as in Electron.
        Add(new TextBlock { Text = "Reframe", FontSize = 15, FontWeight = FontWeights.Bold, LineHeight = 21, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, HorizontalAlignment = HorizontalAlignment.Center }, 6);
        var version = Centered(T("about.version", ("version", Version)));
        version.Foreground = B("mutedBrush");
        Add(version, 2, 2);
        var tagline = Centered(T("about.tagline"));
        tagline.Foreground = B("mutedBrush");
        Add(tagline, 2, 2);

        // .about-links: a column, 4 apart, 8 above and below.
        var links = new StackPanel();
        links.Children.Add(Link(RepoUrl, Centered(T("about.github")), T("about.github")));
        var star = new StackPanel { Orientation = Orientation.Horizontal };
        star.Children.Add(new Viewbox { Width = 15, Height = 15, Child = Star(), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
        star.Children.Add(new TextBlock { Text = T("about.star"), Style = S("Text13"), VerticalAlignment = VerticalAlignment.Center });
        var starLink = Link(RepoUrl, star, T("about.star"));
        starLink.Margin = new Thickness(0, 4, 0, 0);
        links.Children.Add(starLink);
        Add(links, 8, 8);

        // .about-credit: the text, the heart, "in Austria by reframe Team aka" and the author's link.
        var madeWith = T("about.madeWith").Split("❤️");
        var credit = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        credit.Children.Add(new TextBlock { Text = madeWith[0], Style = S("Text13"), VerticalAlignment = VerticalAlignment.Center });
        credit.Children.Add(new Viewbox { Width = 15, Height = 15, Child = Heart(), Margin = new Thickness(0, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        credit.Children.Add(new TextBlock { Text = (madeWith.Length > 1 ? madeWith[1] : "") + " ", Style = S("Text13"), VerticalAlignment = VerticalAlignment.Center });
        credit.Children.Add(Link(AuthorUrl, new TextBlock { Text = "TheyCallMeJuzou", Style = S("Text13") }, "TheyCallMeJuzou"));
        Add(credit, 8, 8);

        Add(LicenseDetails(), 6, 4);

        CloseButton = new Button { Style = S("PrimaryButton"), Content = new TextBlock { Text = T("about.close"), Style = S("Text13") }, HorizontalAlignment = HorizontalAlignment.Center };
        AutomationProperties.SetName(CloseButton, T("about.close"));
        CloseButton.Click += (_, _) => CloseRequested?.Invoke();
        Add(CloseButton);

        (Frame, _) = ModalHost.Frame(420, column);
        AutomationProperties.SetName(Frame, T("about.title"));
    }

    /// <summary>details.about-license: the summary (a disclosure triangle and the muted text) and, opened, the licence
    /// (pre: 11 px, padding 8, radius 4, bg, at most 180 high).</summary>
    static FrameworkElement LicenseDetails()
    {
        var details = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        var triangle = new Path { Data = Geometry.Parse("M0,0 L6,3.5 L0,7 Z"), Fill = B("mutedBrush"), Width = 7, Height = 7, Margin = new Thickness(1, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, RenderTransformOrigin = new Point(0.5, 0.5) };
        var summary = new StackPanel { Orientation = Orientation.Horizontal };
        summary.Children.Add(triangle);
        summary.Children.Add(new TextBlock { Text = T("about.license"), Style = S("Text13"), Foreground = B("mutedBrush") });
        var toggle = new ToggleButton { Content = summary, Cursor = System.Windows.Input.Cursors.Hand, FocusVisualStyle = null, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
        toggle.Template = new ControlTemplate(typeof(ToggleButton)) { VisualTree = Presenter() };
        AutomationProperties.SetName(toggle, T("about.license"));
        var pre = new ScrollViewer
        {
            MaxHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false,
            Content = new TextBlock { Text = License(), FontFamily = new FontFamily("Consolas"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = B("textBrush") },
        };
        var box = new Border { Background = B("bgBrush"), CornerRadius = new CornerRadius(4), Padding = new Thickness(8), Margin = new Thickness(0, 13, 0, 0), Child = pre, Visibility = Visibility.Collapsed };
        toggle.Checked += (_, _) =>
        {
            box.Visibility = Visibility.Visible;
            triangle.RenderTransform = new RotateTransform(90);
        };
        toggle.Unchecked += (_, _) =>
        {
            box.Visibility = Visibility.Collapsed;
            triangle.RenderTransform = null;
        };
        details.Children.Add(toggle);
        details.Children.Add(box);
        return details;
    }

    static FrameworkElementFactory Presenter()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        border.AppendChild(presenter);
        return border;
    }

    /// <summary>docs/logo/reframe-icon.svg at <paramref name="size"/> px: the dark rounded square and the four tiles.</summary>
    public static FrameworkElement Logo(double size)
    {
        var canvas = new Canvas { Width = 512, Height = 512 };
        void Rect(double x, double y, double s, double r, string color)
        {
            var rect = new Rectangle { Width = s, Height = s, RadiusX = r, RadiusY = r, Fill = (Brush)new BrushConverter().ConvertFromString(color)! };
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, y);
            canvas.Children.Add(rect);
        }
        Rect(30.72, 30.72, 450.56, 102.4, "#202228");
        Rect(102.4, 102.4, 135.68, 24.422, "#d97757");
        Rect(273.92, 102.4, 135.68, 24.422, "#565c6b");
        Rect(102.4, 273.92, 135.68, 24.422, "#565c6b");
        Rect(273.92, 273.92, 135.68, 24.422, "#565c6b");
        return new Viewbox { Width = size, Height = size, Child = canvas, HorizontalAlignment = HorizontalAlignment.Center };
    }

    /// <summary>⭐ as Segoe UI Emoji draws it: a gold five-pointed star with rounded points.</summary>
    static FrameworkElement Star()
    {
        var points = new PointCollection();
        for (var i = 0; i < 10; i++)
        {
            var angle = Math.PI / 5 * i - Math.PI / 2;
            var radius = i % 2 == 0 ? 15.5 : 7;
            points.Add(new Point(16 + radius * Math.Cos(angle), 17 + radius * Math.Sin(angle)));
        }
        var star = new Polygon { Points = points, Fill = new SolidColorBrush(Color.FromRgb(0xf6, 0xc3, 0x3a)), Stroke = new SolidColorBrush(Color.FromRgb(0xeb, 0xa5, 0x2a)), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
        return new Canvas { Width = 32, Height = 32, Children = { star } };
    }

    /// <summary>❤️ as Segoe UI Emoji draws it in Chromium: a pink-red heart (a gradient from #ff4f5a to #f8308f).</summary>
    static FrameworkElement Heart()
    {
        var heart = new Path
        {
            Data = Geometry.Parse("M16,29 C14,27 2,19.5 2,11 C2,6.5 5.5,3 9.5,3 C12.5,3 14.8,4.8 16,7 C17.2,4.8 19.5,3 22.5,3 C26.5,3 30,6.5 30,11 C30,19.5 18,27 16,29 Z"),
            Fill = new LinearGradientBrush(Color.FromRgb(0xff, 0x4f, 0x5a), Color.FromRgb(0xf8, 0x30, 0x8f), 90),
        };
        return new Canvas { Width = 32, Height = 32, Children = { heart } };
    }
}
