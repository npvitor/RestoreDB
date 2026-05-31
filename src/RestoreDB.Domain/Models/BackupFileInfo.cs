namespace RestoreDB.Domain.Models;

/// <summary>
/// Um arquivo lógico contido em um backup (linha de RESTORE FILELISTONLY). Permite montar a
/// cláusula WITH MOVE para realocar arquivos durante o RESTORE.
/// </summary>
public sealed class BackupFileInfo
{
    public string LogicalName { get; init; } = string.Empty;
    public string PhysicalName { get; init; } = string.Empty;

    /// <summary>D = dados (ROWS), L = log, S = FILESTREAM, F = full-text.</summary>
    public string Type { get; init; } = string.Empty;

    public string FileGroupName { get; init; } = string.Empty;

    public bool IsData => string.Equals(Type, "D", StringComparison.OrdinalIgnoreCase);
    public bool IsLog => string.Equals(Type, "L", StringComparison.OrdinalIgnoreCase);
}
