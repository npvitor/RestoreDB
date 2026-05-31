namespace RestoreDB.Domain.Models;

/// <summary>
/// Uma linha estruturada da saída de DBCC CHECKDB (...) WITH TABLERESULTS. Permite decidir,
/// de forma confiável (sem parsear texto livre), o nível mínimo de reparo e se a corrupção
/// está restrita a índices não-clusterizados (recuperáveis sem perda de dados).
/// </summary>
public sealed class CorruptionDetail
{
    public int Error { get; init; }
    public int Level { get; init; }
    public int State { get; init; }
    public string MessageText { get; init; } = string.Empty;

    /// <summary>Nível mínimo de reparo sugerido pelo próprio CHECKDB (ex.: "repair_rebuild",
    /// "repair_allow_data_loss"). Vazio quando o erro não é reparável pelo DBCC.</summary>
    public string RepairLevel { get; init; } = string.Empty;

    public int ObjectId { get; init; }

    /// <summary>0 = heap; 1 = índice clusterizado; &gt; 1 = índice NÃO clusterizado.</summary>
    public int IndexId { get; init; }

    public string ObjectName { get; set; } = string.Empty;
    public string IndexName { get; set; } = string.Empty;

    /// <summary>Corrupção em índice não-clusterizado: reconstruível com ALTER INDEX REBUILD,
    /// SEM perda de dados.</summary>
    public bool IsNonClusteredIndex => IndexId > 1;

    public bool RepairLevelIsRebuild =>
        RepairLevel.Contains("rebuild", StringComparison.OrdinalIgnoreCase);

    public bool RepairLevelIsDataLoss =>
        RepairLevel.Contains("allow_data_loss", StringComparison.OrdinalIgnoreCase);
}
