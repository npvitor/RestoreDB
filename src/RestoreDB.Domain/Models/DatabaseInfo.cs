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
    private string _userAccess = string.Empty;
    private bool _isReadOnly;

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
            OnPropertyChanged(nameof(StateDisplay));
        }
    }

    /// <summary>MULTI_USER / SINGLE_USER / RESTRICTED_USER (sys.databases.user_access_desc).</summary>
    public string UserAccess
    {
        get => _userAccess;
        set
        {
            if (string.Equals(_userAccess, value, StringComparison.Ordinal)) return;
            _userAccess = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasAccessRestriction));
            OnPropertyChanged(nameof(StateDisplay));
        }
    }

    /// <summary>Banco marcado como somente leitura (READ_ONLY).</summary>
    public bool IsReadOnly
    {
        get => _isReadOnly;
        set
        {
            if (_isReadOnly == value) return;
            _isReadOnly = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasAccessRestriction));
            OnPropertyChanged(nameof(StateDisplay));
        }
    }

    public bool IsProblematic => AppConstants.IsProblematic(StateDesc);
    public bool IsHealthy => !IsProblematic;
    public bool IsSystemDatabase => AppConstants.IsSystemDatabase(Name);

    /// <summary>
    /// ONLINE, mas com o acesso restrito (SINGLE_USER/RESTRICTED_USER) ou somente leitura — o estado
    /// "parece" saudável, porém o sistema não consegue usar o banco normalmente.
    /// </summary>
    public bool HasAccessRestriction =>
        IsReadOnly || (UserAccess.Length > 0 && !UserAccess.Equals("MULTI_USER", StringComparison.OrdinalIgnoreCase));

    /// <summary>Estado para exibição: "ONLINE", "ONLINE · SINGLE_USER", "ONLINE · SOMENTE LEITURA"...</summary>
    public string StateDisplay =>
        StateDesc
        + (UserAccess.Length > 0 && !UserAccess.Equals("MULTI_USER", StringComparison.OrdinalIgnoreCase) ? " · " + UserAccess : string.Empty)
        + (IsReadOnly ? " · SOMENTE LEITURA" : string.Empty);

    public string StateFriendly => StateInfo.Friendly(StateDesc);
    public string RecommendedAction => StateInfo.RecommendedAction(StateDesc);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
