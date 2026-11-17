using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace SkyLink.App;

public static class Converters
{
    public static readonly IValueConverter NotNullToVisible = new Fn(v => v is null || v as string == "" ? Visibility.Collapsed : Visibility.Visible);
    public static readonly IValueConverter ZeroToVisible = new Fn(v => v is 0 ? Visibility.Visible : Visibility.Collapsed);
    public static readonly IValueConverter ErrorToSeverity = new Fn(v => v is true ? InfoBarSeverity.Error : InfoBarSeverity.Success);
    public static readonly IValueConverter OkToBrush = new Fn(v => v is true ? new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0x6E)) : new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)));

    private sealed class Fn(Func<object?, object> f) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => f(value);
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
