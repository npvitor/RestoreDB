namespace RestoreDB.Domain.Models;

/// <summary>
/// Uma transação DESTRUTIVA recente lida do log de transações (fn_dblog) — DELETE em massa, DROP ou
/// TRUNCATE. Permite identificar o "quando" de uma exclusão acidental e restaurar até LOGO ANTES dela
/// (ponto-no-tempo). Forense somente leitura; só enxerga a parte ativa do log.
/// </summary>
public sealed class ForensicTransaction
{
    public string TransactionId { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;        // "DELETE", "DROP", "TRUNCATE"
    public string BeginTimeRaw { get; init; } = string.Empty;
    public DateTime? BeginTime { get; init; }
    public int RowsAffected { get; init; }

    public string Display =>
        $"{(BeginTime.HasValue ? BeginTime.Value.ToString("yyyy-MM-dd HH:mm:ss") : BeginTimeRaw)}  •  {Kind}" +
        (RowsAffected > 0 ? $"  •  {RowsAffected} linha(s)" : string.Empty);
}
