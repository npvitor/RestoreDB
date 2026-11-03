namespace RestoreDB.Domain.Models;

public enum RepairStatus
{
    /// <summary>Banco já estava saudável; nada a fazer.</summary>
    NoActionNeeded,

    /// <summary>Reparo concluído com sucesso.</summary>
    Success,

    /// <summary>
    /// O reparo SEM perda de dados não foi suficiente. A UI deve oferecer o reparo
    /// COM possível perda de dados, exigindo confirmação explícita do usuário.
    /// </summary>
    NeedsDataLossConfirmation,

    /// <summary>Não foi possível reparar automaticamente.</summary>
    Failed
}

/// <summary>Resultado consolidado de uma operação de reparo.</summary>
public sealed class RepairResult
{
    public RepairStatus Status { get; set; }
    public string Message { get; set; } = string.Empty;
    public string FinalStateDesc { get; set; } = string.Empty;
    public string? BackupFile { get; set; }

    /// <summary>
    /// Banco de salvamento mantido após o reparo (linhas extraídas antes do reparo que não puderam ser
    /// devolvidas automaticamente — ex.: tabela sem chave primária). A UI oferece o Assistente de salvamento.
    /// </summary>
    public string? SalvageDatabase { get; set; }
}
