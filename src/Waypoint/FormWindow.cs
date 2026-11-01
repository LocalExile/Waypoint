using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Shell;

namespace Waypoint;

/// <summary>A dialog with fields (Appearance, an account change, the close choice): a resizable window with the theme's own
/// title bar and Windows' behaviour for it (moving, resizing, the system menu). Notices and questions are
/// <see cref="Dialog"/>'s. Its sizes come from the Font* resources, so the text size reaches it without the window's zoom
/// (Theme.Dress).</summary>
public class FormWindow : Window
{
    FrameworkElement? _caption;
    Button? _maximize;
    Border? _frame;

    /// <summary>A tool's own window rather than a dialog: no owner, in the taskbar, with Minimize and Maximize. Set before
    /// it is shown.</summary>
    protected bool Standalone { get; set; }

    public FormWindow()
    {
        SetResourceReference(StyleProperty, "Shell.FormWindow");
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 32, ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(1), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false });
        CommandBindings.Add(new CommandBinding(SystemCommands.CloseWindowCommand, (_, _) => SystemCommands.CloseWindow(this)));
        CommandBindings.Add(new CommandBinding(SystemCommands.MinimizeWindowCommand, (_, _) => SystemCommands.MinimizeWindow(this)));
        CommandBindings.Add(new CommandBinding(SystemCommands.MaximizeWindowCommand, (_, _) => SystemCommands.MaximizeWindow(this)));
        CommandBindings.Add(new CommandBinding(SystemCommands.RestoreWindowCommand, (_, _) => SystemCommands.RestoreWindow(this)));
        StateChanged += (_, _) => UpdateState();
        SetResourceReference(BackgroundProperty, "Shell.Background");
        SetResourceReference(ForegroundProperty, "Shell.Text");
        SetResourceReference(FontFamilyProperty, "AppFont");
        SetResourceReference(FontSizeProperty, "Font13");
        // Long sentences wrap in these windows, without a style on each.
        Resources.Add(typeof(TextBlock), new Style(typeof(TextBlock)) { Setters = { new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap) } });
        ShowInTaskbar = false;
        UseLayoutRounding = true;
        Language = XmlLanguage.GetLanguage(L.Current.Culture.Name);
        Theme.TitleBar(this);
    }

    public override void OnApplyTemplate()
    {
        if (_caption is not null) _caption.SizeChanged -= CaptionSizeChanged;
        base.OnApplyTemplate();
        _caption = GetTemplateChild("DialogCaption") as FrameworkElement;
        if (GetTemplateChild("DialogCloseButton") is Button close)
        {
            string label = L.Text("Close {0}", Title);
            AutomationProperties.SetName(close, label);
            close.ToolTip = label;
        }
        if (_caption is not null) _caption.SizeChanged += CaptionSizeChanged;
        UpdateCaption();
        _frame = GetTemplateChild("DialogFrame") as Border;
        _maximize = GetTemplateChild("DialogMaximizeButton") as Button;
        if (Standalone && GetTemplateChild("DialogMinimizeButton") is Button minimize && _maximize is not null)
        {
            minimize.Visibility = _maximize.Visibility = Visibility.Visible;
            minimize.ToolTip = L.Text("Minimize");
            AutomationProperties.SetName(minimize, L.Text("Minimize"));
        }
        UpdateState();
    }

    /// <summary>Maximize or Restore, and a maximized window kept inside the frame Windows puts beyond the screen.</summary>
    void UpdateState()
    {
        bool maximized = WindowState == WindowState.Maximized;
        if (_frame is not null) _frame.Margin = maximized ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
        if (_maximize is null) return;
        _maximize.Content = maximized ? "" : "";
        _maximize.Command = maximized ? SystemCommands.RestoreWindowCommand : SystemCommands.MaximizeWindowCommand;
        _maximize.ToolTip = maximized ? L.Text("Restore down") : L.Text("Maximize");
        AutomationProperties.SetName(_maximize, (string)_maximize.ToolTip);
    }

    void CaptionSizeChanged(object sender, SizeChangedEventArgs e) => UpdateCaption();

    void UpdateCaption()
    {
        if (_caption is { ActualHeight: > 0 } && WindowChrome.GetWindowChrome(this) is { } chrome)
            chrome.CaptionHeight = _caption.ActualHeight + 1; // Include the outer border.
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // A dialog keeps moving, resizing and the system menu, but does not minimize its blocked owner or offer to maximize
        // without buttons for it.
        if (Standalone) return;
        nint hwnd = new WindowInteropHelper(this).Handle;
        const int styleIndex = -16;
        long style = GetWindowLongPtr(hwnd, styleIndex).ToInt64();
        SetWindowLongPtr(hwnd, styleIndex, (nint)(style & ~0x30000L)); // WS_MINIMIZEBOX | WS_MAXIMIZEBOX
        SetWindowPos(hwnd, 0, 0, 0, 0, 0, 0x37); // FRAMECHANGED, NOMOVE, NOSIZE, NOZORDER, NOACTIVATE
    }

    /// <summary>Brings a large window inside the work area of its monitor: on a small screen, or centred on a smaller owner,
    /// its title bar and buttons would start off the screen, with no way to reach them.</summary>
    protected void KeepOnScreen()
    {
        nint hwnd = new WindowInteropHelper(this).Handle;
        if (!GetWindowRect(hwnd, out var r)) return;
        var work = System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea;
        var (left, top, width, height) = Fit((r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top), (work.Left, work.Top, work.Width, work.Height));
        SetWindowPos(hwnd, 0, left, top, width, height, 0x14); // NOZORDER, NOACTIVATE
    }

    /// <summary>The window's place within the work area: no larger than it, and moved only as far as it takes.</summary>
    internal static (int Left, int Top, int Width, int Height) Fit((int Left, int Top, int Width, int Height) window, (int Left, int Top, int Width, int Height) work)
    {
        int width = Math.Min(window.Width, work.Width), height = Math.Min(window.Height, work.Height);
        return (Math.Clamp(window.Left, work.Left, work.Left + work.Width - width), Math.Clamp(window.Top, work.Top, work.Top + work.Height - height), width, height);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
}

