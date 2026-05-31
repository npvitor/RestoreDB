namespace RestoreDB.Domain.Models;

/// <summary>
/// Uma página suspeita/corrompida registrada em msdb.dbo.suspect_pages — a fonte autoritativa
/// do SQL Server para corrupção detectada em tempo de execução (erros 823/824/825).
/// </summary>
public sealed class SuspectPageInfo
{
    public int FileId { get; init; }
    public long PageId { get; init; }

    /// <summary>1 = erro 823/824 ou checksum ruim; 2 = checksum ruim; 3 = página rasgada (torn);
    /// 4 = restaurada; 5 = reparada (DBCC); 7 = desalocada por DBCC.</summary>
    public int EventType { get; init; }

    public int ErrorCount { get; init; }
    public DateTime? LastUpdateDate { get; init; }

    public string EventTypeDescription => EventType switch
    {
        1 => "Erro de E/S (823) ou consistência (824)",
        2 => "Checksum incorreto",
        3 => "Página rasgada (torn page)",
        4 => "Restaurada",
        5 => "Reparada (DBCC)",
        7 => "Desalocada pelo DBCC",
        _ => $"Tipo {EventType}"
    };

    /// <summary>Eventos 1, 2 e 3 indicam corrupção ativa (não apenas histórico de reparo).</summary>
    public bool IsActiveCorruption => EventType is 1 or 2 or 3;
}
