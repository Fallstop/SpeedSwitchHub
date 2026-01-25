using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace GAutoSwitch.UI.Converters;

public class StringNotEmptyToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string stringValue)
        {
            return !string.IsNullOrEmpty(stringValue);
        }
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
