using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>An icon of <see cref="Icons"/> (the Electron UI's geometry), drawn in the inherited text colour at
/// <see cref="Size"/> px: the stroked ones 1.6 viewBox units wide with round caps and joins, as the SVGs are.</summary>
public sealed class VectorIcon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string), typeof(VectorIcon),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(nameof(Size), typeof(double), typeof(VectorIcon),
        new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(VectorIcon),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    static readonly Dictionary<string, Geometry> Cache = [];

    static Geometry GeometryOf(IconDefinition icon)
    {
        if (Cache.TryGetValue(icon.Name, out var cached)) return cached;
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (var part in icon.Parts)
            group.Children.Add(part switch
            {
                IconPath p => Geometry.Parse(SvgPath.ToWpf(p.D)),
                IconRect r => new RectangleGeometry(new Rect(r.X, r.Y, r.Width, r.Height), r.Rx, r.Rx),
                IconCircle c => new EllipseGeometry(new Point(c.Cx, c.Cy), c.R, c.R),
                _ => Geometry.Empty,
            });
        group.Freeze();
        return Cache[icon.Name] = group;
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    protected override void OnRender(DrawingContext dc)
    {
        if (Icons.Find(Kind) is not { } icon) return;
        var scale = Size / icon.ViewBox;
        dc.PushTransform(new ScaleTransform(scale, scale));
        var geometry = GeometryOf(icon);
        if (icon.Filled) dc.DrawGeometry(Foreground, null, geometry);
        else
        {
            var pen = new Pen(Foreground, 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            dc.DrawGeometry(null, pen, geometry);
        }
        dc.Pop();
    }
}

/// <summary>StatusDot.tsx: an 8 px dot per status (styles.css .status-*): idle green, busy accent (pulsing), background
/// accent dimmed, waiting yellow (pulsing faster), starting grey (pulsing), ended a hollow ring, error red.</summary>
public sealed class StatusDot : FrameworkElement
{
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(nameof(Status), typeof(string), typeof(StatusDot),
        new FrameworkPropertyMetadata("ended", FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((StatusDot)d).Animate()));

    /// <summary>No pulse (diagnostic captures, so that the same frame is captured each time).</summary>
    public static bool Still { get; set; }

    public string Status { get => (string)GetValue(StatusProperty); set => SetValue(StatusProperty, value); }

    public StatusDot()
    {
        Width = Height = 8;
        SnapsToDevicePixels = true;
        Loaded += (_, _) => Animate();
    }

    void Animate()
    {
        var seconds = Status switch { "busy" or "starting" => 1.2, "waiting" => 0.9, _ => 0 };
        if (seconds == 0 || Still || SystemParameters.ClientAreaAnimation == false)
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = Status == "background" ? 0.45 : 1;
            return;
        }
        // @keyframes pulse { 50% { opacity: 0.35 } }, ease-in-out.
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.35, TimeSpan.FromSeconds(seconds / 2))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
    }

    static Brush Resource(string key) => (Brush)Application.Current.FindResource(key);

    protected override void OnRender(DrawingContext dc)
    {
        var center = new Point(4, 4);
        if (Status == "ended")
        {
            dc.DrawEllipse(null, new Pen(Resource("mutedBrush"), 1.5), center, 3.25, 3.25);
            return;
        }
        var fill = Status switch
        {
            "idle" => Resource("okBrush"),
            "busy" or "background" => Resource("accentBrush"),
            "waiting" => Resource("warnBrush"),
            "error" => Resource("dangerBrush"),
            _ => Resource("mutedBrush"),
        };
        dc.DrawEllipse(fill, null, center, 4, 4);
    }
}

/// <summary>Text with CSS letter-spacing (WPF's TextBlock has none): the sidebar's section headings
/// (.sidebar-heading: 11px, 600, letter-spacing 0.06em, upper case).</summary>
public sealed class SpacedText : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(SpacedText),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(SpacedText),
        new FrameworkPropertyMetadata(0.06, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(SpacedText),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(typeof(SpacedText),
        new FrameworkPropertyMetadata(11.0, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(typeof(SpacedText),
        new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty FontWeightProperty = TextElement.FontWeightProperty.AddOwner(typeof(SpacedText),
        new FrameworkPropertyMetadata(FontWeights.Normal, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure));
    /// <summary>The line box's height (CSS line-height in px); the text is centred in it as CSS does.</summary>
    public static readonly DependencyProperty LineHeightProperty = DependencyProperty.Register(nameof(LineHeight), typeof(double), typeof(SpacedText),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    /// <summary>In em.</summary>
    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public FontWeight FontWeight { get => (FontWeight)GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }
    public double LineHeight { get => (double)GetValue(LineHeightProperty); set => SetValue(LineHeightProperty, value); }

    List<FormattedText> Glyphs() => Text.Select(c => new FormattedText(c.ToString(), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
        new Typeface(FontFamily, FontStyles.Normal, FontWeight, FontStretches.Normal), FontSize, Foreground,
        VisualTreeHelper.GetDpi(this).PixelsPerDip)).ToList();

    double Advance(FormattedText glyph) => glyph.WidthIncludingTrailingWhitespace + Spacing * FontSize;

    protected override Size MeasureOverride(Size availableSize)
    {
        var glyphs = Glyphs();
        var height = double.IsNaN(LineHeight) ? (glyphs.Count > 0 ? glyphs.Max(g => g.Height) : FontSize * 1.33) : LineHeight;
        return new Size(glyphs.Sum(Advance), height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var x = 0.0;
        foreach (var glyph in Glyphs())
        {
            var y = double.IsNaN(LineHeight) ? 0 : (LineHeight - glyph.Height) / 2;
            dc.DrawText(glyph, new Point(x, y));
            x += Advance(glyph);
        }
    }
}
