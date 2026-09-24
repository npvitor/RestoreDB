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
    FreeDiskSpace,

    /// <summary>Log de transações cheio (9002) ou prestes a encher — NÃO é corrupção: libera-se o espaço
    /// (backup de log, crescimento/arquivo extra) e o banco volta sem perda de dados.</summary>
    ResolveLogFull,

    /// <summary>Arquivo do banco ausente, sem permissão ou preso por outro processo — NÃO é corrupção:
    /// aponta-se o novo caminho / concede-se acesso e o banco é religado sem perda de dados.</summary>
    FixFileAccess,

    /// <summary>Integridade LÓGICA comprometida (FK órfã / violação de constraint) com os
    /// arquivos físicos íntegros — corrige-se removendo as linhas violadoras, não com CHECKDB.</summary>
    FixConstraints
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

    /// <summary>
    /// Corrupção LÓGICA (integridade referencial): há linhas violando chaves estrangeiras
    /// ou CHECK constraints — detectada por DBCC CHECKCONSTRAINTS, não pelo CHECKDB.
    /// </summary>
    public bool LogicalCorruptionSuspected { get; set; }

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

    /// <summary>Violações de integridade lógica encontradas por DBCC CHECKCONSTRAINTS.</summary>
    public List<ConstraintViolation> ConstraintViolations { get; } = new();

    /// <summary>Nomes de constraints marcadas como NÃO confiáveis (is_not_trusted) — indício de
    /// integridade lógica que nunca foi validada (causa comum de FK órfã silenciosa).</summary>
    public List<string> UntrustedConstraints { get; } = new();

    /// <summary>Mensagens de erro de DBCC CHECKCATALOG (consistência de metadados), quando houver.</summary>
    public List<string> CatalogErrors { get; } = new();

    /// <summary>Avaliação da política de backup/recuperação (RPO): FULL sem log, full antiga, etc.</summary>
    public BackupPolicyAssessment? BackupPolicy { get; set; }

    /// <summary>Banco criptografado (TDE) — restaurar em outra instância exige o certificado.</summary>
    public bool IsEncrypted { get; set; }

    /// <summary>Operação demorada em andamento (recuperação/RESTORE) — para não parecer travado.</summary>
    public RecoveryProgress? Recovery { get; set; }

    /// <summary>Achados de SAÚDE LÓGICA adicionais (índices desabilitados, dependências quebradas, etc.).</summary>
    public List<PolicyFinding> HealthFindings { get; } = new();

    /// <summary>Achados de CAUSA-RAIZ (erros de E/S, corrupção recorrente ⇒ suspeita de hardware).</summary>
    public List<PolicyFinding> RootCauseFindings { get; } = new();

    /// <summary>Novidades em relação ao último diagnóstico (ex.: corrupção nova) — detecção precoce.</summary>
    public List<string> NewSinceLastRun { get; } = new();

    /// <summary>Situação do log de transações (uso, motivo de espera, limites dos arquivos).</summary>
    public LogSpaceInfo? LogSpace { get; set; }

    /// <summary>Arquivos que o SQL Server não consegue abrir (ausentes, sem permissão, presos).</summary>
    public List<FileAccessProblem> FileProblems { get; } = new();

    /// <summary>Linhas que violam constraints DESABILITADAS (informativo — não são corrupção).</summary>
    public int DisabledConstraintHits { get; set; }

    /// <summary>Linhas violadoras de FK que podem ser removidas para restaurar a integridade.</summary>
    public IReadOnlyList<ConstraintViolation> ForeignKeyViolations =>
        ConstraintViolations.Where(c => c.IsForeignKey).ToList();

    /// <summary>Toda a corrupção encontrada está em índices não-clusterizados → reparável SEM perda.</summary>
    public bool IndexOnlyRepairPossible { get; set; }

    public RecommendedActionKind RecommendedAction { get; set; } = RecommendedActionKind.Unknown;

    public string Summary { get; set; } = string.Empty;
    public string Recommendation { get; set; } = string.Empty;
}
