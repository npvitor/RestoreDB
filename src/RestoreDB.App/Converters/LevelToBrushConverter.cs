using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using RestoreDB.Domain.Models;

namespace RestoreDB.App.Converters;

/// <summary>
/// Cor do texto de cada linha de log conforme o nível (sucesso=verde, erro=vermelho, etc.).
/// Resolve a cor a partir dos recursos do tema ativo (chaves Log*Brush), para acompanhar o
/// modo claro/escuro. Após trocar o tema, a lista é atualizada (Items.Refresh) para reaplicar.
/// </summary>
public sealed class LevelToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            RepairLogLevel.Command => "LogCommandBrush",
            RepairLogLevel.Success => "LogSuccessBrush",
            RepairLogLevel.Warning => "LogWarnBrush",
            RepairLogLevel.Error => "LogErrorBrush",
            _ => "LogInfoBrush"
        };
        return (Application.Current?.TryFindResource(key) as Brush) ?? Brushes.Black;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
