namespace RestoreDB.Domain.Models;

/// <summary>Severidade de um achado de diagnóstico/checagem (para classificação e cor no relatório).</summary>
public enum FindingSeverity
{
    Info,
    Low,
    Medium,
    High,
    Critical
}

/// <summary>Um achado da avaliação da política de backup/recuperação.</summary>
public sealed class PolicyFinding
{
    public FindingSeverity Severity { get; init; } = FindingSeverity.Info;
    public string Message { get; init; } = string.Empty;

    public string Display => $"[{Severity.ToString().ToUpperInvariant()}] {Message}";
}

/// <summary>
/// Avaliação da política de backup/recuperação de um banco: cruza o modelo de recuperação com o
/// histórico de backups (msdb) para apontar riscos de RPO — p.ex. FULL sem backups de log
/// (crescimento do log + perda potencial), última full muito antiga ou cadeia interrompida.
/// </summary>
public sealed class BackupPolicyAssessment
{
    public string DatabaseName { get; init; } = string.Empty;
    public string RecoveryModel { get; set; } = string.Empty;

    public DateTime? LastFull { get; set; }
    public DateTime? LastLog { get; set; }

    public List<PolicyFinding> Findings { get; } = new();

    /// <summary>Maior severidade entre os achados (Info quando não há nenhum).</summary>
    public FindingSeverity MaxSeverity =>
        Findings.Count == 0 ? FindingSeverity.Info : Findings.Max(f => f.Severity);

    public bool HasRisk => Findings.Any(f => f.Severity >= FindingSeverity.Medium);

    public string Summary { get; set; } = string.Empty;
}
