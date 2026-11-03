using RestoreDB.DataAccess;
using RestoreDB.Domain.Constants;
using RestoreDB.Domain.Models;

namespace RestoreDB.Services;

/// <summary>
/// AJUSTES RECOMENDADOS (sem perda de dados): corrige configuração e metadados que o diagnóstico
/// encontrou — PAGE_VERIFY, AUTO_SHRINK/CLOSE, crescimento dos arquivos, log fragmentado (VLFs),
/// regras não confiáveis, índices desabilitados, objetos com referência quebrada, usuários órfãos,
/// dono inválido, somente leitura — e LIBERA o acesso de um banco travado em SINGLE_USER.
/// Nenhuma correção apaga ou altera linhas. Todo o T-SQL é compatível com SQL Server 2008 R2.
/// </summary>
public sealed class AdjustmentService
{
    private readonly SqlDataAccess _data = new();
    private readonly LogService _log;

    public AdjustmentService(LogService log) => _log = log;

    // ----------------------------------------------------------------------------------
    // Avaliação (chamada pelo diagnóstico, na mesma sessão)
    // ----------------------------------------------------------------------------------

    /// <summary>
    /// Lê configurações e situação de acesso e preenche o diagnóstico (Settings, AccessRestricted/Holders).
    /// Roda ANTES do CHECKDB: se o banco está preso em SINGLE_USER por outra sessão, o CHECKDB nem consegue rodar.
    /// </summary>
    internal static async Task ReadAccessAndSettingsAsync(SqlSession session, DatabaseInfo db, DiagnosisResult diag, CancellationToken ct)
    {
        try
        {
            diag.Settings = await session.GetDatabaseSettingsAsync(db.Name, ct);
            if (diag.Settings is { } s)
            {
                db.UserAccess = s.UserAccess;
                db.IsReadOnly = s.IsReadOnly;
                var restricted = s.UserAccess.Length > 0 && !s.UserAccess.Equals("MULTI_USER", StringComparison.OrdinalIgnoreCase);
                if (restricted && diag.StateDesc.Equals("ONLINE", StringComparison.OrdinalIgnoreCase))
                {
                    diag.AccessRestricted = true;
                    diag.AccessHolders.AddRange(await session.GetDatabaseSessionsAsync(db.Name, ct));
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* best-effort */ }
    }

    /// <summary>Monta a lista de ajustes a partir das configurações e dos achados de saúde lógica.</summary>
    internal static void BuildAdjustments(DatabaseInfo db, DiagnosisResult diag, IReadOnlyCollection<string> disabledIndexes, IReadOnlyCollection<string> brokenModules)
    {
        if (AppConstants.IsSystemDatabase(db.Name)) return;
        diag.Adjustments.Clear();
        diag.Adjustments.AddRange(HealthEvaluator.Evaluate(diag.Settings, diag.UntrustedConstraints, disabledIndexes, brokenModules));
    }

    // ----------------------------------------------------------------------------------
    // Aplicar ajustes
    // ----------------------------------------------------------------------------------

    public async Task<RepairResult> ApplyAsync(
        DatabaseInfo db, IReadOnlyCollection<HealthIssueKind> kinds, string connectionString,
        IProgress<RepairLogEntry> p, CancellationToken ct = default)
    {
        var result = new RepairResult();
        Report(p, db.Name, RepairLogLevel.Info, $"════════ Ajustes recomendados em [{db.Name}] ════════");
        if (AppConstants.IsSystemDatabase(db.Name))
        {
            result.Status = RepairStatus.Failed;
            result.Message = "Ajustes automáticos não são aplicados em bancos de sistema.";
            Report(p, db.Name, RepairLogLevel.Error, result.Message);
            return result;
        }

        await using var s = await _data.OpenSessionAsync(connectionString, ct);
        var settings = await s.GetDatabaseSettingsAsync(db.Name, ct) ?? new DatabaseSettings();
        var ok = 0;
        var pending = new List<string>();

        // Liberar a gravação vem ANTES de tudo: com o banco somente leitura, revalidar regras/objetos, reconstruir
        // índices, religar usuários e reorganizar o log falhariam.
        foreach (var kind in kinds.Distinct().OrderBy(k => k == HealthIssueKind.ReadOnly ? -1 : (int)k))
        {
            ct.ThrowIfCancellationRequested();
            var (done, note) = kind switch
            {
                HealthIssueKind.PageVerify => await AlterDbAsync(s, db, p, "Ligando PAGE_VERIFY CHECKSUM", "SET PAGE_VERIFY CHECKSUM WITH NO_WAIT", ct),
                HealthIssueKind.AutoShrink => await AlterDbAsync(s, db, p, "Desligando AUTO_SHRINK", "SET AUTO_SHRINK OFF WITH NO_WAIT", ct),
                HealthIssueKind.AutoClose => await AlterDbAsync(s, db, p, "Desligando AUTO_CLOSE", "SET AUTO_CLOSE OFF WITH NO_WAIT", ct),
                HealthIssueKind.ReadOnly => await AlterDbAsync(s, db, p, "Liberando gravação (READ_WRITE)", "SET READ_WRITE WITH ROLLBACK IMMEDIATE", ct),
                HealthIssueKind.FileGrowth => await FixGrowthAsync(s, db, settings, p, ct),
                HealthIssueKind.TooManyVlfs => await FixVlfsAsync(s, db, settings, p, ct),
                HealthIssueKind.UntrustedConstraints => await RetrustConstraintsAsync(s, db, p, ct),
                HealthIssueKind.DisabledIndexes => await RebuildDisabledIndexesAsync(s, db, p, ct),
                HealthIssueKind.BrokenModules => await RefreshModulesAsync(s, db, p, ct),
                HealthIssueKind.OrphanedUsers => await RelinkUsersAsync(s, db, settings, p, ct),
                HealthIssueKind.InvalidOwner => await FixOwnerAsync(s, db, settings, p, ct),
                _ => (false, "ajuste apenas informativo")
            };
            if (done) ok++;
            if (note.Length > 0) pending.Add(note);
        }

        // Reflete na lista (somente leitura / acesso).
        var after = await s.GetDatabaseSettingsAsync(db.Name, CancellationToken.None);
        if (after is not null) { db.IsReadOnly = after.IsReadOnly; db.UserAccess = after.UserAccess; }

        result.Status = pending.Count == 0 ? RepairStatus.Success : ok > 0 ? RepairStatus.Success : RepairStatus.Failed;
        result.FinalStateDesc = db.StateDesc;
        result.Message = $"{ok} de {kinds.Count} ajuste(s) aplicado(s) sem alterar dados." +
                         (pending.Count > 0 ? " Pendências: " + string.Join(" ", pending) : string.Empty);
        Report(p, db.Name, pending.Count == 0 ? RepairLogLevel.Success : RepairLogLevel.Warning, result.Message);
        return result;
    }

    private async Task<(bool, string)> AlterDbAsync(SqlSession s, DatabaseInfo db, IProgress<RepairLogEntry> p, string description, string setClause, CancellationToken ct)
    {
        var r = await StepAsync(s, db, p, description,
            "DECLARE @c nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@db) + N' " + setClause + ";';\nEXEC sys.sp_executesql @c;",
            new Dictionary<string, object?> { ["@db"] = db.Name }, ct);
        return r.Success ? (true, string.Empty) : (false, $"{description}: não aplicado.");
    }

    private async Task<(bool, string)> FixGrowthAsync(SqlSession s, DatabaseInfo db, DatabaseSettings settings, IProgress<RepairLogEntry> p, CancellationToken ct)
    {
        var all = true;
        foreach (var f in settings.Files.Where(HealthEvaluator.NeedsGrowthFix))
        {
            var mb = HealthEvaluator.RecommendedGrowthMb(f);
            var r = await StepAsync(s, db, p, $"Crescimento fixo de {mb} MB para {f.LogicalName}",
                "DECLARE @c nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@db) + N' MODIFY FILE (NAME = ' + QUOTENAME(@f, '''') + N', FILEGROWTH = ' + CAST(@mb AS nvarchar(10)) + N'MB);';\nEXEC sys.sp_executesql @c;",
                new Dictionary<string, object?> { ["@db"] = db.Name, ["@f"] = f.LogicalName, ["@mb"] = mb }, ct);
            all &= r.Success;
        }
        return all ? (true, string.Empty) : (false, "Crescimento: algum arquivo não aceitou a mudança.");
    }

    /// <summary>
    /// Reorganiza o log: (SIMPLE: CHECKPOINT) → encolhe o arquivo de log ao mínimo possível → recria no
    /// tamanho original em blocos de até 8 GB (poucos VLFs grandes) → crescimento fixo. Não toca em dados.
    /// </summary>
    private async Task<(bool, string)> FixVlfsAsync(SqlSession s, DatabaseInfo db, DatabaseSettings settings, IProgress<RepairLogEntry> p, CancellationToken ct)
    {
        var log = settings.Files.FirstOrDefault(f => f.IsLog);
        if (log is null) return (false, "Log: arquivo não encontrado.");
        var before = await s.CountVlfsAsync(db.Name, ct);
        var targetMb = (long)Math.Ceiling(Math.Max(log.SizeMb, 64));

        if (settings.RecoveryModel.Equals("SIMPLE", StringComparison.OrdinalIgnoreCase))
            await StepAsync(s, db, p, "Liberando o log (CHECKPOINT)", "DECLARE @proc nvarchar(300) = QUOTENAME(@db) + N'.sys.sp_executesql';\nEXEC @proc N'CHECKPOINT;';",
                new Dictionary<string, object?> { ["@db"] = db.Name }, ct);

        var inner = $"DBCC SHRINKFILE ({Quote(log.LogicalName)}, 1) WITH NO_INFOMSGS;";
        await StepAsync(s, db, p, "Encolhendo o log ao mínimo",
            "DECLARE @proc nvarchar(300) = QUOTENAME(@db) + N'.sys.sp_executesql';\nEXEC @proc @stmt;",
            new Dictionary<string, object?> { ["@db"] = db.Name, ["@stmt"] = inner }, ct);

        // Recria no tamanho original em blocos grandes (cada bloco ≤ 8000 MB gera 16 VLFs grandes).
        var now = (await s.GetDatabaseSettingsAsync(db.Name, ct))?.Files.FirstOrDefault(f => f.IsLog)?.SizeMb ?? log.SizeMb;
        var size = (long)Math.Ceiling(now);
        var regrowOk = true;
        while (size < targetMb)
        {
            var next = Math.Min(targetMb, size + 8000);
            var r = await StepAsync(s, db, p, $"Recriando o log: {next:N0} MB",
                "DECLARE @c nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@db) + N' MODIFY FILE (NAME = ' + QUOTENAME(@f, '''') + N', SIZE = ' + CAST(@mb AS nvarchar(20)) + N'MB);';\nEXEC sys.sp_executesql @c;",
                new Dictionary<string, object?> { ["@db"] = db.Name, ["@f"] = log.LogicalName, ["@mb"] = next }, ct);
            if (!r.Success) { regrowOk = false; break; }
            size = next;
        }
        var growth = HealthEvaluator.RecommendedGrowthMb(new FileGrowthSetting { IsLog = true, SizeMb = targetMb });
        await StepAsync(s, db, p, $"Crescimento fixo de {growth} MB para o log",
            "DECLARE @c nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@db) + N' MODIFY FILE (NAME = ' + QUOTENAME(@f, '''') + N', FILEGROWTH = ' + CAST(@mb AS nvarchar(10)) + N'MB);';\nEXEC sys.sp_executesql @c;",
            new Dictionary<string, object?> { ["@db"] = db.Name, ["@f"] = log.LogicalName, ["@mb"] = growth }, ct);

        var after = await s.CountVlfsAsync(db.Name, ct);
        Report(p, db.Name, after >= 0 && after < before ? RepairLogLevel.Success : RepairLogLevel.Warning,
            $"VLFs no log: {before:N0} → {after:N0}.");
        if (after > HealthEvaluator.VlfWarnThreshold)
            return (after < before, "Log: ainda há muitos VLFs (a parte ativa do log não pôde ser encolhida) — repita depois do próximo backup de log.");
        return regrowOk ? (true, string.Empty) : (true, "Log: não foi possível devolver o tamanho original (o arquivo cresce sozinho quando precisar).");
    }

    private async Task<(bool, string)> RetrustConstraintsAsync(SqlSession s, DatabaseInfo db, IProgress<RepairLogEntry> p, CancellationToken ct)
    {
        var list = await s.GetUntrustedEnabledConstraintsAsync(db.Name, ct);
        var failed = new List<string>();
        foreach (var (table, constraint) in list)
        {
            var r = await s.ExecuteInDatabaseAsync($"Revalidando {constraint}", db.Name,
                $"ALTER TABLE {table} WITH CHECK CHECK CONSTRAINT {Quote(constraint)};", ct: ct);
            if (!r.Success) failed.Add(constraint);
        }
        Report(p, db.Name, failed.Count == 0 ? RepairLogLevel.Success : RepairLogLevel.Warning,
            $"Regras revalidadas: {list.Count - failed.Count} de {list.Count}." +
            (failed.Count > 0 ? $" Com dados violando (continuam não confiáveis — use o diagnóstico/Corrigir integridade): {string.Join(", ", failed.Take(10))}." : string.Empty));
        return failed.Count == 0 ? (true, string.Empty) : (list.Count > failed.Count, $"{failed.Count} regra(s) têm dados que as violam.");
    }

    private async Task<(bool, string)> RebuildDisabledIndexesAsync(SqlSession s, DatabaseInfo db, IProgress<RepairLogEntry> p, CancellationToken ct)
    {
        var list = await s.GetDisabledIndexDetailsAsync(db.Name, ct);
        var failed = 0;
        foreach (var (table, index, _) in list)
        {
            var r = await s.ExecuteInDatabaseAsync($"Reconstruindo {table}.{index}", db.Name, $"ALTER INDEX {index} ON {table} REBUILD;", ct: ct);
            Report(p, db.Name, r.Success ? RepairLogLevel.Success : RepairLogLevel.Warning,
                $"  {(r.Success ? "✔" : "⚠")} {table}.{index}" + (r.Success ? string.Empty : " — " + string.Join(" ", r.ErrorMessages)));
            if (!r.Success) failed++;
        }
        return failed == 0 ? (true, string.Empty) : (list.Count > failed, $"{failed} índice(s) não reconstruído(s).");
    }

    private async Task<(bool, string)> RefreshModulesAsync(SqlSession s, DatabaseInfo db, IProgress<RepairLogEntry> p, CancellationToken ct)
    {
        var list = await s.GetModulesWithBrokenReferencesAsync(db.Name, ct);
        var failed = new List<string>();
        foreach (var m in list)
        {
            var r = await s.ExecuteInDatabaseAsync($"Revalidando {m}", db.Name,
                $"EXEC sys.sp_refreshsqlmodule N'{m.Replace("'", "''")}';", ct: ct);
            if (!r.Success) failed.Add(m);
        }
        var still = await s.GetModulesWithBrokenReferencesAsync(db.Name, ct);
        Report(p, db.Name, still.Count == 0 ? RepairLogLevel.Success : RepairLogLevel.Warning,
            still.Count == 0
                ? $"{list.Count} objeto(s) revalidado(s); nenhuma referência quebrada restante."
                : $"{still.Count} objeto(s) continuam citando objetos inexistentes (depende do fornecedor): {string.Join(", ", still.Take(10))}.");
        return still.Count == 0 ? (true, string.Empty) : (still.Count < list.Count, $"{still.Count} objeto(s) dependem do fornecedor.");
    }

    private async Task<(bool, string)> RelinkUsersAsync(SqlSession s, DatabaseInfo db, DatabaseSettings settings, IProgress<RepairLogEntry> p, CancellationToken ct)
    {
        var fixable = settings.OrphanedUsers.Where(u => u.LoginExists).Select(u => u.User).ToList();
        var failed = 0;
        foreach (var u in fixable)
        {
            var r = await s.ExecuteInDatabaseAsync($"Religando o usuário {u} ao login", db.Name,
                $"ALTER USER {Quote(u)} WITH LOGIN = {Quote(u)};", ct: ct);
            if (!r.Success) failed++;
        }
        var noLogin = settings.OrphanedUsers.Count - fixable.Count;
        Report(p, db.Name, failed == 0 ? RepairLogLevel.Success : RepairLogLevel.Warning,
            $"Usuários religados: {fixable.Count - failed} de {fixable.Count}." +
            (noLogin > 0 ? $" {noLogin} usuário(s) sem login de mesmo nome no servidor — crie o login e rode de novo." : string.Empty));
        return (failed == 0 && fixable.Count > 0, noLogin > 0 ? $"{noLogin} usuário(s) precisam de um login criado no servidor." : string.Empty);
    }

    private async Task<(bool, string)> FixOwnerAsync(SqlSession s, DatabaseInfo db, DatabaseSettings settings, IProgress<RepairLogEntry> p, CancellationToken ct)
    {
        if (settings.Trustworthy) return (false, "Dono: TRUSTWORTHY ligado — trocar o dono exige avaliação do DBA.");
        var sa = await s.GetSaLoginNameAsync(ct);
        if (sa.Length == 0) return (false, "Dono: login administrador não encontrado.");
        var r = await StepAsync(s, db, p, $"Tornando [{sa}] o dono do banco",
            "DECLARE @c nvarchar(max) = N'ALTER AUTHORIZATION ON DATABASE::' + QUOTENAME(@db) + N' TO ' + QUOTENAME(@sa) + N';';\nEXEC sys.sp_executesql @c;",
            new Dictionary<string, object?> { ["@db"] = db.Name, ["@sa"] = sa }, ct);
        return r.Success ? (true, string.Empty) : (false, "Dono: não foi possível trocar.");
    }

    // ----------------------------------------------------------------------------------
    // Acesso travado (SINGLE_USER / RESTRICTED_USER)
    // ----------------------------------------------------------------------------------

    /// <summary>
    /// Devolve o acesso normal (MULTI_USER). Com <paramref name="disconnectHolders"/>, desconecta antes as
    /// sessões presas NESTE banco (transações abertas delas são desfeitas pelo SQL Server). Sem perda de
    /// dados gravados.
    /// </summary>
    public async Task<RepairResult> ReleaseAccessAsync(
        DatabaseInfo db, bool disconnectHolders, string connectionString, IProgress<RepairLogEntry> p, CancellationToken ct = default)
    {
        Report(p, db.Name, RepairLogLevel.Info, $"════════ Liberando o acesso a [{db.Name}] ════════");
        await using var s = await _data.OpenSessionAsync(connectionString, ct);
        foreach (var h in await s.GetDatabaseSessionsAsync(db.Name, ct))
            Report(p, db.Name, RepairLogLevel.Info, "  Conectado: " + h.Display);

        var (killed, finalAccess, error) = await s.ReleaseDatabaseAccessAsync(db.Name, disconnectHolders, ct);
        foreach (var k in killed)
            Report(p, db.Name, RepairLogLevel.Warning, $"  Sessão {k} desconectada (estava presa ao banco).");

        db.UserAccess = finalAccess;
        var ok = finalAccess.Equals("MULTI_USER", StringComparison.OrdinalIgnoreCase);
        var result = new RepairResult
        {
            Status = ok ? RepairStatus.Success : RepairStatus.Failed,
            FinalStateDesc = db.StateDesc,
            Message = ok
                ? "Acesso liberado: o banco voltou ao modo normal (MULTI_USER)" + (killed.Count > 0 ? $"; {killed.Count} sessão(ões) presa(s) desconectada(s)." : ".")
                : $"Não foi possível liberar o acesso (continua {finalAccess}). " +
                  (disconnectHolders ? error : "Tente de novo marcando 'desconectar as sessões presas'.")
        };
        Report(p, db.Name, ok ? RepairLogLevel.Success : RepairLogLevel.Error, result.Message);
        return result;
    }

    // ----------------------------------------------------------------------------------

    private async Task<CommandExecutionResult> StepAsync(SqlSession s, DatabaseInfo db, IProgress<RepairLogEntry> p,
        string description, string sql, Dictionary<string, object?> prm, CancellationToken ct)
    {
        Report(p, db.Name, RepairLogLevel.Command, "» " + description);
        var r = await s.ExecuteAsync(description, sql, prm, AppConstants.RepairCommandTimeoutSeconds, ct);
        foreach (var m in r.ErrorMessages) Report(p, db.Name, RepairLogLevel.Error, "    " + m);
        Report(p, db.Name, r.Success ? RepairLogLevel.Success : RepairLogLevel.Warning, $"    → {(r.Success ? "OK" : "não concluído")}: {description}");
        return r;
    }

    private static string Quote(string ident) => "[" + ident.Replace("]", "]]") + "]";

    private void Report(IProgress<RepairLogEntry> p, string db, RepairLogLevel level, string message)
    {
        var entry = new RepairLogEntry { Level = level, Message = message, Database = db };
        _log.Write(entry);
        p.Report(entry);
    }
}
