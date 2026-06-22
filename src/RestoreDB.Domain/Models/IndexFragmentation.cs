namespace RestoreDB.Domain.Models;

/// <summary>
/// Fragmentação de um índice (de sys.dm_db_index_physical_stats) — base da manutenção preventiva:
/// 10–30% ⇒ REORGANIZE; &gt; 30% ⇒ REBUILD.
/// </summary>
public sealed class IndexFragmentation
{
    public string Table { get; init; } = string.Empty;     // já vem com QUOTENAME
    public string Index { get; init; } = string.Empty;
    public double FragmentationPercent { get; init; }
    public long PageCount { get; init; }

    public string RecommendedAction =>
        FragmentationPercent > 30 ? "REBUILD" : FragmentationPercent >= 10 ? "REORGANIZE" : "OK";

    public bool NeedsMaintenance => FragmentationPercent >= 10;

    public string Display => $"{Table} / {Index}: {FragmentationPercent:0.#}% ({PageCount:N0} págs) → {RecommendedAction}";
}
