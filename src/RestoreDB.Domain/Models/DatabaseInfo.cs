using System.ComponentModel;
using System.Runtime.CompilerServices;
using RestoreDB.Domain.Constants;

namespace RestoreDB.Domain.Models;

/// <summary>
/// Representa um banco do servidor (linha de sys.databases).
/// Implementa INotifyPropertyChanged para que a grade da UI reflita as mudanças de
/// estado após cada operação de reparo.
/// </summary>
public sealed class DatabaseInfo : INotifyPropertyChanged
{
    private string _stateDesc = string.Empty;

    public string Name { get; init; } = string.Empty;
    public int State { get; set; }
    public string RecoveryModel { get; set; } = string.Empty;

    public string StateDesc
    {
        get => _stateDesc;
        set
        {
            if (string.Equals(_stateDesc, value, StringComparison.Ordinal)) return;
            _stateDesc = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsProblematic));
            OnPropertyChanged(nameof(IsHealthy));
            OnPropertyChanged(nameof(StateFriendly));
            OnPropertyChanged(nameof(RecommendedAction));
        }
    }

    public bool IsProblematic => AppConstants.IsProblematic(StateDesc);
    public bool IsHealthy => !IsProblematic;
    public bool IsSystemDatabase => AppConstants.IsSystemDatabase(Name);

    public string StateFriendly => StateInfo.Friendly(StateDesc);
    public string RecommendedAction => StateInfo.RecommendedAction(StateDesc);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
