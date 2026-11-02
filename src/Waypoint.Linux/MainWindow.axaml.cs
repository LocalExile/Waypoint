using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;

namespace Waypoint;

/// <summary>A section of the sidebar and the view it shows.</summary>
sealed record Section(string Name, MaterialIconKind Icon, Control View)
{
    public string DisplayName => L.Text(Name);
}

/// <summary>A service's lamp in the status strip. An optional one shows only while it runs: stopped is its normal state.</summary>
sealed class StatusLight(Service service, string name, bool optional = false) : INotifyPropertyChanged
{
    public Service Service { get; } = service;
    public string Name => L.Text(name);
    public IBrush Lamp { get; private set; } = Brushes.Gray;
    public string Tip { get; private set; } = "";
    public bool Shown { get; private set; } = !optional;

    public void Update(ServiceSample? sample)
    {
        (string lamp, string state) = sample switch
        {
            { Running: true, Listening: true } => ("Shell.Good", "Running"),
            { Running: true } when Service.Ports.Length == 0 => ("Shell.Good", "Running"),
            { Running: true } => ("Shell.Warning", "Starting"),
            { Listening: true } => ("Shell.Error", "Port held by another"),
            _ => ("Shell.Idle", "Stopped"),
        };
        Lamp = App.Brush(lamp);
        Tip = $"{Name}: {L.Text(state)}";
        Shown = !optional || sample is { Running: true };
        PropertyChanged?.Invoke(this, new(null));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow : Window
{
    readonly Shared _shared = new();
    readonly Section[] _sections;
    readonly StatusLight[] _lights =
    [
        new(Service.Patch, "Patch"), new(Service.Gateway, "Gateway"), new(Service.Auth, "Auth"), new(Service.Client, "Game"),
        new(Service.DevHost, "Dev host", optional: true), new(Service.Recorder, "Walk maps", optional: true),
    ];

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = $"v{Updates.Current}";
        SimpleMode.Content = L.Text("Simple");
        AdvancedMode.Content = L.Text("Advanced");
        Lights.ItemsSource = _lights;
        _sections = Sectionlist();
        foreach (var section in _sections) { section.View.IsVisible = false; SectionHost.Children.Add(section.View); }
        _shared.Monitor.Sampled += samples => Dispatcher.UIThread.Post(() => ShowStatus(samples));
        _shared.LiveRunning += _ => Dispatcher.UIThread.Post(() => ShowStatus(_shared.Monitor.Latest));
        ShowMode();
        Opened += (_, _) => _ = _shared.Workspace.RefreshAsync();
        Closed += (_, _) => _shared.Dispose();
    }


    static Section[] Sectionlist() =>
    [
        new("Play", MaterialIconKind.Play, Pending("Play")),
    ];

    static Control Pending(string name) => new TextBlock { Text = name };

    void ShowMode()
    {
        bool advanced = _shared.Settings.Advanced;
        ModeSwitch.IsVisible = _shared.Settings.Developer;
        Sections.ItemsSource = _sections;
        Sections.SelectedIndex = 0;
        StatusStrip.IsVisible = advanced;
        BranchChip.IsVisible = advanced && !Repo.IsInstall;
        BranchName.Text = Repo.Branch() ?? L.Text("no git checkout");
        SimpleMode.IsChecked = !advanced;
        AdvancedMode.IsChecked = advanced;
    }

    void ModeChosen(object? sender, RoutedEventArgs e)
    {
        bool advanced = AdvancedMode.IsChecked == true;
        if (advanced == _shared.Settings.Advanced) return;
        _shared.Settings.AdvancedMode = advanced;
        _shared.Settings.Save();
        ShowMode();
    }

    void SectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Sections.SelectedItem is not Section shown) return;
        foreach (var s in _sections) s.View.IsVisible = s == shown;
        SectionTitle.Text = shown.DisplayName;
    }

    void ShowStatus(IReadOnlyList<ServiceSample> samples)
    {
        foreach (var light in _lights) light.Update(samples.FirstOrDefault(s => s.Service == light.Service));
        StatusText.Text = _shared.IsLiveRunning ? L.Text("{0} running…", _shared.BusyWith) : "";
    }
}

/// <summary>Shown when no checkout or release is chosen yet.</summary>
sealed class FirstStartWindow : Window
{
    public FirstStartWindow() { Title = "Waypoint"; Width = 700; Height = 460; }
}
