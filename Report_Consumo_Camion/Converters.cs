using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
namespace Report_Consumo_Camion
{
    public class ChatAlignmentConverter : IValueConverter { public object Convert(object v, Type t, object p, CultureInfo c) => (bool)v ? HorizontalAlignment.Left : HorizontalAlignment.Right; public object ConvertBack(object v, Type t, object p, CultureInfo c) => null!; }
    public class ChatBubbleBackgroundConverter : IValueConverter { public object Convert(object v, Type t, object p, CultureInfo c) => new SolidColorBrush((bool)v ? Colors.LightGray : Colors.WhiteSmoke); public object ConvertBack(object v, Type t, object p, CultureInfo c) => null!; }
    public class ChatBubbleBorderBrushConverter : IValueConverter { public object Convert(object v, Type t, object p, CultureInfo c) => new SolidColorBrush((bool)v ? Colors.Gray : Color.FromRgb(0, 122, 204)); public object ConvertBack(object v, Type t, object p, CultureInfo c) => null!; }
    public class UtenteToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // Mostra il bottone solo se IsUtente è false
            if (value is bool isUtente && !isUtente)
                return Visibility.Visible;

            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }



    }
    public class MessaggioChat
    {
        public string? Testo { get; set; }
        public bool IsUtente { get; set; }
        public string? Result { get; set; }
        public string? Formula { get; set; }
        public string? Explain { get; set; }
        public bool HasPython => !string.IsNullOrWhiteSpace(Result);
    }

    public record PythonOutput(string Result, string Formula, string Explain);
}
