namespace RestoreDB.Domain.Models;

/// <summary>
/// Plano de restauração ponto-no-tempo montado a partir do histórico de backups: o FULL base, o
/// DIFERENCIAL aplicável (se houver) e a sequência de LOGs até o instante alvo, já validados quanto
/// à continuidade de LSN. <see cref="Pieces"/> sai na ordem exata de aplicação (a última recebe
/// RECOVERY). Quando a cadeia está quebrada/incompleta, <see cref="IsComplete"/> é false e
/// <see cref="Warnings"/> explica o que falta — para BLOQUEAR a execução antes de tentar.
/// </summary>
public sealed class BackupChainPlan
{
    public string DatabaseName { get; init; } = string.Empty;

    public BackupSetInfo? BaseFull { get; set; }
    public BackupSetInfo? Differential { get; set; }
    public List<BackupSetInfo> Logs { get; } = new();

    /// <summary>Peças na ordem de aplicação (full → diferencial → logs).</summary>
    public List<RestorePiece> Pieces { get; } = new();

    public List<string> Warnings { get; } = new();

    /// <summary>true quando há um FULL base e a cadeia cobre o alvo sem interrupções.</summary>
    public bool IsComplete { get; set; }

    /// <summary>Indica se a última peça de log atinge/ultrapassa o alvo (relevante p/ STOPAT).</summary>
    public bool ReachesTarget { get; set; }

    public string Summary { get; set; } = string.Empty;
}
