namespace RestoreDB.Domain.Models;

/// <summary>Como uma tabela foi extraída antes do reparo destrutivo.</summary>
public enum ExtractionMode
{
    /// <summary>Tabela inteira lida de uma vez (nenhuma página ruim no caminho).</summary>
    Whole,
    /// <summary>Lida por faixas da chave, pulando só os trechos ilegíveis.</summary>
    KeyRanges,
    /// <summary>Não foi possível ler (estrutura danificada ou chave não suportada para faixas).</summary>
    Failed
}

/// <summary>Resultado da extração de UMA tabela para o banco de salvamento.</summary>
public sealed class TableExtraction
{
    public string Table { get; init; } = string.Empty;
    public ExtractionMode Mode { get; set; }
    public long RowsExtracted { get; set; }

    /// <summary>Linhas estimadas pelos metadados antes do reparo (para medir o que ficou ilegível).</summary>
    public long RowsBefore { get; set; }

    /// <summary>Faixas da chave que não puderam ser lidas (ex.: "Id 1201–1400").</summary>
    public List<string> UnreadableRanges { get; } = new();

    /// <summary>Linhas PARCIAIS (só as colunas de um índice não-clusterizado) guardadas para revisão.</summary>
    public long PartialRows { get; set; }

    /// <summary>A tabela tem chave primária — dá para devolver as linhas sem duplicar.</summary>
    public bool HasPrimaryKey { get; set; }

    public string Error { get; set; } = string.Empty;
}

/// <summary>
/// Cópia das linhas LEGÍVEIS das tabelas danificadas, feita no instante anterior ao reparo destrutivo
/// (banco em acesso exclusivo). Como retrata exatamente aquele momento, devolver "o que falta" depois
/// do reparo nunca ressuscita registros excluídos de propósito.
/// </summary>
public sealed class PreRepairExtraction
{
    /// <summary>Banco de salvamento criado (vazio quando a extração não aconteceu).</summary>
    public string SalvageDatabase { get; set; } = string.Empty;
    public List<TableExtraction> Tables { get; } = new();
    public bool Performed => SalvageDatabase.Length > 0 && Tables.Count > 0;
    public string SkippedReason { get; set; } = string.Empty;
}
