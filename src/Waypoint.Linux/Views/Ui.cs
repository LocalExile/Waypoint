using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Waypoint;

/// <summary>What the Windows views get from WPF and the shell in one call each.</summary>
static class Ui
{
    /// <summary>The window a view is in.</summary>
    public static Window? Owner(Control view) => TopLevel.GetTopLevel(view) as Window;

    /// <summary>WPF's IsVisibleChanged, for a section: it came into view or left it. Its window showing counts, so that a section
    /// visible from the start hears of it too. Sections sit directly in the window's host, so only their own flag matters.</summary>
    public static void VisibilityChanged(Control view, Action<bool> changed)
    {
        bool shown = false;
        void Update()
        {
            bool now = view.IsLoaded && view.IsVisible;
            if (now == shown) return;
            shown = now;
            changed(now);
        }
        view.PropertyChanged += (_, e) => { if (e.Property == Visual.IsVisibleProperty) Update(); };
        view.Loaded += (_, _) => Update();
        view.Unloaded += (_, _) => Update();
    }

    /// <summary>A page, a file or a folder in the player's own program for it.</summary>
    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo("xdg-open", [target]) { UseShellExecute = false })?.Dispose(); }
        catch (Win32Exception e) { Dialog.Tell(null, DialogKind.Warning, target, e.Message); }
    }

    /// <summary>A window as a dialog over <paramref name="owner"/>, or on its own when there is none (the first start); ends
    /// when it closes.</summary>
    public static Task ShowAsync(Window window, Window? owner)
    {
        if (owner is { IsVisible: true }) return window.ShowDialog(owner);
        var closed = new TaskCompletionSource();
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show();
        return closed.Task;
    }

    /// <summary>The chosen folder, null when the player cancels. Answers before returning, as on Windows, the way
    /// <see cref="Dialog"/> does.</summary>
    public static string? PickFolder(Window owner, string title, string? start = null)
    {
        string? chosen = null;
        var frame = new DispatcherFrame();
        _ = PickAsync();
        Dispatcher.UIThread.PushFrame(frame);
        return chosen;

        async Task PickAsync()
        {
            try
            {
                var storage = owner.StorageProvider;
                var options = new FolderPickerOpenOptions { Title = title };
                if (start is not null) options.SuggestedStartLocation = await storage.TryGetFolderFromPathAsync(start);
                chosen = (await storage.OpenFolderPickerAsync(options)).FirstOrDefault()?.TryGetLocalPath();
            }
            // No portal and no GTK: the fields that take a typed path still work.
            catch (Exception e) when (e is InvalidOperationException or NotSupportedException or IOException or UnauthorizedAccessException)
            {
                Dialog.Tell(owner, DialogKind.Warning, title, e.Message);
            }
            finally { frame.Continue = false; }
        }
    }
}
