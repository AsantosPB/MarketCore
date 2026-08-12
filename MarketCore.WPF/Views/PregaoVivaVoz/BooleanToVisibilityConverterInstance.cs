using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MarketCore.WPF.Views.PregaoVivaVoz
{
    /// <summary>
    /// Converter singleton pra usar diretamente em XAML.
    /// Uso: Visibility="{Binding IsVisible, Converter={x:Static local:BooleanToVisibilityConverterInstance.Instance}}"
    /// </summary>
    public class BooleanToVisibilityConverterInstance : IValueConverter
    {
        public static BooleanToVisibilityConverterInstance Instance { get; } = new BooleanToVisibilityConverterInstance();
        
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool b)
            {
                return b ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }
        
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is Visibility v)
            {
                return v == Visibility.Visible;
            }
            return false;
        }
    }
}