static class DialogLayout
{
    public static void Configure(FormWindow dialog, Window? owner, string title, int width = 680)
    {
        if (owner is { IsVisible: true }) dialog.Owner = owner;
        dialog.Title = title;
        dialog.Width = width;
        dialog.SizeToContent = SizeToContent.Height;
        dialog.MaxHeight = Math.Min(650, SystemParameters.WorkArea.Height - 40);
        dialog.MinWidth = 420;
        dialog.MinHeight = 220;
        dialog.WindowStartupLocation = dialog.Owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        if (dialog.Owner is null) dialog.ShowInTaskbar = true;
        dialog.ResizeMode = ResizeMode.CanResize;
    }

    public static UIElement WithActions(Panel body, UIElement actions)
    {
        body.Children.Remove(actions);
        var layout = new DockPanel { Margin = body.Margin };
        body.Margin = new Thickness(0);
        DockPanel.SetDock(actions, Dock.Bottom);
        layout.Children.Add(actions);
        layout.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        return layout;
    }
}

static class ThemeResources
{
    public static double Font(double size) => size * Theme.Zoom;
}

/// <summary>The account dialog's labels and buttons.</summary>
static class AccountUi
{
    public static void Bind(DependencyObject element, DependencyProperty property, string text) => element.SetValue(property, L.Text(text));
    public static TextBlock Text(string text, string size = "Font13", bool muted = false)
    {
        var label = new TextBlock { Text = L.Text(text) };
        label.SetResourceReference(TextBlock.FontSizeProperty, size);
        if (muted) label.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        return label;
    }
    public static Button Button(string text, Action click, bool primary = false)
    {
        var button = new Button { Content = L.Text(text) };
        if (primary) button.SetResourceReference(FrameworkElement.StyleProperty, "Primary");
        button.Click += (_, _) => click();
        return button;
    }
}

