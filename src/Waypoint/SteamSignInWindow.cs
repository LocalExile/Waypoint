using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Waypoint;

/// <summary>
/// The Steam sign-in for the game client's download: DepotDownloader's own QR sign-in, with the code it prints drawn here
/// (<see cref="SteamDownload.SignInAsync"/>). The user scans it with the Steam mobile app and approves; Steam changes the
/// code now and then, and the picture follows. The tool gives up after about two minutes without an approval: the window
/// then says so and offers a new code. Waypoint sees no password and no token: the tool keeps the sign-in, and says only
/// the account's name (<see cref="Account"/>).
/// </summary>
sealed class SteamSignInWindow : FormWindow
{
    readonly Image _code = new() { Width = 340, Height = 340, Stretch = Stretch.Uniform };
    readonly TextBlock _status = new() { TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
    readonly Button _again = new() { Margin = new Thickness(0, 0, 8, 0), Visibility = Visibility.Collapsed };
    readonly CancellationTokenSource _stop = new();
    readonly string _tool, _scratch;
    readonly ListedBuild _build;
    readonly Action<string> _log;

    /// <summary>The account the tool now remembers; null when the window closed without a sign-in.</summary>
    public string? Account { get; private set; }

    /// <summary>The user has no Steam mobile app and asked for the tool's password prompt instead: the window that opened
    /// this one runs it.</summary>
    public bool UsePassword { get; private set; }

    public SteamSignInWindow(Window? owner, string tool, ListedBuild build, string scratch, Action<string> log)
    {
        (_tool, _build, _scratch, _log) = (tool, build, scratch, log);
        DialogLayout.Configure(this, owner, L.Text("Sign in to Steam"), 460);
        RenderOptions.SetBitmapScalingMode(_code, BitmapScalingMode.NearestNeighbor);
        var root = new StackPanel { Margin = new Thickness(24) };
        root.Children.Add(new TextBlock
        {
            TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 14),
            Text = L.Text("Scan this code with the Steam mobile app (Steam Guard, then the scan button) and approve the sign-in. The game comes from your own Steam account, which needs Path of Exile in its library."),
        });
        // A code is read against white, whatever the theme.
        root.Children.Add(new Border { Background = Brushes.White, Padding = new Thickness(10), HorizontalAlignment = HorizontalAlignment.Center, Child = _code });
        root.Children.Add(_status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        _again.Content = L.Text("New code");
        _again.Click += (_, _) => _ = RunAsync();
        var cancel = new Button { Content = L.Text("Cancel"), IsCancel = true };
        cancel.Click += (_, _) => Close();
        var password = new Button { Content = L.Text("Sign in with a password…"), Margin = new Thickness(0, 0, 8, 0),
            ToolTip = L.Text("Without the Steam mobile app: the download tool asks for the password and the Steam Guard code in a window of its own. Waypoint sees neither.") };
        password.Click += (_, _) => { UsePassword = true; Close(); };
        buttons.Children.Add(password);
        buttons.Children.Add(_again);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = root;
        Closed += (_, _) => _stop.Cancel();
        Loaded += (_, _) => _ = RunAsync();
    }

    /// <summary>One run of the tool's sign-in: it ends with an account, or without one when nobody approved a code in time.</summary>
    async Task RunAsync()
    {
        _again.Visibility = Visibility.Collapsed;
        _status.Text = L.Text("Asking Steam for a code …");
        try
        {
            Account = await SteamDownload.SignInAsync(_tool, _build, _scratch, rows => Dispatcher.BeginInvoke(() =>
            {
                _code.Source = Picture(rows);
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
        _again.Visibility = Visibility.Visible;
    }

    /// <summary>The code as a picture, a pixel a square: the Image scales it without smoothing.</summary>
    static BitmapSource Picture(bool[][] rows)
    {
        int width = rows.Max(row => row.Length), height = rows.Length;
        byte[] pixels = new byte[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                pixels[y * width + x] = x < rows[y].Length && rows[y][x] ? (byte)0 : (byte)255;
        var picture = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
        picture.Freeze();
        return picture;
    }
}
