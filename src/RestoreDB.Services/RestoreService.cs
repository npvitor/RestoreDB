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
        await using var session = await _data.OpenSessionAsync(connectionString, ct);
        return await RestoreDatabaseAsync(session, request, progress, ct);
    }

    /// <summary>
    /// Núcleo da restauração, na sessão do CHAMADOR. Importante: quem tomou o acesso exclusivo
    /// (SINGLE_USER + tail-log NORECOVERY) precisa executar o RESTORE na MESMA conexão — de outra
    /// conexão o SQL Server recusa com a Msg 3101 (a vaga de usuário único pertence à primeira).
    /// </summary>
    private async Task<RepairResult> RestoreDatabaseAsync(
        SqlSession session, RestoreRequest request, IProgress<RepairLogEntry> progress, CancellationToken ct)
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

        // Realocação automática de arquivos (WITH MOVE) quando solicitado e sem mapa explícito:
        // arquivos cuja pasta original não existe no servidor são movidos para a pasta padrão.
        if (request.AutoRelocate && request.MoveMap.Count == 0)
            await ApplyAutoRelocateAsync(session, request, progress, ct);

        // Sobrescrever um banco ONLINE com conexões abertas (a aplicação de faturamento, por exemplo)
        // falha com a Msg 3101 ("Exclusive access could not be obtained"). O acesso exclusivo é tomado
        // no MESMO lote do primeiro RESTORE (ver BuildRestore) — sem janela para outro processo ocupar
        // a vaga SINGLE_USER entre um comando e outro.
        var takeExclusive = request.Replace
            && (await session.GetDatabaseStateAsync(db, ct)).Equals("ONLINE", StringComparison.OrdinalIgnoreCase);
        if (takeExclusive)
            Report(progress, db, RepairLogLevel.Info,
                "O banco está ONLINE: as conexões abertas serão encerradas (SINGLE_USER WITH ROLLBACK IMMEDIATE) no início da restauração.");

        for (var i = 0; i < request.Pieces.Count; i++)
        {
            var piece = request.Pieces[i];
            var isLast = i == request.Pieces.Count - 1;

            // Verificação da mídia antes de aplicar (best-effort) — todos os arquivos (striped).
            var (vfrom, vprm) = DeviceFrom(piece.AllDevices);
            vprm["@f"] = piece.Position;
            var verify = await RunStepAsync(session, db, progress,
                $"Verificando o backup ({Path.GetFileName(piece.DiskPath)}, posição {piece.Position})",
                $"RESTORE VERIFYONLY FROM {vfrom} WITH FILE = @f;", vprm, ct);
            if (!verify.Success)
                Report(progress, db, RepairLogLevel.Warning, "VERIFYONLY acusou problema na mídia — a restauração será tentada mesmo assim.");

            var (sql, prm) = BuildRestore(request, piece, isFirst: i == 0, isLast: isLast);
            if (i == 0 && takeExclusive)
                sql = ExclusiveAccessPrefix + sql;
            var r = await RunStepAsync(session, db, progress,
                isLast ? "Restaurando e recuperando (WITH RECOVERY)" : "Restaurando (WITH NORECOVERY)", sql, prm, ct);

            if (!r.Success)
            {
                result.Status = RepairStatus.Failed;
                result.Message = $"Falha ao restaurar a peça {i + 1}. Verifique o log de operações.";
                Report(progress, db, RepairLogLevel.Error, result.Message);
                // Se a primeira peça falhou, o banco original continua ONLINE — mas em SINGLE_USER.
                if (i == 0 && takeExclusive) await EnsureMultiUserAsync(session, db, progress);
                return result;
            }
        }

        var finalState = await session.GetDatabaseStateAsync(db, ct);
        result.FinalStateDesc = finalState;

        if (finalState.Equals("ONLINE", StringComparison.OrdinalIgnoreCase))
        {
            // O tail-log capturado depois do SINGLE_USER carrega essa opção: sem isto o banco
            // restaurado poderia voltar em acesso restrito.
            await EnsureMultiUserAsync(session, db, progress);
            await FixOrphanedUsersAsync(session, db, progress, ct);
            await session.ClearSuspectPagesAsync(db, ct);
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

    /// <summary>Resolve o WITH MOVE automaticamente a partir do FILELISTONLY do primeiro backup.</summary>
    private async Task ApplyAutoRelocateAsync(SqlSession session, RestoreRequest request, IProgress<RepairLogEntry> progress, CancellationToken ct, bool forceRelocate = false)
    {
        var first = request.Pieces[0];
        var files = await session.RestoreFileListOnlyAsync(first.DiskPath, first.Position, ct);
        if (files.Count == 0) return;

        var dataDir = await session.GetDefaultDataDirAsync(ct);
        var logDir = await session.GetDefaultLogDirAsync(ct);

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in files.Select(f => Path.GetDirectoryName(f.PhysicalName) ?? string.Empty)
                                  .Where(d => d.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            if (await session.PathExistsOnServerAsync(dir, ct)) existing.Add(dir);

        var map = RestorePlanner.BuildMoveMap(files, existing, dataDir, logDir, request.DatabaseName, forceRelocate);
        foreach (var kv in map) request.MoveMap[kv.Key] = kv.Value;

        if (map.Count > 0)
            Report(progress, request.DatabaseName, RepairLogLevel.Info,
                $"Realocação automática de {map.Count} arquivo(s) para a pasta padrão da instância.");
    }

    /// <summary>
    /// SALVAMENTO (restore-to-copy): restaura a cadeia mais recente de <paramref name="originalDb"/>
    /// para um banco de CÓPIA (<paramref name="copyName"/>), com arquivos realocados, SEM tocar no
    /// original. Permite extrair dados de um backup quando o banco vivo está corrompido. Não
    /// sobrescreve um banco já existente de mesmo nome.
    /// </summary>
    public async Task<RepairResult> RestoreAsCopyAsync(
        string originalDb, string? copyName, string connectionString, IProgress<RepairLogEntry> progress, CancellationToken ct = default,
        bool baseFullOnly = false, string? explicitFullBackupPath = null,
        DateTime? stopAt = null, Action<BackupChainPlan>? onPlanned = null)
    {
        var result = new RepairResult();
        var copy = string.IsNullOrWhiteSpace(copyName) ? originalDb + "_Recuperado" : copyName!;
        Report(progress, originalDb, RepairLogLevel.Info, $"════════ Recuperar para cópia: [{originalDb}] → [{copy}] (não altera o original) ════════");

        await using var session = await _data.OpenSessionAsync(connectionString, ct);

        if (!(await session.GetDatabaseStateAsync(copy, ct)).Equals("DESCONHECIDO", StringComparison.OrdinalIgnoreCase))
        {
            result.Status = RepairStatus.Failed;
            result.Message = $"Já existe um banco chamado [{copy}]. Escolha outro nome para a cópia (o original não foi tocado).";
            Report(progress, originalDb, RepairLogLevel.Error, result.Message);
            return result;
        }

        BackupChainPlan plan;
        if (!string.IsNullOrWhiteSpace(explicitFullBackupPath))
        {
            // O usuário escolheu um .bak específico (ex.: guardado em outra mídia) — usa o FULL DESSE
            // arquivo, ignorando o histórico do msdb. O SQL Server precisa conseguir LER esse caminho.
            Report(progress, originalDb, RepairLogLevel.Info, $"Usando o backup escolhido pelo usuário: {explicitFullBackupPath}");
            plan = await BuildFullOnlyPlanFromFileAsync(session, originalDb, explicitFullBackupPath!, ct);
        }
        else
        {
            // Recuperação de registros-pai (baseFullOnly): só o FULL agendado mais recente, sem logs — para
            // não reproduzir a própria perda do pai na cópia (ver BackupChainPlanner.PlanBaseFullOnly).
            plan = await PlanAvailableAsync(session, originalDb, h => baseFullOnly
                ? BackupChainPlanner.PlanBaseFullOnly(originalDb, h)
                : BackupChainPlanner.Plan(originalDb, h, target: stopAt), ct);
        }
        foreach (var w in plan.Warnings) Report(progress, originalDb, RepairLogLevel.Warning, "  ⚠ " + w);
        if (!plan.IsComplete || plan.BaseFull is null)
        {
            result.Status = RepairStatus.Failed;
            result.Message = "Sem cadeia de backup utilizável para criar a cópia. " + plan.Summary;
            Report(progress, originalDb, RepairLogLevel.Error, result.Message);
            return result;
        }

        onPlanned?.Invoke(plan);

        var request = new RestoreRequest
        {
            DatabaseName = copy,
            Replace = true,
            StopAt = stopAt,
            Pieces = new List<RestorePiece>(plan.Pieces)
        };
        // Força realocar TODOS os arquivos (com nomes novos) para nunca colidir com os do original.
        await ApplyAutoRelocateAsync(session, request, progress, ct, forceRelocate: true);

        // A cópia foi criada AGORA (não existia): se a restauração falhar ou for cancelada no meio, ela fica em
        // RESTORING, inútil — e bloquearia a próxima tentativa com o mesmo nome ("Já existe um banco...").
        var restore = new RepairResult { Status = RepairStatus.Failed };
        try
        {
            restore = await RestoreDatabaseAsync(session, request, progress, ct);
        }
        finally
        {
            if (restore.Status != RepairStatus.Success && !await session.DropDatabaseBestEffortAsync(copy, CancellationToken.None))
                Report(progress, originalDb, RepairLogLevel.Warning, $"Não foi possível descartar a cópia incompleta [{copy}] — remova-a manualmente.");
        }
        if (restore.Status == RepairStatus.Success)
        {
            restore.Message = $"Cópia [{copy}] criada e ONLINE. Use-a para extrair/salvar dados sem afetar [{originalDb}].";
            Report(progress, originalDb, RepairLogLevel.Success, restore.Message);
        }
        return restore;
    }

    /// <summary>
    /// Monta um plano "somente FULL" a partir de UM arquivo de backup escolhido pelo usuário: lê o
    /// cabeçalho (RESTORE HEADERONLY) e usa o backup COMPLETO mais recente contido no arquivo. Não
    /// aplica logs (evita reproduzir a perda do pai). Falha de forma clara se o arquivo for ilegível
    /// pelo servidor ou não contiver um FULL.
    /// </summary>
    private async Task<BackupChainPlan> BuildFullOnlyPlanFromFileAsync(SqlSession session, string db, string path, CancellationToken ct)
    {
        var plan = new BackupChainPlan { DatabaseName = db };
        List<BackupSetInfo> sets;
        try
        {
            sets = await session.RestoreHeaderOnlyAsync(path, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            plan.Warnings.Add($"Não foi possível ler o arquivo de backup escolhido '{path}' (o SQL Server precisa ter acesso a ele): {ex.Message}");
            plan.IsComplete = false;
            plan.Summary = "Arquivo de backup escolhido ilegível.";
            return plan;
        }

        var full = sets.Where(s => s.IsFull).OrderByDescending(s => s.BackupFinishDate ?? DateTime.MinValue).FirstOrDefault();
        if (full is null)
        {
            plan.Warnings.Add($"O arquivo escolhido '{path}' não contém um backup COMPLETO (FULL).");
            plan.IsComplete = false;
            plan.Summary = "Sem FULL no arquivo escolhido.";
            return plan;
        }

        plan.BaseFull = full;
        plan.Pieces.Add(new RestorePiece { DiskPath = path, Position = full.Position <= 0 ? 1 : full.Position, IsLog = false });
        plan.IsComplete = true;
        plan.ReachesTarget = true;
        plan.Summary = $"Recuperação de registros-pai: FULL do arquivo escolhido ({Path.GetFileName(path)}).";
        return plan;
    }

    /// <summary>
    /// Anexa um banco a partir de arquivos físicos (.mdf [+ .ldf]). Sem o .ldf (ou com
    /// <paramref name="rebuildLog"/>), recria o log via FOR ATTACH_REBUILD_LOG. Não sobrescreve um
    /// banco existente de mesmo nome. Ao final, corrige usuários órfãos.
    /// </summary>
    public async Task<RepairResult> AttachDatabaseAsync(
        string dbName, string mdfPath, string? ldfPath, bool rebuildLog,
        string connectionString, IProgress<RepairLogEntry> progress, CancellationToken ct = default)
    {
        var result = new RepairResult();
        Report(progress, dbName, RepairLogLevel.Info, $"════════ Anexando [{dbName}] a partir de arquivo ════════");

        if (string.IsNullOrWhiteSpace(mdfPath))
        {
            result.Status = RepairStatus.Failed;
            result.Message = "Informe o arquivo de dados (.mdf).";
            Report(progress, dbName, RepairLogLevel.Error, result.Message);
            return result;
        }

        await using var session = await _data.OpenSessionAsync(connectionString, ct);

        if (!(await session.GetDatabaseStateAsync(dbName, ct)).Equals("DESCONHECIDO", StringComparison.OrdinalIgnoreCase))
        {
            result.Status = RepairStatus.Failed;
            result.Message = $"Já existe um banco chamado [{dbName}]. Escolha outro nome.";
            Report(progress, dbName, RepairLogLevel.Error, result.Message);
            return result;
        }

        var withLog = !rebuildLog && !string.IsNullOrWhiteSpace(ldfPath);
        var fileList = new List<string> { $"(FILENAME = N'{Escape(mdfPath)}')" };
        if (withLog) fileList.Add($"(FILENAME = N'{Escape(ldfPath!)}')");
        var clauseLiteral = string.Join(", ", fileList).Replace("'", "''");
        var forClause = withLog ? "FOR ATTACH" : "FOR ATTACH_REBUILD_LOG";

        var sql =
            "DECLARE @cmd nvarchar(max);\n" +
            "SET @cmd = N'CREATE DATABASE ' + QUOTENAME(@n) + N' ON " + clauseLiteral + " " + forClause + ";';\n" +
            "EXEC sys.sp_executesql @cmd;";
        var r = await RunStepAsync(session, dbName, progress,
            withLog ? "Anexando (FOR ATTACH)" : "Anexando e recriando o log (FOR ATTACH_REBUILD_LOG)", sql,
            new Dictionary<string, object?> { ["@n"] = dbName }, ct);

        var state = await session.GetDatabaseStateAsync(dbName, ct);
        result.FinalStateDesc = state;
        if (r.Success && state.Equals("ONLINE", StringComparison.OrdinalIgnoreCase))
        {
            await FixOrphanedUsersAsync(session, dbName, progress, ct);
            result.Status = RepairStatus.Success;
            result.Message = $"Banco [{dbName}] anexado e ONLINE.";
            Report(progress, dbName, RepairLogLevel.Success, result.Message);
        }
        else
        {
            result.Status = RepairStatus.Failed;
            result.Message = $"Não foi possível anexar [{dbName}]. Estado: {state}. Verifique os caminhos e as permissões da conta de serviço do SQL.";
            Report(progress, dbName, RepairLogLevel.Error, result.Message);
        }
        return result;
    }

    /// <summary>Lista as tabelas de usuário de um banco (para o assistente de salvamento).</summary>
    public async Task<List<string>> ListUserTablesAsync(string databaseName, string connectionString, CancellationToken ct = default)
    {
        await using var session = await _data.OpenSessionAsync(connectionString, ct);
        return await session.ListUserTablesAsync(databaseName, ct);
    }

    /// <summary>
    /// Lista transações DESTRUTIVAS recentes (DELETE/DROP/TRUNCATE) do log — para investigar uma
    /// exclusão acidental e, em seguida, restaurar até LOGO ANTES dela com
    /// <see cref="RestoreToPointInTimeAsync"/> (target = início da transação menos um instante).
    /// </summary>
    public async Task<List<ForensicTransaction>> ListRecentDestructiveTransactionsAsync(
        string databaseName, string connectionString, CancellationToken ct = default)
    {
        await using var session = await _data.OpenSessionAsync(connectionString, ct);
        return await session.GetRecentDestructiveTransactionsAsync(databaseName, max: 50, ct);
    }

    /// <summary>
    /// Desfaz uma EXCLUSÃO ACIDENTAL sem sobrescrever o banco: restaura uma CÓPIA da cadeia de backups até
    /// <paramref name="target"/> (logo antes do DELETE — inclui um backup de log COPY_ONLY tirado agora,
    /// para alcançar transações recentes sem mexer na cadeia) e devolve ao banco VIVO só as linhas que
    /// faltam nas tabelas indicadas (por chave primária, gatilhos desligados). Tudo o que foi feito DEPOIS
    /// da exclusão continua lá — ao contrário de restaurar o banco inteiro até aquele instante.
    /// A cópia é descartada se todas as tabelas forem recuperadas; senão fica disponível para o assistente
    /// de salvamento (nome em <see cref="RepairResult.BackupFile"/>).
    /// </summary>
    public async Task<RepairResult> RecoverDeletedRowsAsync(
        string databaseName, DateTime target, IReadOnlyList<string> tables, string connectionString,
        IProgress<RepairLogEntry> progress, CancellationToken ct = default)
    {
        var result = new RepairResult();
        Report(progress, databaseName, RepairLogLevel.Info,
            $"════════ Recuperar linhas excluídas de [{databaseName}] (estado de {target:yyyy-MM-dd HH:mm:ss}) — sem sobrescrever o banco ════════");

        if (tables.Count == 0)
        {
            result.Status = RepairStatus.Failed;
            result.Message = "Nenhuma tabela informada para recuperar.";
            Report(progress, databaseName, RepairLogLevel.Error, result.Message);
            return result;
        }

        await using var session = await _data.OpenSessionAsync(connectionString, ct);
        var model = await session.GetRecoveryModelAsync(databaseName, ct);
        if (model.Equals("SIMPLE", StringComparison.OrdinalIgnoreCase))
        {
            result.Status = RepairStatus.Failed;
            result.Message = "O banco está no modelo SIMPLE: não há backups de log para voltar a um instante exato. " +
                             "Use 'Recuperar para cópia' (último backup) + 'Assistente de salvamento'.";
            Report(progress, databaseName, RepairLogLevel.Error, result.Message);
            return result;
        }

        // Log COPY_ONLY: leva a cópia até transações que ainda não estão em nenhum backup, SEM truncar o
        // log nem alterar a cadeia de backups agendada.
        var logFile = $"{AppConstants.SanitizeToken(databaseName, "banco")}_undo_delete_{DateTime.Now:yyyyMMdd_HHmmss}.trn";
        await RunStepAsync(session, databaseName, progress, "Backup de log COPY_ONLY (alcança as transações mais recentes)",
            "DECLARE @cmd nvarchar(max) = N'BACKUP LOG ' + QUOTENAME(@db) + N' TO DISK = @f WITH COPY_ONLY, INIT, NAME = @n;';\n" +
            "EXEC sys.sp_executesql @cmd, N'@f nvarchar(4000), @n nvarchar(256)', @f = @file, @n = @name;",
            new Dictionary<string, object?> { ["@db"] = databaseName, ["@file"] = logFile, ["@name"] = $"RestoreDB log (recuperar exclusão) de {databaseName}" }, ct);

        var copy = $"{AppConstants.SanitizeToken(databaseName, "banco")}_AntesExclusao_{DateTime.Now:yyyyMMddHHmmss}";
        var restore = await RestoreAsCopyAsync(databaseName, copy, connectionString, progress, ct, stopAt: target);
        if (restore.Status != RepairStatus.Success)
        {
            await session.DropDatabaseBestEffortAsync(copy, CancellationToken.None);
            result.Status = RepairStatus.Failed;
            result.Message = "Não foi possível montar a cópia de antes da exclusão: " + restore.Message;
            Report(progress, databaseName, RepairLogLevel.Error, result.Message);
            return result;
        }

        var total = 0;
        var failed = new List<string>();
        try
        {
            foreach (var t in tables.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var n = await session.SalvageTableAsync(copy, databaseName, t, ct);
                    total += n;
                    Report(progress, databaseName, RepairLogLevel.Success, $"  ✔ {t}: {n} linha(s) devolvida(s).");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    failed.Add(t);
                    Report(progress, databaseName, RepairLogLevel.Warning, $"  ⚠ {t}: {ex.Message}");
                }
            }
        }
        catch
        {
            // Cancelado no meio: ninguém recebe o nome da cópia para o assistente de salvamento — não a deixa para trás.
            await session.DropDatabaseBestEffortAsync(copy, CancellationToken.None);
            throw;
        }

        if (failed.Count == 0)
        {
            await session.DropDatabaseBestEffortAsync(copy, CancellationToken.None);
            result.Status = RepairStatus.Success;
            result.Message = $"{total} linha(s) excluída(s) devolvida(s) ao banco, sem sobrescrever o restante (nada duplicado).";
        }
        else
        {
            result.Status = total > 0 ? RepairStatus.Success : RepairStatus.Failed;
            result.BackupFile = copy;
            result.Message = $"{total} linha(s) devolvida(s). {failed.Count} tabela(s) não puderam ser tratadas automaticamente " +
                             $"({string.Join(", ", failed)}); a cópia [{copy}] foi mantida para o assistente de salvamento.";
        }
        Report(progress, databaseName, result.Status == RepairStatus.Success ? RepairLogLevel.Success : RepairLogLevel.Warning, result.Message);
        return result;
    }

    /// <summary>
    /// Copia de um banco de CÓPIA (recuperado) para o banco VIVO apenas as linhas que faltam (por
    /// chave primária) de uma tabela — o "assistente de salvamento". Não duplica registros.
    /// </summary>
    public async Task<RepairResult> SalvageTableAsync(
        string fromDb, string toDb, string schemaTable, string connectionString, IProgress<RepairLogEntry> progress, CancellationToken ct = default)
    {
        var result = new RepairResult();
        Report(progress, toDb, RepairLogLevel.Info, $"Salvamento de dados: copiando linhas faltantes de [{fromDb}].{schemaTable} → [{toDb}].{schemaTable}...");
        try
        {
            await using var session = await _data.OpenSessionAsync(connectionString, ct);
            var rows = await session.SalvageTableAsync(fromDb, toDb, schemaTable, ct);
            result.Status = RepairStatus.Success;
            result.Message = $"{rows} linha(s) copiada(s) para [{toDb}].{schemaTable} (registros que faltavam).";
            Report(progress, toDb, RepairLogLevel.Success, result.Message);
        }
        catch (Exception ex)
        {
            result.Status = RepairStatus.Failed;
            result.Message = "Não foi possível salvar a tabela: " + ex.Message;
            Report(progress, toDb, RepairLogLevel.Error, result.Message);
        }
        return result;
    }

    /// <summary>
    /// Verifica a SAÚDE dos backups recentes do banco: roda RESTORE VERIFYONLY ... WITH CHECKSUM em
    /// cada conjunto do histórico, comprovando que a mídia é legível e consistente (sem restaurar).
    /// </summary>
    public async Task<List<BackupHealthItem>> VerifyBackupHealthAsync(
        string databaseName, string connectionString, IProgress<RepairLogEntry> progress, CancellationToken ct = default, int max = 10)
    {
        var items = new List<BackupHealthItem>();
        Report(progress, databaseName, RepairLogLevel.Info, $"════════ Saúde dos backups de [{databaseName}] (RESTORE VERIFYONLY WITH CHECKSUM) ════════");

        await using var session = await _data.OpenSessionAsync(connectionString, ct);
        var history = (await session.GetBackupHistoryAsync(databaseName, ct)).Take(max).ToList();
        if (history.Count == 0)
        {
            Report(progress, databaseName, RepairLogLevel.Warning, "Sem histórico de backups para verificar.");
            return items;
        }

        foreach (var set in history)
        {
            var label = $"VERIFYONLY {set.BackupTypeFriendly} ({Path.GetFileName(set.PhysicalDevice)}, pos {set.Position})";
            var (from, prm) = DeviceFrom(set.AllDevices);
            prm["@f"] = set.Position;

            // Preferimos validar os page-checksums (WITH CHECKSUM); se a mídia não tiver checksums
            // (backup feito sem CHECKSUM — comum), recai para o VERIFYONLY simples, que ainda
            // comprova que o backup está completo e legível.
            var withChecksum = await RunStepAsync(session, databaseName, progress, label + " [CHECKSUM]",
                $"RESTORE VERIFYONLY FROM {from} WITH FILE = @f, CHECKSUM;", prm, ct);

            bool verified;
            string message;
            if (withChecksum.Success)
            {
                verified = true;
                message = "Íntegro (verificado com CHECKSUM).";
            }
            else
            {
                var plain = await RunStepAsync(session, databaseName, progress, label,
                    $"RESTORE VERIFYONLY FROM {from} WITH FILE = @f;", prm, ct);
                verified = plain.Success;
                message = plain.Success
                    ? "Íntegro (verificado; mídia sem page-checksum)."
                    : "FALHOU na verificação — backup possivelmente danificado.";
            }

            items.Add(new BackupHealthItem { Set = set, Verified = verified, Message = message });
        }

        var ok = items.Count(i => i.Verified);
        Report(progress, databaseName, ok == items.Count ? RepairLogLevel.Success : RepairLogLevel.Warning,
            $"Saúde dos backups: {ok}/{items.Count} verificado(s) com sucesso.");
        return items;
    }

    /// <summary>
    /// Restauração PONTO-NO-TEMPO com cadeia automática: monta (FULL → DIFERENCIAL → LOGs) a partir
    /// do histórico (msdb) até <paramref name="target"/> (ou o ponto mais recente, se nulo), validando
    /// a continuidade de LSN. Se a cadeia estiver quebrada/incompleta, ABORTA antes de executar.
    /// Opcionalmente captura o tail-log (transações mais recentes) antes de sobrescrever o banco.
    /// </summary>
    public async Task<RepairResult> RestoreToPointInTimeAsync(
        string databaseName, DateTime? target, bool tailLogBackup,
        string connectionString, IProgress<RepairLogEntry> progress, CancellationToken ct = default)
    {
        var result = new RepairResult();
        var alvo = target.HasValue ? target.Value.ToString("yyyy-MM-dd HH:mm:ss") : "ponto mais recente";
        Report(progress, databaseName, RepairLogLevel.Info,
            $"════════ Restauração ponto-no-tempo de [{databaseName}] → {alvo} ════════");

        await using var session = await _data.OpenSessionAsync(connectionString, ct);

        var history = await session.GetBackupHistoryAsync(databaseName, ct);
        if (history.Count == 0)
        {
            result.Status = RepairStatus.Failed;
            result.Message = "Sem histórico de backups em msdb para montar a cadeia. Use 'Escolher arquivo .bak' para restaurar manualmente.";
            Report(progress, databaseName, RepairLogLevel.Error, result.Message);
            return result;
        }

        var plan = await PlanAvailableAsync(session, databaseName, h => BackupChainPlanner.Plan(databaseName, h, target), ct);
        foreach (var w in plan.Warnings)
            Report(progress, databaseName, RepairLogLevel.Warning, "  ⚠ " + w);

        if (!plan.IsComplete || plan.BaseFull is null)
        {
            result.Status = RepairStatus.Failed;
            result.Message = "Cadeia de backup INCOMPLETA — restauração abortada para não deixar o banco inconsistente. " + plan.Summary;
            Report(progress, databaseName, RepairLogLevel.Error, result.Message);
            return result;
        }
        Report(progress, databaseName, RepairLogLevel.Info, plan.Summary);

        // Tail-log (best-effort): captura as transações ainda não copiadas, minimizando a perda. É
        // SEMPRE capturado (o REPLACE destruiria o log atual), mas só entra na cadeia quando o alvo está
        // além dos backups existentes: aplicar um log depois de o STOPAT já ter sido atingido faz o
        // RESTORE falhar e deixaria o banco em RESTORING.
        var pieces = new List<RestorePiece>(plan.Pieces);
        try
        {
            if (tailLogBackup)
            {
                var tail = await TryBackupTailLogAsync(session, databaseName, progress, ct);
                if (tail is not null && (target is null || !plan.ReachesTarget))
                    pieces.Add(tail);
                else if (tail is not null)
                    Report(progress, databaseName, RepairLogLevel.Info,
                        $"O instante alvo já é coberto pelos backups existentes — o tail-log ({tail.DiskPath}) fica guardado, mas não é aplicado.");
            }

            var request = new RestoreRequest
            {
                DatabaseName = databaseName,
                Replace = true,
                StopAt = target,
                Pieces = pieces
            };
            return await RestoreDatabaseAsync(session, request, progress, ct);
        }
        catch
        {
            // O tail-log toma o acesso exclusivo (SINGLE_USER) ANTES do BACKUP LOG: se ele falhou e a operação foi
            // cancelada (ou caiu) antes da restauração, o banco continua ONLINE, mas preso a uma única conexão.
            await EnsureMultiUserAsync(session, databaseName, progress);
            throw;
        }
    }

    /// <summary>
    /// Restaura a cadeia mais recente deixando o banco em STANDBY (somente leitura) em vez de
    /// finalizar — permite CONSULTAR os dados para achar o ponto certo antes de aplicar a recuperação
    /// definitiva (FinalizeRecoveryAsync). O arquivo de undo é criado na pasta padrão da instância.
    /// </summary>
    public async Task<RepairResult> RestoreToStandbyAsync(
        string databaseName, DateTime? target, string connectionString, IProgress<RepairLogEntry> progress, CancellationToken ct = default)
    {
        var result = new RepairResult();
        Report(progress, databaseName, RepairLogLevel.Info, $"════════ Restauração em STANDBY (somente leitura) de [{databaseName}] ════════");

        await using var session = await _data.OpenSessionAsync(connectionString, ct);

        var plan = await PlanAvailableAsync(session, databaseName, h => BackupChainPlanner.Plan(databaseName, h, target), ct);
        foreach (var w in plan.Warnings) Report(progress, databaseName, RepairLogLevel.Warning, "  ⚠ " + w);
        if (!plan.IsComplete || plan.BaseFull is null)
        {
            result.Status = RepairStatus.Failed;
            result.Message = "Cadeia de backup incompleta — restauração em STANDBY abortada. " + plan.Summary;
            Report(progress, databaseName, RepairLogLevel.Error, result.Message);
            return result;
        }

        var dataDir = await session.GetDefaultDataDirAsync(ct);
        var undo = Path.Combine(string.IsNullOrEmpty(dataDir) ? "." : dataDir,
            $"{AppConstants.SanitizeToken(databaseName, "banco")}_undo_{DateTime.Now:yyyyMMdd_HHmmss}.bak");

        var request = new RestoreRequest
        {
            DatabaseName = databaseName,
            Replace = true,
            StopAt = target,
            StandbyUndoFile = undo,
            AutoRelocate = true,
            Pieces = new List<RestorePiece>(plan.Pieces)
        };

        var restore = await RestoreDatabaseAsync(session, request, progress, ct);
        // Em STANDBY o estado fica STANDBY/RESTORING (não ONLINE) — isso é o esperado aqui.
        var state = await session.GetDatabaseStateAsync(databaseName, ct);
        result.FinalStateDesc = state;
        if (restore.Status == RepairStatus.Failed)
            return restore;

        result.Status = RepairStatus.Success;
        result.Message = $"Banco em STANDBY (somente leitura) — consulte os dados e finalize com 'Finalizar recuperação'. Estado: {state}.";
        Report(progress, databaseName, RepairLogLevel.Success, result.Message);
        return result;
    }

    /// <summary>Finaliza a recuperação de um banco que está em STANDBY/RESTORING: RESTORE ... WITH RECOVERY.</summary>
    public async Task<RepairResult> FinalizeRecoveryAsync(
        string databaseName, string connectionString, IProgress<RepairLogEntry> progress, CancellationToken ct = default)
    {
        var result = new RepairResult();
        Report(progress, databaseName, RepairLogLevel.Info, $"Finalizando a recuperação de [{databaseName}] (WITH RECOVERY)...");
        await using var session = await _data.OpenSessionAsync(connectionString, ct);

        const string sql =
            "DECLARE @cmd nvarchar(max) = N'RESTORE DATABASE ' + QUOTENAME(@db) + N' WITH RECOVERY;';\n" +
            "EXEC sys.sp_executesql @cmd;";
        var r = await RunStepAsync(session, databaseName, progress, "RESTORE WITH RECOVERY", sql,
            new Dictionary<string, object?> { ["@db"] = databaseName }, ct);

        var state = await session.GetDatabaseStateAsync(databaseName, ct);
        result.FinalStateDesc = state;
        var ok = state.Equals("ONLINE", StringComparison.OrdinalIgnoreCase);
        result.Status = ok ? RepairStatus.Success : RepairStatus.Failed;
        result.Message = ok ? "Recuperação finalizada — banco ONLINE."
                            : $"Após finalizar, o banco está {state}." + (r.Success ? "" : " O comando retornou erros.");
        Report(progress, databaseName, ok ? RepairLogLevel.Success : RepairLogLevel.Warning, result.Message);
        return result;
    }

    /// <summary>
    /// Captura o tail-log (BACKUP LOG ... WITH NORECOVERY) para preservar as transações mais
    /// recentes antes de uma restauração. Best-effort: em modelo SIMPLE ou sem log, devolve null.
    /// NO_TRUNCATE permite capturar mesmo com os arquivos de DADOS danificados/offline (basta o
    /// .ldf legível) e CONTINUE_AFTER_ERROR tolera dano no próprio log.
    /// </summary>
    private async Task<RestorePiece?> TryBackupTailLogAsync(SqlSession session, string db, IProgress<RepairLogEntry> p, CancellationToken ct)
    {
        var model = await session.GetRecoveryModelAsync(db, ct);
        if (model.Equals("SIMPLE", StringComparison.OrdinalIgnoreCase))
        {
            Report(p, db, RepairLogLevel.Info, "Tail-log não se aplica (modelo de recuperação SIMPLE).");
            return null;
        }

        var file = $"{AppConstants.SanitizeToken(db, "banco")}_tail_{DateTime.Now:yyyyMMdd_HHmmss}.trn";
        var comp = await session.SupportsBackupCompressionAsync(ct) ? ", COMPRESSION" : string.Empty;
        // WITH NORECOVERY exige acesso exclusivo (Msg 3101 com a aplicação conectada): encerra as
        // conexões no mesmo lote do BACKUP LOG.
        var sql =
            ExclusiveAccessPrefix +
            "DECLARE @cmd nvarchar(max);\n" +
            "SET @cmd = N'BACKUP LOG ' + QUOTENAME(@db) + N' TO DISK = @f WITH NORECOVERY, NO_TRUNCATE, CONTINUE_AFTER_ERROR, INIT" + comp + ", NAME = @n;';\n" +
            "EXEC sys.sp_executesql @cmd, N'@f nvarchar(4000), @n nvarchar(256)', @f = @file, @n = @name;";
        var r = await RunStepAsync(session, db, p, "Capturando o tail-log (BACKUP LOG ... WITH NORECOVERY)", sql,
            new Dictionary<string, object?> { ["@db"] = db, ["@file"] = file, ["@name"] = $"RestoreDB tail-log de {db}" }, ct);
        if (!r.Success)
        {
            Report(p, db, RepairLogLevel.Warning, "Não foi possível capturar o tail-log — a restauração prosseguirá sem as transações mais recentes.");
            return null;
        }
        Report(p, db, RepairLogLevel.Success, $"Tail-log capturado: {file}");
        return new RestorePiece { DiskPath = file, Position = 1, IsLog = true };
    }

    /// <summary>
    /// TESTA a restaurabilidade de um backup SEM tocar no banco de produção: restaura a cadeia para
    /// um banco DESCARTÁVEL (com MOVE para a pasta do tempdb), roda DBCC CHECKDB e descarta o banco
    /// de teste ao final (mesmo em caso de falha). Prova que o backup é íntegro e restaurável.
    /// </summary>
    public async Task<RepairResult> TestRestoreAsync(
        string databaseName, IReadOnlyList<RestorePiece> pieces,
        string connectionString, IProgress<RepairLogEntry> progress, CancellationToken ct = default)
    {
        var result = new RepairResult();
        var scratch = $"{AppConstants.SanitizeToken(databaseName, "banco")}_RestoreDB_TEST_{DateTime.Now:yyyyMMddHHmmss}";
        Report(progress, databaseName, RepairLogLevel.Info,
            $"════════ Teste de restauração (não altera [{databaseName}]) → destino temporário [{scratch}] ════════");

        if (pieces.Count == 0)
        {
            result.Status = RepairStatus.Failed;
            result.Message = "Informe ao menos o backup full para testar.";
            Report(progress, databaseName, RepairLogLevel.Error, result.Message);
            return result;
        }

        await using var session = await _data.OpenSessionAsync(connectionString, ct);

        var dir = await session.GetScratchDataDirAsync(ct);
        if (string.IsNullOrEmpty(dir))
        {
            result.Status = RepairStatus.Failed;
            result.Message = "Não foi possível determinar uma pasta temporária no servidor para o teste.";
            Report(progress, databaseName, RepairLogLevel.Error, result.Message);
            return result;
        }

        // Mapeia os arquivos lógicos do full para destinos temporários (WITH MOVE).
        var files = await session.RestoreFileListOnlyAsync(pieces[0].DiskPath, pieces[0].Position, ct);
        if (files.Count == 0)
        {
            result.Status = RepairStatus.Failed;
            result.Message = "Não foi possível ler a lista de arquivos do backup (FILELISTONLY).";
            Report(progress, databaseName, RepairLogLevel.Error, result.Message);
            return result;
        }

        var move = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dataIdx = 0;
        foreach (var f in files)
        {
            var isLog = f.Type.Equals("L", StringComparison.OrdinalIgnoreCase);
            var ext = isLog ? ".ldf" : (dataIdx++ == 0 ? ".mdf" : ".ndf");
            move[f.LogicalName] = Path.Combine(dir, $"{scratch}_{move.Count}{ext}");
        }

        var request = new RestoreRequest
        {
            DatabaseName = scratch,
            Replace = true,
            MoveMap = move,
            Pieces = new List<RestorePiece>(pieces)
        };

        try
        {
            var restore = await RestoreDatabaseAsync(session, request, progress, ct);
            if (restore.Status != RepairStatus.Success)
            {
                result.Status = RepairStatus.Failed;
                result.Message = "O backup NÃO é restaurável: " + restore.Message;
                Report(progress, databaseName, RepairLogLevel.Error, result.Message);
                return result;
            }

            Report(progress, databaseName, RepairLogLevel.Info, "Restaurado no destino de teste — verificando consistência (DBCC CHECKDB)...");
            var check = await session.CheckDbWithResultsAsync(scratch, repairOption: null, ct);
            var ok = check.Executed && !check.HasErrors;

            result.Status = ok ? RepairStatus.Success : RepairStatus.Failed;
            result.Message = ok
                ? "Backup VÁLIDO: restaurou com sucesso e o DBCC CHECKDB não encontrou erros."
                : $"Backup restaurável, mas o DBCC CHECKDB acusou {check.ErrorCount} erro(s) no conteúdo — o backup contém corrupção.";
            Report(progress, databaseName, ok ? RepairLogLevel.Success : RepairLogLevel.Warning, result.Message);
            return result;
        }
        finally
        {
            await DropScratchDatabaseAsync(session, scratch, progress);
        }
    }

    /// <summary>Descarta o banco de teste (liberando também os arquivos movidos). Best-effort.</summary>
    private async Task DropScratchDatabaseAsync(SqlSession session, string scratch, IProgress<RepairLogEntry> progress)
    {
        var ok = await session.DropDatabaseBestEffortAsync(scratch, CancellationToken.None);
        Report(progress, scratch, ok ? RepairLogLevel.Info : RepairLogLevel.Warning,
            ok ? $"Banco de teste [{scratch}] descartado." : $"Não foi possível descartar [{scratch}] — remova-o manualmente.");
    }

    /// <summary>
    /// Restauração de PÁGINA montando a cadeia AUTOMATICAMENTE a partir do histórico (msdb): usa o
    /// último FULL + toda a cadeia de LOGs e, por fim, captura um BACKUP LOG novo — DEPOIS de restaurar
    /// as páginas — aplicado WITH RECOVERY para concluir. Exige modelo FULL/BULK_LOGGED.
    /// <paramref name="pages"/> no formato "fileId:pageId" separados por vírgula.
    /// </summary>
    public async Task<RepairResult> RestorePagesFromHistoryAsync(
        string databaseName, string pages, string connectionString, IProgress<RepairLogEntry> progress, CancellationToken ct = default)
    {
        var result = new RepairResult();
        Report(progress, databaseName, RepairLogLevel.Info, $"════════ Restauração de página (cadeia automática) de [{databaseName}] ════════");

        if (string.IsNullOrWhiteSpace(pages) || RestoreService.SanitizePages(pages).Length == 0)
        {
            result.Status = RepairStatus.Failed;
            result.Message = "Informe as páginas corrompidas no formato fileId:pageId (ex.: 1:57,1:58).";
            Report(progress, databaseName, RepairLogLevel.Error, result.Message);
            return result;
        }

        await using var session = await _data.OpenSessionAsync(connectionString, ct);

        var model = await session.GetRecoveryModelAsync(databaseName, ct);
        if (model.Equals("SIMPLE", StringComparison.OrdinalIgnoreCase))
        {
            result.Status = RepairStatus.Failed;
            result.Message = "A restauração de página exige o modelo de recuperação FULL ou BULK_LOGGED. Em SIMPLE, restaure o banco inteiro de backup.";
            Report(progress, databaseName, RepairLogLevel.Error, result.Message);
            return result;
        }

        // Cadeia validada por LSN (FULL → DIFERENCIAL → LOGs). O diferencial encurta a fila de
        // logs a aplicar — em cadeias longas, é a diferença entre minutos e horas.
        var plan = await PlanAvailableAsync(session, databaseName, h => BackupChainPlanner.Plan(databaseName, h, target: null), ct);
        foreach (var w in plan.Warnings)
            Report(progress, databaseName, RepairLogLevel.Warning, "  ⚠ " + w);
        if (!plan.IsComplete || plan.BaseFull is null)
        {
            result.Status = RepairStatus.Failed;
            result.Message = "Cadeia de backup incompleta — a restauração de página exige FULL + logs contíguos até o presente. " + plan.Summary;
            Report(progress, databaseName, RepairLogLevel.Error, result.Message);
            return result;
        }
        Report(progress, databaseName, RepairLogLevel.Info, plan.Summary);

        return await RestorePagesAsync(databaseName, pages, plan.Pieces, connectionString, progress, ct);
    }

    /// <summary>
    /// BACKUP LOG normal (sem NORECOVERY) para um arquivo novo — último elo da restauração de página. Precisa
    /// ser tirado DEPOIS do RESTORE ... PAGE: só um log que cobre o instante da restauração leva as páginas
    /// até o presente; um log anterior a ela deixa as páginas em RestorePending (Msg 829 ao lê-las).
    /// </summary>
    private async Task<RestorePiece?> BackupCurrentLogAsync(SqlSession session, string db, IProgress<RepairLogEntry> p, CancellationToken ct)
    {
        var file = $"{AppConstants.SanitizeToken(db, "banco")}_pagelog_{DateTime.Now:yyyyMMdd_HHmmss}.trn";
        var comp = await session.SupportsBackupCompressionAsync(ct) ? ", COMPRESSION" : string.Empty;
        var sql =
            "DECLARE @cmd nvarchar(max);\n" +
            "SET @cmd = N'BACKUP LOG ' + QUOTENAME(@db) + N' TO DISK = @f WITH INIT" + comp + ", NAME = @n;';\n" +
            "EXEC sys.sp_executesql @cmd, N'@f nvarchar(4000), @n nvarchar(256)', @f = @file, @n = @name;";
        var r = await RunStepAsync(session, db, p, "Backup de log atual (para concluir a restauração de página)", sql,
            new Dictionary<string, object?> { ["@db"] = db, ["@file"] = file, ["@name"] = $"RestoreDB page-restore log de {db}" }, ct);
        return r.Success ? new RestorePiece { DiskPath = file, Position = 1, IsLog = true } : null;
    }


    /// <summary>
    /// Restauração de PÁGINA: recupera apenas as páginas corrompidas indicadas, preservando o
    /// restante do banco. <paramref name="pieces"/> é a cadeia full + (diferencial) + logs, toda aplicada
    /// WITH NORECOVERY; em seguida um BACKUP LOG novo é tirado e aplicado WITH RECOVERY (é ele que traz as
    /// páginas até o presente). Exige o modelo de recuperação FULL. <paramref name="pages"/> no formato
    /// "fileId:pageId" separados por vírgula.
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

        // 2) Aplicar as peças subsequentes: diferencial com RESTORE DATABASE (o engine aplica só
        //    as páginas em restauração), logs com RESTORE LOG — todas NORECOVERY.
        for (var i = 1; i < pieces.Count; i++)
        {
            if (!(await ApplyPagePieceAsync(session, databaseName, pieces[i], recovery: false, progress, ct)).Success)
            {
                result.Status = RepairStatus.Failed;
                result.Message = $"Falha ao aplicar {(pieces[i].IsLog ? "o log" : "o diferencial")} na restauração de página.";
                Report(progress, databaseName, RepairLogLevel.Error, result.Message);
                return result;
            }
        }

        // 3) Último elo: um BACKUP LOG tirado AGORA (depois do RESTORE ... PAGE), aplicado WITH RECOVERY.
        var tail = await BackupCurrentLogAsync(session, databaseName, progress, ct);
        var last = tail is null ? null : await ApplyPagePieceAsync(session, databaseName, tail, recovery: true, progress, ct);

        // Msg 4303 ("additional roll forward ... is required"): o log não alcançou o presente e as páginas
        // continuam RestorePending — o banco aparece ONLINE, mas ler essas páginas dá erro 829.
        var incomplete = last is null || !last.Success || last.InfoMessages.Any(m => m.StartsWith("Msg 4303,", StringComparison.Ordinal));
        var finalState = await session.GetDatabaseStateAsync(databaseName, ct);
        result.FinalStateDesc = finalState;
        result.Status = !incomplete && finalState.Equals("ONLINE", StringComparison.OrdinalIgnoreCase) ? RepairStatus.Success : RepairStatus.Failed;
        result.Message = result.Status == RepairStatus.Success
            ? "Restauração de página concluída. Banco ONLINE."
            : incomplete
                ? "A restauração de página NÃO foi concluída: as páginas ficaram aguardando o último backup de log (RestorePending). " +
                  "Faça um BACKUP LOG e aplique-o WITH RECOVERY, ou acione o DBA."
                : $"Após a restauração de página, o banco está {finalState}.";
        Report(progress, databaseName, result.Status == RepairStatus.Success ? RepairLogLevel.Success : RepairLogLevel.Warning, result.Message);
        return result;
    }

    /// <summary>Aplica um diferencial (RESTORE DATABASE) ou log (RESTORE LOG) de uma restauração de página.</summary>
    private Task<CommandExecutionResult> ApplyPagePieceAsync(
        SqlSession session, string databaseName, RestorePiece p, bool recovery, IProgress<RepairLogEntry> progress, CancellationToken ct)
    {
        var withClause = recovery ? "RECOVERY" : "NORECOVERY";
        var verb = p.IsLog ? "RESTORE LOG " : "RESTORE DATABASE ";
        var (from, stepPrm) = DeviceFrom(p.AllDevices.Count > 0 ? p.AllDevices : new List<string> { p.DiskPath });
        var paramDecl = "@f int" + string.Concat(Enumerable.Range(0, stepPrm.Count).Select(j => $", @p{j} nvarchar(4000)"));
        var paramPass = "@f = @pos" + string.Concat(Enumerable.Range(0, stepPrm.Count).Select(j => $", @p{j} = @p{j}"));
        stepPrm["@db"] = databaseName;
        stepPrm["@pos"] = p.Position;
        var stepSql =
            "DECLARE @cmd nvarchar(max);\n" +
            "SET @cmd = N'" + verb + "' + QUOTENAME(@db) + N' FROM " + from + " WITH FILE = @f, " + withClause + ";';\n" +
            "EXEC sys.sp_executesql @cmd, N'" + paramDecl + "', " + paramPass + ";";
        return RunStepAsync(session, databaseName, progress,
            $"Aplicando {(p.IsLog ? "log" : "diferencial")} ({Path.GetFileName(p.DiskPath)}) {withClause}", stepSql, stepPrm, ct);
    }

    /// <summary>
    /// Corrige usuários órfãos após um RESTORE: reassocia o usuário do banco ao login do servidor
    /// de mesmo nome (ALTER USER ... WITH LOGIN). Best-effort, não interrompe em caso de falha.
    /// </summary>
    private async Task FixOrphanedUsersAsync(SqlSession session, string databaseName, IProgress<RepairLogEntry> progress, CancellationToken ct)
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

    /// <summary>
    /// Prefixo T-SQL que encerra as conexões de um banco ONLINE (SINGLE_USER WITH ROLLBACK IMMEDIATE),
    /// para ser concatenado ao MESMO lote do comando que exige exclusividade (RESTORE / tail-log
    /// NORECOVERY). Usa o parâmetro @db do comando seguinte. Em outros estados não faz nada.
    /// </summary>
    internal const string ExclusiveAccessPrefix =
        "IF EXISTS (SELECT 1 FROM sys.databases WHERE name = @db AND state_desc = N'ONLINE')\n" +
        "BEGIN\n" +
        "  DECLARE @excl nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@db) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE;';\n" +
        "  EXEC sys.sp_executesql @excl;\n" +
        "END;\n";

    /// <summary>Devolve o banco a MULTI_USER se ele estiver ONLINE em acesso restrito. Best-effort.</summary>
    private async Task EnsureMultiUserAsync(SqlSession session, string db, IProgress<RepairLogEntry> progress)
    {
        const string sql =
            "IF EXISTS (SELECT 1 FROM sys.databases WHERE name = @db AND state_desc = N'ONLINE' AND user_access_desc <> N'MULTI_USER')\n" +
            "BEGIN\n" +
            "  DECLARE @cmd nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@db) + N' SET MULTI_USER WITH ROLLBACK IMMEDIATE;';\n" +
            "  EXEC sys.sp_executesql @cmd;\n" +
            "END";
        var r = await session.ExecuteAsync("Garantir acesso normal (MULTI_USER)", sql,
            new Dictionary<string, object?> { ["@db"] = db }, AppConstants.RepairCommandTimeoutSeconds, CancellationToken.None);
        if (!r.Success)
            Report(progress, db, RepairLogLevel.Warning, "Não foi possível devolver o banco a MULTI_USER automaticamente — verifique.");
    }

    /// <summary>
    /// Monta o plano de restauração a partir do histórico do msdb DESCARTANDO os conjuntos cujos
    /// arquivos não existem mais no servidor (limpeza de retenção, cópia de simulação apagada, mídia
    /// removida...). Sem isto, o plano escolheria um backup inexistente e a restauração só falharia no
    /// meio — possivelmente depois de o tail-log já ter deixado o banco em RESTORING. Um arquivo que não
    /// pôde ser verificado (sem permissão, compartilhamento inacessível à sonda) é mantido.
    /// </summary>
    public async Task<BackupChainPlan> PlanAvailableAsync(
        SqlSession session, string databaseName, Func<IReadOnlyList<BackupSetInfo>, BackupChainPlan> planner, CancellationToken ct = default)
    {
        var pool = await session.GetBackupHistoryAsync(databaseName, ct);
        var exists = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
        var notes = new List<string>();

        for (var attempt = 0; ; attempt++)
        {
            var plan = planner(pool);
            var used = new List<BackupSetInfo>();
            if (plan.BaseFull is not null) used.Add(plan.BaseFull);
            if (plan.Differential is not null) used.Add(plan.Differential);
            used.AddRange(plan.Logs);

            var missing = new List<BackupSetInfo>();
            foreach (var set in used)
            {
                foreach (var device in set.AllDevices)
                {
                    if (!exists.TryGetValue(device, out var e))
                        exists[device] = e = await session.FileExistsOnServerAsync(device, ct);
                    if (e == false) { missing.Add(set); break; }
                }
            }

            if (missing.Count == 0 || attempt >= 30)
            {
                plan.Warnings.InsertRange(0, notes);
                return plan;
            }

            foreach (var m in missing)
            {
                pool.Remove(m);
                notes.Add($"Backup {m.BackupTypeFriendly} de {m.BackupFinishDate:yyyy-MM-dd HH:mm} ignorado: o arquivo " +
                          $"'{m.AllDevices.FirstOrDefault()}' não existe mais no servidor.");
            }
        }
    }

    // ---- Construção de comandos ----

    private static (string sql, Dictionary<string, object?> prm) BuildRestore(RestoreRequest req, RestorePiece piece, bool isFirst, bool isLast)
    {
        var verb = piece.IsLog ? "RESTORE LOG " : "RESTORE DATABASE ";
        var prm = new Dictionary<string, object?> { ["@db"] = req.DatabaseName, ["@f"] = piece.Position };

        // FROM DISK = @p0, DISK = @p1, ... — suporta backup dividido em vários arquivos (striped).
        var devices = piece.AllDevices.Count > 0 ? piece.AllDevices : new List<string> { piece.DiskPath };
        var diskClauses = new List<string>();
        for (var i = 0; i < devices.Count; i++)
        {
            diskClauses.Add($"DISK = @p{i}");
            prm[$"@p{i}"] = devices[i];
        }
        var fromClause = string.Join(", ", diskClauses);

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

        // Estado final da última peça: STANDBY (somente leitura entre logs) ou RECOVERY; senão NORECOVERY.
        if (isLast && !string.IsNullOrWhiteSpace(req.StandbyUndoFile))
            options.Add($"STANDBY = N'{Escape(req.StandbyUndoFile!)}'");
        else
            options.Add(isLast ? "RECOVERY" : "NORECOVERY");

        // As opções (MOVE/STOPAT/STANDBY) podem conter aspas simples (delimitadores N'...' e caminhos).
        // Como o conjunto inteiro é embutido num literal externo N'...', TODAS as aspas simples
        // precisam ser duplicadas mais uma vez — senão um caminho como 'C:\' quebra o comando.
        var optionsLiteral = string.Join(", ", options).Replace("'", "''");

        var paramDecl = "@f int" + string.Concat(devices.Select((_, i) => $", @p{i} nvarchar(4000)"));
        var paramPass = "@f = @f" + string.Concat(devices.Select((_, i) => $", @p{i} = @p{i}"));

        var sql =
            "DECLARE @cmd nvarchar(max);\n" +
            "SET @cmd = N'" + verb + "' + QUOTENAME(@db) + N' FROM " + fromClause + " WITH " + optionsLiteral + ";';\n" +
            "EXEC sys.sp_executesql @cmd, N'" + paramDecl + "', " + paramPass + ";";
        return (sql, prm);
    }

    private static (string sql, Dictionary<string, object?> prm) BuildPageRestore(string db, string pages, RestorePiece full)
    {
        // PAGE não pode ser parâmetro; é validado (apenas dígitos, ':' e ',') antes de montar.
        var safePages = SanitizePages(pages);
        var (from, prm) = DeviceFrom(full.AllDevices.Count > 0 ? full.AllDevices : new List<string> { full.DiskPath });
        var paramDecl = "@f int" + string.Concat(Enumerable.Range(0, prm.Count).Select(j => $", @p{j} nvarchar(4000)"));
        var paramPass = "@f = @pos" + string.Concat(Enumerable.Range(0, prm.Count).Select(j => $", @p{j} = @p{j}"));
        prm["@db"] = db;
        prm["@pos"] = full.Position;
        var sql =
            "DECLARE @cmd nvarchar(max);\n" +
            "SET @cmd = N'RESTORE DATABASE ' + QUOTENAME(@db) + N' PAGE = ''" + safePages + "'' FROM " + from + " WITH FILE = @f, NORECOVERY;';\n" +
            "EXEC sys.sp_executesql @cmd, N'" + paramDecl + "', " + paramPass + ";";
        return (sql, prm);
    }

    /// <summary>Mantém apenas dígitos, ':' e ',' — formato "fileId:pageId,fileId:pageId".</summary>
    public static string SanitizePages(string pages)
        => new string(pages.Where(c => char.IsDigit(c) || c == ':' || c == ',').ToArray());

    private static string Escape(string s) => s.Replace("'", "''");

    /// <summary>Monta "DISK = @p0, DISK = @p1, ..." e preenche os parâmetros (suporta striped).</summary>
    private static (string from, Dictionary<string, object?> prm) DeviceFrom(IReadOnlyList<string> devices)
    {
        var prm = new Dictionary<string, object?>();
        var clauses = new List<string>();
        for (var i = 0; i < devices.Count; i++)
        {
            clauses.Add($"DISK = @p{i}");
            prm[$"@p{i}"] = devices[i];
        }
        return (string.Join(", ", clauses), prm);
    }

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
