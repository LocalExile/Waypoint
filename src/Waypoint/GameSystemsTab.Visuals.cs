#if WINDOWS
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Element = System.Windows.FrameworkElement;
using ShapePath = System.Windows.Shapes.Path;
#else
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using FontWeights = Avalonia.Media.FontWeight;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Styling;
using Element = Avalonia.Controls.Control;
using ShapePath = Avalonia.Controls.Shapes.Path;
#endif

namespace Waypoint;

public sealed partial class GameSystemsTab
{
    static LeagueArtwork? _nativeArtwork;
    static TextBlock Type(string text, double size = 13, bool strong = false, string ink = "Shell.Text")
    {
        var label = new TextBlock { Text = text, FontSize = size, FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        Ink(label, ink); return label;
    }
    static void Ink(TextBlock text, string key)
    {
#if WINDOWS
        text.SetResourceReference(TextBlock.ForegroundProperty, key);
#else
        text.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension(key));
#endif
    }
    static Border Card(Element content, double padding = 16, string surface = "Shell.Panel")
    {
        var border = new Border { Child = content, Padding = new Thickness(padding), CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1) };
#if WINDOWS
        border.SetResourceReference(Border.BackgroundProperty, surface);
        border.SetResourceReference(Border.BorderBrushProperty, "Shell.Border");
#else
        border.Bind(Border.BackgroundProperty, new DynamicResourceExtension(surface));
        border.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("Shell.Border"));
#endif
        return border;
    }
    static Border Badge(string text, string ink = "Shell.TextSecondary")
    {
        var result = Card(Type(text, 11, true, ink), 0, "Shell.Banner");
        result.Padding = new Thickness(8, 4, 8, 4); result.CornerRadius = new CornerRadius(5);
        result.HorizontalAlignment = HorizontalAlignment.Left; result.VerticalAlignment = VerticalAlignment.Center;
        return result;
    }
    static ShapePath LineIcon(string data, string ink, double width = 20, double height = 20)
    {
        var path = new ShapePath { Data = Geometry.Parse(data), Width = width, Height = height, Stretch = Stretch.Uniform, StrokeThickness = 1.6,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false };
#if WINDOWS
        path.SetResourceReference(ShapePath.StrokeProperty, ink);
#else
        path.Bind(ShapePath.StrokeProperty, new DynamicResourceExtension(ink));
#endif
        return path;
    }
    static Border Pennant(bool hardcore, bool large = false, string flagName = "")
    {
        string ink = flagName.Length > 0 ? FlagInk(flagName, hardcore) : hardcore ? "Shell.Error" : "Shell.Accent";
        string? artwork = hardcore ? _nativeArtwork?.Hardcore : _nativeArtwork?.Normal;
        if (artwork is not null && File.Exists(artwork))
        {
#if WINDOWS
            var source = new BitmapImage(); source.BeginInit(); source.CacheOption = BitmapCacheOption.OnLoad; source.UriSource = new Uri(artwork); source.EndInit(); source.Freeze();
#else
            var source = new Avalonia.Media.Imaging.Bitmap(artwork);
#endif
            var image = new Image { Source = source, Width = large ? 72 : 24, Height = large ? 133 : 44, Stretch = Stretch.Uniform, IsHitTestVisible = false };
            var frame = Card(image, large ? 8 : 3, "Shell.Background");
#if WINDOWS
            frame.SetResourceReference(Border.BorderBrushProperty, ink);
#else
            frame.Bind(Border.BorderBrushProperty, new DynamicResourceExtension(ink));
#endif
            return frame;
        }
        var drawing = new Grid { Width = large ? 42 : 28, Height = large ? 54 : 36 };
        drawing.Children.Add(LineIcon("M3,2 L25,2 L25,34 L14,27 L3,34 Z M14,8 L20,14 L14,20 L8,14 Z", ink, large ? 36 : 24, large ? 48 : 32));
        var tile = Card(drawing, large ? 10 : 4, "Shell.Banner"); tile.BorderThickness = new Thickness(0);
        return tile;
    }
    static Element FlagTileVisual(GameLeagueCard card)
    {
        var row = new DockPanel { LastChildFill = true };
        var icon = Pennant(card.Primary.Hardcore, flagName: card.Name); icon.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(icon, Dock.Left); row.Children.Add(icon);
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(Type(card.Name, 14, true));
        var caption = Type(card.Visible ? L.Text("League flag") : L.Text("Not shown"), 11, false, "Shell.TextSecondary");
        caption.Margin = new Thickness(0, 5, 0, 0); words.Children.Add(caption); row.Children.Add(words);
        return row;
    }
