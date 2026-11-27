using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Waypoint;

/// <summary>
/// The Steam sign-in for the game client's download: DepotDownloader's own QR sign-in, with the code it prints drawn here
/// (<see cref="SteamDownload.SignInAsync"/>). The user scans it with the Steam mobile app and approves; Steam changes the
/// code now and then, and the picture follows. The tool gives up after about two minutes without an approval: the window
/// then says so and offers a new code. Waypoint sees no password and no token: the tool keeps the sign-in, and says only
/// the account's name (<see cref="Account"/>). The Linux copy of src/Waypoint/SteamSignInWindow.cs, without the password
/// sign-in, which needs the tool's own console window.
/// </summary>
sealed class SteamSignInWindow : FormWindow
{
    readonly QrCode _code = new() { Width = 340, Height = 340 };
    readonly TextBlock _status = new() { TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
    readonly Button _again = new() { IsVisible = false };
    readonly CancellationTokenSource _stop = new();
    readonly string _tool, _scratch;
    readonly ListedBuild _build;
    readonly Action<string> _log;

    /// <summary>The account the tool now remembers; null when the window closed without a sign-in.</summary>
    public string? Account { get; private set; }

    public SteamSignInWindow(Window? owner, string tool, ListedBuild build, string scratch, Action<string> log)
    {
        (_tool, _build, _scratch, _log) = (tool, build, scratch, log);
        DialogLayout.Configure(this, owner, L.Text("Sign in to Steam"), 460);
        var root = new StackPanel { Margin = new Thickness(24) };
        root.Children.Add(new TextBlock
        {
            TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 14),
            Text = L.Text("Scan this code with the Steam mobile app (Steam Guard, then the scan button) and approve the sign-in. The game comes from your own Steam account, which needs Path of Exile in its library."),
        });
        // A code is read against white, whatever the theme.
        root.Children.Add(new Border { Background = Brushes.White, Padding = new Thickness(10), HorizontalAlignment = HorizontalAlignment.Center, Child = _code });
        root.Children.Add(_status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        _again.Content = L.Text("New code");
        _again.Click += (_, _) => _ = RunAsync();
        buttons.Children.Add(_again);
        buttons.Children.Add(Dismiss(L.Text("Cancel")));
        root.Children.Add(buttons);
        Content = root;
        Closed += (_, _) => _stop.Cancel();
        Opened += (_, _) => _ = RunAsync();
    }

    /// <summary>One run of the tool's sign-in: it ends with an account, or without one when nobody approved a code in time.</summary>
    async Task RunAsync()
    {
        _again.IsVisible = false;
        _status.Text = L.Text("Asking Steam for a code …");
        try
        {
            Account = await SteamDownload.SignInAsync(_tool, _build, _scratch, rows => Dispatcher.UIThread.Post(() =>
            {
                _code.Rows = rows;
                _code.Opacity = 1;
                _status.Text = L.Text("Waiting for you to scan and approve …");
            }), _log, _stop.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException) { _log(e.Message); }
        if (_stop.IsCancellationRequested) return;
        if (Account is not null)
        {
            Close();
            return;
        }
        // The code on screen is no longer good: it stays, greyed, so the window does not jump.
        _code.Opacity = 0.15;
        _status.Text = L.Text("The code was not approved in time, so Steam let it go. Ask for a new one when you have the Steam app ready.");
        _again.IsVisible = true;
    }

    /// <summary>The code, a square for each of its cells, dark on the white around it, with no smoothing between them.</summary>
    sealed class QrCode : Control
    {
        bool[][] _rows = [];

        public bool[][] Rows
        {
            get => _rows;
            set { _rows = value; InvalidateVisual(); }
        }

        public override void Render(DrawingContext context)
        {
            int height = _rows.Length, width = height == 0 ? 0 : _rows.Max(row => row.Length);
            if (width == 0) return;
            double cell = Math.Floor(Math.Min(Bounds.Width / width, Bounds.Height / height));
            if (cell < 1) cell = Math.Min(Bounds.Width / width, Bounds.Height / height);
            double left = (Bounds.Width - cell * width) / 2, top = (Bounds.Height - cell * height) / 2;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < _rows[y].Length; x++)
                    if (_rows[y][x]) context.FillRectangle(Brushes.Black, new Rect(left + x * cell, top + y * cell, cell, cell));
        }
    }
}
