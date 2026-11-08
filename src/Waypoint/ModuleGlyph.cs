#if WINDOWS
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;
using Element = System.Windows.FrameworkElement;
using ShapePath = System.Windows.Shapes.Path;
#else
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Material.Icons.Avalonia;
using Element = Avalonia.Controls.Control;
using ShapePath = Avalonia.Controls.Shapes.Path;
#endif

namespace Waypoint;

/// <summary>Dynamic presentation resources update every instance without rebuilding an unsaved settings form.</summary>
sealed class ModuleGlyph : ContentControl
{
    readonly double _size;
#if WINDOWS
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(ModuleIcon), typeof(ModuleGlyph),
        new PropertyMetadata(null, (d, _) => ((ModuleGlyph)d).Redraw()));
    public ModuleIcon? Icon { get => (ModuleIcon?)GetValue(IconProperty); set => SetValue(IconProperty, value); }
#else
    public static readonly StyledProperty<ModuleIcon?> IconProperty = AvaloniaProperty.Register<ModuleGlyph, ModuleIcon?>(nameof(Icon));
    public ModuleIcon? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    { base.OnPropertyChanged(change); if (change.Property == IconProperty) Redraw(); }
#endif
    public ModuleGlyph(string moduleId, double size)
    {
        _size = size; Width = Height = size; IsHitTestVisible = false;
        HorizontalAlignment = HorizontalAlignment.Center; VerticalAlignment = VerticalAlignment.Center;
#if WINDOWS
        SetResourceReference(IconProperty, ModuleIcons.Resource(moduleId));
#else
        Bind(IconProperty, new DynamicResourceExtension(ModuleIcons.Resource(moduleId)));
#endif
        Redraw();
    }
    void Redraw() { if (Icon is { } icon) Content = Draw(icon, _size); }
    public static Element Draw(ModuleIcon icon, double size, string ink = "Shell.TextSecondary")
    {
        if (icon.Lines is { } lines)
        {
            var path = new ShapePath { Data = Geometry.Parse(lines), Width = size, Height = size, Stretch = Stretch.Uniform, StrokeThickness = 1.6, IsHitTestVisible = false };
#if WINDOWS
            path.SetResourceReference(ShapePath.StrokeProperty, ink);
#else
            path.Bind(ShapePath.StrokeProperty, new DynamicResourceExtension(ink));
#endif
            return path;
        }
#if WINDOWS
        var glyph = new PackIcon { Kind = icon.Kind, Width = size, Height = size, IsHitTestVisible = false };
        glyph.SetResourceReference(ForegroundProperty, ink);
#else
        var glyph = new MaterialIcon { Kind = icon.Kind, Width = size, Height = size, IsHitTestVisible = false };
        glyph.Bind(ForegroundProperty, new DynamicResourceExtension(ink));
#endif
        return glyph;
    }
}
