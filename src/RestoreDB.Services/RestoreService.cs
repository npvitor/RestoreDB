using System.Globalization;
using RestoreDB.DataAccess;
using RestoreDB.Domain.Constants;
using RestoreDB.Domain.Models;

namespace RestoreDB.Services;

/// <summary>
/// Restauração de bancos a partir de backup — o caminho correto para corrupção de DADOS
/// (.mdf/.ndf), onde o reparo com perda de dados apenas descarta o que está corrompido.
/// Cobre: listar conjuntos de um arquivo (HEADERONLY) e seus arquivos lógicos (FILELISTONLY),
/// histórico de backups (msdb), verificação (VERIFYONLY), RESTORE DATABASE (com MOVE/REPLACE/
/// STOPAT), restauração de PÁGINA (page restore) e correção de usuários órfãos pós-restore.
///
/// Todo o T-SQL usa QUOTENAME no nome do banco e parâmetros para caminhos (sem injeção).
/// </summary>
public sealed class RestoreService
{
    private readonly SqlDataAccess _data = new();
    private readonly LogService _log;

    public RestoreService(LogService log) => _log = log;

    // ---- Leitura (somente metadados) ----

    public async Task<List<BackupSetInfo>> ListBackupSetsInFileAsync(string diskPath, string connectionString, CancellationToken ct = default)
    {
        await using var session = await _data.OpenSessionAsync(connectionString, ct);
        return await session.RestoreHeaderOnlyAsync(diskPath, ct);
    }

    public async Task<List<BackupFileInfo>> GetBackupFilesAsync(string diskPath, int position, string connectionString, CancellationToken ct = default)
    {
        await using var session = await _data.OpenSessionAsync(connectionString, ct);
        return await session.RestoreFileListOnlyAsync(diskPath, position, ct);
    }

    public async Task<List<BackupSetInfo>> ListBackupHistoryAsync(string databaseName, string connectionString, CancellationToken ct = default)
    {
        await using var session = await _data.OpenSessionAsync(connectionString, ct);
        return await session.GetBackupHistoryAsync(databaseName, ct);
    }

    // ---- Restauração ----

    /// <summary>
    /// Restaura o banco a partir das peças informadas (full → diferencial → logs). Verifica cada
    /// peça (VERIFYONLY) antes de aplicar e, ao final, tenta corrigir usuários órfãos.
    /// </summary>
    public async Task<RepairResult> RestoreDatabaseAsync(
        RestoreRequest request, string connectionString, IProgress<RepairLogEntry> progress, CancellationToken ct = default)
    {
        var result = new RepairResult();
        var db = request.DatabaseName;
        Report(progress, db, RepairLogLevel.Info, $"════════ Restaurando [{db}] a partir de backup ════════");

        if (request.Pieces.Count == 0)
        {
            result.Status = RepairStatus.Failed;
            result.Message = "Nenhum arquivo de backup informado.";
            Report(progress, db, RepairLogLevel.Error, result.Message);
            return result;
        }

        await using var session = await _data.OpenSessionAsync(connectionString, ct);

        for (var i = 0; i < request.Pieces.Count; i++)
        {
            var piece = request.Pieces[i];
            var isLast = i == request.Pieces.Count - 1;

            // Verificação da mídia antes de aplicar (best-effort).
            var verify = await RunStepAsync(session, db, progress,
                $"Verificando o backup ({Path.GetFileName(piece.DiskPath)}, posição {piece.Position})",
                "RESTORE VERIFYONLY FROM DISK = @p WITH FILE = @f;",
                new Dictionary<string, object?> { ["@p"] = piece.DiskPath, ["@f"] = piece.Position }, ct);
            if (!verify.Success)
                Report(progress, db, RepairLogLevel.Warning, "VERIFYONLY acusou problema na mídia — a restauração será tentada mesmo assim.");

            var (sql, prm) = BuildRestore(request, piece, isFirst: i == 0, isLast: isLast);
            var r = await RunStepAsync(session, db, progress,
                isLast ? "Restaurando e recuperando (WITH RECOVERY)" : "Restaurando (WITH NORECOVERY)", sql, prm, ct);

            if (!r.Success)
            {
                result.Status = RepairStatus.Failed;
                result.Message = $"Falha ao restaurar a peça {i + 1}. Verifique o log de operações.";
                Report(progress, db, RepairLogLevel.Error, result.Message);
                return result;
            }
        }

        var finalState = await session.GetDatabaseStateAsync(db, ct);
        result.FinalStateDesc = finalState;

        if (finalState.Equals("ONLINE", StringComparison.OrdinalIgnoreCase))
        {
            await FixOrphanedUsersAsync(session, db, progress, ct);
            result.Status = RepairStatus.Success;
            result.Message = $"Restauração concluída. Banco ONLINE.";
            Report(progress, db, RepairLogLevel.Success, result.Message);
        }
        else
        {
            result.Status = RepairStatus.Failed;
            result.Message = $"Após a restauração, o banco está {finalState} (esperado ONLINE).";
            Report(progress, db, RepairLogLevel.Warning, result.Message);
        }

        Report(progress, db, RepairLogLevel.Info, $"════════ Fim da restauração de [{db}] ════════");
        return result;
    }

