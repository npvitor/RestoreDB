using RestoreDB.Domain.Models;

namespace RestoreDB.Services;

/// <summary>
/// Avalia a política de backup/recuperação cruzando o modelo de recuperação com o histórico de
/// backups (msdb). Aponta riscos de RPO: ausência de FULL, modelo FULL sem backups de LOG
/// (crescimento do log + janela de perda), última FULL antiga, etc. Lógica PURA (testável).
/// </summary>
public static class BackupPolicyEvaluator
{
    public const int FullStaleWarningDays = 7;
    public const int FullStaleHighDays = 30;

    public static BackupPolicyAssessment Evaluate(
        string databaseName, string recoveryModel, IReadOnlyList<BackupSetInfo> history, DateTime now)
    {
        var a = new BackupPolicyAssessment { DatabaseName = databaseName, RecoveryModel = recoveryModel };

        a.LastFull = history.Where(b => b.IsFull).Max(b => b.BackupFinishDate);
        a.LastDifferential = history.Where(b => b.IsDifferential).Max(b => b.BackupFinishDate);
        a.LastLog = history.Where(b => b.IsLog).Max(b => b.BackupFinishDate);

        var model = (recoveryModel ?? string.Empty).Trim().ToUpperInvariant();
        var isFullModel = model is "FULL" or "BULK_LOGGED";

        if (a.LastFull is null)
        {
            a.Findings.Add(new PolicyFinding
            {
                Severity = FindingSeverity.Critical,
                Message = "Sem backup FULL no histórico: o banco não pode ser restaurado de backup. Faça um BACKUP DATABASE o quanto antes."
            });
        }
        else
        {
            var age = now - a.LastFull.Value;
            if (age.TotalDays >= FullStaleHighDays)
                a.Findings.Add(new PolicyFinding { Severity = FindingSeverity.High,
                    Message = $"Último backup FULL há {age.Days} dias — muito antigo. O ponto de recuperação está desatualizado." });
            else if (age.TotalDays >= FullStaleWarningDays)
                a.Findings.Add(new PolicyFinding { Severity = FindingSeverity.Medium,
                    Message = $"Último backup FULL há {age.Days} dias. Considere uma rotina de FULL mais frequente." });
        }

        if (isFullModel)
        {
            if (a.LastLog is null)
                a.Findings.Add(new PolicyFinding { Severity = FindingSeverity.High,
                    Message = $"Modelo de recuperação {model} SEM backups de LOG: o log de transações cresce indefinidamente e " +
                              "a perda potencial é de tudo desde a última FULL/diferencial. Configure BACKUP LOG periódico (ou mude para SIMPLE se não precisar de ponto-no-tempo)." });
        }
        else if (model == "SIMPLE")
        {
            a.Findings.Add(new PolicyFinding { Severity = FindingSeverity.Info,
                Message = "Modelo SIMPLE: não há backup de log nem restauração ponto-no-tempo. A recuperação vai até a última FULL/diferencial." });
        }

        a.Summary = a.Findings.Count == 0
            ? "Política de backup adequada."
            : $"{a.Findings.Count} ponto(s) de atenção na política de backup (severidade máxima: {a.MaxSeverity}).";
        return a;
    }
}
