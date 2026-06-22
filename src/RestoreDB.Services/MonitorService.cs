using System.IO;
using System.Text;
using RestoreDB.Domain.Constants;
using RestoreDB.Domain.Models;

namespace RestoreDB.Services;

/// <summary>
/// Monitoramento agendado (B5 → fecha o loop do histórico): roda um diagnóstico somente-leitura,
/// compara com a última verificação e, havendo NOVIDADE (corrupção nova, mais violações, etc.),
/// grava um ALERTA em arquivo. Sem dependências externas — o Probe usa o resultado para devolver
/// um código de saída distinto, que o Agendador de Tarefas do Windows usa para disparar um e-mail.
/// </summary>
public sealed class MonitorService
{
    private readonly RepairService _repair;
    private readonly string _alertDir;

    public MonitorService(LogService log, string? alertDir = null)
    {
        _repair = new RepairService(log);
        _alertDir = alertDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RestoreDB", "Alerts");
    }

    /// <summary>Decide se a verificação deve gerar alerta: quando há algo NOVO desde a última vez.</summary>
    public static bool ShouldAlert(DiagnosisResult diag) => diag.NewSinceLastRun.Count > 0;

    public async Task<MonitorResult> RunCheckAsync(
        DatabaseInfo db, string connectionString, IProgress<RepairLogEntry> progress, CancellationToken ct = default)
    {
        var diag = await _repair.DiagnoseAsync(db, connectionString, progress, ct);

        var res = new MonitorResult
        {
            DatabaseName = db.Name,
            HasNewIssue = ShouldAlert(diag),
            CorruptionPresent = (diag.CheckDb is { HasErrors: true }) || diag.DataCorruptionSuspected || diag.LogicalCorruptionSuspected,
            RecommendedAction = diag.RecommendedAction.ToString()
        };
        res.Messages.AddRange(diag.NewSinceLastRun);

        if (res.HasNewIssue)
        {
            res.AlertFilePath = WriteAlert(db.Name, diag);
            res.AlertWritten = res.AlertFilePath is not null;
            Report(progress, db.Name, RepairLogLevel.Warning,
                $"⚠ ALERTA: {diag.NewSinceLastRun.Count} novidade(s) desde a última verificação. Alerta gravado em {res.AlertFilePath}");
        }
        else
        {
            Report(progress, db.Name, RepairLogLevel.Success, "Monitoramento: nada novo desde a última verificação.");
        }
        return res;
    }

    private string? WriteAlert(string db, DiagnosisResult diag)
    {
        try
        {
            Directory.CreateDirectory(_alertDir);
            var path = Path.Combine(_alertDir, $"{AppConstants.SanitizeToken(db)}_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            var sb = new StringBuilder();
            sb.AppendLine($"RestoreDB — ALERTA de monitoramento");
            sb.AppendLine($"Banco: {db}");
            sb.AppendLine($"Data: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Estado: {diag.StateDesc}");
            sb.AppendLine($"Ação recomendada: {diag.RecommendedAction}");
            sb.AppendLine();
            sb.AppendLine("Novidades desde a última verificação:");
            foreach (var n in diag.NewSinceLastRun) sb.AppendLine("  • " + n);
            File.WriteAllText(path, sb.ToString());
            return path;
        }
        catch { return null; }
    }

    private void Report(IProgress<RepairLogEntry> p, string db, RepairLogLevel level, string message)
        => p.Report(new RepairLogEntry { Level = level, Message = message, Database = db });
}
