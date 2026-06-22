namespace RestoreDB.Domain.Models;

/// <summary>
/// Progresso de uma operação demorada em andamento no banco (recuperação, RESTORE, ROLLBACK) lido
/// de sys.dm_exec_requests. Permite dizer "faltam ~X min" em vez do banco parecer travado.
/// </summary>
public sealed class RecoveryProgress
{
    public string Command { get; init; } = string.Empty;
    public double PercentComplete { get; init; }

    /// <summary>Tempo estimado restante (ms) informado pelo motor.</summary>
    public long EstimatedRemainingMs { get; init; }

    public int EstimatedRemainingMinutes => (int)Math.Ceiling(EstimatedRemainingMs / 60000.0);

    public string Display =>
        $"{Command}: {PercentComplete:0.#}% concluído" +
        (EstimatedRemainingMs > 0 ? $" — faltam ~{EstimatedRemainingMinutes} min" : string.Empty);
}
