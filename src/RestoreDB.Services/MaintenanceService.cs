using RestoreDB.DataAccess;
using RestoreDB.Domain.Constants;
using RestoreDB.Domain.Models;

namespace RestoreDB.Services;

/// <summary>
/// Manutenção PREVENTIVA (não-destrutiva): desfragmenta índices conforme a fragmentação
/// (10–30% ⇒ REORGANIZE; &gt; 30% ⇒ REBUILD) e atualiza estatísticas. Complementa o foco em
/// emergência da ferramenta, tornando-a também de manutenção do dia a dia.
/// </summary>
public sealed class MaintenanceService
{
    private readonly SqlDataAccess _data = new();
    private readonly LogService _log;

    public MaintenanceService(LogService log) => _log = log;

    public async Task<List<IndexFragmentation>> GetFragmentationAsync(
        string databaseName, string connectionString, int minPages = 100, CancellationToken ct = default)
    {
        await using var session = await _data.OpenSessionAsync(connectionString, ct);
        return await session.GetFragmentationAsync(databaseName, minPages, ct);
    }

    public async Task<RepairResult> RunMaintenanceAsync(
        DatabaseInfo db, string connectionString, IProgress<RepairLogEntry> progress, CancellationToken ct = default)
    {
        var result = new RepairResult();
        Report(progress, db.Name, RepairLogLevel.Info, $"════════ Manutenção preventiva de [{db.Name}] ════════");

        if (AppConstants.IsSystemDatabase(db.Name))
        {
            result.Status = RepairStatus.Failed;
            result.Message = "Manutenção automática não disponível para bancos de sistema.";
            Report(progress, db.Name, RepairLogLevel.Error, result.Message);
            return result;
        }

        await using var session = await _data.OpenSessionAsync(connectionString, ct);

        var frags = await session.GetFragmentationAsync(db.Name, minPages: 100, ct);
        var targets = frags.Where(f => f.NeedsMaintenance).ToList();
        Report(progress, db.Name, RepairLogLevel.Info,
            $"{targets.Count} índice(s) precisam de manutenção (de {frags.Count} avaliado(s)).");

        var failed = 0;
        foreach (var f in targets)
        {
            var op = f.RecommendedAction; // REBUILD ou REORGANIZE
            var inner = $"ALTER INDEX {Quote(f.Index)} ON {f.Table} {op};";
            var sql =
                "DECLARE @proc nvarchar(300) = QUOTENAME(@db) + N'.sys.sp_executesql';\n" +
                "EXEC @proc @stmt;";
            var r = await session.ExecuteAsync($"{op} {f.Table}/{f.Index}", sql,
                new Dictionary<string, object?> { ["@db"] = db.Name, ["@stmt"] = inner }, AppConstants.RepairCommandTimeoutSeconds, ct);
            Report(progress, db.Name, r.Success ? RepairLogLevel.Success : RepairLogLevel.Warning,
                $"  {(r.Success ? "✔" : "⚠")} {op} {f.Table}/{f.Index} ({f.FragmentationPercent:0.#}%)");
            if (!r.Success) failed++;
        }

        // Atualizar estatísticas (sempre, mesmo sem fragmentação relevante).
        var statsSql =
            "DECLARE @proc nvarchar(300) = QUOTENAME(@db) + N'.sys.sp_executesql';\n" +
            "EXEC @proc N'EXEC sys.sp_updatestats;';";
        var stats = await session.ExecuteAsync("Atualizando estatísticas (sp_updatestats)", statsSql,
            new Dictionary<string, object?> { ["@db"] = db.Name }, AppConstants.RepairCommandTimeoutSeconds, ct);
        Report(progress, db.Name, stats.Success ? RepairLogLevel.Success : RepairLogLevel.Warning,
            stats.Success ? "Estatísticas atualizadas." : "Não foi possível atualizar todas as estatísticas.");

        result.Status = failed == 0 && stats.Success ? RepairStatus.Success : RepairStatus.Failed;
        result.Message = Summary(targets.Count, failed, stats.Success);
        Report(progress, db.Name, result.Status == RepairStatus.Success ? RepairLogLevel.Success : RepairLogLevel.Warning, result.Message);
        return result;
    }

    /// <summary>Resumo da manutenção — nunca diz "concluída" se algum índice ou as estatísticas falharam.</summary>
    internal static string Summary(int indexes, int failed, bool statsOk)
        => failed == 0 && statsOk
            ? $"Manutenção concluída: {indexes} índice(s) tratado(s), estatísticas atualizadas."
            : $"Manutenção INCOMPLETA: {indexes - failed} de {indexes} índice(s) tratado(s)" +
              (statsOk ? ", estatísticas atualizadas." : "; as estatísticas NÃO foram atualizadas.") +
              " Veja o registro de operações.";

    /// <summary>
    /// Corrige os CONTADORES de espaço/linhas do catálogo (DBCC UPDATEUSAGE) — relatórios de tamanho e
    /// sp_spaceused errados, típico de bancos vindos do SQL 2000/2005. Só ajusta metadados; não toca em dados.
    /// </summary>
    public async Task<RepairResult> UpdateUsageAsync(
        DatabaseInfo db, string connectionString, IProgress<RepairLogEntry> progress, CancellationToken ct = default)
    {
        Report(progress, db.Name, RepairLogLevel.Info, $"Corrigindo os contadores de espaço de [{db.Name}] (DBCC UPDATEUSAGE)...");
        await using var session = await _data.OpenSessionAsync(connectionString, ct);
        var sql =
            "DECLARE @c nvarchar(max) = N'DBCC UPDATEUSAGE (' + QUOTENAME(@db) + N') WITH COUNT_ROWS;';\n" +
            "EXEC sys.sp_executesql @c;";
        var r = await session.ExecuteAsync("DBCC UPDATEUSAGE", sql,
            new Dictionary<string, object?> { ["@db"] = db.Name }, AppConstants.RepairCommandTimeoutSeconds, ct);
        var fixedCount = r.InfoMessages.Count(m => m.Contains("UPDATEUSAGE", StringComparison.OrdinalIgnoreCase)
                                                 || m.Contains("Usage counts", StringComparison.OrdinalIgnoreCase)
                                                 || m.Contains("contagens", StringComparison.OrdinalIgnoreCase));
        foreach (var m in r.ErrorMessages) Report(progress, db.Name, RepairLogLevel.Warning, "    " + m);
        var result = new RepairResult
        {
            Status = r.Success ? RepairStatus.Success : RepairStatus.Failed,
            FinalStateDesc = db.StateDesc,
            Message = r.Success
                ? (fixedCount > 0 ? $"Contadores de espaço corrigidos ({fixedCount} ajuste(s) no catálogo)." : "Contadores de espaço conferidos: já estavam corretos.")
                : "Não foi possível corrigir os contadores de espaço (veja o registro)."
        };
        Report(progress, db.Name, r.Success ? RepairLogLevel.Success : RepairLogLevel.Warning, result.Message);
        return result;
    }

    private static string Quote(string ident) => "[" + ident.Replace("]", "]]") + "]";

    private void Report(IProgress<RepairLogEntry> p, string db, RepairLogLevel level, string message)
    {
        var entry = new RepairLogEntry { Level = level, Message = message, Database = db };
        _log.Write(entry);
        p.Report(entry);
    }
}
