using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace Waypoint;

/// <summary>Interface text in XAML, as on Windows: <c>{l:Loc 'Stop all'}</c> shows the selected language's text for that English
/// source (Launcher.Core's catalogs, <see cref="L"/>) and follows a language change at once.</summary>
sealed class LocExtension(string text)
{
    public BindingBase ProvideValue() =>
        new ReflectionBinding(nameof(L.Language)) { Source = L.Current, Mode = BindingMode.OneWay, Converter = Translate.Instance, ConverterParameter = text };

    sealed class Translate : IValueConverter
    {
        public static readonly Translate Instance = new();
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => L.Text((string)parameter!);
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
