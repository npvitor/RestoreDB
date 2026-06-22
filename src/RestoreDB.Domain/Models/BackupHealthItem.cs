namespace RestoreDB.Domain.Models;

/// <summary>
/// Resultado da verificação de saúde de UM conjunto de backup (RESTORE VERIFYONLY ... WITH CHECKSUM).
/// Comprova que a mídia é legível e consistente — sem precisar restaurar de fato.
/// </summary>
public sealed class BackupHealthItem
{
    public BackupSetInfo Set { get; init; } = new();

    /// <summary>true = VERIFYONLY confirmou que o conjunto é válido.</summary>
    public bool Verified { get; init; }

    public string Message { get; init; } = string.Empty;

    public string Display =>
        $"{(Verified ? "✔" : "✘")}  {Set.BackupTypeFriendly}  •  " +
        $"{(Set.BackupFinishDate.HasValue ? Set.BackupFinishDate.Value.ToString("yyyy-MM-dd HH:mm") : "?")}  •  {Message}";
}
