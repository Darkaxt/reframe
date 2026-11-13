using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Reframe.Shell;

/// <summary>
/// .modal-backdrop and .modal (styles.css): the dialog centred over the whole window (toolbar included) on a 45 % black
/// backdrop, 16 px from its edges; a click on the backdrop or Escape closes it. Live, the backdrop is an owned
/// borderless window over the main one, so the terminals (native child windows, which WPF cannot draw over) are dimmed
/// too; in a capture it is an element over the root (<see cref="Overlay"/>), which DiagCapture draws again over the
/// terminals.
/// </summary>
sealed class ModalHost
{
    static Brush B(string key) => (Brush)Application.Current.FindResource(key);

    readonly Grid backdrop;
    readonly Window? window;
    readonly Panel? capturePanel;
    public event Action? Closed;

    /// <summary>The backdrop (with the dialog) while it is shown.</summary>
    public FrameworkElement Overlay => backdrop;

    ModalHost(FrameworkElement dialog, Window owner, Panel? captureRoot)
    {
        backdrop = new Grid { Background = B("backdropBrush") };
        dialog.Margin = new Thickness(16);
        dialog.HorizontalAlignment = HorizontalAlignment.Center;
        dialog.VerticalAlignment = VerticalAlignment.Center;
        backdrop.Children.Add(dialog);
        backdrop.MouseDown += (_, e) =>
        {
            if (e.OriginalSource == backdrop) Close();
        };
        backdrop.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        };
        if (captureRoot is not null)
        {
            capturePanel = captureRoot;
            Grid.SetRowSpan(backdrop, Math.Max(1, (captureRoot as Grid)?.RowDefinitions.Count ?? 1));
            Grid.SetColumnSpan(backdrop, Math.Max(1, (captureRoot as Grid)?.ColumnDefinitions.Count ?? 1));
            Panel.SetZIndex(backdrop, 100);
            captureRoot.Children.Add(backdrop);
            return;
        }
        // Over the owner's client area (its root), kept there while it moves or resizes.
        window = new Window
        {
            Owner = owner, WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize, Content = backdrop, ShowActivated = true,
            FontFamily = (FontFamily)Application.Current.FindResource("FontFamily.Ui"), FontSize = 13, Foreground = B("textBrush"),
        };
        TextOptions.SetTextFormattingMode(window, TextFormattingMode.Ideal);
        void Place()
        {
            if (owner.Content is not FrameworkElement client || PresentationSource.FromVisual(client) is null) return;
            var topLeft = client.PointToScreen(new Point(0, 0));
            var scale = PresentationSource.FromVisual(client)!.CompositionTarget.TransformFromDevice;
            var at = scale.Transform(topLeft);
            window.Left = at.X;
            window.Top = at.Y;
            window.Width = client.ActualWidth;
            window.Height = client.ActualHeight;
        }
        Place();
        EventHandler moved = (_, _) => Place();
        owner.LocationChanged += moved;
        owner.SizeChanged += (_, _) => Place();
        window.Closed += (_, _) =>
        {
            owner.LocationChanged -= moved;
            owner.Activate();
            Closed?.Invoke();
        };
        window.Show();
    }

    /// <summary>Shows <paramref name="dialog"/> over <paramref name="owner"/> (over <paramref name="captureRoot"/> in a capture).</summary>
    public static ModalHost Show(FrameworkElement dialog, Window owner, Panel? captureRoot) => new(dialog, owner, captureRoot);

    public void Close()
    {
        if (window is not null)
        {
            if (window.IsLoaded) window.Close();
            return;
        }
        if (capturePanel is null || !capturePanel.Children.Contains(backdrop)) return;
        capturePanel.Children.Remove(backdrop);
        Closed?.Invoke();
    }

    /// <summary>.modal: 1 px border-strong, radius 8, surface; the content scrolls inside the border (overflow-y on the
    /// modal, its padding 18px 20px inside the scrolled area).</summary>
    public static (Border Frame, ScrollViewer Scroll) Frame(double width, UIElement content)
    {
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false, Content = new Border { Padding = new Thickness(20, 18, 20, 18), Child = content },
        };
        var frame = new Border
        {
            Width = width, BorderBrush = B("border-strongBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Background = B("surfaceBrush"), Child = scroll,
        };
        // The scroll bar stays inside the rounded corners.
        frame.SizeChanged += (_, _) => scroll.Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(0, frame.ActualWidth - 2), Math.Max(0, frame.ActualHeight - 2)), 7, 7);
        System.Windows.Automation.AutomationProperties.SetIsDialog(frame, true);
        return (frame, scroll);
    }
}
