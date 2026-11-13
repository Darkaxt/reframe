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

/// <summary>
/// An input's <c>&lt;datalist&gt;</c> (the model field's MODEL_SUGGESTIONS) as Chromium shows it: a list under the
/// field with the suggestions that match what is typed (<see cref="NewSession.MatchingModels"/>), opened by typing, a
/// click in the field or Down; Up/Down move through it, Enter or a click takes one, Escape or leaving the field closes
/// it. Drawn like the dialogs' select lists; focus stays in the field.
/// </summary>
public static class Suggestions
{
    static Brush B(string key) => (Brush)Application.Current.FindResource(key);

    public static void Attach(System.Windows.Controls.TextBox box)
    {
        var list = new System.Windows.Controls.StackPanel();
        var popup = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = box, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, AllowsTransparency = true,
            Focusable = false, StaysOpen = true, VerticalOffset = 2,
            Child = new System.Windows.Controls.Border
            {
                Background = B("surfaceBrush"), BorderBrush = B("border-strongBrush"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4), Padding = new Thickness(0, 4, 0, 4), Child = list,
            },
        };
        System.Windows.Automation.AutomationProperties.SetName(popup.Child, "Suggestions");
        var shown = new List<string>();
        var highlighted = -1;
        var picking = false;

        void Paint()
        {
            for (var i = 0; i < list.Children.Count; i++)
                ((System.Windows.Controls.Border)list.Children[i]).Background = i == highlighted ? B("border-strongBrush") : Brushes.Transparent;
        }
        void Pick(string value)
        {
            picking = true;
            box.Text = value;
            box.CaretIndex = value.Length;
            picking = false;
            popup.IsOpen = false;
        }
        void Open()
        {
            shown = NewSession.MatchingModels(box.Text);
            highlighted = -1;
            list.Children.Clear();
            foreach (var value in shown)
            {
                var row = new System.Windows.Controls.Border
                {
                    Padding = new Thickness(8, 3, 8, 3), Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand,
                    Child = new System.Windows.Controls.TextBlock { Text = value, FontSize = 13, Foreground = B("textBrush") },
                };
                System.Windows.Automation.AutomationProperties.SetName(row, value);
                row.MouseEnter += (_, _) => { highlighted = list.Children.IndexOf(row); Paint(); };
                row.PreviewMouseLeftButtonDown += (_, e) => { e.Handled = true; Pick(value); };
                list.Children.Add(row);
            }
            popup.MinWidth = box.ActualWidth;
            popup.IsOpen = shown.Count > 0 && box.IsKeyboardFocusWithin && box.IsEnabled;
        }

        box.TextChanged += (_, _) =>
        {
            if (!picking && box.IsKeyboardFocusWithin) Open();
        };
        box.PreviewMouseLeftButtonUp += (_, _) =>
        {
            if (!popup.IsOpen) Open();
        };
        box.LostKeyboardFocus += (_, _) => popup.IsOpen = false;
        box.IsEnabledChanged += (_, _) => popup.IsOpen = false;
        box.PreviewKeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case System.Windows.Input.Key.Down when !popup.IsOpen:
                    Open();
                    e.Handled = popup.IsOpen;
                    break;
                case System.Windows.Input.Key.Down or System.Windows.Input.Key.Up when popup.IsOpen:
                    var step = e.Key == System.Windows.Input.Key.Down ? 1 : -1;
                    highlighted = highlighted < 0 ? (step > 0 ? 0 : shown.Count - 1) : (highlighted + step + shown.Count) % shown.Count;
                    Paint();
                    e.Handled = true;
                    break;
                case System.Windows.Input.Key.Enter when popup.IsOpen && highlighted >= 0:
                    Pick(shown[highlighted]);
                    e.Handled = true;
                    break;
                case System.Windows.Input.Key.Escape when popup.IsOpen:
                    popup.IsOpen = false;
                    e.Handled = true;
                    break;
            }
        };
    }
}
