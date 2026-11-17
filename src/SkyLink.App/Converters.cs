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
    public static readonly IValueConverter BannerBackground = new Fn(v => v is true
        ? new SolidColorBrush(Color.FromArgb(0x40, 0xC4, 0x2B, 0x1C))
        : new SolidColorBrush(Color.FromArgb(0x38, 0x4C, 0xC2, 0x6E)));
    public static readonly IValueConverter BannerBorder = new Fn(v => v is true
        ? new SolidColorBrush(Color.FromArgb(0x90, 0xE8, 0x4A, 0x3A))
        : new SolidColorBrush(Color.FromArgb(0x80, 0x4C, 0xC2, 0x6E)));
    public static readonly IValueConverter BannerIcon = new Fn(v => v is true ? SymbolRegular.ErrorCircle24 : SymbolRegular.CheckmarkCircle24);
    public static readonly IValueConverter FoundToBrush = new Fn(v => v is true
        ? (object)(Application.Current.TryFindResource("AccentTextFillColorPrimaryBrush") ?? Brushes.White)
        : Application.Current.TryFindResource("TextFillColorTertiaryBrush") ?? Brushes.Gray);
    public static readonly IValueConverter OkToBrush = new Fn(v => v is true ? new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0x6E)) : new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)));

    private sealed class Fn(Func<object?, object> f) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => f(value);
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
