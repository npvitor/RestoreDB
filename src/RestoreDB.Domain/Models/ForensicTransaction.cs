using System.Globalization;

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

    /// <summary>Tabela afetada ("schema.tabela"), quando o log a identifica (DELETE). Vazio se desconhecida.</summary>
    public string Table { get; init; } = string.Empty;

    /// <summary>Uma exclusão de LINHAS numa tabela conhecida pode ser desfeita sem sobrescrever o banco.</summary>
    public bool CanRecoverRowsInPlace => Kind == "DELETE" && Table.Length > 0;

    public string Display =>
        $"{(BeginTime.HasValue ? BeginTime.Value.ToString("yyyy-MM-dd HH:mm:ss") : BeginTimeRaw)}  •  {Kind}" +
        (Table.Length > 0 ? $"  •  {Table}" : string.Empty) +
        (RowsAffected > 0 ? $"  •  {RowsAffected} linha(s)" : string.Empty);

    /// <summary>
    /// Converte o [Begin Time] do fn_dblog — formato "yyyy/MM/dd HH:mm:ss:fff" (milissegundos separados
    /// por DOIS-PONTOS, não ponto) — em DateTime. Um DateTime.TryParse comum rejeita esse formato, o que
    /// impedia restaurar até antes da exclusão. Devolve null se não reconhecer.
    /// </summary>
    public static DateTime? ParseLogTime(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        string[] formats =
        {
            "yyyy/MM/dd HH:mm:ss:fff", "yyyy/MM/dd HH:mm:ss.fff", "yyyy/MM/dd HH:mm:ss",
            "yyyy-MM-dd HH:mm:ss:fff", "yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-dd HH:mm:ss"
        };
        if (DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return dt;
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt) ? dt : null;
    }

    /// <summary>
    /// Extrai "schema.tabela" do AllocUnitName do fn_dblog ("dbo.NotaFiscal.PK_NotaFiscal" ou
    /// "dbo.NotaFiscal" num heap). Vazio quando não reconhece.
    /// </summary>
    public static string TableFromAllocUnit(string? allocUnit)
    {
        if (string.IsNullOrWhiteSpace(allocUnit)) return string.Empty;
        var parts = allocUnit.Trim().Split('.');
        return parts.Length >= 2 && parts[0].Length > 0 && parts[1].Length > 0 ? $"{parts[0]}.{parts[1]}" : string.Empty;
    }
}
