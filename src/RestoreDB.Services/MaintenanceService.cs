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

        var ok = true;
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
            ok = ok && r.Success;
        }

        // Atualizar estatísticas (sempre, mesmo sem fragmentação relevante).
        var statsSql =
            "DECLARE @proc nvarchar(300) = QUOTENAME(@db) + N'.sys.sp_executesql';\n" +
            "EXEC @proc N'EXEC sys.sp_updatestats;';";
        var stats = await session.ExecuteAsync("Atualizando estatísticas (sp_updatestats)", statsSql,
            new Dictionary<string, object?> { ["@db"] = db.Name }, AppConstants.RepairCommandTimeoutSeconds, ct);
        Report(progress, db.Name, stats.Success ? RepairLogLevel.Success : RepairLogLevel.Warning,
            stats.Success ? "Estatísticas atualizadas." : "Não foi possível atualizar todas as estatísticas.");

        result.Status = ok && stats.Success ? RepairStatus.Success : RepairStatus.Failed;
        result.Message = $"Manutenção concluída: {targets.Count} índice(s) tratado(s), estatísticas atualizadas.";
        Report(progress, db.Name, RepairLogLevel.Success, result.Message);
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
