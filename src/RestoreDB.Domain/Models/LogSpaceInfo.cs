namespace RestoreDB.Domain.Models;

/// <summary>Um arquivo de LOG do banco com seus limites de crescimento (sys.master_files).</summary>
public sealed class LogFileInfo
{
    public string LogicalName { get; init; } = string.Empty;
    public string PhysicalName { get; init; } = string.Empty;
    public double SizeMb { get; init; }

    /// <summary>Tamanho máximo em MB; nulo = ilimitado.</summary>
    public double? MaxSizeMb { get; init; }

    /// <summary>Crescimento automático: em MB, ou em % quando <see cref="GrowthIsPercent"/>. 0 = desligado.</summary>
    public double Growth { get; init; }
    public bool GrowthIsPercent { get; init; }

    /// <summary>Espaço livre no volume do arquivo (MB); nulo quando não foi possível medir.</summary>
    public double? VolumeFreeMb { get; init; }

    public bool AutoGrowthDisabled => Growth <= 0;

    /// <summary>O arquivo já chegou ao limite máximo configurado (não cresce mais).</summary>
    public bool AtMaxSize => MaxSizeMb is double max && SizeMb >= max - 1;

    public string Display =>
        $"{LogicalName}: {SizeMb:N0} MB" +
        (MaxSizeMb is double m ? $" (máx. {m:N0} MB)" : " (sem limite)") +
        (AutoGrowthDisabled ? " • crescimento DESLIGADO" : GrowthIsPercent ? $" • cresce {Growth:0}%" : $" • cresce {Growth:N0} MB") +
        (VolumeFreeMb is double f ? $" • {f:N0} MB livres no disco" : string.Empty);
}

/// <summary>
/// Situação do LOG de transações de um banco: uso, motivo que impede a reutilização do espaço
/// (log_reuse_wait_desc) e os limites dos arquivos. Base do diagnóstico e da resolução do erro
/// 9002 ("log cheio") — que NÃO é corrupção e se resolve sem perda de dados.
/// </summary>
public sealed class LogSpaceInfo
{
    public string RecoveryModel { get; init; } = string.Empty;

    /// <summary>sys.databases.log_reuse_wait_desc (LOG_BACKUP, ACTIVE_TRANSACTION, REPLICATION...).</summary>
    public string ReuseWait { get; init; } = string.Empty;

    public double? UsedPercent { get; init; }
    public double? TotalSizeMb { get; init; }
    public List<LogFileInfo> Files { get; } = new();

    /// <summary>Uso crítico: acima de 90% com algo impedindo a reutilização (vai estourar).</summary>
    public bool IsCritical => UsedPercent is >= 90 && ReuseWait is not ("" or "NOTHING" or "CHECKPOINT");

    /// <summary>Nenhum arquivo de log consegue crescer (limite atingido, crescimento desligado ou disco cheio).</summary>
    public bool CannotGrow => Files.Count > 0 && Files.All(f =>
        f.AtMaxSize || f.AutoGrowthDisabled || f.VolumeFreeMb is < 64);

    /// <summary>Texto amigável do motivo de espera.</summary>
    public string ReuseWaitFriendly => ReuseWait switch
    {
        "LOG_BACKUP" => "aguardando BACKUP DE LOG (modelo FULL/BULK sem backup de log recente)",
        "ACTIVE_TRANSACTION" => "uma transação ABERTA há muito tempo segura o log",
        "REPLICATION" or "LOG_SCAN" => "replicação/CDC ainda não leu o log",
        "DATABASE_MIRRORING" or "AVAILABILITY_REPLICA" => "réplica/espelho atrasado",
        "CHECKPOINT" => "aguardando CHECKPOINT",
        "NOTHING" => "nada impede a reutilização",
        "" => "desconhecido",
        _ => ReuseWait
    };

    public string Summary =>
        (UsedPercent is double u ? $"Log {u:0.#}% usado" : "Uso do log indisponível") +
        (TotalSizeMb is double t ? $" de {t:N0} MB" : string.Empty) +
        $" — {ReuseWaitFriendly}.";
}
