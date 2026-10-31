using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;

namespace Waypoint;

/// <summary>Interface text in XAML: <c>{l:Loc 'Stop all'}</c> shows the selected language's text for that English source
/// (Launcher.Core's catalogs, <see cref="L"/>) and follows a language change at once.</summary>
public sealed class LocExtension(string text) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding(nameof(L.Language)) { Source = L.Current, Mode = BindingMode.OneWay, Converter = Translate.Instance, ConverterParameter = text }
            .ProvideValue(serviceProvider);

    sealed class Translate : IValueConverter
    {
        public static readonly Translate Instance = new();
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => L.Text((string)parameter);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}

/// <summary>What a language change has to redo by hand.</summary>
static class LocRefresh
{
    /// <summary>A closed ComboBox shows a copy of its selection's text, taken when it was chosen: choosing it again brings
    /// the new language.</summary>
    public static void Reselect(params ComboBox[] boxes)
    {
        foreach (var box in boxes)
        {
            int selected = box.SelectedIndex;
            box.SelectedIndex = -1;
            box.SelectedIndex = selected;
        }
    }
}