/// <summary>The report for a bug: redacted, previewed, and copied only when the player asks.</summary>
public sealed class DiagnosticPreviewDialog : FormWindow
{
    public DiagnosticPreviewDialog(Window owner, Func<string[], string> create)
    {
        DialogLayout.Configure(this, owner, L.Text("Diagnostic report preview"), 820);
        Height = 620; SizeToContent = SizeToContent.Manual;
        var grid = new Grid { Margin = new Thickness(20) };
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = L.Text("Review the redacted text before copying. Redaction recognizes common patterns; check for any private details it missed.") });
        var namesPanel = new StackPanel { Margin = new Thickness(0, 10, 0, 10) };
        namesPanel.Children.Add(new TextBlock { Text = L.Text("Additional account / character names to remove (comma separated)"), Margin = new Thickness(0, 0, 0, 6) });
        var names = new TextBox { ToolTip = L.Text("Optional account/character names to remove, separated by commas") };
        System.Windows.Automation.AutomationProperties.SetName(names, L.Text("Additional names to remove (comma separated)"));
        namesPanel.Children.Add(names);
        Grid.SetRow(namesPanel, 1); grid.Children.Add(namesPanel);
        var preview = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = ThemeResources.Font(12),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        preview.Text = create([]);
        names.TextChanged += (_, _) => preview.Text = create(names.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        Grid.SetRow(preview, 2); grid.Children.Add(preview);
        var buttons = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        var copy = new Button { Content = L.Text("Copy reviewed report") };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(preview.Text); copy.Content = L.Text("Copied"); }
            catch (System.Runtime.InteropServices.COMException) { copy.Content = L.Text("Clipboard busy — try again"); }
        };
        buttons.Children.Add(copy); buttons.Children.Add(new Button { Content = L.Text("Close"), IsCancel = true });
        Grid.SetRow(buttons, 3); grid.Children.Add(buttons); Content = grid;
    }
}

/// <summary>What closing the window should do, asked until the player remembers an answer.</summary>
public sealed class CloseBehaviorDialog : FormWindow
{
    public WindowCloseAction? Choice { get; private set; }
    public CheckBox RememberChoice { get; } = new() { IsChecked = true };
    public WindowBehaviorChoices Choices { get; } = new() { PromptMode = true };
    public RadioButton StopServices => Choices.StopServices;
    public Button ApplyChoice { get; } = new();

    public CloseBehaviorDialog(Window owner, bool stopServicesOnExit = false, bool closeToTray = false, bool alwaysAsk = false)
    {
        DialogLayout.Configure(this, owner, L.Text("Close Waypoint"), 640);
        var body = new StackPanel { Margin = new Thickness(24) };
        body.Children.Add(new TextBlock { Text = L.Text("What should closing the window do?"), FontSize = ThemeResources.Font(18), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 16) });
        Choices.Load(closeToTray, stopServicesOnExit);
        RememberChoice.IsChecked = !alwaysAsk;
        body.Children.Add(Choices);
        var footer = new StackPanel { Margin = new Thickness(0, 20, 0, 0) };
        RememberChoice.Content = new TextBlock { Text = L.Text("Remember this for next time"), TextWrapping = TextWrapping.Wrap };
        footer.Children.Add(RememberChoice);
        var help = new TextBlock { Text = L.Text("You can change this in Settings → Window behavior."), Margin = new Thickness(0, 4, 0, 18) };
        help.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        footer.Children.Add(help);
        var buttons = new WrapPanel();
        ApplyChoice.SetResourceReference(StyleProperty, "Primary");
        ApplyChoice.Content = Choices.ActionLabel;
        Choices.ChoiceChanged += _ => ApplyChoice.Content = Choices.ActionLabel;
        ApplyChoice.Click += (_, _) => { Choice = Choices.CloseToTray ? WindowCloseAction.Hide : WindowCloseAction.Exit; DialogResult = true; };
        buttons.Children.Add(ApplyChoice);
        var cancel = new Button { Content = L.Text("Cancel"), IsCancel = true, IsDefault = true };
        buttons.Children.Add(cancel);
        footer.Children.Add(buttons);
        var layout = new Grid { Margin = body.Margin };
        body.Margin = new Thickness(0);
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Grid.SetRow(footer, 1); layout.Children.Add(footer);
        Content = layout;
        Loaded += (_, _) => cancel.Focus();
    }
}

