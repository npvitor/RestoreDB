namespace RestoreDB.Domain.Models;

/// <summary>
/// Uma linha da saída de DBCC CHECKCONSTRAINTS (...) WITH TABLERESULTS — uma violação de
/// integridade LÓGICA (chave estrangeira ou CHECK) que o DBCC CHECKDB NÃO detecta.
///
/// O caso clássico é uma linha com uma chave estrangeira apontando para uma chave primária
/// inexistente (registro órfão) — comum após corrupção física ou após um
/// REPAIR_ALLOW_DATA_LOSS, que desaloca páginas SEM respeitar as constraints.
///
/// A coluna <see cref="Where"/> é o predicado gerado pelo próprio SQL Server que identifica
/// as linhas violadoras — usado para remover (ou isolar) exatamente esses registros.
/// </summary>
public sealed class ConstraintViolation
{
    /// <summary>Tabela que contém as linhas violadoras (a tabela FILHA, no caso de FK).</summary>
    public string Table { get; init; } = string.Empty;

    /// <summary>Nome da constraint violada (FK ou CHECK).</summary>
    public string Constraint { get; init; } = string.Empty;

    /// <summary>Predicado que localiza as linhas violadoras (ex.: "[ClienteId] = 42"). Server-generated.</summary>
    public string Where { get; init; } = string.Empty;

    /// <summary>true quando a constraint é uma chave estrangeira (resolvida em sys.foreign_keys).
    /// Só FKs têm remoção automática de órfãos; violações de CHECK são apenas reportadas.</summary>
    public bool IsForeignKey { get; set; }

    public string Display =>
        $"{(IsForeignKey ? "FK" : "CHECK")} {Constraint} em {Table} — {Where}".TrimEnd(' ', '—');
}
