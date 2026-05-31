namespace RestoreDB.Domain.Models;

/// <summary>Ação principal sugerida pelo diagnóstico, usada pela UI para destacar o caminho certo.</summary>
public enum RecommendedActionKind
{
    Unknown,
    NoAction,
    SafeRepair,
    RebuildIndexesOnly,
    RestoreFromBackup,
    PageRestore,
    RebuildLog,
    DataLossRepair,
    FreeDiskSpace,
    ReviewTopology
}

/// <summary>
/// Resultado de um diagnóstico SOMENTE LEITURA. Além de distinguir corrupção de DADOS
/// (.mdf/.ndf) × LOG (.ldf) pelo log de erros do SQL Server, agrega evidências autoritativas:
/// páginas suspeitas (msdb.dbo.suspect_pages), saída estruturada do DBCC CHECKDB, topologia de
/// alta disponibilidade, espaço em disco e capacidades da instância.
/// </summary>
public sealed class DiagnosisResult
{
    public string DatabaseName { get; init; } = string.Empty;
    public string StateDesc { get; set; } = string.Empty;

    public bool LogCorruptionSuspected { get; set; }
    public bool DataCorruptionSuspected { get; set; }

    /// <summary>Log de transações cheio (erro 9002): não é corrupção; resolve-se com espaço/backup de log.</summary>
    public bool LogFull { get; set; }

    /// <summary>Erros de E/S físicos (823/824/825) encontrados no log de erros.</summary>
    public bool IoErrors { get; set; }

    public List<string> Evidence { get; } = new();
    public bool HasEvidence => Evidence.Count > 0;

    public ServerInfo? Server { get; set; }
    public TopologyInfo? Topology { get; set; }
    public List<SuspectPageInfo> SuspectPages { get; } = new();
    public CheckDbReport? CheckDb { get; set; }
    public List<DiskSpaceInfo> Disks { get; } = new();

    /// <summary>Toda a corrupção encontrada está em índices não-clusterizados → reparável SEM perda.</summary>
    public bool IndexOnlyRepairPossible { get; set; }

    public RecommendedActionKind RecommendedAction { get; set; } = RecommendedActionKind.Unknown;

    public string Summary { get; set; } = string.Empty;
    public string Recommendation { get; set; } = string.Empty;
}