    /// <summary>
    /// Restauração de PÁGINA: recupera apenas as páginas corrompidas indicadas, preservando o
    /// restante do banco. Exige a cadeia full + (diferencial) + logs até o presente e o modelo
    /// de recuperação FULL. <paramref name="pages"/> no formato "fileId:pageId" separados por vírgula.
    /// </summary>
    public async Task<RepairResult> RestorePagesAsync(
        string databaseName, string pages, IReadOnlyList<RestorePiece> pieces,
        string connectionString, IProgress<RepairLogEntry> progress, CancellationToken ct = default)
    {
        var result = new RepairResult();
        Report(progress, databaseName, RepairLogLevel.Info, $"════════ Restauração de página de [{databaseName}] ════════");

        if (pieces.Count == 0 || string.IsNullOrWhiteSpace(pages))
        {
            result.Status = RepairStatus.Failed;
            result.Message = "Informe as páginas (fileId:pageId) e ao menos o backup full.";
            Report(progress, databaseName, RepairLogLevel.Error, result.Message);
            return result;
        }

        await using var session = await _data.OpenSessionAsync(connectionString, ct);

        // 1) RESTORE ... PAGE = '...' FROM DISK = <full> WITH NORECOVERY
        var full = pieces[0];
        var (sql, prm) = BuildPageRestore(databaseName, pages, full);
        var r = await RunStepAsync(session, databaseName, progress,
            $"Restaurando páginas {pages} (full, NORECOVERY)", sql, prm, ct);
        if (!r.Success)
        {
            result.Status = RepairStatus.Failed;
            result.Message = "Falha na restauração de página a partir do backup full.";
            Report(progress, databaseName, RepairLogLevel.Error, result.Message);
            return result;
        }

        // 2) Aplicar logs subsequentes (NORECOVERY) e o último WITH RECOVERY.
        for (var i = 1; i < pieces.Count; i++)
        {
            var isLast = i == pieces.Count - 1;
            var p = pieces[i];
            var withClause = isLast ? "RECOVERY" : "NORECOVERY";
            var stepSql =
                "DECLARE @cmd nvarchar(max);\n" +
                "SET @cmd = N'RESTORE LOG ' + QUOTENAME(@db) + N' FROM DISK = @p WITH FILE = @f, " + withClause + ";';\n" +
                "EXEC sys.sp_executesql @cmd, N'@p nvarchar(4000), @f int', @p=@path, @f=@pos;";
            var lr = await RunStepAsync(session, databaseName, progress,
                $"Aplicando log ({Path.GetFileName(p.DiskPath)}) {withClause}", stepSql,
                new Dictionary<string, object?> { ["@db"] = databaseName, ["@path"] = p.DiskPath, ["@pos"] = p.Position }, ct);
            if (!lr.Success)
            {
                result.Status = RepairStatus.Failed;
                result.Message = "Falha ao aplicar o log na restauração de página.";
                Report(progress, databaseName, RepairLogLevel.Error, result.Message);
                return result;
            }
        }

        var finalState = await session.GetDatabaseStateAsync(databaseName, ct);
        result.FinalStateDesc = finalState;
        result.Status = finalState.Equals("ONLINE", StringComparison.OrdinalIgnoreCase) ? RepairStatus.Success : RepairStatus.Failed;
        result.Message = result.Status == RepairStatus.Success
            ? "Restauração de página concluída. Banco ONLINE."
            : $"Após a restauração de página, o banco está {finalState}.";
        Report(progress, databaseName, result.Status == RepairStatus.Success ? RepairLogLevel.Success : RepairLogLevel.Warning, result.Message);
        return result;
    }

    /// <summary>
    /// Corrige usuários órfãos após um RESTORE: reassocia o usuário do banco ao login do servidor
    /// de mesmo nome (ALTER USER ... WITH LOGIN). Best-effort, não interrompe em caso de falha.
    /// </summary>
    public async Task FixOrphanedUsersAsync(SqlSession session, string databaseName, IProgress<RepairLogEntry> progress, CancellationToken ct)
    {
        Report(progress, databaseName, RepairLogLevel.Info, "Verificando usuários órfãos pós-restauração...");
        // Reassocia automaticamente os usuários cujo login de mesmo nome já existe no servidor.
        // O lote interno roda NO CONTEXTO do banco alvo (EXEC [db].sys.sp_executesql), pois
        // ALTER USER é uma instrução com escopo de banco. Compatível com SQL Server 2008 R2.
        var sql =
            "DECLARE @inner nvarchar(max) = N'\n" +
            "    DECLARE @s nvarchar(max) = N'''';\n" +
            "    SELECT @s = @s + N''ALTER USER '' + QUOTENAME(dp.name) + N'' WITH LOGIN = '' + QUOTENAME(sp.name) + N''; ''\n" +
            "    FROM sys.database_principals dp\n" +
            "    JOIN sys.server_principals sp ON sp.name = dp.name\n" +
            "    WHERE dp.type IN (''S'', ''U'') AND dp.principal_id > 4 AND dp.sid <> sp.sid;\n" +
            "    IF @s <> N'''' EXEC sys.sp_executesql @s;';\n" +
            "DECLARE @proc nvarchar(300) = QUOTENAME(@db) + N'.sys.sp_executesql';\n" +
            "EXEC @proc @inner;";
        var r = await RunStepAsync(session, databaseName, progress, "Reassociando usuários órfãos", sql,
            new Dictionary<string, object?> { ["@db"] = databaseName }, ct);
        Report(progress, databaseName, r.Success ? RepairLogLevel.Success : RepairLogLevel.Warning,
            r.Success ? "Usuários órfãos verificados/corrigidos." : "Não foi possível corrigir usuários órfãos automaticamente (verifique manualmente).");
    }

