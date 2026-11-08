#if WINDOWS
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using A11y = System.Windows.Automation.AutomationProperties;
using Glyph = MaterialDesignThemes.Wpf.PackIcon;
using GlyphKind = MaterialDesignThemes.Wpf.PackIconKind;
#else
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using A11y = Avalonia.Automation.AutomationProperties;
using Glyph = Material.Icons.Avalonia.MaterialIcon;
using GlyphKind = Material.Icons.MaterialIconKind;
#endif

namespace Waypoint;

public enum ManagedContentSection { Mods, Modules, Leagues }

/// <summary>One heading hierarchy and compact relationship help for the three content-management sections.</summary>
public sealed class ContentSectionHeading : UserControl
{
    ManagedContentSection _section;
    bool _watchingLanguage;
    public ManagedContentSection Section { get => _section; set { _section = value; Render(); } }
    public ContentSectionHeading()
    {
        Loaded += (_, _) =>
        {
            if (!_watchingLanguage) L.Current.PropertyChanged += LanguageChanged;
            _watchingLanguage = true; Render();
        };
        Unloaded += (_, _) =>
        {
            if (_watchingLanguage) L.Current.PropertyChanged -= LanguageChanged;
            _watchingLanguage = false;
        };
        Render();
    }
    void LanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
#if WINDOWS
        Dispatcher.BeginInvoke(Render);
#else
        Avalonia.Threading.Dispatcher.UIThread.Post(Render);
#endif
    }
    static void Ink(TextBlock label, string resource)
    {
#if WINDOWS
        label.SetResourceReference(TextBlock.ForegroundProperty, resource);
#else
        label.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension(resource));
#endif
    }
    void Render()
    {
        var heading = new StackPanel();
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var title = new TextBlock { Text = _section switch { ManagedContentSection.Mods => L.Text("Mods"), ManagedContentSection.Modules => L.Text("Modules"), _ => L.Text("Leagues") }, FontSize = 26 };
#if WINDOWS
        title.FontWeight = FontWeights.SemiBold;
#else
        title.FontWeight = FontWeight.SemiBold;
#endif
        Ink(title, "Shell.Text"); row.Children.Add(title);
        string help = L.Text("About Mods, Modules and Leagues");
        var symbol = new Glyph { Kind = GlyphKind.InformationOutline, Width = 16, Height = 16, IsHitTestVisible = false };
        var about = new Button { Content = symbol, Width = 28, Height = 28, Padding = new Thickness(5), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        A11y.SetName(about, help);
#if WINDOWS
        about.SetResourceReference(StyleProperty, "Shell.IconButton"); about.ToolTip = help;
        symbol.SetResourceReference(ForegroundProperty, "Shell.TextSecondary");
#else
        about.Background = Brushes.Transparent; ToolTip.SetTip(about, help);
        symbol.Bind(ForegroundProperty, new DynamicResourceExtension("Shell.TextSecondary"));
#endif
        about.Click += (_, _) =>
        {
            Window? owner =
#if WINDOWS
                Window.GetWindow(this);
#else
                TopLevel.GetTopLevel(this) as Window;
#endif
            Dialog.Tell(owner, DialogKind.Info, L.Text("Mods, Modules and Leagues"),
                L.Text("Mods are add-ons installed for this checkout. Enable, update and configure them in Mods.") + "\n\n"
                + L.Text("Modules are LocalExile gameplay systems. Their settings are shared by all leagues using them.") + "\n\n"
                + L.Text("Leagues define the flags and linked game modes players can choose. Select the modules each league uses in Leagues."));
        };
        row.Children.Add(about); heading.Children.Add(row);
        var description = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0), Text = _section switch
        {
            ManagedContentSection.Mods => L.Text("Install and configure add-ons for this checkout."),
            ManagedContentSection.Modules => L.Text("Configure shared gameplay systems; choose which leagues use them in Leagues."),
            _ => L.Text("Manage player-facing flags, game modes and each league's module choices.")
        } };
        Ink(description, "Shell.TextSecondary"); heading.Children.Add(description); Content = heading;
    }
}
