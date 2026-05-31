namespace RestoreDB.Domain.Models;

/// <summary>
/// Um conjunto de backup (linha de msdb.dbo.backupset ou de RESTORE HEADERONLY). Usado para
/// listar backups disponíveis para restauração de um banco.
/// </summary>
public sealed class BackupSetInfo
{
    public string DatabaseName { get; init; } = string.Empty;

    /// <summary>D = Full, I = Differential, L = Transaction Log, F = File/Filegroup.</summary>
    public string BackupTypeCode { get; init; } = string.Empty;

    /// <summary>Posição do conjunto dentro do arquivo físico (parâmetro WITH FILE = n).</summary>
    public int Position { get; init; }

    public DateTime? BackupStartDate { get; init; }
    public DateTime? BackupFinishDate { get; init; }
    public bool IsCopyOnly { get; init; }
    public long BackupSizeBytes { get; init; }
    public string RecoveryModel { get; init; } = string.Empty;
    public string ServerName { get; init; } = string.Empty;
    public string PhysicalDevice { get; init; } = string.Empty;
    public decimal FirstLsn { get; init; }
    public decimal LastLsn { get; init; }

    public string BackupTypeFriendly => BackupTypeCode.ToUpperInvariant() switch
    {
        "D" => "Completo (Full)",
        "I" => "Diferencial",
        "L" => "Log de transações",
        "F" => "Arquivo/Filegroup",
        "G" => "Diferencial de arquivo",
        "P" => "Parcial",
        "Q" => "Diferencial parcial",
        _   => BackupTypeCode
    };

    public double BackupSizeMb => BackupSizeBytes / 1024.0 / 1024.0;

    /// <summary>Texto para exibição em lista (UI).</summary>
    public string Display =>
        $"{BackupTypeFriendly}  •  {(BackupFinishDate.HasValue ? BackupFinishDate.Value.ToString("yyyy-MM-dd HH:mm") : "?")}" +
        $"  •  {BackupSizeMb:0.#} MB  •  pos {Position}{(IsCopyOnly ? "  •  COPY_ONLY" : string.Empty)}";
}
