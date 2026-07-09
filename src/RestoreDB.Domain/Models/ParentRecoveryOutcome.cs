namespace RestoreDB.Domain.Models;

/// <summary>
/// Resultado de uma tentativa de reinserir um registro-pai (recuperado de um backup) no banco vivo,
/// para desfazer uma chave estrangeira órfã antes de recorrer à exclusão da linha filha.
/// </summary>
public enum ParentRecoveryOutcome
{
    /// <summary>A linha-pai foi reinserida no banco vivo (a FK deixa de estar órfã por essa chave).</summary>
    Inserted,

    /// <summary>A linha-pai já existia no banco vivo (violação de PK 2627/2601 tratada como sucesso silencioso).</summary>
    AlreadyPresent,

    /// <summary>A linha-pai não foi encontrada na cópia do backup — a linha filha seguirá para exclusão.</summary>
    NotFoundInBackup,

    /// <summary>Erro inesperado na tentativa (logado como aviso; nunca derruba o fluxo de reparo).</summary>
    Failed
}