/// <summary>Finds LocalExile checkouts in the usual places, or lets the player point at one. Used on the first start and
/// from Settings; a found checkout is only ever used after the player picks it.</summary>
public sealed class CheckoutDialog : FormWindow
{
    readonly CancellationTokenSource _cancel = new();
    readonly TextBlock _status = new() { Margin = new Thickness(0, 0, 0, 10) };
    readonly StackPanel _found = new();
    readonly string? _current;

    /// <summary>The checkout the player chose; null when they cancelled.</summary>
    public string? Chosen { get; private set; }

    public CheckoutDialog(Window? owner, string? current)
    {
        _current = current;
        DialogLayout.Configure(this, owner, L.Text("Choose your LocalExile checkout"), 680);
        var root = new DockPanel { Margin = new Thickness(24) };
        var buttons = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        var browse = new Button { Content = L.Text("Browse…") };
        browse.Click += (_, _) => { if (App.ChooseServer(this) is { } root2) { Chosen = root2; DialogResult = true; } };
        buttons.Children.Add(browse);
        buttons.Children.Add(new Button { Content = L.Text("Cancel"), IsCancel = true });
        root.Children.Add(buttons);
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = L.Text("Choose the folder of your LocalExile server checkout: the one with GameServer.slnx and scripts\\dev\\live.ps1. Waypoint remembers it."), Margin = new Thickness(0, 0, 0, 12) });
        body.Children.Add(_status);
        body.Children.Add(_found);
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = root;
        Closed += (_, _) => _cancel.Cancel();
        Loaded += async (_, _) => await SearchAsync();
    }

    async Task SearchAsync()
    {
        _status.Text = L.Text("Checking upstream HEAD and searching common local folders… You can still Browse to choose a location.");
        try
        {
            var roots = RepositoryDiscovery.DefaultRoots(AppContext.BaseDirectory, Environment.CurrentDirectory, _current ?? "");
            var result = await RepositoryLocator.FindAsync(roots, _cancel.Token);
            int matches = result.Repositories.Count(r => r.MatchesUpstream);
            _status.Text = matches > 1 ? L.Text("{0} checkouts match upstream HEAD. Choose one below, or Browse elsewhere.", matches)
                : matches == 1 ? L.Text("One checkout matches upstream HEAD. You can use it below or Browse elsewhere.")
                : result.Repositories.Length > 0 ? L.Text("No matching upstream HEAD found. Other LocalExile checkouts are listed below; you can use any of them or Browse elsewhere.")
                : L.Text("No LocalExile checkout found in the searched locations. Browse to your checkout instead.");
            if (result.Limited) _status.Text += L.Text("\nSearch limits reached; other checkouts may exist. Browse to any location.");
            foreach (var repository in result.Repositories) _found.Children.Add(Row(repository));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            _status.Text = L.Text("Automatic lookup could not finish. Browse to your checkout instead. ") + ex.Message;
        }
    }

    UIElement Row(DiscoveredRepository repository)
    {
        var use = new Button { Content = L.Text("Use this checkout"), VerticalAlignment = VerticalAlignment.Center };
        use.Click += (_, _) => { Chosen = repository.Root; DialogResult = true; };
        DockPanel.SetDock(use, Dock.Right);
        var text = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        text.Children.Add(new TextBlock { Text = repository.Root, FontWeight = FontWeights.SemiBold });
        var detail = new TextBlock { Text = repository.Summary + " · " + repository.CommitLabel, Margin = new Thickness(0, 2, 0, 0) };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        text.Children.Add(detail);
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
        row.Children.Add(use);
        row.Children.Add(text);
        return row;
    }
}
