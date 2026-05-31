namespace RestoreDB.Domain.Models;

/// <summary>
/// Relatório consolidado de um DBCC CHECKDB (...) WITH TABLERESULTS. Agrega as linhas de
/// corrupção e deriva decisões: existe erro? o nível mínimo de reparo é REBUILD (sem perda)
/// ou ALLOW_DATA_LOSS? toda a corrupção está em índices não-clusterizados (reconstruíveis
/// sem perda)?
/// </summary>
public sealed class CheckDbReport
{
    public List<CorruptionDetail> Details { get; } = new();

    /// <summary>true se o CHECKDB executou e retornou um resultado (mesmo sem erros).</summary>
    public bool Executed { get; set; }

    public bool HasErrors => Details.Any(d => d.Error != 0 && d.Level >= 16);

    /// <summary>Todos os erros estão em índices não-clusterizados (reconstruíveis sem perda).</summary>
    public bool AllErrorsAreNonClusteredIndex =>
        HasErrors && RepairableDetails.All(d => d.IsNonClusteredIndex);

    private IEnumerable<CorruptionDetail> RepairableDetails =>
        Details.Where(d => d.Error != 0 && d.Level >= 16 && (d.ObjectId != 0 || d.IndexId != 0));

    /// <summary>Pares (ObjectId, IndexId) de índices não-clusterizados a reconstruir.</summary>
    public IReadOnlyList<(int ObjectId, int IndexId)> NonClusteredIndexesToRebuild =>
        Details.Where(d => d.IsNonClusteredIndex)
               .Select(d => (d.ObjectId, d.IndexId))
               .Distinct()
               .ToList();

    /// <summary>Nível mínimo de reparo informado pelo CHECKDB para o conjunto de erros.</summary>
    public string MinRepairLevel
    {
        get
        {
            if (Details.Any(d => d.RepairLevelIsDataLoss)) return "REPAIR_ALLOW_DATA_LOSS";
            if (Details.Any(d => d.RepairLevelIsRebuild)) return "REPAIR_REBUILD";
            return string.Empty;
        }
    }

    public int ErrorCount => Details.Count(d => d.Error != 0 && d.Level >= 16);
}
