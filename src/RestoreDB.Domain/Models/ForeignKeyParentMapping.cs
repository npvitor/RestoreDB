namespace RestoreDB.Domain.Models;

/// <summary>
/// Mapeamento resolvido de uma chave estrangeira: o schema/tabela do lado PAI (referenciado) e os
/// pares ordenados (coluna-filha → coluna-pai). Cobre chaves compostas. Usado para recuperar o
/// registro-pai que falta a partir do valor da chave presente na linha filha órfã (o predicado do
/// DBCC CHECKCONSTRAINTS traz o valor exato da coluna-filha).
/// </summary>
public sealed class ForeignKeyParentMapping
{
    /// <summary>Schema da tabela PAI (referenciada) — o alvo da reinserção.</summary>
    public string ParentSchema { get; init; } = "dbo";

    /// <summary>Nome da tabela PAI (referenciada).</summary>
    public string ParentTable { get; init; } = string.Empty;

    /// <summary>Pares (coluna-filha → coluna-pai) na ordem da chave (relevante para chaves compostas).</summary>
    public List<ForeignKeyColumnPair> ColumnPairs { get; } = new();
}

/// <summary>Par de colunas correspondentes entre a tabela FILHA e a tabela PAI de uma FK.</summary>
public sealed class ForeignKeyColumnPair
{
    /// <summary>Coluna na tabela FILHA (referenciadora) — é o que aparece no predicado do CHECKCONSTRAINTS.</summary>
    public string ChildColumn { get; init; } = string.Empty;

    /// <summary>Coluna correspondente na tabela PAI (referenciada) — usada para reescrever o predicado.</summary>
    public string ParentColumn { get; init; } = string.Empty;
}
