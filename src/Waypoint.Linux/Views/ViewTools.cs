using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Waypoint;

/// <summary>What the sections ask of the desktop, and of WPF, that Avalonia words differently.</summary>
static class ViewTools
{
    /// <summary>The window a view is in (WPF's Window.GetWindow).</summary>
    public static Window? Owner(Control control) => TopLevel.GetTopLevel(control) as Window;

    /// <summary>WPF's IsVisibleChanged to true: the first time a view is on screen and each time it comes back, through
    /// its own visibility or an ancestor's. Avalonia's IsVisible is the control's own, and the effective one has no event.</summary>
    public static void WhenShown(Control control, Action shown)
    {
        bool visible = false;
        List<Visual> watched = [];

        void Check()
        {
            bool now = TopLevel.GetTopLevel(control) is not null && control.GetSelfAndVisualAncestors().All(v => v.IsVisible);
            if (now && !visible) shown();
            visible = now;
        }

        void Changed(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == Visual.IsVisibleProperty) Check();
        }

        void Watch()
        {
            foreach (var visual in control.GetSelfAndVisualAncestors())
            {
                visual.PropertyChanged += Changed;
                watched.Add(visual);
            }
            Check();
        }

        control.AttachedToVisualTree += (_, _) => Watch();
        control.DetachedFromVisualTree += (_, _) =>
        {
            foreach (var visual in watched) visual.PropertyChanged -= Changed;
            watched.Clear();
            visible = false;
        };
        if (TopLevel.GetTopLevel(control) is not null) Watch();
    }

    /// <summary>Opens a folder or a file with the desktop's own program. A system with no xdg-open opens nothing.</summary>
    public static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { path }, UseShellExecute = false })?.Dispose(); }
        catch (Win32Exception) { }
    }

    /// <summary>The text on the clipboard; false when the desktop's clipboard could not take it.</summary>
    public static async Task<bool> Copy(Control control, string text)
    {
        if (TopLevel.GetTopLevel(control)?.Clipboard is not { } clipboard) return false;
        try
        {
            await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, text);
            return true;
        }
        catch (Exception e) when (e is InvalidOperationException or TimeoutException or OperationCanceledException or IOException) { return false; }
    }
}
