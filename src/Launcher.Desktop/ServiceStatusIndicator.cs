using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Launcher.Core;

namespace Launcher.Desktop;

public enum ServiceLight { Unknown, Off, On, Starting, Attention }

/// <summary>A verified checkout state, never inferred from a launch request or a stale success.</summary>
public static class ServiceLightState
{
    public static ServiceLight From(ServiceStatus? service)
    {
        if (service is null) return ServiceLight.Unknown;
        if (service.Status is "Failed" or "Error" or "Conflict") return ServiceLight.Attention;
        if (service.Name == "Game" && service.Status == "Running" && service.ProcessId > 0) return ServiceLight.On;
        if (service.Name != "Game" && service.Status == "Ready" && service.ProcessId > 0) return ServiceLight.On;
        if (service.IsRunning || service.Status == "Starting") return ServiceLight.Starting;
        return service.Status == "Stopped" ? ServiceLight.Off : ServiceLight.Unknown;
    }
}

public sealed class ServiceStatusIndicator : Button
{
    static readonly IReadOnlyDictionary<ServiceLight, Geometry> Symbols = new Dictionary<ServiceLight, string>
    {
        [ServiceLight.On] = "M 1,4 L 3,6 L 7,2", [ServiceLight.Off] = "M 1,4 L 7,4",
        [ServiceLight.Starting] = "M 4,1 L 4,4 L 6,5", [ServiceLight.Attention] = "M 4,1 L 4,5 M 4,7 L 4,7.2",
        [ServiceLight.Unknown] = "M 2,2 A 2,2 0 1 1 5,4 L 4,5 M 4,7 L 4,7.2"
    }.ToDictionary(pair => pair.Key, pair => { var geometry = Geometry.Parse(pair.Value); geometry.Freeze(); return geometry; });
    readonly TextBlock label = new() { FontWeight = FontWeights.SemiBold }, status = new();
    readonly Ellipse lamp = new() { Width = 13, Height = 13, StrokeThickness = 1.5 };
    readonly Path symbol = new() { StrokeThickness = 1.7, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly DropShadowEffect glow = new() { BlurRadius = 10, ShadowDepth = 0, Opacity = .55, RenderingBias = RenderingBias.Performance };
    ServiceStatus? service;
    public string ServiceName { get; }
    public ServiceLight Light { get; private set; }
    public string StatusText => status.Text;
    public bool HasGlow => lamp.Effect is not null;

    public ServiceStatusIndicator(string name)
    {
        ServiceName = name;
        SetResourceReference(StyleProperty, "ServiceIndicatorButton");
        SetResourceReference(FontSizeProperty, "Font12");
        var content = new DockPanel();
        var mark = new Grid { Width = 30, Height = 30, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
        mark.Children.Add(lamp); mark.Children.Add(symbol);
        content.Children.Add(mark);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        text.Children.Add(label); text.Children.Add(status); content.Children.Add(text);
        Content = content;
        BindingOperations.SetBinding(glow, DropShadowEffect.ColorProperty, new Binding("Stroke.Color") { Source = lamp });
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
        Update(null);
    }

    public void Update(ServiceStatus? value)
    {
        service = value;
        Light = ServiceLightState.From(value);
        string role = Light switch { ServiceLight.On => "Success", ServiceLight.Off => "Error", ServiceLight.Starting or ServiceLight.Attention => "Warning", _ => "Muted" };
        lamp.SetResourceReference(Shape.StrokeProperty, role);
        // An outlined disc and a distinct inner mark remain understandable without color or glow.
        symbol.SetResourceReference(Shape.StrokeProperty, role);
        symbol.Data = Symbols[Light];
        label.Text = L.Text(ServiceName);
        status.Text = L.Text(Light switch
        {
            ServiceLight.On => ServiceName == "Game" ? "Running" : "Ready",
            ServiceLight.Off => "Stopped", ServiceLight.Starting => "Starting",
            ServiceLight.Attention => "Needs attention", _ => "Not checked"
        });
        string detail = value is null ? L.Text("Refresh to verify this checkout’s current status.") : L.Describe(value.Detail);
        AutomationProperties.SetName(this, L.Text("{0}: {1}. Show service details.", label.Text, status.Text));
        AutomationProperties.SetHelpText(this, detail);
        ToolTip = label.Text + " · " + status.Text + "\n" + detail + "\n" + L.Text("Show service details");
        RefreshAppearance();
    }

    public void RefreshAppearance() => lamp.Effect = Application.Current.TryFindResource("ServiceGlowEnabled") is true && Light != ServiceLight.Unknown ? glow : null;
    public void RefreshLanguage() => Update(service);
}
