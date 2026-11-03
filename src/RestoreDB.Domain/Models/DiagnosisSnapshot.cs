namespace RestoreDB.Domain.Models;

/// <summary>
/// Instantâneo persistível de um diagnóstico — usado pelo histórico para detecção precoce:
/// comparando o snapshot atual com o anterior, identifica corrupção/violações NOVAS desde a última
/// verificação. Serializado em JSON sob %LOCALAPPDATA%\RestoreDB\History.
/// </summary>
public sealed class DiagnosisSnapshot
{
    public string DatabaseName { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; }
    public string StateDesc { get; set; } = string.Empty;

    public bool PhysicalCorruption { get; set; }
    public bool LogicalCorruption { get; set; }
    public int CheckDbErrors { get; set; }
    public int ForeignKeyViolations { get; set; }
    public int SuspectPages { get; set; }

    /// <summary>Severidade máxima da política de backup no momento (0 = Info).</summary>
    public int MaxPolicySeverity { get; set; }

    public string RecommendedAction { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;

    /// <summary>Cria um snapshot a partir de um diagnóstico concluído.</summary>
    public static DiagnosisSnapshot From(DiagnosisResult d) => new()
    {
        DatabaseName = d.DatabaseName,
        TimestampUtc = DateTime.UtcNow,
        StateDesc = d.StateDesc,
        PhysicalCorruption = d.DataCorruptionSuspected || (d.CheckDb is { HasErrors: true }),
        LogicalCorruption = d.LogicalCorruptionSuspected,
        CheckDbErrors = d.CheckDb?.ErrorCount ?? 0,
        ForeignKeyViolations = d.ForeignKeyViolations.Count,
        SuspectPages = d.SuspectPages.Count,
        MaxPolicySeverity = (int)(d.BackupPolicy?.MaxSeverity ?? FindingSeverity.Info),
        RecommendedAction = d.RecommendedAction.ToString(),
        Summary = d.Summary
    };
}
