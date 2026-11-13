using System.Windows;
using System.Windows.Input;

namespace Reframe.Shell;

/// <summary>The search field's clear button (Chromium's search input shows one while there is text).</summary>
public static class SearchCommands
{
    public static readonly RoutedUICommand Clear = new("Clear search", "Clear", typeof(SearchCommands));
}

/// <summary>Diagnostics: <c>Focused</c> draws a control as focused without taking keyboard focus (captures run on a
/// desktop that need not give the window focus, and Electron's reference shows the search field focused).</summary>
public static class Diag
{
    public static readonly DependencyProperty FocusedProperty = DependencyProperty.RegisterAttached("Focused", typeof(bool), typeof(Diag),
        new FrameworkPropertyMetadata(false));

    public static bool GetFocused(DependencyObject element) => (bool)element.GetValue(FocusedProperty);
    public static void SetFocused(DependencyObject element, bool value) => element.SetValue(FocusedProperty, value);
}
