namespace RestoreDB.Domain.Models;

/// <summary>Perda estimada de uma tabela num reparo simulado (linhas antes × depois).</summary>
public sealed class TableLoss
{
    public string Table { get; init; } = string.Empty;
    public long RowsBefore { get; init; }
    public long RowsAfter { get; init; }
    public long RowsLost => Math.Max(0, RowsBefore - RowsAfter);

    public string Display => $"{Table}: {RowsBefore:N0} → {RowsAfter:N0}  ({RowsLost:N0} perdida(s))";
}

/// <summary>
/// Resultado de um REPARO SIMULADO: o reparo destrutivo é executado numa CÓPIA descartável do banco,
/// não na produção, e a perda é quantificada por tabela. Permite ao usuário decidir com conhecimento
/// total antes de aplicar (ou não) o reparo real.
/// </summary>
public sealed class RepairSimulationResult
{
    public string DatabaseName { get; init; } = string.Empty;

    /// <summary>true se a simulação conseguiu rodar (cópia criada e reparada).</summary>
    public bool Executed { get; set; }

    /// <summary>true se o DBCC CHECKDB ficou LIMPO na cópia após o reparo.</summary>
    public bool CheckDbCleanAfter { get; set; }

    public List<TableLoss> TableLosses { get; } = new();

    /// <summary>Total de linhas que seriam descartadas no reparo real.</summary>
    public long TotalRowsLost => TableLosses.Sum(t => t.RowsLost);

    /// <summary>Tabelas que perderiam ao menos uma linha.</summary>
    public IReadOnlyList<TableLoss> AffectedTables => TableLosses.Where(t => t.RowsLost > 0).ToList();

    public string Message { get; set; } = string.Empty;
}
