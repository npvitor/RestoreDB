using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using RestoreDB.Domain.Models;

namespace RestoreDB.App.Converters;

/// <summary>Cor do texto de cada linha de log conforme o nível (sucesso=verde, erro=vermelho, etc.).</summary>
public sealed class LevelToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Info = new(Color.FromRgb(0x33, 0x33, 0x33));
    private static readonly SolidColorBrush Command = new(Color.FromRgb(0x0B, 0x5A, 0x8A));
    private static readonly SolidColorBrush Success = new(Color.FromRgb(0x1B, 0x7A, 0x2F));
    private static readonly SolidColorBrush Warning = new(Color.FromRgb(0xB5, 0x6A, 0x00));
    private static readonly SolidColorBrush Error = new(Color.FromRgb(0xC0, 0x1B, 0x1B));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        RepairLogLevel.Command => Command,
        RepairLogLevel.Success => Success,
        RepairLogLevel.Warning => Warning,
        RepairLogLevel.Error => Error,
        _ => Info
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