#if WINDOWS
    // Keep the data objects as list items: selection and draft guards continue to use the original model.
    public sealed class FlagTile : ContentControl
    {
        public FlagTile() { DataContextChanged += (_, _) => Content = DataContext switch { GameLeagueCard card => FlagTileVisual(card), GameSystem module => ModuleTileVisual(module), _ => null }; }
    }
#endif
    void DressNavigation()
    {
#if WINDOWS
        _list.ItemTemplate = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(FlagTile)) };
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter)); presenter.SetValue(MarginProperty, new Thickness(12)); border.AppendChild(presenter);
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(TemplateProperty, new ControlTemplate(typeof(ListBoxItem)) { VisualTree = border }));
        style.Setters.Add(new Setter(MarginProperty, new Thickness(0, 0, 0, 8)));
        style.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("Shell.Panel")));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("Shell.Border")));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("Shell.Banner")));
        selected.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("Shell.Accent"))); style.Triggers.Add(selected);
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("Shell.Accent"))); style.Triggers.Add(hover);
        var focus = new Trigger { Property = IsKeyboardFocusWithinProperty, Value = true };
        focus.Setters.Add(new Setter(Control.BorderThicknessProperty, new DynamicResourceExtension("FocusThickness"))); style.Triggers.Add(focus);
        _list.ItemContainerStyle = style;
#else
        _list.ItemTemplate = new FuncDataTemplate<object>((item, _) => item is GameLeagueCard card ? FlagTileVisual(card) : ModuleTileVisual((GameSystem)item));
        var style = new Style(s => s.OfType<ListBoxItem>());
        style.Setters.Add(new Setter(TemplatedControl.PaddingProperty, new Thickness(12)));
        style.Setters.Add(new Setter(MarginProperty, new Thickness(0, 0, 0, 8)));
        style.Setters.Add(new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(8)));
        style.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, new DynamicResourceExtension("Shell.Panel")));
        style.Setters.Add(new Setter(TemplatedControl.BorderBrushProperty, new DynamicResourceExtension("Shell.Border")));
        style.Setters.Add(new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(1)));
        _list.Styles.Add(style);
        var selected = new Style(s => s.OfType<ListBoxItem>().Class(":selected"));
        selected.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, new DynamicResourceExtension("Shell.Banner")));
        selected.Setters.Add(new Setter(TemplatedControl.BorderBrushProperty, new DynamicResourceExtension("Shell.Accent"))); _list.Styles.Add(selected);
#endif
    }
    static Element ModuleLabel(GameSystem module)
    {
        var row = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Center };
        var icon = new ModuleGlyph(module.Id, 18); icon.Margin = new Thickness(0, 0, 12, 0);
        DockPanel.SetDock(icon, Dock.Left); row.Children.Add(icon); row.Children.Add(Type(ModuleTitle(module), 13, true));
        return row;
    }
    static Element ModuleTileVisual(GameSystem module)
    {
        var row = new DockPanel { LastChildFill = true };
        var icon = Card(new ModuleGlyph(module.Id, 22), 8, "Shell.Background"); icon.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(icon, Dock.Left); row.Children.Add(icon);
        row.Children.Add(Type(ModuleTitle(module), 13, true));
        return row;
    }
    void Primary(Button button)
    {
#if WINDOWS
        if (TryFindResource("Shell.PrimaryButton") is Style style) button.Style = style;
#else
        button.Classes.Add("accent");
#endif
    }
}