    // ---- Construção de comandos ----

    private static (string sql, Dictionary<string, object?> prm) BuildRestore(RestoreRequest req, RestorePiece piece, bool isFirst, bool isLast)
    {
        var verb = piece.IsLog ? "RESTORE LOG " : "RESTORE DATABASE ";
        var options = new List<string> { "FILE = @f" };

        // MOVE (realocação de arquivos) — apenas na primeira peça (RESTORE DATABASE).
        if (!piece.IsLog && req.MoveMap.Count > 0)
        {
            foreach (var kv in req.MoveMap)
                options.Add($"MOVE N'{Escape(kv.Key)}' TO N'{Escape(kv.Value)}'");
        }

        if (req.Replace && isFirst && !piece.IsLog)
            options.Add("REPLACE");
        if (req.KeepReplication && isLast)
            options.Add("KEEP_REPLICATION");
        if (req.StopAt.HasValue && piece.IsLog)
            options.Add($"STOPAT = N'{req.StopAt.Value.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)}'");

        options.Add(isLast ? "RECOVERY" : "NORECOVERY");

        var sql =
            "DECLARE @cmd nvarchar(max);\n" +
            "SET @cmd = N'" + verb + "' + QUOTENAME(@db) + N' FROM DISK = @p WITH " + string.Join(", ", options) + ";';\n" +
            "EXEC sys.sp_executesql @cmd, N'@p nvarchar(4000), @f int', @p = @path, @f = @pos;";
        return (sql, new Dictionary<string, object?> { ["@db"] = req.DatabaseName, ["@path"] = piece.DiskPath, ["@pos"] = piece.Position });
    }

    private static (string sql, Dictionary<string, object?> prm) BuildPageRestore(string db, string pages, RestorePiece full)
    {
        // PAGE não pode ser parâmetro; é validado (apenas dígitos, ':' e ',') antes de montar.
        var safePages = SanitizePages(pages);
        var sql =
            "DECLARE @cmd nvarchar(max);\n" +
            "SET @cmd = N'RESTORE DATABASE ' + QUOTENAME(@db) + N' PAGE = ''" + safePages + "'' FROM DISK = @p WITH FILE = @f, NORECOVERY;';\n" +
            "EXEC sys.sp_executesql @cmd, N'@p nvarchar(4000), @f int', @p = @path, @f = @pos;";
        return (sql, new Dictionary<string, object?> { ["@db"] = db, ["@path"] = full.DiskPath, ["@pos"] = full.Position });
    }

    /// <summary>Mantém apenas dígitos, ':' e ',' — formato "fileId:pageId,fileId:pageId".</summary>
    public static string SanitizePages(string pages)
        => new string(pages.Where(c => char.IsDigit(c) || c == ':' || c == ',').ToArray());

    private static string Escape(string s) => s.Replace("'", "''");

    // ---- Execução + log ----

    private async Task<CommandExecutionResult> RunStepAsync(
        SqlSession s, string db, IProgress<RepairLogEntry> p, string description, string sql,
        Dictionary<string, object?> prm, CancellationToken ct)
    {
        Report(p, db, RepairLogLevel.Command, "» " + description);
        var r = await s.ExecuteAsync(description, sql, prm, AppConstants.RepairCommandTimeoutSeconds, ct);
        foreach (var m in r.InfoMessages) Report(p, db, RepairLogLevel.Info, "    " + m);
        foreach (var m in r.ErrorMessages) Report(p, db, RepairLogLevel.Error, "    " + m);
        Report(p, db, r.Success ? RepairLogLevel.Success : RepairLogLevel.Warning,
            $"    → {(r.Success ? "OK" : "Retornou erros / não concluído")}: {description}");
        return r;
    }

    private void Report(IProgress<RepairLogEntry> p, string db, RepairLogLevel level, string message)
    {
        var entry = new RepairLogEntry { Level = level, Message = message, Database = db };
        _log.Write(entry);
        p.Report(entry);
    }
}
