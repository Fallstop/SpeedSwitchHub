using Microsoft.UI.Xaml.Data;

namespace GAutoSwitch.UI.Converters;

public class BoolToMicButtonTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool isRunning)
        {
            return isRunning ? "Disable" : "Enable";
        }
        return "Enable";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
