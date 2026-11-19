using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Reframe.Shell;

/// <summary>A picture from the chat shown large over the window that holds it, on a dimmed background: − and + (or the
/// mouse wheel) zoom it, the download button saves it as a PNG, and ✕, Esc or a click outside the picture closes it.</summary>
sealed class ImageViewer : Window
{
    static readonly double[] Steps = [0.1, 0.25, 0.5, 0.75, 1, 1.5, 2, 3, 4, 6, 8];
    readonly BitmapSource source;
    readonly Image image;
    readonly TextBlock percent;
    double zoom = 1;

    ImageViewer(Window owner, BitmapSource source)
    {
        this.source = source;
        Owner = owner;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(0xCC, 0, 0, 0));
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        var origin = owner.PointToScreen(new Point(0, 0));
        var top = (PresentationSource.FromVisual(owner)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity).Transform(origin);
        var box = new Rect(top.X, top.Y, owner.ActualWidth, owner.ActualHeight);
        Left = box.Left;
        Top = box.Top;
        Width = box.Width;
        Height = box.Height;

        image = new Image { Source = source, Stretch = Stretch.None, SnapsToDevicePixels = true };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        // A big picture starts fitted to the window; a small one at its own size.
        var fit = Math.Min((box.Width - 120) / source.PixelWidth, (box.Height - 160) / source.PixelHeight);
        zoom = Math.Clamp(Math.Min(1, fit), 0.05, 1);
        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brushes.Transparent, Content = new Grid { Children = { image }, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        scroller.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is not Image) Close();
        };
        PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control && e.OriginalSource is not Image) return;
            e.Handled = true;
            Step(e.Delta > 0 ? 1 : -1);
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key is Key.OemPlus or Key.Add) Step(1);
            else if (e.Key is Key.OemMinus or Key.Subtract) Step(-1);
        };

        percent = new TextBlock { Foreground = Brushes.White, FontSize = 13, Width = 52, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var minus = Round("−", "Zoom out", () => Step(-1));
        var plus = Round("+", "Zoom in", () => Step(1));
        var bar = new Border
        {
            CornerRadius = new CornerRadius(22), Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x22, 0x22, 0x26)), Padding = new Thickness(6),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 24),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { minus, percent, plus } },
        };
        var save = Round("⤓", "Save the image", Save);
        var close = Round("✕", "Close", Close);
        var top2 = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 16, 16, 0),
            Children = { save, close },
        };
        Content = new Grid { Children = { scroller, bar, top2 } };
        Apply();
        Loaded += (_, _) => Focus();
    }

    static Button Round(string glyph, string tip, Action click)
    {
        var button = new Button
        {
            Width = 40, Height = 40, Margin = new Thickness(4), Cursor = Cursors.Hand, ToolTip = tip, Foreground = Brushes.White, FontSize = 18,
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x2E, 0x2E, 0x33)), BorderThickness = new Thickness(0), Content = glyph,
            Template = Rounded(),
        };
        button.Click += (_, _) => click();
        return button;
    }

    static ControlTemplate Rounded()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(20));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

    void Step(int direction)
    {
        var index = direction > 0 ? Array.FindIndex(Steps, s => s > zoom + 0.001) : Array.FindLastIndex(Steps, s => s < zoom - 0.001);
        if (index >= 0) zoom = Steps[index];
        Apply();
    }

    void Apply()
    {
        image.LayoutTransform = new ScaleTransform(zoom, zoom);
        percent.Text = $"{Math.Round(zoom * 100)}%";
    }

    void Save()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "PNG image|*.png", FileName = "image.png" };
        if (dialog.ShowDialog(this) != true) return;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        try
        {
            using var file = File.Create(dialog.FileName);
            encoder.Save(file);
        }
        catch (IOException error)
        {
            MessageBox.Show(this, error.Message, "Could not save the image");
        }
    }

    /// <summary>Opens the picture over the window that holds <paramref name="from"/>.</summary>
    public static void Open(DependencyObject from, BitmapSource source)
    {
        if (Window.GetWindow(from) is { } owner) new ImageViewer(owner, source).ShowDialog();
    }
}
