using RestoreDB.Domain.Models;

namespace RestoreDB.Services;

/// <summary>
/// Monta a CADEIA de restauração ponto-no-tempo a partir do histórico de backups: escolhe o FULL
/// base, o DIFERENCIAL aplicável e a sequência de LOGs até o instante alvo, validando a
/// continuidade de LSN. Lógica PURA (sem banco) — testável isoladamente.
///
/// Continuidade:
///  • um DIFERENCIAL pertence ao FULL quando seu database_backup_lsn == checkpoint_lsn do full;
///  • um LOG encadeia quando first_lsn ≤ âncora &lt; last_lsn (a âncora avança para last_lsn);
///  • quando os LSNs não estão disponíveis (0), recai para a ordem cronológica.
/// </summary>
public static class BackupChainPlanner
{
    public static BackupChainPlan Plan(string databaseName, IReadOnlyList<BackupSetInfo> history, DateTime? target)
    {
        var plan = new BackupChainPlan { DatabaseName = databaseName, Target = target };

        bool AtOrBeforeTarget(BackupSetInfo b) =>
            target is null || (b.BackupFinishDate ?? DateTime.MinValue) <= target.Value;

        // 1) FULL base: o mais recente que termina até o alvo.
        var baseFull = history.Where(b => b.IsFull && AtOrBeforeTarget(b))
                              .OrderByDescending(b => b.BackupFinishDate ?? DateTime.MinValue)
                              .FirstOrDefault();
        if (baseFull is null)
        {
            plan.Warnings.Add(target is null
                ? "Nenhum backup FULL encontrado no histórico — não há base para restaurar."
                : "Nenhum backup FULL anterior ao instante alvo — não há base para restaurar até esse ponto.");
            plan.IsComplete = false;
            plan.Summary = "Cadeia indisponível: sem FULL base.";
            return plan;
        }

        plan.BaseFull = baseFull;
        plan.Pieces.Add(ToPiece(baseFull, isLog: false));
        var anchor = baseFull.LastLsn;

        // 2) DIFERENCIAL aplicável (opcional): mais recente após o full, ligado a ele por LSN.
        var diff = history.Where(b => b.IsDifferential && AtOrBeforeTarget(b)
                                      && (b.BackupFinishDate ?? DateTime.MinValue) >= (baseFull.BackupFinishDate ?? DateTime.MinValue)
                                      && BelongsTo(b, baseFull))
                          .OrderByDescending(b => b.BackupFinishDate ?? DateTime.MinValue)
                          .FirstOrDefault();
        if (diff is not null)
        {
            plan.Differential = diff;
            plan.Pieces.Add(ToPiece(diff, isLog: false));
            if (diff.LastLsn > anchor) anchor = diff.LastLsn;
        }

        // 3) LOGs: encadeados a partir da âncora, em ordem cronológica, até cobrir o alvo.
        var logs = history.Where(b => b.IsLog)
                          .OrderBy(b => b.LastLsn)
                          .ThenBy(b => b.BackupFinishDate ?? DateTime.MinValue)
                          .ToList();

        var reached = false;
        foreach (var log in logs)
        {
            // Já totalmente contido na âncora atual: ignora.
            if (anchor > 0 && log.LastLsn > 0 && log.LastLsn <= anchor) continue;

            // Começa depois do ponto coberto: quebra de cadeia (faltam transações).
            if (anchor > 0 && log.FirstLsn > 0 && log.FirstLsn > anchor)
            {
                plan.Warnings.Add(
                    "Quebra na cadeia de LOG: faltam backups de log entre o ponto coberto e o próximo " +
                    "disponível. A restauração só consegue ir até o último ponto contíguo.");
                break;
            }

            plan.Logs.Add(log);
            plan.Pieces.Add(ToPiece(log, isLog: true));
            if (log.LastLsn > anchor) anchor = log.LastLsn;

            if (target is not null && (log.BackupFinishDate ?? DateTime.MinValue) >= target.Value)
            {
                reached = true;
                break;
            }
        }

        // Alcance do alvo.
        if (target is null)
        {
            plan.ReachesTarget = true;
        }
        else
        {
            // Também é alcançado se o full/diferencial já passou do alvo (raro) ou um log o cobriu.
            var anchorDate = (plan.Logs.LastOrDefault()?.BackupFinishDate)
                             ?? plan.Differential?.BackupFinishDate ?? baseFull.BackupFinishDate;
            plan.ReachesTarget = reached || (anchorDate ?? DateTime.MinValue) >= target.Value;
            if (!plan.ReachesTarget)
                plan.Warnings.Add("O instante alvo está além do último backup disponível — será restaurado o ponto mais recente possível.");
        }

        var hasGap = plan.Warnings.Any(w => w.Contains("Quebra na cadeia"));
        plan.IsComplete = plan.BaseFull is not null && !hasGap;

        var partes = 1 + (plan.Differential is not null ? 1 : 0) + plan.Logs.Count;
        plan.Summary = plan.IsComplete
            ? $"Cadeia pronta: FULL{(plan.Differential is not null ? " + DIFERENCIAL" : "")} + {plan.Logs.Count} log(s) — {partes} peça(s)."
            : "Cadeia incompleta — ver avisos.";
        return plan;
    }

    /// <summary>Um diferencial/log pertence ao full quando seu database_backup_lsn casa com o
    /// checkpoint_lsn do full. Sem LSNs (0), aceita pela cronologia (validação relaxada).</summary>
    private static bool BelongsTo(BackupSetInfo dependent, BackupSetInfo baseFull)
        => dependent.DatabaseBackupLsn == 0 || baseFull.CheckpointLsn == 0
           || dependent.DatabaseBackupLsn == baseFull.CheckpointLsn;

    private static RestorePiece ToPiece(BackupSetInfo b, bool isLog) =>
        new() { DiskPath = b.PhysicalDevice, Position = b.Position <= 0 ? 1 : b.Position, IsLog = isLog };
}
