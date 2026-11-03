using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using RestoreDB.DataAccess;
using RestoreDB.Domain.Constants;
using RestoreDB.Domain.Models;

namespace RestoreDB.Services;

/// <summary>
/// Orquestra o diagnóstico e o reparo dos bancos, do método menos agressivo ao mais
/// agressivo. Toda a sequência roda em UMA única sessão (conexão) para manter o bloqueio
/// SINGLE_USER. Cada comando é registrado (log em arquivo + progresso na tela).
///
/// Todo o T-SQL é compatível com SQL Server 2008 R2. Os nomes de banco são protegidos com
/// QUOTENAME e passados via parâmetro a sp_executesql (sem concatenação insegura).
/// </summary>
public sealed class RepairService
{
    private readonly SqlDataAccess _data = new();
    private readonly LogService _log;
    private readonly RestoreService _restore;

    /// <summary>
    /// Resultado da recuperação de linhas da execução corrente (uma operação por vez — a UI não permite
    /// duas): banco de salvamento mantido para revisão e o resumo, em linguagem simples, do que ficou perdido.
    /// </summary>
    private string? _keptSalvageDb;
    private string _lossSummary = string.Empty;

    /// <summary>Algum DBCC da execução corrente foi recusado por transações adiadas (Msg 7929).</summary>
    private bool _deferredTransactions;

    public RepairService(LogService log, RestoreService restore)
    {
        _log = log;
        _restore = restore;
    }

    /// <summary>
    /// Executa o reparo do banco. Quando <paramref name="allowDataLoss"/> é false (padrão),
    /// só são aplicados reparos SEM perda de dados; se não forem suficientes, retorna
    /// <see cref="RepairStatus.NeedsDataLossConfirmation"/> para que a UI peça confirmação.
    /// </summary>
    public async Task<RepairResult> RepairAsync(
        DatabaseInfo db,
        string connectionString,
        bool allowDataLoss,
        IProgress<RepairLogEntry> progress,
        CancellationToken ct = default,
        ParentRecoveryOptions? recovery = null)
    {
        var result = new RepairResult();
        _keptSalvageDb = null;
        _lossSummary = string.Empty;
        _deferredTransactions = false;
        var mode = allowDataLoss ? "COM possível PERDA DE DADOS" : "seguro (sem perda de dados)";
        Report(progress, db.Name, RepairLogLevel.Info, $"════════ Iniciando reparo de [{db.Name}] — modo {mode} ════════");

        await using var session = await _data.OpenSessionAsync(connectionString, ct);

        var state = await session.GetDatabaseStateAsync(db.Name, ct);
        Report(progress, db.Name, RepairLogLevel.Info, $"Estado atual: {state} — {StateInfo.Friendly(state)}");

        RepairStatus status;
        try
        {
            status = state.Trim().ToUpperInvariant() switch
            {
                "ONLINE" => await OnlineRepairFlowAsync(session, db, connectionString, allowDataLoss, recovery, progress, ct),
                "OFFLINE" => await BringOnlineAsync(session, db, progress, ct),
                "RESTORING" => await RestoreWithRecoveryAsync(session, db, progress, ct),
                "RECOVERING" => await RestoreWithRecoveryAsync(session, db, progress, ct),
                "RECOVERY_PENDING" => await RecoveryPendingAsync(session, db, connectionString, allowDataLoss, recovery, progress, ct),
                "SUSPECT" => await EmergencyRepairFlowAsync(session, db, connectionString, allowDataLoss, recovery, progress, ct),
                "EMERGENCY" => await EmergencyRepairFlowAsync(session, db, connectionString, allowDataLoss, recovery, progress, ct),
                _ => RepairStatus.Failed
            };
        }
        catch (OperationCanceledException)
        {
            Report(progress, db.Name, RepairLogLevel.Warning, "Operação cancelada.");
            await TrySetMultiUserAsync(session, db, progress, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            Report(progress, db.Name, RepairLogLevel.Error, "Erro inesperado durante o reparo: " + ex.Message);
            await TrySetMultiUserAsync(session, db, progress, CancellationToken.None);
            status = RepairStatus.Failed;
        }

        string finalState;
        try
        {
            finalState = state.Trim().Equals("ONLINE", StringComparison.OrdinalIgnoreCase)
                ? await session.GetDatabaseStateAsync(db.Name, ct)
                : await GetSettledStateAsync(session, db.Name, ct);
        }
        catch (Exception ex)
        {
            // Nunca deixar uma falha na leitura do estado final escapar do método.
            Report(progress, db.Name, RepairLogLevel.Warning, "Não foi possível reler o estado final: " + ex.Message);
            finalState = db.StateDesc;
        }

        result.Status = status;
        result.FinalStateDesc = finalState;
        db.StateDesc = finalState; // a grade da UI reflete via INotifyPropertyChanged

        result.Message = status switch
        {
            RepairStatus.Success => $"Reparo concluído com sucesso. Estado final: {finalState}.",
            RepairStatus.NoActionNeeded => "Nada a reparar: o DBCC CHECKDB e as chaves estrangeiras não acusaram erros. Nenhuma alteração foi feita.",
            RepairStatus.NeedsDataLossConfirmation =>
                "O reparo SEM perda de dados não foi suficiente. É possível tentar o reparo COM possível perda de dados (requer confirmação).",
            _ => $"Não foi possível reparar automaticamente. Estado atual: {finalState}."
        };
        if (_lossSummary.Length > 0) result.Message += " " + _lossSummary;
        if (_deferredTransactions && status != RepairStatus.Success) result.Message += " " + DeferredTransactionsHint;
        if (_keptSalvageDb is not null)
        {
            result.SalvageDatabase = _keptSalvageDb;
            result.Message += $" A cópia de salvamento [{_keptSalvageDb}] foi mantida para revisão.";
        }

        var level = status switch
        {
            RepairStatus.Success or RepairStatus.NoActionNeeded => RepairLogLevel.Success,
            RepairStatus.NeedsDataLossConfirmation => RepairLogLevel.Warning,
            _ => RepairLogLevel.Error
        };
        Report(progress, db.Name, level, result.Message);
        Report(progress, db.Name, RepairLogLevel.Info, $"════════ Fim do reparo de [{db.Name}] ════════");
        return result;
    }

    // ----------------------------------------------------------------------------------
    // Diagnóstico SOMENTE LEITURA (dados x log)
    // ----------------------------------------------------------------------------------

    /// <summary>
    /// Analisa, sem alterar o banco, se a causa provável do estado problemático está nos
    /// arquivos de DADOS (.mdf/.ndf) ou no LOG de transações (.ldf). Baseia-se no log de
    /// erros do SQL Server (sp_readerrorlog) e nos arquivos de sys.master_files.
    /// </summary>
    public async Task<DiagnosisResult> DiagnoseAsync(
        DatabaseInfo db, string connectionString, IProgress<RepairLogEntry> progress, CancellationToken ct = default,
        bool physicalOnly = false)
    {
        var diag = new DiagnosisResult { DatabaseName = db.Name, StateDesc = db.StateDesc };
        Report(progress, db.Name, RepairLogLevel.Info, $"──── Diagnóstico (somente leitura) de [{db.Name}]"
            + (physicalOnly ? " — verificação rápida (PHYSICAL_ONLY)" : "") + " ────");

        try
        {
            // Capacidades da instância (conexão própria, best-effort).
            try { diag.Server = await _data.GetServerInfoAsync(connectionString, ct); } catch { /* ignora */ }
            if (diag.Server is not null)
                Report(progress, db.Name, RepairLogLevel.Info,
                    $"Instância: {diag.Server.ShortDescription} — login {(diag.Server.IsSysadmin ? "É" : "NÃO é")} sysadmin.");

            await using var session = await _data.OpenSessionAsync(connectionString, ct);
            diag.StateDesc = await session.GetDatabaseStateAsync(db.Name, ct);

            // Configurações e ACESSO antes do CHECKDB: preso em SINGLE_USER por outra sessão, o banco
            // aparece ONLINE mas ninguém (nem o CHECKDB) consegue entrar.
            await AdjustmentService.ReadAccessAndSettingsAsync(session, db, diag, ct);
            if (diag.AccessRestricted)
            {
                Report(progress, db.Name, RepairLogLevel.Warning,
                    $"ACESSO TRAVADO: o banco está em {diag.Settings?.UserAccess} — o sistema não consegue usá-lo normalmente.");
                foreach (var h in diag.AccessHolders)
                    Report(progress, db.Name, RepairLogLevel.Warning, "  • Segurando o acesso: " + h.Display);
            }
            if (diag.Settings is { IsReadOnly: true })
                Report(progress, db.Name, RepairLogLevel.Warning, "O banco está em modo SOMENTE LEITURA (READ_ONLY).");

            // TDE: avisa que restaurar em outra instância exige o certificado.
            diag.IsEncrypted = await session.IsEncryptedAsync(db.Name, ct);
            if (diag.IsEncrypted)
                Report(progress, db.Name, RepairLogLevel.Warning,
                    "Banco CRIPTOGRAFADO (TDE): restaurar em OUTRA instância exige o certificado/Chave Mestra do servidor de origem. Garanta o backup do certificado.");

            // Operação demorada em andamento (recuperação/RESTORE) — informa o progresso.
            if (diag.StateDesc.Trim().ToUpperInvariant() is "RECOVERING" or "RESTORING" or "RECOVERY_PENDING")
            {
                diag.Recovery = await session.GetRecoveryProgressAsync(db.Name, ct);
                if (diag.Recovery is not null)
                    Report(progress, db.Name, RepairLogLevel.Info, "Em andamento — " + diag.Recovery.Display + " (aguarde antes de intervir).");
            }

            var files = await session.GetDatabaseFilesAsync(db.Name, ct);
            foreach (var f in files)
                Report(progress, db.Name, RepairLogLevel.Info, $"Arquivo {f.TypeDesc}: {f.PhysicalName}");

            // Arquivos que o SQL Server não encontra no caminho registrado (checado NO SERVIDOR).
            foreach (var f in files.Where(f => f.IsData || f.IsLog))
            {
                if (await session.FileExistsOnServerAsync(f.PhysicalName, ct) == false)
                {
                    diag.FileProblems.Add(new FileAccessProblem { LogicalName = f.LogicalName, PhysicalName = f.PhysicalName, IsLog = f.IsLog, Kind = FileProblemKind.Missing });
                    Report(progress, db.Name, RepairLogLevel.Warning, $"Arquivo AUSENTE no servidor: {f.PhysicalName}");
                }
            }

            // Log de transações: uso e o que impede a reutilização (9002 = log cheio, não é corrupção).
            try
            {
                diag.LogSpace = await session.GetLogSpaceInfoAsync(db.Name, ct);
                var ls = diag.LogSpace;
                Report(progress, db.Name, ls.IsCritical ? RepairLogLevel.Warning : RepairLogLevel.Info, ls.Summary);
                foreach (var lf in ls.Files.Where(x => x.AtMaxSize || x.AutoGrowthDisabled || x.VolumeFreeMb is < 1024))
                    Report(progress, db.Name, RepairLogLevel.Warning, "  • Arquivo de log sem folga para crescer: " + lf.Display);
                if (ls.IsCritical && diag.StateDesc.Equals("ONLINE", StringComparison.OrdinalIgnoreCase))
                {
                    diag.LogFull = true;
                    Report(progress, db.Name, RepairLogLevel.Warning,
                        "O log de transações está praticamente CHEIO — logo o banco recusará gravações (erro 9002).");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* best-effort */ }

            // Topologia (espelhamento/AG/log shipping/replicação) — afeta a segurança do reparo.
            diag.Topology = await session.GetTopologyInfoAsync(db.Name, ct);
            if (diag.Topology.IsRisky)
            {
                Report(progress, db.Name, RepairLogLevel.Warning, "TOPOLOGIA: " + diag.Topology.Summary);
                foreach (var w in diag.Topology.Warnings)
                    Report(progress, db.Name, RepairLogLevel.Warning, "  ⚠ " + w);
            }

            // Espaço em disco dos volumes do banco.
            diag.Disks.AddRange(await session.GetDiskSpaceAsync(db.Name, ct));
            foreach (var d in diag.Disks.Where(d => d.IsLow))
                Report(progress, db.Name, RepairLogLevel.Warning,
                    $"DISCO BAIXO: volume {d.VolumeMountPoint} com {d.AvailableGb:0.0} GB livres ({d.PercentFree:0.#}%).");

            // DBCC CHECKDB estruturado (somente leitura) — é a VERDADE do momento; roda antes de
            // interpretar as páginas suspeitas (que são um registro histórico).
            await TryStructuredCheckDbAsync(session, db, diag, progress, ct, physicalOnly);
            var checkDbClean = diag.CheckDb is { Executed: true, HasErrors: false };

            // Integridade LÓGICA (FK órfã / CHECK / catálogo) — o CHECKDB NÃO cobre isso. Roda
            // sempre que o banco está acessível (mesmo ONLINE aparentemente saudável).
            await TryLogicalIntegrityChecksAsync(session, db, diag, progress, ct);

            // Páginas suspeitas (msdb.dbo.suspect_pages). Reconciliadas com o CHECKDB: se o CHECKDB
            // está limpo, as entradas referem-se a corrupção JÁ RESOLVIDA (o SQL Server não as
            // remove automaticamente). Só são "corrupção ativa" se o CHECKDB também acusar (ou não
            // tiver conseguido rodar). Isso evita falso positivo após um reparo.
            diag.SuspectPages.AddRange(await session.GetSuspectPagesAsync(db.Name, ct));
            var active = diag.SuspectPages.Where(p => p.IsActiveCorruption).ToList();
            if (active.Count > 0 && checkDbClean)
            {
                Report(progress, db.Name, RepairLogLevel.Info,
                    $"msdb.suspect_pages tem {active.Count} registro(s) histórico(s), mas o CHECKDB está limpo — corrupção já resolvida. Limpando o histórico.");
                await session.ClearSuspectPagesAsync(db.Name, ct);
                diag.SuspectPages.Clear();
            }
            else if (active.Count > 0)
            {
                diag.DataCorruptionSuspected = true;
                Report(progress, db.Name, RepairLogLevel.Warning,
                    $"msdb.suspect_pages: {active.Count} página(s) corrompida(s) registrada(s).");
                foreach (var p in active.Take(8))
                    Report(progress, db.Name, RepairLogLevel.Warning,
                        $"  • arquivo {p.FileId}, página {p.PageId} — {p.EventTypeDescription} (x{p.ErrorCount})");
            }

            var logNames = files.Where(f => f.IsLog).Select(f => Path.GetFileName(f.PhysicalName))
                                .Where(n => !string.IsNullOrEmpty(n)).ToList();
            var dataNames = files.Where(f => f.IsData).Select(f => Path.GetFileName(f.PhysicalName))
                                 .Where(n => !string.IsNullOrEmpty(n)).ToList();

            Report(progress, db.Name, RepairLogLevel.Info, "Lendo o log de erros do SQL Server (sp_readerrorlog)...");
            var lines = await session.ReadErrorLogAsync(db.Name, ct,
                extraSearchTerms: dataNames.Concat(logNames).Select(n => n!));

            // Falhas ao ABRIR arquivos (permissão, arquivo preso, não encontrado) — só fazem sentido como
            // causa atual quando o banco NÃO está ONLINE (se está ONLINE, os arquivos abriram).
            if (!diag.StateDesc.Equals("ONLINE", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var line in lines)
                {
                    if (FileAccessProblem.Classify(line) is not FileProblemKind kind) continue;
                    var file = files.FirstOrDefault(f => (f.IsData || f.IsLog) && !string.IsNullOrEmpty(f.PhysicalName)
                        && line.Contains(Path.GetFileName(f.PhysicalName), StringComparison.OrdinalIgnoreCase));
                    if (file is null || diag.FileProblems.Any(x => x.PhysicalName == file.PhysicalName)) continue;
                    diag.FileProblems.Add(new FileAccessProblem { LogicalName = file.LogicalName, PhysicalName = file.PhysicalName, IsLog = file.IsLog, Kind = kind });
                }
                foreach (var fp in diag.FileProblems.Where(x => x.Kind != FileProblemKind.Missing))
                    Report(progress, db.Name, RepairLogLevel.Warning, fp.Display);
            }

            // O log de erros também é HISTÓRICO. Se o CHECKDB ao vivo está limpo, as linhas de
            // corrupção referem-se a um problema JÁ RESOLVIDO — não devem disparar os flags de
            // corrupção ativa (evita falso positivo após um reparo). Coletamos como evidência
            // apenas para transparência.
            foreach (var line in lines)
            {
                if (!checkDbClean && ContainsAny(line, "9002")) diag.LogFull = true;
                if (!checkDbClean && ContainsAny(line, " 823", " 824", " 825", "823,", "824,", "825,")) diag.IoErrors = true;

                if (!LooksLikeCorruptionLine(line)) continue;

                var refLog = logNames.Any(n => line.Contains(n, StringComparison.OrdinalIgnoreCase))
                             || line.Contains(".ldf", StringComparison.OrdinalIgnoreCase);
                var refData = dataNames.Any(n => line.Contains(n, StringComparison.OrdinalIgnoreCase))
                              || line.Contains(".mdf", StringComparison.OrdinalIgnoreCase)
                              || line.Contains(".ndf", StringComparison.OrdinalIgnoreCase);

                diag.Evidence.Add(line);
                if (!checkDbClean && refLog) diag.LogCorruptionSuspected = true;
                if (!checkDbClean && refData) diag.DataCorruptionSuspected = true;
            }

            // Avaliação da política de backup/recuperação (RPO) — advisory, não muda a ação recomendada.
            await TryAssessBackupPolicyAsync(session, db, diag, progress, ct);

            // Saúde lógica adicional: configurações, índices desabilitados, referências quebradas → ajustes recomendados.
            await TryLogicalHealthChecksAsync(session, db, diag, progress, ct);

            // Causa-raiz: erros de E/S e corrupção recorrente (suspeita de hardware/volume).
            TryRootCauseAnalysis(db, diag, progress);

            BuildConclusion(diag);

            // Histórico: compara com a última verificação para apontar o que é NOVO (detecção precoce).
            TryRecordHistory(db, diag, progress);

            if (checkDbClean && diag.Evidence.Count > 0)
                Report(progress, db.Name, RepairLogLevel.Info,
                    $"O log de erros tem {diag.Evidence.Count} entrada(s) de corrupção ANTIGA(s), mas o CHECKDB está limpo — já resolvida(s).");
            else
                foreach (var e in diag.Evidence.Take(8))
                    Report(progress, db.Name, RepairLogLevel.Warning, "  • " + e);

            if (!diag.HasEvidence && diag.SuspectPages.Count == 0 && (diag.CheckDb is null || !diag.CheckDb.Executed))
                Report(progress, db.Name, RepairLogLevel.Info, "Sem entradas de corrupção encontradas (ou sem permissão para lê-las).");

            Report(progress, db.Name,
                diag.LogCorruptionSuspected || diag.DataCorruptionSuspected || diag.LogFull ? RepairLogLevel.Warning : RepairLogLevel.Success,
                diag.Summary);
            if (diag.Recommendation.Length > 0)
                Report(progress, db.Name, RepairLogLevel.Info, "Recomendação: " + diag.Recommendation);
        }
        catch (Exception ex)
        {
            diag.Summary = "Não foi possível concluir o diagnóstico: " + ex.Message;
            Report(progress, db.Name, RepairLogLevel.Error, diag.Summary);
        }

        Report(progress, db.Name, RepairLogLevel.Info, "──── Fim do diagnóstico ────");
        return diag;
    }

    /// <summary>Explicação leiga para a Msg 7929 (transações adiadas), no diagnóstico e no reparo.</summary>
    internal const string DeferredTransactionsHint =
        "O banco tem transações ADIADAS (deferred transactions): o SQL Server não conseguiu desfazer uma transação " +
        "porque uma página está danificada e a deixou pendente. Assim nem o DBCC CHECKDB nem o reparo do DBCC rodam. " +
        "Restaure as páginas danificadas a partir do backup ('Restaurar páginas') ou o banco inteiro; sem backup, acione o DBA.";

    /// <summary>A execução de um DBCC foi recusada por transações adiadas (Msg 7929)?</summary>
    private static bool HitDeferredTransactions(CommandExecutionResult r)
        => r.ErrorMessages.Any(m => m.StartsWith("Msg 7929,", StringComparison.Ordinal));

    private static bool LooksLikeCorruptionLine(string line) => ContainsAny(line,
        // EN
        "checksum", "consistency-based", "consistency based", "i/o error", "torn page",
        "activation error", "cannot open", "could not open", "corrupt", "incorrect",
        // PT
        "verificação", "consistência", "erro de e/s", "página corrompida", "ativação de arquivo",
        "não é possível abrir", "não foi possível abrir", "corrompid", "incorreta");

    private static bool ContainsAny(string text, params string[] terms)
        => terms.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Executa um DBCC CHECKDB estruturado (WITH TABLERESULTS) somente leitura e resolve os nomes
    /// dos objetos/índices envolvidos. Funciona quando o banco está acessível (ONLINE/EMERGENCY);
    /// em SUSPECT pode falhar — nesse caso o relatório fica vazio e o diagnóstico usa as demais fontes.
    /// </summary>
    private async Task TryStructuredCheckDbAsync(SqlSession session, DatabaseInfo db, DiagnosisResult diag, IProgress<RepairLogEntry> progress, CancellationToken ct, bool physicalOnly = false)
    {
        Report(progress, db.Name, RepairLogLevel.Info, "Analisando consistência (DBCC CHECKDB ... WITH TABLERESULTS, somente leitura)...");
        // DATA_PURITY: valida também os VALORES das colunas (fora de faixa/inválidos) — em bancos
        // migrados do SQL 2000 essa checagem não é automática até rodar uma vez com sucesso.
        var report = await session.CheckDbWithResultsAsync(db.Name, repairOption: null, ct,
            physicalOnly: physicalOnly, dataPurity: !physicalOnly);
        diag.CheckDb = report;

        if (report.DeferredTransactions)
        {
            diag.DataCorruptionSuspected = true;
            Report(progress, db.Name, RepairLogLevel.Warning, DeferredTransactionsHint);
            return;
        }
        if (!report.Executed)
        {
            Report(progress, db.Name, RepairLogLevel.Info, "Não foi possível rodar o CHECKDB agora (banco inacessível neste estado). Usando demais evidências.");
            return;
        }
        if (!report.HasErrors)
        {
            Report(progress, db.Name, RepairLogLevel.Success, "DBCC CHECKDB não encontrou erros de consistência.");
            return;
        }

        // Resolve nomes de objetos/índices para tornar o relatório legível.
        foreach (var d in report.Details.Where(x => x.ObjectId != 0))
        {
            d.ObjectName = await session.ResolveObjectNameAsync(db.Name, d.ObjectId, ct);
            if (d.IndexId > 0)
                d.IndexName = await session.ResolveIndexNameAsync(db.Name, d.ObjectId, d.IndexId, ct);
        }

        diag.DataCorruptionSuspected = true;
        diag.IndexOnlyRepairPossible = report.AllErrorsAreNonClusteredIndex;

        Report(progress, db.Name, RepairLogLevel.Warning,
            $"DBCC CHECKDB: {report.ErrorCount} erro(s). Nível mínimo de reparo: {(report.MinRepairLevel.Length > 0 ? report.MinRepairLevel : "não reparável pelo DBCC")}.");
        foreach (var d in report.Details.Where(x => x.Error != 0 && x.Level >= 16).Take(8))
        {
            var obj = string.IsNullOrEmpty(d.ObjectName) ? $"objeto {d.ObjectId}" : d.ObjectName;
            var idx = string.IsNullOrEmpty(d.IndexName) ? (d.IndexId > 0 ? $"índice {d.IndexId}" : "") : $"índice '{d.IndexName}'";
            Report(progress, db.Name, RepairLogLevel.Warning, $"  • {obj} {idx}: {d.MessageText}".TrimEnd());
        }
        if (diag.IndexOnlyRepairPossible)
            Report(progress, db.Name, RepairLogLevel.Info,
                "Toda a corrupção está em índices NÃO clusterizados — reconstruíveis SEM perda de dados.");
    }

    /// <summary>
    /// Verifica a integridade LÓGICA do banco — o que o DBCC CHECKDB NÃO faz: consistência de
    /// metadados (CHECKCATALOG), constraints não confiáveis e, principalmente, violações de chave
    /// estrangeira / CHECK (CHECKCONSTRAINTS), como o registro órfão cuja FK aponta para uma PK
    /// inexistente. Só roda quando o banco está acessível (o CHECKDB conseguiu executar).
    /// </summary>
    private async Task TryLogicalIntegrityChecksAsync(SqlSession session, DatabaseInfo db, DiagnosisResult diag, IProgress<RepairLogEntry> progress, CancellationToken ct)
    {
        if (diag.CheckDb is not { Executed: true })
        {
            Report(progress, db.Name, RepairLogLevel.Info,
                "Verificação de integridade lógica adiada (banco inacessível neste estado).");
            return;
        }
        if (diag.CheckDb.HasErrors)
        {
            // CHECKCONSTRAINTS lê os dados pelos índices — com páginas corrompidas, um 824
            // (severidade 24) derrubaria a conexão. Repare o físico primeiro; a checagem
            // lógica roda na próxima verificação (e a reconciliação pós-reparo já cobre FKs).
            Report(progress, db.Name, RepairLogLevel.Info,
                "Verificação de integridade lógica adiada (há corrupção FÍSICA — repare-a primeiro).");
            return;
        }

        Report(progress, db.Name, RepairLogLevel.Info, "Verificando integridade LÓGICA (DBCC CHECKCATALOG / CHECKCONSTRAINTS)...");

        // Consistência de metadados.
        var catalog = await session.CheckCatalogAsync(db.Name, ct);
        if (catalog.Count > 0)
        {
            diag.CatalogErrors.AddRange(catalog);
            diag.LogicalCorruptionSuspected = true;
            Report(progress, db.Name, RepairLogLevel.Warning, $"DBCC CHECKCATALOG: {catalog.Count} inconsistência(s) de metadados.");
            foreach (var e in catalog.Take(5)) Report(progress, db.Name, RepairLogLevel.Warning, "  • " + e);
        }

        // Constraints não confiáveis: integridade nunca validada (causa comum de FK órfã silenciosa). Mesma
        // consulta da correção (só as HABILITADAS), para o ajuste sumir depois de aplicado.
        var untrusted = (await session.GetUntrustedEnabledConstraintsAsync(db.Name, ct)).Select(c => c.Constraint).ToList();
        if (untrusted.Count > 0)
        {
            diag.UntrustedConstraints.AddRange(untrusted);
            Report(progress, db.Name, RepairLogLevel.Info,
                $"{untrusted.Count} constraint(s) NÃO confiável(is) (is_not_trusted): {string.Join(", ", untrusted.Take(10))}.");
        }

        // Violações de FK/CHECK (o caso do registro órfão). Constraints DESABILITADAS (NOCHECK) são
        // apenas informadas: o próprio sistema optou por não aplicá-las — não é corrupção.
        var all = await session.CheckConstraintsAsync(db.Name, ct, includeDisabled: true);
        var disabledHits = all.Where(v => v.IsDisabled).ToList();
        diag.DisabledConstraintHits = disabledHits.Count;
        if (disabledHits.Count > 0)
            Report(progress, db.Name, RepairLogLevel.Info,
                $"{disabledHits.Count} linha(s) violam constraints DESABILITADAS ({string.Join(", ", disabledHits.Select(v => v.Constraint).Distinct().Take(5))}) — " +
                "dados aceitos pelo sistema por decisão de projeto; NÃO serão tratados como corrupção nem removidos.");
        var violations = all.Where(v => !v.IsDisabled).ToList();
        if (violations.Count > 0)
        {
            diag.ConstraintViolations.AddRange(violations);
            diag.LogicalCorruptionSuspected = true;
            var fk = violations.Count(v => v.IsForeignKey);
            Report(progress, db.Name, RepairLogLevel.Warning,
                $"DBCC CHECKCONSTRAINTS: {violations.Count} violação(ões) de integridade ({fk} de chave estrangeira).");
            foreach (var v in violations.Take(8)) Report(progress, db.Name, RepairLogLevel.Warning, "  • " + v.Display);
        }
        else if (catalog.Count == 0)
        {
            Report(progress, db.Name, RepairLogLevel.Success, "Integridade lógica (constraints/catálogo) OK.");
        }
    }

    /// <summary>
    /// Saúde lógica adicional, somente leitura: índices DESABILITADOS e views/procedures com referência
    /// QUEBRADA, somados às configurações e constraints não confiáveis já lidas — tudo vira AJUSTES RECOMENDADOS.
    /// </summary>
    private async Task TryLogicalHealthChecksAsync(SqlSession session, DatabaseInfo db, DiagnosisResult diag, IProgress<RepairLogEntry> progress, CancellationToken ct)
    {
        try
        {
            // Índices desabilitados e referências quebradas só podem ser lidos com o banco acessível; as
            // configurações (PAGE_VERIFY, AUTO_SHRINK, crescimento...) já foram lidas no início. As consultas
            // são as mesmas das correções, para o diagnóstico listar exatamente o que o ajuste resolve.
            var accessible = diag.CheckDb is { Executed: true };
            var disabled = accessible
                ? (await session.GetDisabledIndexDetailsAsync(db.Name, ct)).Select(i => PlainName($"{i.Table}.{i.Index}")).ToList()
                : new List<string>();
            var broken = accessible
                ? (await session.GetModulesWithBrokenReferencesAsync(db.Name, ct)).Select(PlainName).ToList()
                : new List<string>();

            // Vira AJUSTES RECOMENDADOS (corrigíveis sem perda de dados, ou só avisos com o motivo).
            AdjustmentService.BuildAdjustments(db, diag, disabled, broken);
            foreach (var a in diag.Adjustments)
                Report(progress, db.Name, SeverityToLevel(a.Severity),
                    "  • Ajuste recomendado: " + a.Title + (a.CanFix ? "" : " (apenas aviso)"));
        }
        catch (OperationCanceledException) { throw; }
        catch { /* best-effort */ }
    }

    /// <summary>"[dbo].[Pedido].[IX_Data]" → "dbo.Pedido.IX_Data" (só para exibir).</summary>
    private static string PlainName(string quoted) => quoted.Replace("[", "").Replace("]", "");

    /// <summary>
    /// Análise de CAUSA-RAIZ: erros de E/S físicos (823/824/825) apontam para storage/disco; e
    /// corrupção RECORRENTE (presente também em verificações anteriores, via histórico) reforça a
    /// suspeita de hardware. Apenas advisory.
    /// </summary>
    private void TryRootCauseAnalysis(DatabaseInfo db, DiagnosisResult diag, IProgress<RepairLogEntry> progress)
    {
        try
        {
            if (diag.IoErrors)
                diag.RootCauseFindings.Add(new PolicyFinding { Severity = FindingSeverity.High,
                    Message = "Erros de E/S físicos (823/824/825) no log do SQL — investigue discos/controladora/storage (possível falha de hardware)." });

            var corruptNow = diag.DataCorruptionSuspected || (diag.CheckDb is { HasErrors: true });
            if (corruptNow)
            {
                var priorCorrupt = new HistoryService().Load(db.Name).Count(s => s.PhysicalCorruption);
                if (priorCorrupt >= 1)
                    diag.RootCauseFindings.Add(new PolicyFinding { Severity = FindingSeverity.High,
                        Message = $"Corrupção RECORRENTE: já apareceu em {priorCorrupt} verificação(ões) anterior(es) — suspeite do volume/hardware e considere migrar os arquivos." });
            }

            foreach (var f in diag.RootCauseFindings)
                Report(progress, db.Name, RepairLogLevel.Warning, "  ⚠ Causa-raiz: " + f.Message);
        }
        catch { /* best-effort */ }
    }

    /// <summary>Persiste o snapshot do diagnóstico e reporta o que é NOVO desde a última verificação.</summary>
    private void TryRecordHistory(DatabaseInfo db, DiagnosisResult diag, IProgress<RepairLogEntry> progress)
    {
        try
        {
            var history = new HistoryService();
            var previous = history.LoadLatest(db.Name);
            var current = DiagnosisSnapshot.From(diag);
            var news = HistoryService.Compare(previous, current);
            diag.NewSinceLastRun.AddRange(news);
            history.Save(db.Name, current);

            foreach (var n in news)
                Report(progress, db.Name, RepairLogLevel.Warning, "⚠ NOVO desde a última verificação: " + n);
        }
        catch { /* best-effort: histórico nunca derruba o diagnóstico */ }
    }

    /// <summary>
    /// Avalia a política de backup/recuperação (modelo × histórico de backups) e registra os
    /// achados de RPO no diagnóstico. Best-effort: sem permissão em msdb, apenas não contribui.
    /// </summary>
    private async Task TryAssessBackupPolicyAsync(SqlSession session, DatabaseInfo db, DiagnosisResult diag, IProgress<RepairLogEntry> progress, CancellationToken ct)
    {
        if (AppConstants.IsSystemDatabase(db.Name)) return;
        try
        {
            var model = await session.GetRecoveryModelAsync(db.Name, ct);
            var history = await session.GetBackupHistoryAsync(db.Name, ct);
            var assess = BackupPolicyEvaluator.Evaluate(db.Name, model, history, DateTime.Now);
            diag.BackupPolicy = assess;

            Report(progress, db.Name, assess.HasRisk ? RepairLogLevel.Warning : RepairLogLevel.Info,
                $"Política de backup (modelo {(model.Length > 0 ? model : "?")}): {assess.Summary}");
            foreach (var f in assess.Findings)
                Report(progress, db.Name, SeverityToLevel(f.Severity), "  • " + f.Message);
        }
        catch { /* best-effort */ }
    }

    private static RepairLogLevel SeverityToLevel(FindingSeverity s) => s switch
    {
        FindingSeverity.Critical => RepairLogLevel.Error,
        FindingSeverity.High or FindingSeverity.Medium => RepairLogLevel.Warning,
        _ => RepairLogLevel.Info
    };

    internal static void BuildConclusion(DiagnosisResult d)
    {
        // Acesso travado (SINGLE_USER/RESTRICTED_USER) num banco ONLINE, sem corrupção confirmada: o problema
        // é o acesso. Se o CHECKDB nem rodou (a vaga está com outra sessão), liberar vem antes de qualquer reparo.
        var anyCorruption = d.DataCorruptionSuspected || d.LogCorruptionSuspected || d.LogicalCorruptionSuspected || d.LogFull;
        if (d.AccessRestricted && (!anyCorruption || d.CheckDb is not { Executed: true }))
        {
            var mode = d.Settings?.UserAccess ?? "SINGLE_USER";
            d.RecommendedAction = RecommendedActionKind.FixAccess;
            d.Summary = d.CheckDb is { Executed: true, HasErrors: false }
                ? $"Banco íntegro, mas TRAVADO em {mode}: o sistema não consegue usá-lo."
                : $"Banco TRAVADO em {mode}: o sistema (e a verificação) não consegue usá-lo.";
            d.Recommendation = (d.AccessHolders.Count > 0
                    ? $"{d.AccessHolders.Count} sessão(ões) presa(s) segura(m) o acesso (ex.: {d.AccessHolders[0].Display}). "
                    : "Nenhuma sessão está usando o banco agora — a opção ficou ligada (ex.: reparo interrompido). ")
                + "Use 'Liberar acesso': devolve o modo normal (MULTI_USER), desconectando a sessão presa se preciso. Nenhum dado gravado é perdido."
                + (d.CheckDb is not { Executed: true } ? " Depois, diagnostique de novo para conferir a integridade." : string.Empty);
            return;
        }

        // Estado íntegro: CHECKDB ao vivo limpo, integridade lógica OK e sem flags ativas.
        if (d.CheckDb is { Executed: true, HasErrors: false }
            && !d.DataCorruptionSuspected && !d.LogCorruptionSuspected && !d.LogFull
            && !d.LogicalCorruptionSuspected)
        {
            d.RecommendedAction = RecommendedActionKind.NoAction;
            d.Summary = "Nenhuma corrupção detectada — DBCC CHECKDB e integridade lógica íntegros.";
            var fixable = d.Adjustments.Count(a => a.CanFix);
            d.Recommendation = fixable > 0
                ? $"Há {fixable} ajuste(s) recomendado(s) de configuração — opcionais, sem perda de dados (veja 'Ajustes recomendados')."
                : string.Empty;
            return;
        }

        var topoNote = d.Topology is { IsRisky: true }
            ? " ATENÇÃO: " + d.Topology.Summary + " Reparos destrutivos/restore podem afetar essa configuração."
            : string.Empty;

        // RECOVERING: é a recuperação AUTOMÁTICA do SQL Server em andamento (o CHECKDB nem roda). Sem
        // evidência de corrupção, a orientação é esperar — não "sem evidências, a causa pode ser outra".
        if (d.StateDesc.Trim().Equals("RECOVERING", StringComparison.OrdinalIgnoreCase)
            && !d.DataCorruptionSuspected && !d.LogCorruptionSuspected)
        {
            d.RecommendedAction = RecommendedActionKind.SafeRepair;
            d.Summary = "O SQL Server está recuperando o banco automaticamente (RECOVERING).";
            d.Recommendation = "Aguarde a recuperação terminar" + (d.Recovery is not null ? $" (andamento: {d.Recovery.Display})" : string.Empty)
                + " e diagnostique de novo. Só use 'Reparar (modo seguro)' se o andamento não mudar por muito tempo." + topoNote;
            return;
        }

        // 0) O SQL Server não consegue ABRIR arquivos do banco (ausente/sem permissão/preso) — não é
        //    corrupção, e nenhum reparo de DBCC resolveria. Tem prioridade: com os arquivos inacessíveis
        //    o CHECKDB nem roda, e as demais evidências ficam enviesadas.
        if (d.FileProblems.Count > 0 && !d.StateDesc.Equals("ONLINE", StringComparison.OrdinalIgnoreCase))
        {
            d.RecommendedAction = RecommendedActionKind.FixFileAccess;
            var missing = d.FileProblems.Count(f => f.Kind == FileProblemKind.Missing);
            var denied = d.FileProblems.Count(f => f.Kind == FileProblemKind.AccessDenied);
            var locked = d.FileProblems.Count(f => f.Kind == FileProblemKind.InUseByAnotherProcess);
            d.Summary = "O SQL Server não consegue abrir arquivo(s) do banco — isso NÃO é corrupção.";
            d.Recommendation =
                (missing > 0 ? $"{missing} arquivo(s) ausente(s): use 'Localizar arquivo' para apontar onde ele está agora (ex.: disco/letra trocados). " : "") +
                (denied > 0 ? $"{denied} arquivo(s) sem permissão: 'Corrigir acesso e religar' concede acesso à conta do SQL Server (exige executar a ferramenta como Administrador no servidor; sem isso, os comandos aparecem no registro). " : "") +
                (locked > 0 ? $"{locked} arquivo(s) preso(s) por outro processo: feche antivírus/backup de arquivos/cópias e religue. " : "") +
                "Nenhum dado é perdido." + topoNote;
            return;
        }

        // 1) Corrupção restrita a índices não-clusterizados → reconstrução SEM perda de dados.
        if (d.IndexOnlyRepairPossible)
        {
            d.RecommendedAction = RecommendedActionKind.RebuildIndexesOnly;
            d.Summary = "A corrupção está restrita a índices NÃO clusterizados.";
            d.Recommendation = "Use 'Reparar índices (sem perda de dados)': basta reconstruir os índices afetados (ALTER INDEX REBUILD). Nenhum dado é perdido." + topoNote;
            return;
        }

        // 1a) O próprio CHECKDB diz que o REPAIR_REBUILD basta (ex.: chaves fora de ordem): conserta SEM perda,
        //     a partir dos próprios dados — restaurar um backup desfaria à toa o que foi gravado depois dele.
        if (d.CheckDb is { Executed: true, HasErrors: true } rebuildable
            && rebuildable.MinRepairLevel.Equals("REPAIR_REBUILD", StringComparison.OrdinalIgnoreCase)
            && !d.LogCorruptionSuspected && d.SuspectPages.All(p => !p.IsActiveCorruption))
        {
            d.RecommendedAction = RecommendedActionKind.SafeRepair;
            d.Summary = "Há corrupção na estrutura das tabelas que o reparo SEM perda de dados corrige.";
            d.Recommendation = "Use 'Reparar (modo seguro)': o DBCC reconstrói as estruturas danificadas a partir dos próprios dados (REPAIR_REBUILD). Nenhum dado é perdido." + topoNote;
            return;
        }

        // 1b) Integridade LÓGICA comprometida (FK órfã / constraint) com os arquivos FÍSICOS
        //     íntegros → remover as linhas violadoras. O CHECKDB não conserta isso. Físico tem
        //     prioridade: só recomenda esta correção quando não há corrupção de dados/log.
        if (d.LogicalCorruptionSuspected && !d.DataCorruptionSuspected && !d.LogCorruptionSuspected && !d.LogFull)
        {
            var fk = d.ForeignKeyViolations.Count;
            d.RecommendedAction = RecommendedActionKind.FixConstraints;
            d.Summary = "Integridade LÓGICA comprometida: há violações de constraint (arquivos físicos íntegros).";
            // A correção tenta recuperar o pai do backup e MOVE o que sobrar para a quarentena (reversível) —
            // ver ReconcileForeignKeyOrphansAsync. Restaurar de backup desfaz o que foi gravado depois dele.
            d.Recommendation = (fk > 0
                ? $"Há {fk} violação(ões) de chave estrangeira (registros órfãos). Use 'Corrigir integridade': tenta recuperar o registro-pai do backup e o que sobrar sai da tabela para a QUARENTENA (banco {AppConstants.QuarantineDatabaseName}), de onde pode ser devolvido. "
                : "Há violações de CHECK e/ou inconsistências de catálogo: não há correção automática — revise os dados com o fornecedor do sistema/DBA. ")
                + "Alternativa: RESTAURAR de backup (desfaz o que foi gravado depois do backup, se a cadeia de logs não cobrir)." + topoNote;
            return;
        }

        // 2) Log de transações cheio (erro 9002) — não é corrupção.
        if (d.LogFull && !d.DataCorruptionSuspected && !d.LogCorruptionSuspected)
        {
            d.RecommendedAction = RecommendedActionKind.ResolveLogFull;
            d.Summary = "O LOG de transações está CHEIO (erro 9002) — não é corrupção.";
            d.Recommendation = "Use 'Resolver log cheio (sem perda de dados)': a ferramenta identifica o que segura o log " +
                (d.LogSpace is { ReuseWait.Length: > 0 } ls ? $"(agora: {ls.ReuseWaitFriendly}) " : "") +
                "e libera espaço — backup de log, crescimento do arquivo ou um arquivo de log extra em outro disco — e religa o banco." + topoNote;
            return;
        }

        // 3) Corrupção de DADOS → restaurar de backup (ou restauração de página, se poucas páginas).
        if (d.DataCorruptionSuspected && !d.LogCorruptionSuspected)
        {
            // A restauração de página exige o modelo FULL/BULK_LOGGED (em SIMPLE ela só aborta) e o banco ONLINE:
            // num banco SUSPECT o SQL Server recusa ("only a file restore is currently possible", Msg 4309).
            var simple = "SIMPLE".Equals(d.BackupPolicy?.RecoveryModel, StringComparison.OrdinalIgnoreCase)
                         || "SIMPLE".Equals(d.Settings?.RecoveryModel, StringComparison.OrdinalIgnoreCase);
            var online = d.StateDesc.Trim().Equals("ONLINE", StringComparison.OrdinalIgnoreCase);
            var fewPages = !simple && online && d.SuspectPages.Count(p => p.IsActiveCorruption) is > 0 and <= 100;
            d.RecommendedAction = fewPages ? RecommendedActionKind.PageRestore : RecommendedActionKind.RestoreFromBackup;
            d.Summary = "Há corrupção nos arquivos de DADOS (.mdf/.ndf).";
            d.Recommendation = (fewPages
                ? "Poucas páginas afetadas: prefira RESTAURAR APENAS AS PÁGINAS (page restore), preservando o resto. "
                : "Prefira RESTAURAR de backup. ")
                + "Sem backup, resta o reparo COM perda de dados (DBCC CHECKDB), que descarta o que está corrompido." + topoNote;
            return;
        }

        // 4) Corrupção apenas no LOG → reconstruir o log.
        if (d.LogCorruptionSuspected && !d.DataCorruptionSuspected)
        {
            d.RecommendedAction = RecommendedActionKind.RebuildLog;
            d.Summary = "Os arquivos de DADOS parecem íntegros e o LOG de transações (.ldf) apresenta corrupção.";
            // O modo seguro (SET ONLINE / REPAIR_REBUILD) não recria log; quem o recria é o REPAIR_ALLOW_DATA_LOSS
            // em EMERGENCY, oferecido depois que o modo seguro não basta, ou o detach/attach do último recurso.
            d.Recommendation = "Comece por 'Reparar (modo seguro)'. Se não bastar, a ferramenta oferece o reparo COM possível perda de dados, que reconstrói o log no próprio servidor; " +
                "'Reconstruir o log' (último recurso) exige Administrador no servidor. Os dados já gravados nos arquivos de dados costumam ser preservados, " +
                "mas as transações que estavam em andamento no log se perdem." + topoNote;
            return;
        }

        // 5) Ambos corrompidos.
        if (d.DataCorruptionSuspected && d.LogCorruptionSuspected)
        {
            d.RecommendedAction = RecommendedActionKind.RestoreFromBackup;
            d.Summary = "Há indícios de corrupção tanto nos DADOS quanto no LOG.";
            d.Recommendation = "Prefira RESTAURAR de backup. Caso contrário, reparo COM perda de dados." + topoNote;
            return;
        }

        // 6) Disco baixo como causa provável de RECOVERY_PENDING.
        if (d.Disks.Any(x => x.IsLow))
        {
            d.RecommendedAction = RecommendedActionKind.FreeDiskSpace;
            d.Summary = "Pouco espaço livre em disco — causa comum de RECOVERY_PENDING / falha de recuperação.";
            d.Recommendation = "Libere espaço no(s) volume(s) do banco e tente 'Reparar (modo seguro)'." + topoNote;
            return;
        }

        d.RecommendedAction = RecommendedActionKind.SafeRepair;
        d.Summary = "Não foram encontradas evidências claras de corrupção. A causa pode ser outra (arquivo/recurso indisponível, permissão, etc.).";
        d.Recommendation = "Tente 'Reparar (modo seguro)' e verifique se os arquivos do banco existem e estão acessíveis ao SQL Server." + topoNote;
    }

    // ----------------------------------------------------------------------------------
    // Fluxos por estado
    // ----------------------------------------------------------------------------------

    /// <summary>OFFLINE → SET ONLINE (sem perda de dados).</summary>
    private async Task<RepairStatus> BringOnlineAsync(SqlSession s, DatabaseInfo db, IProgress<RepairLogEntry> p, CancellationToken ct)
    {
        var r = await RunStepAsync(s, db, p, "Religando o banco (SET ONLINE)", AlterDatabase(db.Name, "SET ONLINE"), ct);
        if (r.Success)
            await TrySetMultiUserAsync(s, db, p, ct);

        var st = await s.GetDatabaseStateAsync(db.Name, ct);
        return st.Equals("ONLINE", StringComparison.OrdinalIgnoreCase) ? RepairStatus.Success : RepairStatus.Failed;
    }

    /// <summary>RESTORING / RECOVERING → RESTORE DATABASE ... WITH RECOVERY (sem perda de dados).</summary>
    private async Task<RepairStatus> RestoreWithRecoveryAsync(SqlSession s, DatabaseInfo db, IProgress<RepairLogEntry> p, CancellationToken ct)
    {
        var r = await RunStepAsync(s, db, p, "Finalizando a recuperação (RESTORE WITH RECOVERY)", RestoreRecovery(db.Name), ct);
        var st = await s.GetDatabaseStateAsync(db.Name, ct);
        if (st.Equals("ONLINE", StringComparison.OrdinalIgnoreCase))
            return RepairStatus.Success;
        return r.Success ? RepairStatus.Success : RepairStatus.Failed;
    }

    /// <summary>RECOVERY_PENDING → tentar ONLINE; se não bastar, escalar para EMERGENCY + DBCC.</summary>
    private async Task<RepairStatus> RecoveryPendingAsync(SqlSession s, DatabaseInfo db, string connectionString, bool allowDataLoss, ParentRecoveryOptions? recovery, IProgress<RepairLogEntry> p, CancellationToken ct)
    {
        Report(p, db.Name, RepairLogLevel.Info, "RECOVERY_PENDING: tentando recolocar o banco ONLINE...");
        var r = await RunStepAsync(s, db, p, "Tentando SET ONLINE", AlterDatabase(db.Name, "SET ONLINE"), ct);
        if (r.Success)
        {
            var st = await s.GetDatabaseStateAsync(db.Name, ct);
            if (st.Equals("ONLINE", StringComparison.OrdinalIgnoreCase))
                return RepairStatus.Success;
        }

        Report(p, db.Name, RepairLogLevel.Warning, "SET ONLINE não resolveu. Escalando para reparo via EMERGENCY + DBCC CHECKDB.");
        return await EmergencyRepairFlowAsync(s, db, connectionString, allowDataLoss, recovery, p, ct);
    }

    /// <summary>
    /// SUSPECT / EMERGENCY (e fallback de RECOVERY_PENDING). Sequência validada em engine real:
    /// EMERGENCY → SINGLE_USER → diagnóstico → recuperação normal (SET ONLINE) → REPAIR_REBUILD
    /// (sem perda, exige o banco ONLINE) → verificação. Se não bastar e for autorizado:
    /// REPAIR_ALLOW_DATA_LOSS (em EMERGENCY).
    /// Importante: o SQL Server NÃO permite REPAIR_REBUILD com o banco em modo EMERGENCY
    /// (Msg 7901); por isso a recuperação normal é tentada primeiro.
    /// </summary>
    private async Task<RepairStatus> EmergencyRepairFlowAsync(SqlSession s, DatabaseInfo db, string connectionString, bool allowDataLoss, ParentRecoveryOptions? recovery, IProgress<RepairLogEntry> p, CancellationToken ct)
    {
        if (AppConstants.IsSystemDatabase(db.Name))
        {
            Report(p, db.Name, RepairLogLevel.Error,
                "Banco de sistema não pode ser reparado por esta ferramenta. Acione o DBA/suporte.");
            return RepairStatus.Failed;
        }

        if (await IsTopologyRiskyAsync(s, db, p, ct))
            return RepairStatus.Failed;

        // 1. EMERGENCY (torna o banco acessível para diagnóstico)
        await RunStepAsync(s, db, p, "Colocando o banco em modo EMERGENCY", AlterDatabase(db.Name, "SET EMERGENCY"), ct);

        // 2. SINGLE_USER WITH ROLLBACK IMMEDIATE (acesso exclusivo durante a operação)
        var single = await RunStepAsync(s, db, p,
            "Obtendo acesso exclusivo (SINGLE_USER WITH ROLLBACK IMMEDIATE)",
            AlterDatabase(db.Name, "SET SINGLE_USER WITH ROLLBACK IMMEDIATE"), ct);
        if (!single.Success)
        {
            Report(p, db.Name, RepairLogLevel.Error, "Não foi possível obter acesso exclusivo ao banco.");
            await TrySetMultiUserAsync(s, db, p, ct);
            return RepairStatus.Failed;
        }

        // 3. Diagnóstico (somente leitura) — funciona em modo EMERGENCY
        Report(p, db.Name, RepairLogLevel.Info, "Executando diagnóstico (DBCC CHECKDB)...");
        var diag = await RunCheckDbStepAsync(s, db, p, "Diagnóstico DBCC CHECKDB", db.Name, ct);
        Report(p, db.Name, diag.Success ? RepairLogLevel.Success : RepairLogLevel.Warning,
            diag.Success
                ? "Diagnóstico não encontrou erros de consistência."
                : "Diagnóstico encontrou inconsistências. Tentando reparo SEM perda de dados...");

        // 4. Reparo SEM perda de dados:
        //    (a) recuperação normal (SET ONLINE); (b) se ONLINE, REPAIR_REBUILD; (c) verificação.
        //    Com o banco ONLINE + SINGLE_USER, a sessão OCUPA a vaga (USE): sem isso, a aplicação (que
        //    costuma reconectar sozinha) toma a única vaga entre um comando e outro, o REPAIR_REBUILD
        //    falha por "banco em uso" e o fluxo escalaria à toa para o reparo COM perda.
        bool safeOk = false;
        var occupied = false;
        try
        {
            Report(p, db.Name, RepairLogLevel.Info, "Tentando recuperação SEM perda de dados (SET ONLINE)...");
            await RunStepAsync(s, db, p, "Recuperação normal (SET ONLINE)", AlterDatabase(db.Name, "SET ONLINE"), ct);

            var stateAfterOnline = await s.GetDatabaseStateAsync(db.Name, ct);
            if (stateAfterOnline.Equals("ONLINE", StringComparison.OrdinalIgnoreCase))
            {
                await TryOccupySingleUserSlotAsync(s, db, p, ct);
                occupied = true;
                Report(p, db.Name, RepairLogLevel.Success,
                    "Banco recuperado para ONLINE. Aplicando REPAIR_REBUILD (sem perda) e verificando...");
                var rebuild = await RunStepAsync(s, db, p, "Reparo SEM perda de dados (REPAIR_REBUILD)",
                    Dbcc(db.Name, "REPAIR_REBUILD", noInfoMsgs: false), ct);
                _deferredTransactions |= HitDeferredTransactions(rebuild);
                var verify = await RunCheckDbStepAsync(s, db, p, "Verificação pós-reparo (DBCC CHECKDB)", db.Name, ct);
                // O REPAIR_REBUILD relata como erro (severidade 16) cada problema que ENCONTROU, mesmo quando o
                // conserta ("The error has been repaired"): quem diz se resolveu é a verificação logo depois.
                safeOk = verify.Success;
            }
            else
            {
                Report(p, db.Name, RepairLogLevel.Warning,
                    $"A recuperação normal não foi suficiente (estado atual: {stateAfterOnline}).");
            }

            if (safeOk)
            {
                await TrySetMultiUserAsync(s, db, p, ct);
                return RepairStatus.Success;
            }

            // 4b. Antes de escalar para perda de dados: se a corrupção remanescente está restrita a
            //     índices NÃO clusterizados, reconstruí-los resolve SEM perda de dados.
            var onlineNow = (await s.GetDatabaseStateAsync(db.Name, ct)).Equals("ONLINE", StringComparison.OrdinalIgnoreCase);
            if (onlineNow)
            {
                var structured = await s.CheckDbWithResultsAsync(db.Name, repairOption: null, ct);
                if (structured.HasErrors && structured.AllErrorsAreNonClusteredIndex)
                {
                    Report(p, db.Name, RepairLogLevel.Info,
                        "Corrupção restrita a índices não-clusterizados — reconstruindo os índices (SEM perda de dados)...");
                    var rebuilt = await RebuildNonClusteredIndexesAsync(s, db, structured, p, ct);
                    var verifyIdx = await RunCheckDbStepAsync(s, db, p, "Verificação pós-reconstrução de índices (DBCC CHECKDB)", db.Name, ct);
                    if (rebuilt && verifyIdx.Success)
                    {
                        await s.ClearSuspectPagesAsync(db.Name, ct);
                        await TrySetMultiUserAsync(s, db, p, ct);
                        return RepairStatus.Success;
                    }
                }
            }

            // 5. Reparo seguro insuficiente
            if (!allowDataLoss)
            {
                Report(p, db.Name, RepairLogLevel.Warning,
                    "O reparo SEM perda de dados não foi suficiente. É necessária a CONFIRMAÇÃO do usuário para o reparo COM possível perda de dados.");
                Report(p, db.Name, RepairLogLevel.Info,
                    "Dica: se o problema for o LOG de transações (corrompido/ausente), considere a opção 'Reconstruir log (último recurso)'.");
                await TrySetMultiUserAsync(s, db, p, ct);
                return RepairStatus.NeedsDataLossConfirmation;
            }
        }
        finally
        {
            // Devolve o contexto a master antes de voltar a EMERGENCY (ou de encerrar o fluxo).
            if (occupied) await TryReleaseSingleUserSlotAsync(s, CancellationToken.None);
        }

        // 6. Reparo COM possível perda de dados — backup de segurança antes (best-effort)
        Report(p, db.Name, RepairLogLevel.Warning, "ATENÇÃO: iniciando reparo COM POSSÍVEL PERDA DE DADOS (autorizado pelo usuário).");

        // O banco precisa estar novamente em SINGLE_USER (o passo anterior pode tê-lo liberado).
        await RunStepAsync(s, db, p, "Colocando o banco em modo EMERGENCY", AlterDatabase(db.Name, "SET EMERGENCY"), ct);
        var single2 = await RunStepAsync(s, db, p,
            "Obtendo acesso exclusivo (SINGLE_USER WITH ROLLBACK IMMEDIATE)",
            AlterDatabase(db.Name, "SET SINGLE_USER WITH ROLLBACK IMMEDIATE"), ct);
        if (!single2.Success)
        {
            Report(p, db.Name, RepairLogLevel.Error, "Não foi possível obter acesso exclusivo para o reparo destrutivo.");
            await TrySetMultiUserAsync(s, db, p, ct);
            return RepairStatus.Failed;
        }

        var safety = await TrySafetyNetAsync(s, db, p, ct);
        var rowsBefore = await s.GetTableRowCountsAsync(db.Name, ct);

        // Cópia das linhas ainda legíveis das tabelas danificadas (salvamento mesmo sem backup).
        var extraction = await TryPreRepairExtractionAsync(s, db, rowsBefore, corruptObjectIds: null, recovery, p, ct);

        var repairStep = await RunStepAsync(s, db, p, "Reparo COM possível PERDA DE DADOS (REPAIR_ALLOW_DATA_LOSS)",
            Dbcc(db.Name, "REPAIR_ALLOW_DATA_LOSS", noInfoMsgs: false), ct);
        _deferredTransactions |= HitDeferredTransactions(repairStep);

        // Se o banco continuar em EMERGENCY (somente leitura) após o reparo, nada do que vem a seguir
        // (recuperar linhas, reconciliar FKs, reseed) conseguiria gravar: religa-o.
        if ((await s.GetDatabaseStateAsync(db.Name, ct)).Equals("EMERGENCY", StringComparison.OrdinalIgnoreCase))
            await RunStepAsync(s, db, p, "Religando o banco após o reparo (SET ONLINE)", AlterDatabase(db.Name, "SET ONLINE"), ct);

        var verifyFinal = await RunCheckDbStepAsync(s, db, p, "Verificação final (DBCC CHECKDB)", db.Name, ct);

        var occupiedAfter = false;
        if ((await s.GetDatabaseStateAsync(db.Name, ct)).Equals("ONLINE", StringComparison.OrdinalIgnoreCase))
        {
            await TryOccupySingleUserSlotAsync(s, db, p, ct);
            occupiedAfter = true;
        }
        try
        {
            // Devolve do backup as linhas que o reparo acabou de descartar (quando dá para saber EXATAMENTE
            // quais eram) — antes da reconciliação de FKs, pois um pai recuperado aqui deixa de gerar órfão.
            await TryRecoverRowsLostByRepairAsync(s, db, rowsBefore, repairStep, safety, connectionString, recovery, p, ct,
                extraction: extraction);

            // O REPAIR_ALLOW_DATA_LOSS desaloca páginas SEM respeitar constraints — pode deixar
            // chaves estrangeiras órfãs. Como a perda de dados já foi autorizada, reconciliamos.
            await TryReconcileConstraintsAsync(s, db, p, allowDataLoss: true, connectionString, recovery, ct);
            await TryReseedIdentitiesAsync(s, db, p, ct);
        }
        finally
        {
            if (occupiedAfter) await TryReleaseSingleUserSlotAsync(s, CancellationToken.None);
        }

        await TrySetMultiUserAsync(s, db, p, ct);
        if (verifyFinal.Success)
            await TryPostRepairFullBackupAsync(s, db, p, ct);
        return verifyFinal.Success ? RepairStatus.Success : RepairStatus.Failed;
    }

    /// <summary>
    /// Banco ONLINE: pode estar realmente saudável OU corrompido sem o estado indicar (corrupção
    /// física que só o CHECKDB revela, ou FK órfã que só o CHECKCONSTRAINTS revela). Verifica e,
    /// se houver corrupção, repara em SINGLE_USER (sem precisar de EMERGENCY, pois já está ONLINE):
    /// índices não-clusterizados / REPAIR_REBUILD (sem perda) → e, só com confirmação,
    /// REPAIR_ALLOW_DATA_LOSS / remoção de órfãos (com perda).
    /// </summary>
    private async Task<RepairStatus> OnlineRepairFlowAsync(SqlSession s, DatabaseInfo db, string connectionString, bool allowDataLoss, ParentRecoveryOptions? recovery, IProgress<RepairLogEntry> p, CancellationToken ct)
    {
        Report(p, db.Name, RepairLogLevel.Info, "Banco ONLINE: verificando consistência física e lógica antes de decidir...");
        var report = await s.CheckDbWithResultsAsync(db.Name, repairOption: null, ct);
        // Sem o CHECKDB não há como afirmar que o banco está íntegro (antes, isto virava "nada a reparar").
        if (report.DeferredTransactions)
        {
            _deferredTransactions = true;
            Report(p, db.Name, RepairLogLevel.Error, DeferredTransactionsHint);
            return RepairStatus.Failed;
        }
        if (!report.Executed)
        {
            Report(p, db.Name, RepairLogLevel.Error,
                "Não foi possível rodar o DBCC CHECKDB neste banco agora — sem ele não dá para afirmar que está íntegro. Nada foi alterado; veja o registro e diagnostique de novo.");
            return RepairStatus.Failed;
        }
        var hasPhysical = report.HasErrors;

        // Com corrupção FÍSICA presente, o CHECKCONSTRAINTS é adiado: ele LÊ os dados pelos
        // índices corrompidos e um erro 824 (severidade 24) DERRUBA a conexão da sessão.
        // O físico é reparado primeiro; a reconciliação lógica já roda após o reparo.
        var violations = hasPhysical ? new List<ConstraintViolation>() : await s.CheckConstraintsAsync(db.Name, ct);
        var fkViolations = violations.Where(v => v.IsForeignKey).ToList();

        if (!hasPhysical && violations.Count == 0)
        {
            Report(p, db.Name, RepairLogLevel.Success, "DBCC CHECKDB e CHECKCONSTRAINTS sem erros — banco íntegro.");
            return RepairStatus.NoActionNeeded;
        }

        if (AppConstants.IsSystemDatabase(db.Name))
        {
            Report(p, db.Name, RepairLogLevel.Error,
                "Banco de sistema corrompido: não pode ser reparado por esta ferramenta. Acione o DBA/suporte.");
            return RepairStatus.Failed;
        }
        if (await IsTopologyRiskyAsync(s, db, p, ct))
            return RepairStatus.Failed;

        // ---- Corrupção FÍSICA presente ----
        if (hasPhysical)
        {
            Report(p, db.Name, RepairLogLevel.Warning,
                $"DBCC CHECKDB acusou {report.ErrorCount} erro(s) num banco ONLINE. Reparando em acesso exclusivo...");
            var single = await RunStepAsync(s, db, p,
                "Obtendo acesso exclusivo (SINGLE_USER WITH ROLLBACK IMMEDIATE)",
                AlterDatabase(db.Name, "SET SINGLE_USER WITH ROLLBACK IMMEDIATE"), ct);
            if (!single.Success)
            {
                Report(p, db.Name, RepairLogLevel.Error, "Não foi possível obter acesso exclusivo ao banco.");
                await TrySetMultiUserAsync(s, db, p, ct);
                return RepairStatus.Failed;
            }
            await TryOccupySingleUserSlotAsync(s, db, p, ct);

            try
            {
                // a) Corrupção só em índices não-clusterizados → reconstrução SEM perda.
                if (report.AllErrorsAreNonClusteredIndex)
                {
                    Report(p, db.Name, RepairLogLevel.Info,
                        "Corrupção restrita a índices não-clusterizados — reconstruindo (SEM perda de dados)...");
                    var rebuilt = await RebuildNonClusteredIndexesAsync(s, db, report, p, ct);
                    var verifyIdx = await RunCheckDbStepAsync(s, db, p, "Verificação pós-reconstrução de índices (DBCC CHECKDB)", db.Name, ct);
                    if (rebuilt && verifyIdx.Success)
                    {
                        await s.ClearSuspectPagesAsync(db.Name, ct);
                        return RepairStatus.Success;
                    }
                }

                // b) REPAIR_REBUILD (sem perda; exige ONLINE + SINGLE_USER — ambos satisfeitos).
                Report(p, db.Name, RepairLogLevel.Info, "Tentando reparo SEM perda de dados (REPAIR_REBUILD)...");
                var rebuild = await RunStepAsync(s, db, p, "Reparo SEM perda (REPAIR_REBUILD)",
                    Dbcc(db.Name, "REPAIR_REBUILD", noInfoMsgs: false), ct);
                _deferredTransactions |= HitDeferredTransactions(rebuild);
                var verify = await RunCheckDbStepAsync(s, db, p, "Verificação pós-reparo (DBCC CHECKDB)", db.Name, ct);
                // O REPAIR_REBUILD relata como erro cada problema que encontrou, mesmo consertado: vale a verificação.
                if (verify.Success)
                    return RepairStatus.Success;

                // c) Precisa de REPAIR_ALLOW_DATA_LOSS.
                if (!allowDataLoss)
                {
                    Report(p, db.Name, RepairLogLevel.Warning,
                        "O reparo SEM perda não foi suficiente. É necessária CONFIRMAÇÃO para o reparo COM possível perda de dados.");
                    return RepairStatus.NeedsDataLossConfirmation;
                }

                Report(p, db.Name, RepairLogLevel.Warning, "ATENÇÃO: reparo COM POSSÍVEL PERDA DE DADOS (autorizado).");
                var safety = await TrySafetyNetAsync(s, db, p, ct);
                var rowsBefore = await s.GetTableRowCountsAsync(db.Name, ct);
                var corruptIds = report.Details.Where(d => d.ObjectId != 0).Select(d => d.ObjectId).Distinct().ToList();
                var extraction = await TryPreRepairExtractionAsync(s, db, rowsBefore, corruptIds, recovery, p, ct);
                var repairStep = await RunStepAsync(s, db, p, "Reparo COM possível PERDA DE DADOS (REPAIR_ALLOW_DATA_LOSS)",
                    Dbcc(db.Name, "REPAIR_ALLOW_DATA_LOSS", noInfoMsgs: false), ct);
                _deferredTransactions |= HitDeferredTransactions(repairStep);
                var verifyFinal = await RunCheckDbStepAsync(s, db, p, "Verificação final (DBCC CHECKDB)", db.Name, ct);
                await TryRecoverRowsLostByRepairAsync(s, db, rowsBefore, repairStep, safety, connectionString, recovery, p, ct,
                    corruptObjectIds: corruptIds, extraction: extraction);
                await TryReconcileConstraintsAsync(s, db, p, allowDataLoss: true, connectionString, recovery, ct);
                await TryReseedIdentitiesAsync(s, db, p, ct);
                if (verifyFinal.Success)
                    await TryPostRepairFullBackupAsync(s, db, p, ct);
                return verifyFinal.Success ? RepairStatus.Success : RepairStatus.Failed;
            }
            finally
            {
                await TryReleaseSingleUserSlotAsync(s, ct);
                await TrySetMultiUserAsync(s, db, p, ct);
            }
        }

        // ---- Apenas corrupção LÓGICA (físico íntegro) ----
        if (fkViolations.Count == 0)
        {
            Report(p, db.Name, RepairLogLevel.Warning,
                "Há violações de CHECK e/ou catálogo (sem FK órfã removível automaticamente). Revise os dados ou restaure de backup.");
            return RepairStatus.Failed;
        }

        if (!allowDataLoss)
        {
            Report(p, db.Name, RepairLogLevel.Warning,
                $"Encontrados {fkViolations.Count} registro(s) órfão(s) de chave estrangeira. A remoção é destrutiva e requer CONFIRMAÇÃO.");
            return RepairStatus.NeedsDataLossConfirmation;
        }

        var singleL = await RunStepAsync(s, db, p,
            "Obtendo acesso exclusivo (SINGLE_USER WITH ROLLBACK IMMEDIATE)",
            AlterDatabase(db.Name, "SET SINGLE_USER WITH ROLLBACK IMMEDIATE"), ct);
        if (!singleL.Success)
        {
            await TrySetMultiUserAsync(s, db, p, ct);
            return RepairStatus.Failed;
        }
        await TryOccupySingleUserSlotAsync(s, db, p, ct);
        try
        {
            await TrySafetyNetAsync(s, db, p, ct);
            await TryReconcileConstraintsAsync(s, db, p, allowDataLoss: true, connectionString, recovery, ct);
            var remaining = await s.CheckConstraintsAsync(db.Name, ct);
            return remaining.Count(v => v.IsForeignKey) == 0 ? RepairStatus.Success : RepairStatus.Failed;
        }
        finally
        {
            await TryReleaseSingleUserSlotAsync(s, ct);
            await TrySetMultiUserAsync(s, db, p, ct);
        }
    }

    /// <summary>
    /// Antes do REPAIR_ALLOW_DATA_LOSS, guarda num banco de salvamento as linhas ainda LEGÍVEIS das tabelas
    /// danificadas (ver <see cref="PreRepairExtractor"/>). As tabelas vêm dos objetos com erro no CHECKDB
    /// (estruturado; roda aqui quando o chamador não os tem). Desligado junto com "devolver as linhas
    /// descartadas" (<see cref="ParentRecoveryOptions.RecoverLostRows"/>). Best-effort: nunca impede o reparo.
    /// </summary>
    private async Task<PreRepairExtraction?> TryPreRepairExtractionAsync(
        SqlSession s, DatabaseInfo db, IReadOnlyDictionary<string, long> rowsBefore, IReadOnlyCollection<int>? corruptObjectIds,
        ParentRecoveryOptions? recovery, IProgress<RepairLogEntry> p, CancellationToken ct)
    {
        if (recovery is { RecoverLostRows: false }) return null;
        try
        {
            var ids = corruptObjectIds;
            if (ids is null)
            {
                Report(p, db.Name, RepairLogLevel.Info, "Identificando as tabelas danificadas antes do reparo (DBCC CHECKDB estruturado)...");
                var rep = await s.CheckDbWithResultsAsync(db.Name, repairOption: null, ct);
                ids = rep.Details.Where(d => d.ObjectId > 100).Select(d => d.ObjectId).Distinct().ToList();
            }

            var tables = new List<string>();
            foreach (var oid in ids.Where(i => i > 100).Distinct())
            {
                var name = await s.ResolveSchemaTableAsync(db.Name, oid, ct);
                if (name.Length > 0 && (rowsBefore.Count == 0 || rowsBefore.ContainsKey(name)) && !tables.Contains(name, StringComparer.OrdinalIgnoreCase))
                    tables.Add(name);
            }
            if (tables.Count == 0)
            {
                Report(p, db.Name, RepairLogLevel.Info, "Nenhuma tabela de usuário identificada como danificada — cópia de salvamento dispensada.");
                return null;
            }

            var extractor = new PreRepairExtractor((lvl, msg) => Report(p, db.Name, lvl, msg));
            return await extractor.ExtractAsync(s, db.Name, tables, rowsBefore, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Report(p, db.Name, RepairLogLevel.Warning, "Cópia de salvamento antes do reparo não concluída: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Depois de um REPAIR_ALLOW_DATA_LOSS, devolve ao banco as linhas que o reparo DESCARTOU. Duas fontes,
    /// nesta ordem:
    /// (1) BACKUPS: uma cópia restaurada da cadeia ATÉ O INSTANTE DO REPARO (FULL → diferencial → logs → o
    ///     tail-log da rede de segurança) — traz inclusive o que estava nas páginas destruídas;
    /// (2) CÓPIA DE SALVAMENTO feita antes do reparo — funciona SEM backup, para tudo o que ainda era legível.
    /// As tabelas afetadas vêm (a) das páginas que o próprio reparo diz ter desalocado ("object ID n"),
    /// (b) das tabelas cuja contagem de linhas caiu e (c) dos objetos com erro no CHECKDB. Só insere o que
    /// falta, por chave primária, com gatilhos desligados.
    ///
    /// Segurança: as duas fontes retratam EXATAMENTE o momento anterior ao reparo — toda linha que está nelas
    /// e não no banco foi descartada pelo reparo. Um backup de um momento ANTERIOR (sem tail-log, cadeia
    /// quebrada) ressuscitaria registros excluídos de propósito, então ele não é usado. Ao final, resume em
    /// linguagem simples o que ainda ficou perdido. Best-effort: nunca derruba o reparo.
    /// </summary>
    private async Task TryRecoverRowsLostByRepairAsync(
        SqlSession s, DatabaseInfo db, IReadOnlyDictionary<string, long> rowsBefore, CommandExecutionResult repairStep,
        SafetyNet safety, string connectionString, ParentRecoveryOptions? recovery, IProgress<RepairLogEntry> p,
        CancellationToken ct, IEnumerable<int>? corruptObjectIds = null, PreRepairExtraction? extraction = null)
    {
        var keepSalvage = false;
        try
        {
            if (recovery is { RecoverLostRows: false }) return;

            var after = await s.GetTableRowCountsAsync(db.Name, ct);
            var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (table, before) in rowsBefore)
                if (after.TryGetValue(table, out var a) && a < before) affected.Add(table);

            var objectIds = ParseRepairObjectIds(repairStep.InfoMessages.Concat(repairStep.ErrorMessages))
                .Concat(corruptObjectIds ?? Enumerable.Empty<int>()).Distinct();
            foreach (var oid in objectIds)
            {
                var name = await s.ResolveSchemaTableAsync(db.Name, oid, ct);
                if (name.Length > 0 && (rowsBefore.Count == 0 || rowsBefore.ContainsKey(name))) affected.Add(name);
            }

            if (affected.Count == 0)
            {
                if (rowsBefore.Count == 0)
                {
                    Report(p, db.Name, RepairLogLevel.Warning, "Não foi possível medir as linhas antes do reparo (metadados ilegíveis) — confira os dados; " +
                        "para revisar o que mudou use 'Recuperar para cópia' + 'Assistente de salvamento'.");
                    keepSalvage = extraction is { Performed: true };
                    return;
                }
                Report(p, db.Name, RepairLogLevel.Success, "O reparo não descartou linhas das tabelas de usuário (contagem antes × depois).");
                _lossSummary = "Nenhuma linha foi perdida.";
                return;
            }

            Report(p, db.Name, RepairLogLevel.Warning,
                $"O reparo descartou dados em {affected.Count} tabela(s): " +
                string.Join("; ", affected.Take(10).Select(t =>
                    $"{t} ({rowsBefore.GetValueOrDefault(t):N0} → {after.GetValueOrDefault(t):N0})")) + ".");

            // (1) Backups até o instante do reparo.
            var hasSalvage = extraction is { Performed: true };
            if (!safety.TailLogCaptured)
            {
                Report(p, db.Name, RepairLogLevel.Info,
                    "Sem o estado EXATO de antes do reparo nos backups (sem tail-log: modelo SIMPLE ou log ilegível) — um backup mais antigo " +
                    "traria de volta também registros excluídos de propósito, então ele não é usado." +
                    (hasSalvage ? " Usando a cópia de salvamento feita antes do reparo." : string.Empty));
            }
            else
            {
                var plan = await _restore.PlanAvailableAsync(s, db.Name, h => BackupChainPlanner.Plan(db.Name, h, target: null), ct);
                var lastLog = plan.Logs.LastOrDefault()?.BackupFinishDate ?? DateTime.MinValue;
                if (!plan.IsComplete || lastLog < safety.StartedAt.AddMinutes(-1))
                    Report(p, db.Name, RepairLogLevel.Info,
                        "A cadeia de backups não chega até o instante do reparo (" + plan.Summary + ")" +
                        (hasSalvage ? " — usando a cópia de salvamento feita antes do reparo." : "."));
                else
                    await TryReturnRowsFromBackupChainAsync(s, db, affected, connectionString, p, ct);
            }

            // (2) Cópia de salvamento (sem backup). Inserir só o que falta torna as duas fontes complementares.
            if (hasSalvage)
            {
                var extractor = new PreRepairExtractor((lvl, msg) => Report(p, db.Name, lvl, msg));
                var (returned, notReturned) = await extractor.ReturnRowsAsync(s, db.Name, extraction!, affected, ct);
                Report(p, db.Name, returned > 0 ? RepairLogLevel.Success : RepairLogLevel.Info,
                    returned > 0
                        ? $"{returned:N0} linha(s) descartada(s) pelo reparo foram DEVOLVIDAS a partir da cópia de salvamento."
                        : "A cópia de salvamento não tinha linhas a devolver (o que o reparo descartou já estava ilegível antes dele).");
                keepSalvage = notReturned.Count > 0 || extraction!.Tables.Any(t => t.PartialRows > 0);
            }

            // (3) O que ainda ficou perdido — em linguagem simples, para quem vai explicar ao cliente.
            var final = await s.GetTableRowCountsAsync(db.Name, ct);
            var lost = affected
                .Select(t => (Table: t, Rows: rowsBefore.GetValueOrDefault(t) - final.GetValueOrDefault(t)))
                .Where(x => x.Rows > 0).OrderByDescending(x => x.Rows).ToList();
            if (lost.Count == 0)
            {
                // Nada ficou faltando (ex.: o backup trouxe tudo): as linhas parciais da cópia de salvamento não têm mais
                // o que revisar — mantê-la só deixaria um banco a mais no servidor.
                keepSalvage = false;
                _lossSummary = "Todas as linhas que o reparo descartou foram recuperadas.";
                Report(p, db.Name, RepairLogLevel.Success, _lossSummary);
            }
            else
            {
                _lossSummary = $"{lost.Sum(x => x.Rows):N0} linha(s) não puderam ser recuperadas (estavam em páginas destruídas): " +
                               string.Join(", ", lost.Take(5).Select(x => $"{x.Table} ({x.Rows:N0})")) + ".";
                Report(p, db.Name, RepairLogLevel.Warning, _lossSummary +
                    " Só um backup íntegro traz essas linhas de volta ('Recuperar para cópia' + 'Assistente de salvamento').");
            }
        }
        catch (OperationCanceledException) { keepSalvage = extraction is { Performed: true }; throw; }
        catch (Exception ex)
        {
            keepSalvage = extraction is { Performed: true };
            Report(p, db.Name, RepairLogLevel.Warning, "Recuperação das linhas descartadas não concluída: " + ex.Message);
        }
        finally
        {
            if (extraction is { SalvageDatabase.Length: > 0 } x)
            {
                if (keepSalvage)
                {
                    _keptSalvageDb = x.SalvageDatabase;
                    Report(p, db.Name, RepairLogLevel.Warning,
                        $"A cópia de salvamento [{x.SalvageDatabase}] foi MANTIDA: há linhas que não voltaram sozinhas (tabela sem chave primária, " +
                        "linhas parciais ou erro ao devolver). Use o 'Assistente de salvamento' ou acione o DBA para revisá-las.");
                }
                else if (!await s.DropDatabaseBestEffortAsync(x.SalvageDatabase, CancellationToken.None))
                    Report(p, db.Name, RepairLogLevel.Warning, $"Não foi possível descartar a cópia de salvamento [{x.SalvageDatabase}] — remova-a manualmente.");
            }
        }
    }

    /// <summary>
    /// Restaura numa cópia temporária a cadeia de backups até o instante do reparo e devolve, por chave
    /// primária, as linhas que faltam nas tabelas afetadas. Descarta a cópia ao final.
    /// </summary>
    private async Task TryReturnRowsFromBackupChainAsync(
        SqlSession s, DatabaseInfo db, IReadOnlyCollection<string> affected, string connectionString, IProgress<RepairLogEntry> p, CancellationToken ct)
    {
        Report(p, db.Name, RepairLogLevel.Info,
            "Recuperando as linhas descartadas: restaurando a cadeia de backups até o instante do reparo numa cópia temporária " +
            "(pode demorar conforme o tamanho do banco)...");
        var scratch = $"{AppConstants.SanitizeToken(db.Name, "banco")}_LostRows_{DateTime.Now:yyyyMMddHHmmss}_{Guid.NewGuid().ToString("N")[..6]}";
        try
        {
            var copy = await _restore.RestoreAsCopyAsync(db.Name, scratch, connectionString, p, ct);
            if (copy.Status != RepairStatus.Success)
            {
                Report(p, db.Name, RepairLogLevel.Warning, "Não foi possível montar a cópia de antes do reparo a partir dos backups.");
                return;
            }

            // Várias passadas: uma tabela-filha pode depender de um pai que só volta numa tabela seguinte.
            var pending = affected.ToList();
            var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var total = 0;
            for (var pass = 0; pass < 3 && pending.Count > 0; pass++)
            {
                var failed = new List<string>();
                foreach (var t in pending)
                {
                    try
                    {
                        var n = await s.SalvageTableAsync(scratch, db.Name, t, ct);
                        total += n;
                        errors.Remove(t);
                        if (n > 0) Report(p, db.Name, RepairLogLevel.Success, $"  ✔ {n} linha(s) de {t} devolvida(s) a partir do backup.");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { failed.Add(t); errors[t] = ex.Message; }
                }
                if (failed.Count == pending.Count) break; // nenhuma evolução nesta passada
                pending = failed;
            }
            foreach (var (t, msg) in errors)
                Report(p, db.Name, RepairLogLevel.Warning, $"  ⚠ {t}: não foi possível devolver as linhas automaticamente — {msg}");

            Report(p, db.Name, total > 0 ? RepairLogLevel.Success : RepairLogLevel.Info,
                total > 0
                    ? $"{total:N0} linha(s) descartada(s) pelo reparo foram RECUPERADAS do backup (sem duplicar; gatilhos desligados)."
                    : "Nenhuma linha precisou ser devolvida a partir do backup.");
        }
        finally
        {
            if (!await s.DropDatabaseBestEffortAsync(scratch, CancellationToken.None))
                Report(p, db.Name, RepairLogLevel.Warning, $"Não foi possível descartar a cópia temporária [{scratch}] — remova-a manualmente.");
        }
    }

    /// <summary>
    /// Extrai os object_id das mensagens do DBCC CHECKDB ... REPAIR_ALLOW_DATA_LOSS (ex.: "The page (1:153)
    /// ... has been deallocated from object ID 245575913, index ID 1 ..."), em inglês ou português.
    /// Público para testes.
    /// </summary>
    public static IReadOnlyList<int> ParseRepairObjectIds(IEnumerable<string> messages)
    {
        var ids = new List<int>();
        var rx = new System.Text.RegularExpressions.Regex(
            @"(?:object\s+ID|ID\s+(?:de|do)\s+objeto|objeto\s+(?:de\s+)?ID)\s+(-?\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        foreach (var m in messages)
            foreach (System.Text.RegularExpressions.Match x in rx.Matches(m ?? string.Empty))
                if (int.TryParse(x.Groups[1].Value, out var id) && id > 0 && !ids.Contains(id))
                    ids.Add(id);
        return ids;
    }

    /// <summary>
    /// Reconciliação de integridade referencial após um reparo com perda de dados: localiza as
    /// linhas que violam chaves estrangeiras (registros órfãos) e, quando a perda já foi
    /// autorizada, remove-as. Best-effort — nunca derruba o fluxo de reparo.
    /// </summary>
    private async Task TryReconcileConstraintsAsync(SqlSession s, DatabaseInfo db, IProgress<RepairLogEntry> p, bool allowDataLoss, string connectionString, ParentRecoveryOptions? recovery, CancellationToken ct)
    {
        try
        {
            var violations = await s.CheckConstraintsAsync(db.Name, ct);
            var fk = violations.Where(v => v.IsForeignKey).ToList();
            if (fk.Count == 0) return;

            Report(p, db.Name, RepairLogLevel.Warning,
                $"Integridade referencial: {fk.Count} violação(ões) de chave estrangeira (registros órfãos) após o reparo.");

            if (!allowDataLoss)
            {
                Report(p, db.Name, RepairLogLevel.Info,
                    "Use 'Corrigir integridade (remover registros órfãos)' para resolvê-las (operação destrutiva, requer confirmação).");
                return;
            }

            var (recovered, deleted, _) = await ReconcileForeignKeyOrphansAsync(s, db, fk, p, connectionString, recovery, ct);
            Report(p, db.Name, (recovered > 0 || deleted > 0) ? RepairLogLevel.Success : RepairLogLevel.Info,
                recovered > 0
                    ? $"Reconciliação de integridade concluída: {recovered} registro(s)-pai recuperado(s) do backup e {deleted} registro(s) órfão(s) removido(s)."
                    : $"Reconciliação de integridade concluída: {deleted} registro(s) órfão(s) removido(s).");
        }
        catch (OperationCanceledException) { throw; } // o cancelamento durante a restauração deve propagar
        catch
        {
            // best-effort: a reconciliação nunca deve derrubar o reparo.
        }
    }

    /// <summary>
    /// Reconciliação COMPARTILHADA de chaves estrangeiras órfãs (com a perda de dados já autorizada),
    /// usada tanto pela reconciliação pós-reparo quanto pela correção de integridade dedicada:
    /// (1) tenta RECUPERAR os registros-pai a partir do backup mais recente (best-effort);
    /// (2) recheca as violações REAIS remanescentes (a recuperação pode ter resolvido algumas/todas);
    /// (3) remove os órfãos que sobraram — exatamente como antes. Devolve
    /// (recuperados, removidos, houveFalhaNaRemoção) para o chamador compor sua mensagem/status.
    /// </summary>
    private async Task<(int recovered, int deleted, bool anyDeleteFailed)> ReconcileForeignKeyOrphansAsync(
        SqlSession session, DatabaseInfo db, IReadOnlyList<ConstraintViolation> fkViolations,
        IProgress<RepairLogEntry> progress, string connectionString, ParentRecoveryOptions? recovery, CancellationToken ct)
    {
        var chooseAnother = recovery?.ChooseAnotherBackup;
        var source = recovery?.InitialBackupPath;      // null = automático via histórico
        var totalRecovered = 0;
        IReadOnlyList<ConstraintViolation> current = fkViolations;
        // Backups já tentados: impede um loop sem fim caso o callback devolva repetidamente o mesmo
        // .bak (que não resolve). Cada arquivo distinto é tentado no máximo uma vez.
        var triedBackups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(source)) triedBackups.Add(source!);

        // Loop de recuperação: tenta a fonte atual; se sobrar órfão e houver callback, oferece OUTRO
        // .bak antes de qualquer remoção. Só sai para a exclusão quando não sobra órfão, quando não há
        // callback, ou quando o usuário opta por prosseguir.
        while (true)
        {
            var attempt = await TryRecoverParentRowsAsync(session, db, current, progress, connectionString, source, ct);
            totalRecovered += attempt.Recovered;

            // Recheca os órfãos REAIS restantes só quando algo mudou (recuperar um pai resolve a FK).
            // Sem recuperação, a lista atual ainda vale — evita um DBCC CHECKCONSTRAINTS extra (caro).
            IReadOnlyList<ConstraintViolation> remaining = attempt.Recovered > 0
                ? (await session.CheckConstraintsAsync(db.Name, ct)).Where(v => v.IsForeignKey).ToList()
                : current;

            if (remaining.Count == 0 || chooseAnother is null) { current = remaining; break; }

            var reason = attempt.RestoreSucceeded
                ? $"o backup não continha {remaining.Count} registro(s)-pai (recuperação parcial)"
                : attempt.Reason;
            Report(progress, db.Name, RepairLogLevel.Warning,
                $"Recuperação de registros-pai incompleta ({reason}). Aguardando o usuário escolher outro .bak ou prosseguir com a remoção...");

            string? another;
            try
            {
                another = await chooseAnother(new ParentRecoveryPrompt
                {
                    DatabaseName = db.Name, Recovered = totalRecovered, RemainingOrphans = remaining.Count,
                    Reason = reason
                }, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch { another = null; } // falha na interação não derruba o reparo — prossegue com a remoção

            if (string.IsNullOrWhiteSpace(another))
            {
                Report(progress, db.Name, RepairLogLevel.Info,
                    "Prosseguindo sem novos backups — os órfãos remanescentes serão removidos.");
                current = remaining;
                break;
            }

            if (!triedBackups.Add(another))
            {
                Report(progress, db.Name, RepairLogLevel.Info,
                    "Este backup já foi tentado e não resolveu — prosseguindo com a remoção dos órfãos remanescentes.");
                current = remaining;
                break;
            }

            Report(progress, db.Name, RepairLogLevel.Info, $"Nova tentativa de recuperação com o backup escolhido: {another}");
            source = another;
            current = remaining;   // a próxima tentativa mira apenas o que ainda está órfão
        }

        // Remoção dos órfãos que sobraram — MOVIDOS para a quarentena (reversível), não apagados.
        var deleted = 0;
        var anyDeleteFailed = false;
        var quarantineTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in current)
        {
            try
            {
                var (removed, qTable) = await session.QuarantineViolatingRowsAsync(db.Name, v, ct);
                deleted += removed;
                if (qTable is not null) quarantineTables.Add(qTable);
                Report(progress, db.Name, RepairLogLevel.Success, qTable is not null
                    ? $"  ✔ {removed} registro(s) órfão(s) movido(s) para a quarentena {qTable} — {v.Display}"
                    : $"  ✔ {removed} registro(s) órfão(s) removido(s) (quarentena indisponível; há backup de segurança) — {v.Display}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                anyDeleteFailed = true;
                Report(progress, db.Name, RepairLogLevel.Warning, $"  ⚠ Não foi possível remover órfãos de {v.Constraint}: {ex.Message}");
            }
        }
        if (quarantineTables.Count > 0)
            Report(progress, db.Name, RepairLogLevel.Info,
                $"As linhas removidas NÃO foram perdidas: estão guardadas em {string.Join(", ", quarantineTables)} " +
                "(com a data e a constraint de origem) e podem ser devolvidas quando o registro-pai for recadastrado.");
        return (totalRecovered, deleted, anyDeleteFailed);
    }

    /// <summary>
    /// Antes de excluir os registros filhos órfãos, tenta RECUPERAR automaticamente os registros-pai a
    /// partir do backup mais recente: restaura a cadeia em um banco paralelo/descartável, localiza cada
    /// linha-pai pelo valor EXATO da chave (o predicado gerado pelo próprio SQL Server em
    /// CHECKCONSTRAINTS) e a reinsere no banco vivo. Best-effort — pode não encontrar tudo, ou não haver
    /// backup. Devolve quantos registros-pai foram recuperados (inseridos ou já presentes). Nunca lança
    /// (exceto cancelamento): qualquer falha apenas recai na remoção de órfãos, como antes.
    /// </summary>
    private async Task<(int Recovered, bool RestoreSucceeded, string Reason)> TryRecoverParentRowsAsync(
        SqlSession session, DatabaseInfo db, IReadOnlyList<ConstraintViolation> fkViolations,
        IProgress<RepairLogEntry> progress, string connectionString, string? recoveryBackupPath, CancellationToken ct)
    {
        if (fkViolations.Count == 0) return (0, true, string.Empty);

        var useChosenFile = !string.IsNullOrWhiteSpace(recoveryBackupPath);
        if (!useChosenFile)
        {
            // Checagem barata: só vale a pena tentar se houver uma cadeia de backup COMPLETA (custo ~zero
            // no caso comum de não haver backup). Quando o usuário ESCOLHE um .bak, pulamos esta checagem
            // (o msdb pode nem conhecer o arquivo) e validamos o arquivo direto na restauração.
            // Usa o mesmo critério do restore de recuperação: FULL agendado mais recente (não COPY_ONLY),
            // sem logs — para não reproduzir a própria perda do pai na cópia. Backups cujo arquivo não
            // existe mais no servidor são descartados já aqui.
            BackupChainPlan plan;
            try { plan = await _restore.PlanAvailableAsync(session, db.Name, h => BackupChainPlanner.PlanBaseFullOnly(db.Name, h), ct); }
            catch (OperationCanceledException) { throw; }
            catch { plan = BackupChainPlanner.PlanBaseFullOnly(db.Name, new List<BackupSetInfo>()); }
            if (!plan.IsComplete || plan.BaseFull is null)
            {
                Report(progress, db.Name, RepairLogLevel.Info,
                    "Recuperação automática de registros-pai: sem backup completo disponível no histórico.");
                return (0, false, "não há backup completo no histórico do servidor");
            }
        }

        Report(progress, db.Name, RepairLogLevel.Warning,
            (useChosenFile
                ? $"Tentando RECUPERAR os registros-pai a partir do backup ESCOLHIDO ({recoveryBackupPath}) antes de remover os órfãos "
                : "Tentando RECUPERAR os registros-pai a partir do backup mais recente antes de remover os órfãos ")
            + "(melhor esforço — pode não encontrar tudo; a restauração pode demorar conforme o tamanho do backup).");

        // Sufixo único: o loop de recuperação pode criar vários bancos paralelos em sequência; a
        // precisão de segundos do timestamp sozinha poderia colidir entre tentativas rápidas.
        var scratch = $"{AppConstants.SanitizeToken(db.Name, "banco")}_OrphanRecovery_{DateTime.Now:yyyyMMddHHmmss}_{Guid.NewGuid().ToString("N")[..6]}";
        var recovered = 0;
        var restoreSucceeded = false;
        var failReason = string.Empty;
        try
        {
            var restore = await _restore.RestoreAsCopyAsync(db.Name, scratch, connectionString, progress, ct,
                baseFullOnly: true, explicitFullBackupPath: recoveryBackupPath);
            if (restore.Status != RepairStatus.Success)
            {
                Report(progress, db.Name, RepairLogLevel.Warning, "Não foi possível restaurar a cópia do backup para recuperação.");
                failReason = useChosenFile
                    ? "o arquivo escolhido não pôde ser restaurado (ilegível ou sem backup FULL)"
                    : "a restauração do backup do histórico falhou";
                return (0, false, failReason);
            }

            // Mapeamento pai cacheado por nome de constraint (não repetir a consulta ao catálogo).
            var mappingCache = new Dictionary<string, ForeignKeyParentMapping?>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in fkViolations)
            {
                ct.ThrowIfCancellationRequested();

                var fkName = Unbracket(v.Constraint);
                if (!mappingCache.TryGetValue(fkName, out var map))
                {
                    map = await session.GetForeignKeyParentMappingAsync(db.Name, fkName, ct);
                    mappingCache[fkName] = map;
                }
                if (map is null || map.ColumnPairs.Count == 0 || string.IsNullOrEmpty(map.ParentTable))
                {
                    Report(progress, db.Name, RepairLogLevel.Info,
                        $"  • Não foi possível resolver a tabela-pai de {v.Constraint} — será tratada na remoção de órfãos.");
                    continue;
                }

                var parentPredicate = RewritePredicateToParent(ParseEqualityPredicate(v.Where), map);
                if (parentPredicate is null)
                {
                    Report(progress, db.Name, RepairLogLevel.Info,
                        $"  • Não foi possível interpretar a chave de {v.Display} — será tratada na remoção de órfãos.");
                    continue;
                }

                var target = $"{map.ParentSchema}.{map.ParentTable}";
                var outcome = await session.TryInsertRecoveredParentRowAsync(db.Name, scratch, map.ParentSchema, map.ParentTable, parentPredicate, ct);
                switch (outcome)
                {
                    case ParentRecoveryOutcome.Inserted:
                        recovered++;
                        Report(progress, db.Name, RepairLogLevel.Success,
                            $"  ✔ Registro-pai recuperado do backup em {target} (WHERE {parentPredicate}) — {v.Constraint}.");
                        break;
                    case ParentRecoveryOutcome.AlreadyPresent:
                        recovered++;
                        Report(progress, db.Name, RepairLogLevel.Info, $"  • Registro-pai já presente em {target} — {v.Constraint}.");
                        break;
                    case ParentRecoveryOutcome.NotFoundInBackup:
                        Report(progress, db.Name, RepairLogLevel.Info,
                            $"  • Registro-pai não encontrado no backup para {target} (WHERE {parentPredicate}) — a linha filha será removida.");
                        break;
                    default:
                        Report(progress, db.Name, RepairLogLevel.Warning,
                            $"  ⚠ Não foi possível recuperar o registro-pai de {v.Constraint} — a linha filha será removida.");
                        break;
                }
            }

            restoreSucceeded = true;
            Report(progress, db.Name, recovered > 0 ? RepairLogLevel.Success : RepairLogLevel.Info,
                $"Recuperação de registros-pai concluída: {recovered} registro(s)-pai recuperado(s) do backup.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Report(progress, db.Name, RepairLogLevel.Warning, "Falha na recuperação de registros-pai: " + ex.Message);
            failReason = "erro inesperado ao recuperar: " + ex.Message;
        }
        finally
        {
            // Descarta o banco paralelo (mesmo padrão de limpeza de TestRestoreAsync/SimulateRepairAsync).
            var dropped = await session.DropDatabaseBestEffortAsync(scratch, CancellationToken.None);
            if (!dropped)
                Report(progress, db.Name, RepairLogLevel.Warning,
                    $"Não foi possível descartar o banco paralelo de recuperação [{scratch}] — remova-o manualmente.");
        }
        return (recovered, restoreSucceeded, failReason);
    }

    /// <summary>
    /// Faz o parse do predicado de igualdade gerado pelo SQL Server em DBCC CHECKCONSTRAINTS
    /// (ex.: "[ClienteId] = 42" ou "[A] = 1 AND [B] = N'x'"), devolvendo os pares (coluna, literal).
    /// Divide em AND de nível superior com um scanner que respeita literais entre aspas simples
    /// (inclusive aspas escapadas '') e identificadores entre colchetes — para não quebrar em um valor
    /// de string que contenha " AND ", nem num nome de coluna com caracteres especiais. O literal é
    /// preservado tal como veio (já é sintaxe T-SQL válida). Público para permitir testes unitários.
    /// </summary>
    public static List<(string Column, string Literal)> ParseEqualityPredicate(string where)
    {
        var result = new List<(string Column, string Literal)>();
        if (string.IsNullOrWhiteSpace(where)) return result;

        foreach (var conjunct in SplitTopLevelAnd(where))
        {
            var eq = FindTopLevelEquals(conjunct);
            if (eq < 0) continue;
            var column = conjunct[..eq].Trim();
            var literal = conjunct[(eq + 1)..].Trim();
            if (column.Length == 0 || literal.Length == 0) continue;
            result.Add((column, literal));
        }
        return result;
    }

    /// <summary>Reescreve o predicado trocando as colunas-FILHA (do CHECKCONSTRAINTS) pelas colunas-PAI
    /// correspondentes, reaproveitando o literal. Devolve null se alguma coluna do predicado não fizer
    /// parte do mapeamento da FK (por segurança, não recupera com um predicado incompleto).</summary>
    private static string? RewritePredicateToParent(List<(string Column, string Literal)> pairs, ForeignKeyParentMapping map)
    {
        // Exige correspondência 1-para-1 com as colunas da FK: um predicado parcial (menos colunas que
        // a chave) casaria com VÁRIAS linhas-pai no backup e inseriria registros a mais. Aborta por segurança.
        if (pairs.Count == 0 || pairs.Count != map.ColumnPairs.Count) return null;
        var clauses = new List<string>();
        foreach (var (column, literal) in pairs)
        {
            var child = Unbracket(column);
            var match = map.ColumnPairs.FirstOrDefault(cp => string.Equals(cp.ChildColumn, child, StringComparison.OrdinalIgnoreCase));
            if (match is null || string.IsNullOrEmpty(match.ParentColumn)) return null;
            clauses.Add($"[{match.ParentColumn.Replace("]", "]]")}] = {literal}");
        }
        return string.Join(" AND ", clauses);
    }

    /// <summary>Divide um predicado em conjuntos separados por AND de nível superior, respeitando
    /// literais entre aspas ('...', com '' escapado) e identificadores entre colchetes ([...]).</summary>
    private static List<string> SplitTopLevelAnd(string s)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        bool inQuote = false, inBracket = false;
        int depth = 0, i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (inQuote)
            {
                sb.Append(c);
                if (c == '\'')
                {
                    if (i + 1 < s.Length && s[i + 1] == '\'') { sb.Append('\''); i += 2; continue; }
                    inQuote = false;
                }
                i++;
                continue;
            }
            if (inBracket)
            {
                sb.Append(c);
                if (c == ']')
                {
                    if (i + 1 < s.Length && s[i + 1] == ']') { sb.Append(']'); i += 2; continue; }
                    inBracket = false;
                }
                i++;
                continue;
            }
            if (c == '\'') { inQuote = true; sb.Append(c); i++; continue; }
            if (c == '[') { inBracket = true; sb.Append(c); i++; continue; }
            if (c == '(') { depth++; sb.Append(c); i++; continue; }
            if (c == ')') { if (depth > 0) depth--; sb.Append(c); i++; continue; }
            if (depth == 0 && IsAndKeywordAt(s, i))
            {
                parts.Add(sb.ToString());
                sb.Clear();
                i += 3;
                continue;
            }
            sb.Append(c);
            i++;
        }
        parts.Add(sb.ToString());
        return parts;
    }

    private static bool IsAndKeywordAt(string s, int i)
    {
        if (i + 3 > s.Length) return false;
        if (string.Compare(s, i, "AND", 0, 3, StringComparison.OrdinalIgnoreCase) != 0) return false;
        char before = i == 0 ? ' ' : s[i - 1];
        char after = i + 3 < s.Length ? s[i + 3] : ' ';
        return !IsIdentifierChar(before) && !IsIdentifierChar(after);
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>Índice do primeiro '=' de nível superior (fora de aspas/colchetes/parênteses), ou -1.</summary>
    private static int FindTopLevelEquals(string s)
    {
        bool inQuote = false, inBracket = false;
        int depth = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (inQuote)
            {
                if (c == '\'')
                {
                    if (i + 1 < s.Length && s[i + 1] == '\'') { i++; continue; }
                    inQuote = false;
                }
                continue;
            }
            if (inBracket)
            {
                if (c == ']')
                {
                    if (i + 1 < s.Length && s[i + 1] == ']') { i++; continue; }
                    inBracket = false;
                }
                continue;
            }
            if (c == '\'') { inQuote = true; continue; }
            if (c == '[') { inBracket = true; continue; }
            if (c == '(') { depth++; continue; }
            if (c == ')') { if (depth > 0) depth--; continue; }
            if (c == '=' && depth == 0) return i;
        }
        return -1;
    }

    /// <summary>Remove um nível de QUOTENAME: "[Nome]]X]" → "Nome]X" (nomes do CHECKCONSTRAINTS vêm entre colchetes).</summary>
    private static string Unbracket(string name)
    {
        var s = name.Trim();
        if (s.Length >= 2 && s[0] == '[' && s[^1] == ']')
            return s[1..^1].Replace("]]", "]");
        return s;
    }

    // ----------------------------------------------------------------------------------
    // ÚLTIMO RECURSO: reconstrução do log de transações (recriar o .ldf)
    // ----------------------------------------------------------------------------------

    /// <summary>
    /// Operação de ÚLTIMO RECURSO para um banco irreparável por causa do log de transações.
    /// Reconstrói o .ldf: coloca em EMERGENCY, desanexa (detach), remove o arquivo de log
    /// antigo e reanexa com CREATE DATABASE ... FOR ATTACH_REBUILD_LOG (cria um log novo),
    /// trazendo o banco de volta a ONLINE. Compatível com SQL Server 2008 R2.
    ///
    /// IMPORTANTE: pode haver PERDA DE DADOS (transações não confirmadas são descartadas) e
    /// o banco pode ficar logicamente inconsistente — por isso roda um DBCC CHECKDB ao final.
    /// Os arquivos precisam estar acessíveis a partir desta máquina (servidor local). Se o
    /// SQL Server for remoto, a operação é abortada com orientação, sem desanexar o banco.
    /// </summary>
    public async Task<RepairResult> RebuildTransactionLogAsync(
        DatabaseInfo db,
        string connectionString,
        IProgress<RepairLogEntry> progress,
        CancellationToken ct = default)
    {
        var result = new RepairResult();
        Report(progress, db.Name, RepairLogLevel.Warning,
            $"════════ ÚLTIMO RECURSO: reconstrução do log de [{db.Name}] ════════");

        if (AppConstants.IsSystemDatabase(db.Name))
        {
            result.Status = RepairStatus.Failed;
            result.Message = "Banco de sistema: a reconstrução de log não é permitida por esta ferramenta.";
            Report(progress, db.Name, RepairLogLevel.Error, result.Message);
            return result;
        }

        await using var session = await _data.OpenSessionAsync(connectionString, ct);

        // 1. Coletar os arquivos físicos do banco
        var files = await session.GetDatabaseFilesAsync(db.Name, ct);
        var dataFiles = files.Where(f => f.IsData).ToList();
        var logFiles = files.Where(f => f.IsLog).ToList();

        foreach (var f in files)
            Report(progress, db.Name, RepairLogLevel.Info, $"Arquivo {f.TypeDesc}: {f.PhysicalName}");

        if (dataFiles.Count == 0 || logFiles.Count == 0)
        {
            result.Status = RepairStatus.Failed;
            result.Message = "Não foi possível obter a lista de arquivos do banco.";
            Report(progress, db.Name, RepairLogLevel.Error, result.Message);
            return result;
        }

        // 1b. Guarda de topologia: nunca desanexar um banco espelhado/em AG/log shipping/replicação.
        if (await IsTopologyRiskyAsync(session, db, progress, ct))
        {
            result.Status = RepairStatus.Failed;
            result.Message = "Reconstrução de log bloqueada pela topologia de alta disponibilidade/replicação.";
            return result;
        }

        // 1c. Guarda de FILESTREAM/full-text: o ATTACH_REBUILD_LOG com apenas arquivos de dados
        //     não cobre com segurança esses contêineres especiais.
        if (files.Any(f => f.IsSpecialContainer))
        {
            result.Status = RepairStatus.Failed;
            result.Message =
                "O banco possui arquivos FILESTREAM/full-text. A reconstrução de log por detach/attach não é " +
                "segura nesse caso — prefira RESTAURAR de backup ou acione o DBA.";
            Report(progress, db.Name, RepairLogLevel.Error, result.Message);
            return result;
        }

        // 2. Pré-checagem: os arquivos de dados precisam estar acessíveis DESTA máquina.
        if (dataFiles.Any(f => !File.Exists(f.PhysicalName)))
        {
            result.Status = RepairStatus.Failed;
            result.Message =
                "Os arquivos do banco não estão acessíveis a partir desta máquina (o SQL Server pode ser remoto). " +
                "Execute a ferramenta diretamente no servidor, com permissão sobre a pasta de dados.";
            Report(progress, db.Name, RepairLogLevel.Error, result.Message);
            LogManualInstructions(progress, db, dataFiles, logFiles);
            return result;
        }

        // 2b. Pré-checagem de PERMISSÕES antes de qualquer mudança destrutiva.
        //     É essencial validar AGORA (com o banco ainda anexado) para nunca deixá-lo
        //     desanexado caso não seja possível remover o arquivo de log depois.
        if (!IsRunningElevated())
        {
            result.Status = RepairStatus.Failed;
            result.Message =
                "A reconstrução de log exige executar esta ferramenta COMO ADMINISTRADOR para remover o " +
                "arquivo de log do SQL Server. O banco NÃO foi alterado. Feche e reabra a ferramenta como administrador.";
            Report(progress, db.Name, RepairLogLevel.Error, result.Message);
            LogManualInstructions(progress, db, dataFiles, logFiles);
            return result;
        }

        var directories = files
            .Select(f => Path.GetDirectoryName(f.PhysicalName) ?? string.Empty)
            .Where(d => d.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var notWritable = directories.FirstOrDefault(d => !CanWriteToDirectory(d));
        if (notWritable is not null)
        {
            result.Status = RepairStatus.Failed;
            result.Message =
                $"Sem permissão de escrita na pasta de dados do servidor ('{notWritable}'). O banco NÃO foi alterado. " +
                "Execute no próprio servidor com permissão adequada na pasta de dados.";
            Report(progress, db.Name, RepairLogLevel.Error, result.Message);
            LogManualInstructions(progress, db, dataFiles, logFiles);
            return result;
        }

        // 3. EMERGENCY (permite desanexar mesmo um banco SUSPECT)
        await RunStepAsync(session, db, progress, "Colocando o banco em modo EMERGENCY", AlterDatabase(db.Name, "SET EMERGENCY"), ct);

        // 4. Detach (libera os arquivos no sistema operacional)
        var detach = await RunStepAsync(session, db, progress, "Desanexando o banco (detach)", DetachDb(db.Name), ct);
        if (!detach.Success)
        {
            Report(progress, db.Name, RepairLogLevel.Error, "Não foi possível desanexar o banco. Operação abortada.");
            await TrySetMultiUserAsync(session, db, progress, ct);
            db.StateDesc = await session.GetDatabaseStateAsync(db.Name, ct);
            result.Status = RepairStatus.Failed;
            result.Message = "Não foi possível desanexar o banco para reconstruir o log.";
            return result;
        }

        // 4b. Rede de segurança: com o banco desanexado (arquivos liberados), copiar dados e log
        //     antes de remover o log antigo. Permite reanexar manualmente em caso de problema.
        CopyFilesAside(files, progress, db);

        // 5. Remover (renomear) os arquivos de log antigos — necessário para que o log seja recriado.
        //    Em caso de "acesso negado", tentamos assumir a posse do arquivo (takeown/icacls).
        var renamed = new List<(string original, string backup)>();
        var renameFailed = false;
        foreach (var lf in logFiles)
        {
            if (!File.Exists(lf.PhysicalName))
            {
                Report(progress, db.Name, RepairLogLevel.Info, $"Log já ausente (será recriado): {lf.PhysicalName}");
                continue;
            }

            if (TryMoveLogAside(lf.PhysicalName, progress, db, out var movedTo) && movedTo is not null)
            {
                renamed.Add((lf.PhysicalName, movedTo));
                Report(progress, db.Name, RepairLogLevel.Success, $"Log antigo movido para: {movedTo}");
            }
            else
            {
                renameFailed = true;
                break;
            }
        }

        if (renameFailed)
        {
            RestoreRenamed(renamed, progress, db);
            await TryReattachAsync(session, db, files, progress, ct, "Restaurando o banco (reanexar)");
            db.StateDesc = await session.GetDatabaseStateAsync(db.Name, ct);
            result.Status = RepairStatus.Failed;
            result.Message = "Não foi possível remover o arquivo de log. A operação foi abortada e o banco restaurado.";
            LogManualInstructions(progress, db, dataFiles, logFiles);
            return result;
        }

        // 6. Reanexar reconstruindo o log
        var attach = await RunStepAsync(session, db, progress,
            "Reanexando e reconstruindo o log (FOR ATTACH_REBUILD_LOG)",
            BuildAttach(db.Name, dataFiles, rebuildLog: true), ct);

        if (!attach.Success)
        {
            Report(progress, db.Name, RepairLogLevel.Warning, "Falha ao reconstruir o log. Tentando restaurar o estado anterior...");
            RestoreRenamed(renamed, progress, db);
            await TryReattachAsync(session, db, files, progress, ct, "Restaurando o banco (reanexar com o log original)");
            db.StateDesc = await session.GetDatabaseStateAsync(db.Name, ct);
            result.Status = RepairStatus.Failed;
            result.Message = "Não foi possível reconstruir o log de transações.";
            LogManualInstructions(progress, db, dataFiles, logFiles);
            return result;
        }

        // 7. Normalizar acesso e verificar consistência
        await TrySetMultiUserAsync(session, db, progress, ct);
        var finalState = await session.GetDatabaseStateAsync(db.Name, ct);
        db.StateDesc = finalState;

        var check = await RunCheckDbStepAsync(session, db, progress, "Verificação de consistência (DBCC CHECKDB)", db.Name, ct);

        if (finalState.Equals("ONLINE", StringComparison.OrdinalIgnoreCase))
        {
            result.Status = RepairStatus.Success;
            result.FinalStateDesc = finalState;
            result.Message = check.Success
                ? "Log de transações reconstruído com sucesso. Banco ONLINE e consistente."
                : "Log reconstruído e banco ONLINE, mas o DBCC CHECKDB ainda acusa inconsistências. Considere o reparo COM perda de dados.";
            Report(progress, db.Name, check.Success ? RepairLogLevel.Success : RepairLogLevel.Warning, result.Message);
            // A reconstrução do log quebra definitivamente a cadeia de backups de log.
            await TryPostRepairFullBackupAsync(session, db, progress, ct);
        }
        else
        {
            result.Status = RepairStatus.Failed;
            result.FinalStateDesc = finalState;
            result.Message = $"Após a reconstrução do log, o banco está {finalState}.";
            Report(progress, db.Name, RepairLogLevel.Error, result.Message);
        }

        Report(progress, db.Name, RepairLogLevel.Info, $"════════ Fim da reconstrução de log de [{db.Name}] ════════");
        return result;
    }

    private async Task TryReattachAsync(SqlSession s, DatabaseInfo db, List<DatabaseFile> allFiles,
        IProgress<RepairLogEntry> p, CancellationToken ct, string description)
    {
        if (await s.GetDatabaseStateAsync(db.Name, ct) is not "DESCONHECIDO")
            return; // ainda anexado

        var r = await RunStepAsync(s, db, p, description, BuildAttach(db.Name, allFiles, rebuildLog: false), ct);
        if (!r.Success)
            Report(p, db.Name, RepairLogLevel.Error, "Não foi possível reanexar o banco automaticamente. Veja as instruções manuais no log.");
    }

    private void RestoreRenamed(List<(string original, string backup)> renamed, IProgress<RepairLogEntry> p, DatabaseInfo db)
    {
        foreach (var (original, backup) in renamed)
        {
            try
            {
                if (File.Exists(backup) && !File.Exists(original))
                {
                    File.Move(backup, original);
                    Report(p, db.Name, RepairLogLevel.Info, $"Arquivo de log restaurado: {original}");
                }
            }
            catch (Exception ex)
            {
                Report(p, db.Name, RepairLogLevel.Warning, $"Não foi possível restaurar '{original}': {ex.Message}");
            }
        }
    }

    private void LogManualInstructions(IProgress<RepairLogEntry> p, DatabaseInfo db,
        List<DatabaseFile> dataFiles, List<DatabaseFile> logFiles)
    {
        var allClause = string.Join(",\n    ", dataFiles.Concat(logFiles).Select(f => $"(FILENAME = N'{f.PhysicalName}')"));
        var dataClause = string.Join(",\n    ", dataFiles.Select(f => $"(FILENAME = N'{f.PhysicalName}')"));

        Report(p, db.Name, RepairLogLevel.Info, "── Recuperação manual (no servidor, como administrador/sysadmin) ──");
        Report(p, db.Name, RepairLogLevel.Info,
            $"1) Se o banco ficou DESANEXADO, reanexe-o com o log atual:\nCREATE DATABASE [{db.Name}] ON\n    {allClause}\n FOR ATTACH;");
        Report(p, db.Name, RepairLogLevel.Info,
            "   Se aparecer 'operating system error 5 (Acesso negado)', conceda acesso à conta de serviço do SQL aos arquivos e tente de novo, ex.:\n" +
            string.Join("\n", dataFiles.Concat(logFiles).Select(f => $"icacls \"{f.PhysicalName}\" /grant \"NT SERVICE\\MSSQLSERVER\":(F)")));
        Report(p, db.Name, RepairLogLevel.Info,
            $"2) Para RECONSTRUIR o log: apague/renomeie o(s) arquivo(s) {string.Join("; ", logFiles.Select(f => f.PhysicalName))} e execute:\n" +
            $"CREATE DATABASE [{db.Name}] ON\n    {dataClause}\n FOR ATTACH_REBUILD_LOG;");
    }

    // --- Permissões / sistema de arquivos (operação roda no host do SQL Server) ---

    private static bool IsRunningElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static bool CanWriteToDirectory(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return false;
            var probe = Path.Combine(directory, ".restoredb_probe_" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryMoveLogAside(string path, IProgress<RepairLogEntry> p, DatabaseInfo db, out string? movedTo)
    {
        movedTo = null;
        var backup = path + ".corrupt_" + DateTime.Now.ToString("yyyyMMddHHmmss");
        try
        {
            ClearReadOnly(path);
            File.Move(path, backup);
            movedTo = backup;
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            Report(p, db.Name, RepairLogLevel.Warning, $"Acesso negado ao log. Tentando assumir a posse do arquivo: {path}");
            if (TryTakeOwnershipAndGrant(path, p, db))
            {
                try
                {
                    ClearReadOnly(path);
                    File.Move(path, backup);
                    movedTo = backup;
                    return true;
                }
                catch (Exception ex)
                {
                    Report(p, db.Name, RepairLogLevel.Error, $"Ainda sem acesso ao log '{path}': {ex.Message}");
                    return false;
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            Report(p, db.Name, RepairLogLevel.Error, $"Não foi possível remover o log '{path}': {ex.Message}");
            return false;
        }
    }

    private static void ClearReadOnly(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            if (attrs.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        }
        catch
        {
            // best-effort
        }
    }

    private bool TryTakeOwnershipAndGrant(string path, IProgress<RepairLogEntry> p, DatabaseInfo db)
    {
        var owned = RunTool("takeown", $"/F \"{path}\"");
        // S-1-5-32-544 = grupo Administradores (independente de idioma do Windows)
        var granted = RunTool("icacls", $"\"{path}\" /grant *S-1-5-32-544:F");
        if (!owned || !granted)
            Report(p, db.Name, RepairLogLevel.Warning, "Não foi possível ajustar a posse/permissão do arquivo de log automaticamente.");
        return owned && granted;
    }

    private static bool RunTool(string exe, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(psi);
            if (process is null) return false;
            process.WaitForExit(20000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static (string sql, Dictionary<string, object?> prm) DetachDb(string dbName)
    {
        // sp_detach_db recebe o nome por parâmetro (seguro). @skipchecks evita falhas em bancos problemáticos.
        const string sql = "EXEC master.dbo.sp_detach_db @dbname = @db, @skipchecks = N'true';";
        return (sql, new Dictionary<string, object?> { ["@db"] = dbName });
    }

    private static (string sql, Dictionary<string, object?> prm) BuildAttach(string dbName, IEnumerable<DatabaseFile> filesToList, bool rebuildLog)
    {
        // Caminhos vêm de sys.master_files (metadados confiáveis do servidor); ainda assim escapamos aspas.
        var clause = string.Join(", ", filesToList.Select(f => $"(FILENAME = N'{f.PhysicalName.Replace("'", "''")}')"));
        var forClause = rebuildLog ? "FOR ATTACH_REBUILD_LOG" : "FOR ATTACH";
        var sql =
            "DECLARE @cmd nvarchar(max);\n" +
            "SET @cmd = N'CREATE DATABASE ' + QUOTENAME(@db) + N' ON ' + @files + N' " + forClause + ";';\n" +
            "EXEC sys.sp_executesql @cmd;";
        return (sql, new Dictionary<string, object?> { ["@db"] = dbName, ["@files"] = clause });
    }

    // ----------------------------------------------------------------------------------
    // Backup de segurança e MULTI_USER
    // ----------------------------------------------------------------------------------

    // ----------------------------------------------------------------------------------
    // Reparo SEM perda de dados: reconstrução de índices não-clusterizados
    // ----------------------------------------------------------------------------------

    /// <summary>
    /// Reparo SEM perda de dados quando a corrupção está restrita a índices NÃO clusterizados:
    /// obtém acesso exclusivo, confirma (via CHECKDB estruturado) que toda a corrupção é de
    /// índice não-clusterizado e reconstrói esses índices (ALTER INDEX REBUILD), verificando ao final.
    /// </summary>
    public async Task<RepairResult> RebuildIndexesAsync(
        DatabaseInfo db, string connectionString, IProgress<RepairLogEntry> progress, CancellationToken ct = default)
    {
        var result = new RepairResult();
        Report(progress, db.Name, RepairLogLevel.Info, $"════════ Reparo SEM perda (reconstrução de índices) de [{db.Name}] ════════");

        await using var session = await _data.OpenSessionAsync(connectionString, ct);

        if (await IsTopologyRiskyAsync(session, db, progress, ct))
        {
            result.Status = RepairStatus.Failed;
            result.Message = "Operação bloqueada pela topologia de alta disponibilidade/replicação.";
            return result;
        }

        try
        {
            await RunStepAsync(session, db, progress, "Obtendo acesso exclusivo (SINGLE_USER WITH ROLLBACK IMMEDIATE)",
                AlterDatabase(db.Name, "SET SINGLE_USER WITH ROLLBACK IMMEDIATE"), ct);
            await TryOccupySingleUserSlotAsync(session, db, progress, ct);

            var report = await session.CheckDbWithResultsAsync(db.Name, repairOption: null, ct);
            if (!report.Executed)
            {
                result.Status = RepairStatus.Failed;
                result.Message = "Não foi possível analisar o banco (inacessível). Use o fluxo de reparo padrão.";
                Report(progress, db.Name, RepairLogLevel.Error, result.Message);
                return result;
            }
            if (!report.HasErrors)
            {
                result.Status = RepairStatus.Success;
                result.Message = "Nenhuma inconsistência encontrada — nada a reconstruir.";
                Report(progress, db.Name, RepairLogLevel.Success, result.Message);
                return result;
            }
            if (!report.AllErrorsAreNonClusteredIndex)
            {
                // Failed, não NeedsDataLossConfirmation: o modo seguro (REPAIR_REBUILD) ainda nem foi tentado — a UI
                // não pode saltar direto para a confirmação do reparo COM perda.
                result.Status = RepairStatus.Failed;
                result.Message = "A corrupção NÃO está restrita a índices não-clusterizados: reconstruir índices não resolve. Use 'Reparar (modo seguro)' e, se não bastar, RESTAURE de backup.";
                Report(progress, db.Name, RepairLogLevel.Warning, result.Message);
                return result;
            }

            var ok = await RebuildNonClusteredIndexesAsync(session, db, report, progress, ct);
            var verify = await RunCheckDbStepAsync(session, db, progress, "Verificação pós-reconstrução (DBCC CHECKDB)", db.Name, ct);

            result.Status = ok && verify.Success ? RepairStatus.Success : RepairStatus.Failed;
            if (result.Status == RepairStatus.Success)
            {
                await session.ClearSuspectPagesAsync(db.Name, ct);
                result.Message = "Índices reconstruídos com sucesso — SEM perda de dados. Banco consistente.";
                Report(progress, db.Name, RepairLogLevel.Success, result.Message);
            }
            else
            {
                result.Message = "A reconstrução de índices não eliminou todas as inconsistências. Use 'Reparar (modo seguro)'.";
                Report(progress, db.Name, RepairLogLevel.Warning, result.Message);
            }
            return result;
        }
        finally
        {
            // 1) Devolve o contexto a master e religa na PRÓPRIA sessão (dona do slot SINGLE_USER).
            await TryReleaseSingleUserSlotAsync(session, CancellationToken.None);
            await TrySetMultiUserAsync(session, db, progress, CancellationToken.None);
            // 2) Backstop em conexão NOVA, caso a sessão tenha sido derrubada por um erro severo (824).
            result.FinalStateDesc = await ForceMultiUserAsync(connectionString, db, progress);
            db.StateDesc = result.FinalStateDesc;
        }
    }

    // ----------------------------------------------------------------------------------
    // Correção de integridade LÓGICA: remoção de registros órfãos (violação de FK)
    // ----------------------------------------------------------------------------------

    /// <summary>
    /// Corrige a integridade referencial removendo os registros órfãos (linhas cuja chave
    /// estrangeira aponta para uma chave primária inexistente), detectados por DBCC CHECKCONSTRAINTS.
    /// Operação DESTRUTIVA (descarta linhas) — a UI exige confirmação explícita. Obtém acesso
    /// exclusivo, faz backup/cópia de segurança, remove as linhas violadoras e reverifica.
    /// Violações de CHECK constraint são apenas reportadas (não removidas automaticamente).
    /// </summary>
    public async Task<RepairResult> FixConstraintViolationsAsync(
        DatabaseInfo db, string connectionString, IProgress<RepairLogEntry> progress, CancellationToken ct = default,
        ParentRecoveryOptions? recovery = null)
    {
        var result = new RepairResult();
        Report(progress, db.Name, RepairLogLevel.Warning,
            $"════════ Correção de integridade lógica (remoção de registros órfãos) de [{db.Name}] ════════");

        if (AppConstants.IsSystemDatabase(db.Name))
        {
            result.Status = RepairStatus.Failed;
            result.Message = "Banco de sistema: a correção de integridade não é permitida por esta ferramenta.";
            Report(progress, db.Name, RepairLogLevel.Error, result.Message);
            return result;
        }

        await using var session = await _data.OpenSessionAsync(connectionString, ct);

        if (await IsTopologyRiskyAsync(session, db, progress, ct))
        {
            result.Status = RepairStatus.Failed;
            result.Message = "Operação bloqueada pela topologia de alta disponibilidade/replicação.";
            return result;
        }

        try
        {
            await RunStepAsync(session, db, progress, "Obtendo acesso exclusivo (SINGLE_USER WITH ROLLBACK IMMEDIATE)",
                AlterDatabase(db.Name, "SET SINGLE_USER WITH ROLLBACK IMMEDIATE"), ct);
            await TryOccupySingleUserSlotAsync(session, db, progress, ct);

            var violations = await session.CheckConstraintsAsync(db.Name, ct);
            var fk = violations.Where(v => v.IsForeignKey).ToList();
            var checkOnly = violations.Where(v => !v.IsForeignKey).ToList();

            if (violations.Count == 0)
            {
                result.Status = RepairStatus.Success;
                result.Message = "Nenhuma violação de integridade encontrada — nada a corrigir.";
                Report(progress, db.Name, RepairLogLevel.Success, result.Message);
                return result;
            }

            foreach (var v in checkOnly)
                Report(progress, db.Name, RepairLogLevel.Warning,
                    $"Violação de CHECK (NÃO removida automaticamente — revise): {v.Display}");

            if (fk.Count == 0)
            {
                result.Status = RepairStatus.Failed;
                result.Message = "Há apenas violações de CHECK constraint, que exigem revisão manual dos dados (ou restauração de backup).";
                Report(progress, db.Name, RepairLogLevel.Warning, result.Message);
                return result;
            }

            // Rede de segurança antes de descartar linhas.
            await TrySafetyNetAsync(session, db, progress, ct);

            // Antes de excluir: tenta recuperar os registros-pai do backup; só remove o que sobrar.
            var (recovered, total, anyDeleteFailed) = await ReconcileForeignKeyOrphansAsync(session, db, fk, progress, connectionString, recovery, ct);

            var remaining = await session.CheckConstraintsAsync(db.Name, ct);
            var remainingFk = remaining.Count(v => v.IsForeignKey);

            result.Status = !anyDeleteFailed && remainingFk == 0 ? RepairStatus.Success : RepairStatus.Failed;
            result.Message = result.Status == RepairStatus.Success
                ? (recovered > 0
                    ? $"Integridade referencial restaurada: {recovered} registro(s)-pai recuperado(s) do backup e {total} registro(s) órfão(s) removido(s)."
                    : $"Integridade referencial restaurada: {total} registro(s) órfão(s) removido(s).")
                : $"Recuperados {recovered}, removidos {total} registro(s), mas ainda restam {remainingFk} violação(ões) de FK. Verifique o log.";
            Report(progress, db.Name, result.Status == RepairStatus.Success ? RepairLogLevel.Success : RepairLogLevel.Warning, result.Message);
            return result;
        }
        finally
        {
            await TryReleaseSingleUserSlotAsync(session, CancellationToken.None);
            await TrySetMultiUserAsync(session, db, progress, CancellationToken.None);
            result.FinalStateDesc = await ForceMultiUserAsync(connectionString, db, progress);
            db.StateDesc = result.FinalStateDesc;
        }
    }

    // ----------------------------------------------------------------------------------
    // Reparo por SIMULAÇÃO: pré-visualiza a perda de dados numa CÓPIA, sem tocar na produção
    // ----------------------------------------------------------------------------------

    /// <summary>
    /// SIMULA o reparo COM perda de dados numa CÓPIA descartável do banco (backup COPY_ONLY →
    /// restauração para um banco temporário), executa o REPAIR_ALLOW_DATA_LOSS na cópia e quantifica
    /// a perda por tabela (linhas antes × depois, via metadados). NÃO altera a produção. A cópia é
    /// sempre descartada ao final. Permite ao usuário decidir com conhecimento total.
    /// </summary>
    public async Task<RepairSimulationResult> SimulateRepairAsync(
        DatabaseInfo db, string connectionString, IProgress<RepairLogEntry> progress, CancellationToken ct = default)
    {
        var result = new RepairSimulationResult { DatabaseName = db.Name };
        Report(progress, db.Name, RepairLogLevel.Info,
            $"════════ Simulação de reparo (numa cópia, sem tocar em [{db.Name}]) ════════");

        if (AppConstants.IsSystemDatabase(db.Name))
        {
            result.Message = "Banco de sistema: simulação não disponível.";
            Report(progress, db.Name, RepairLogLevel.Error, result.Message);
            return result;
        }

        await using var session = await _data.OpenSessionAsync(connectionString, ct);

        var dir = await session.GetScratchDataDirAsync(ct);
        if (string.IsNullOrEmpty(dir))
        {
            result.Message = "Não foi possível determinar uma pasta temporária no servidor para a simulação.";
            Report(progress, db.Name, RepairLogLevel.Error, result.Message);
            return result;
        }

        var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
        var scratch = $"{AppConstants.SanitizeToken(db.Name, "banco")}_SimRepair_{stamp}";
        var bak = Path.Combine(dir, $"{AppConstants.SanitizeToken(db.Name, "banco")}_sim_{stamp}.bak");

        try
        {
            // 1) Backup COPY_ONLY do estado ATUAL (numa pasta temporária local da instância).
            var backupSql =
                "DECLARE @cmd nvarchar(max);\n" +
                "SET @cmd = N'BACKUP DATABASE ' + QUOTENAME(@db) + N' TO DISK = @f WITH COPY_ONLY, INIT, CONTINUE_AFTER_ERROR, NAME = @n;';\n" +
                "EXEC sys.sp_executesql @cmd, N'@f nvarchar(4000), @n nvarchar(256)', @f = @file, @n = @name;";
            var b = await RunStepAsync(session, db, progress, "Copiando o estado atual (BACKUP COPY_ONLY)", backupSql,
                new Dictionary<string, object?> { ["@db"] = db.Name, ["@file"] = bak, ["@name"] = $"RestoreDB simulação de {db.Name}" }, ct);
            if (!b.Success)
            {
                result.Message = "Não foi possível copiar o estado atual para simular (o banco pode estar inacessível para backup neste estado).";
                Report(progress, db.Name, RepairLogLevel.Warning, result.Message);
                return result;
            }

            // 2) MOVE dos arquivos para a pasta temporária e RESTORE para o banco de simulação.
            var files = await session.RestoreFileListOnlyAsync(bak, 1, ct);
            var moves = new List<string>();
            var dataIdx = 0;
            foreach (var f in files)
            {
                var isLog = f.Type.Equals("L", StringComparison.OrdinalIgnoreCase);
                var ext = isLog ? ".ldf" : (dataIdx++ == 0 ? ".mdf" : ".ndf");
                var dest = Path.Combine(dir, $"{scratch}_{moves.Count}{ext}");
                moves.Add($"MOVE N'{f.LogicalName.Replace("'", "''")}' TO N'{dest.Replace("'", "''")}'");
            }
            var moveLiteral = string.Join(", ", moves).Replace("'", "''");

            var restoreSql =
                "DECLARE @cmd nvarchar(max);\n" +
                "SET @cmd = N'RESTORE DATABASE ' + QUOTENAME(@s) + N' FROM DISK = @f WITH " + moveLiteral + ", REPLACE, RECOVERY, CONTINUE_AFTER_ERROR;';\n" +
                "EXEC sys.sp_executesql @cmd, N'@f nvarchar(4000)', @f = @file;";
            var rr = await RunStepAsync(session, db, progress, "Restaurando a cópia de simulação", restoreSql,
                new Dictionary<string, object?> { ["@s"] = scratch, ["@file"] = bak }, ct);
            if (!rr.Success)
            {
                result.Message = "Não foi possível restaurar a cópia de simulação.";
                Report(progress, db.Name, RepairLogLevel.Error, result.Message);
                return result;
            }

            // 3) Contagem ANTES, reparo destrutivo NA CÓPIA, verificação, contagem DEPOIS.
            var before = await session.GetTableRowCountsAsync(scratch, ct);

            await RunStepAsync(session, db, progress, "Acesso exclusivo na cópia (SINGLE_USER)",
                AlterDatabase(scratch, "SET SINGLE_USER WITH ROLLBACK IMMEDIATE"), ct);
            await RunStepAsync(session, db, progress, "Reparo COM perda NA CÓPIA (REPAIR_ALLOW_DATA_LOSS)",
                Dbcc(scratch, "REPAIR_ALLOW_DATA_LOSS", noInfoMsgs: false), ct);
            var verify = await session.CheckDbWithResultsAsync(scratch, repairOption: null, ct);
            result.CheckDbCleanAfter = verify.Executed && !verify.HasErrors;
            await RunStepAsync(session, db, progress, "Liberando a cópia (MULTI_USER)",
                AlterDatabase(scratch, "SET MULTI_USER"), ct);

            var after = await session.GetTableRowCountsAsync(scratch, ct);

            foreach (var kv in before)
            {
                after.TryGetValue(kv.Key, out var a);
                result.TableLosses.Add(new TableLoss { Table = kv.Key, RowsBefore = kv.Value, RowsAfter = a });
            }
            result.Executed = true;
            result.Message = result.TotalRowsLost == 0
                ? $"Simulação: o reparo COM perda NÃO descartaria linhas. O CHECKDB {(result.CheckDbCleanAfter ? "ficaria limpo" : "AINDA acusaria erros")}."
                : $"Simulação: o reparo COM perda descartaria {result.TotalRowsLost:N0} linha(s) em {result.AffectedTables.Count} tabela(s). O CHECKDB {(result.CheckDbCleanAfter ? "ficaria limpo" : "AINDA acusaria erros")}.";
            Report(progress, db.Name, result.TotalRowsLost > 0 ? RepairLogLevel.Warning : RepairLogLevel.Success, result.Message);
            foreach (var t in result.AffectedTables.Take(12))
                Report(progress, db.Name, RepairLogLevel.Warning, "  • " + t.Display);
            return result;
        }
        finally
        {
            await DropDbBestEffortAsync(session, scratch, progress);
            try { await session.ExecuteAsync("Removendo backup de simulação", "EXEC master.sys.xp_delete_file 0, @p;",
                new Dictionary<string, object?> { ["@p"] = bak }, AppConstants.QueryCommandTimeoutSeconds, CancellationToken.None); }
            catch { /* best-effort */ }
        }
    }

    private async Task DropDbBestEffortAsync(SqlSession s, string name, IProgress<RepairLogEntry> p)
    {
        var ok = await s.DropDatabaseBestEffortAsync(name, CancellationToken.None);
        Report(p, name, ok ? RepairLogLevel.Info : RepairLogLevel.Warning,
            ok ? $"Cópia de simulação [{name}] descartada." : $"Não foi possível descartar a cópia [{name}] — remova-a manualmente.");
    }

    // ----------------------------------------------------------------------------------
    // Checagens granulares: CHECKTABLE dirigido e reseed de IDENTITY
    // ----------------------------------------------------------------------------------

    /// <summary>Verificação dirigida de UMA tabela (DBCC CHECKTABLE) — mais rápida que o CHECKDB completo.</summary>
    public async Task<CheckDbReport> CheckTableAsync(DatabaseInfo db, string schemaTable, string connectionString, IProgress<RepairLogEntry> progress, CancellationToken ct = default)
    {
        Report(progress, db.Name, RepairLogLevel.Info, $"Verificação dirigida (DBCC CHECKTABLE) de {schemaTable}...");
        await using var session = await _data.OpenSessionAsync(connectionString, ct);
        var report = await session.CheckTableAsync(db.Name, schemaTable, ct);

        if (!report.Executed)
            Report(progress, db.Name, RepairLogLevel.Warning, "CHECKTABLE não pôde ser executado (banco/objeto inacessível).");
        else if (!report.HasErrors)
            Report(progress, db.Name, RepairLogLevel.Success, $"{schemaTable}: sem erros de consistência.");
        else
        {
            Report(progress, db.Name, RepairLogLevel.Warning,
                $"{schemaTable}: {report.ErrorCount} erro(s). Nível mínimo de reparo: {(report.MinRepairLevel.Length > 0 ? report.MinRepairLevel : "não reparável pelo DBCC")}.");
            foreach (var d in report.Details.Where(x => x.Error != 0).Take(8))
                Report(progress, db.Name, RepairLogLevel.Warning, "  • " + d.MessageText);
        }
        return report;
    }

    /// <summary>
    /// Reajusta as sementes de IDENTITY (DBCC CHECKIDENT ... RESEED) de todas as tabelas — útil após
    /// remoção de linhas (reparo com perda / salvamento) para evitar saltos/colisões de identidade.
    /// </summary>
    public async Task<int> ReseedIdentitiesAsync(DatabaseInfo db, string connectionString, IProgress<RepairLogEntry> progress, CancellationToken ct = default)
    {
        Report(progress, db.Name, RepairLogLevel.Info, "Reajustando sementes de IDENTITY (DBCC CHECKIDENT ... RESEED)...");
        await using var session = await _data.OpenSessionAsync(connectionString, ct);
        var n = await session.ReseedIdentitiesAsync(db.Name, ct);
        Report(progress, db.Name, RepairLogLevel.Success, $"{n} tabela(s) com IDENTITY reajustada(s).");
        return n;
    }

    /// <summary>
    /// Garante o retorno do banco a MULTI_USER usando uma conexão NOVA — robusto mesmo que a
    /// sessão principal tenha sido derrubada por um erro severo. Só age se necessário e usa
    /// WITH ROLLBACK IMMEDIATE para não bloquear. Devolve o estado final lido.
    /// </summary>
    private async Task<string> ForceMultiUserAsync(string connectionString, DatabaseInfo db, IProgress<RepairLogEntry> p)
    {
        try
        {
            await using var s = await _data.OpenSessionAsync(connectionString);
            const string sql =
                "IF EXISTS (SELECT 1 FROM sys.databases WHERE name = @db AND user_access_desc <> N'MULTI_USER')\n" +
                "BEGIN\n" +
                "  DECLARE @cmd nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@db) + N' SET MULTI_USER WITH ROLLBACK IMMEDIATE;';\n" +
                "  EXEC sys.sp_executesql @cmd;\n" +
                "END";
            var r = await s.ExecuteAsync("Garantir MULTI_USER (conexão de recuperação)", sql,
                new Dictionary<string, object?> { ["@db"] = db.Name }, AppConstants.RepairCommandTimeoutSeconds, CancellationToken.None);
            Report(p, db.Name, r.Success ? RepairLogLevel.Info : RepairLogLevel.Warning,
                r.Success ? "Acesso normal assegurado (MULTI_USER)." : "Não foi possível assegurar MULTI_USER automaticamente.");
            return await s.GetDatabaseStateAsync(db.Name, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Report(p, db.Name, RepairLogLevel.Warning, "Conexão de recuperação para MULTI_USER falhou: " + ex.Message);
            return db.StateDesc;
        }
    }

    private async Task<bool> RebuildNonClusteredIndexesAsync(
        SqlSession s, DatabaseInfo db, CheckDbReport report, IProgress<RepairLogEntry> p, CancellationToken ct)
    {
        var targets = report.NonClusteredIndexesToRebuild;
        if (targets.Count == 0) return false;

        var ok = true;
        foreach (var (objectId, indexId) in targets)
        {
            var objName = await s.ResolveObjectNameAsync(db.Name, objectId, ct);
            var idxName = await s.ResolveIndexNameAsync(db.Name, objectId, indexId, ct);
            var label = $"índice {(string.IsNullOrEmpty(idxName) ? indexId.ToString() : idxName)} de " +
                        $"{(string.IsNullOrEmpty(objName) ? objectId.ToString() : objName)}";
            var (sql, prm) = RebuildIndex(db.Name, objectId, indexId);
            var r = await RunStepAsync(s, db, p, "Reconstruindo " + label, sql, prm, ct);
            ok = ok && r.Success;
        }
        return ok;
    }

    internal static (string sql, Dictionary<string, object?> prm) RebuildIndex(string db, int objectId, int indexId)
    {
        // O ALTER INDEX roda NO CONTEXTO do banco alvo (EXEC [db].sys.sp_executesql), montando o
        // nome qualificado (schema.tabela) a partir do object_id já lá dentro.
        //
        // IMPORTANTE: primeiro DISABLE, depois REBUILD. Um REBUILD direto LÊ as páginas do índice
        // — se elas estiverem fisicamente corrompidas (erro 824), o comando falha e derruba a
        // conexão. O DISABLE desaloca a estrutura SEM ler as páginas corrompidas, e o REBUILD
        // reconstrói o índice a partir do índice clusterizado/heap (íntegro), sem perda de dados.
        //
        // ATENÇÃO: desabilitar um índice ÚNICO/PK não-clusterizado que é referenciado por chave estrangeira
        // DESABILITA essas FKs em silêncio — e o REBUILD reabilita o índice, mas NÃO as FKs. Sem o passo
        // final, o banco ficaria sem integridade referencial depois de um reparo "sem perda". As FKs que o
        // DISABLE derrubou (e só elas) são reativadas WITH CHECK; se houver linha violando, ficam ativas
        // WITH NOCHECK (não confiáveis — o diagnóstico seguinte aponta) em vez de desligadas.
        const string inner = """
            DECLARE @q nvarchar(300) = QUOTENAME(OBJECT_SCHEMA_NAME(@o)) + N'.' + QUOTENAME(OBJECT_NAME(@o));
            DECLARE @ix sysname = (SELECT name FROM sys.indexes WHERE object_id = @o AND index_id = @i);
            IF @ix IS NOT NULL
            BEGIN
                DECLARE @before TABLE (id int PRIMARY KEY);
                INSERT INTO @before SELECT object_id FROM sys.foreign_keys WHERE referenced_object_id = @o AND is_disabled = 1;
                DECLARE @s1 nvarchar(max) = N'ALTER INDEX ' + QUOTENAME(@ix) + N' ON ' + @q + N' DISABLE;';
                DECLARE @s2 nvarchar(max) = N'ALTER INDEX ' + QUOTENAME(@ix) + N' ON ' + @q + N' REBUILD;';
                EXEC sys.sp_executesql @s1;
                EXEC sys.sp_executesql @s2;
                DECLARE @re nvarchar(max) = N'';
                SELECT @re = @re
                    + N'BEGIN TRY ALTER TABLE ' + QUOTENAME(OBJECT_SCHEMA_NAME(fk.parent_object_id)) + N'.' + QUOTENAME(OBJECT_NAME(fk.parent_object_id))
                    + N' WITH CHECK CHECK CONSTRAINT ' + QUOTENAME(fk.name) + N'; END TRY BEGIN CATCH ALTER TABLE '
                    + QUOTENAME(OBJECT_SCHEMA_NAME(fk.parent_object_id)) + N'.' + QUOTENAME(OBJECT_NAME(fk.parent_object_id))
                    + N' WITH NOCHECK CHECK CONSTRAINT ' + QUOTENAME(fk.name) + N'; END CATCH; '
                FROM sys.foreign_keys fk
                WHERE fk.referenced_object_id = @o AND fk.is_disabled = 1 AND fk.object_id NOT IN (SELECT id FROM @before);
                IF @re <> N'' EXEC sys.sp_executesql @re;
            END
            """;
        const string sql =
            "DECLARE @proc nvarchar(300) = QUOTENAME(@db) + N'.sys.sp_executesql';\n" +
            "EXEC @proc @inner, N'@o int, @i int', @o = @oid, @i = @iid;";
        return (sql, new Dictionary<string, object?> { ["@db"] = db, ["@oid"] = objectId, ["@iid"] = indexId, ["@inner"] = inner });
    }

    // ----------------------------------------------------------------------------------
    // Guardas de segurança (topologia) e rede de segurança (cópia física)
    // ----------------------------------------------------------------------------------

    private async Task<bool> IsTopologyRiskyAsync(SqlSession s, DatabaseInfo db, IProgress<RepairLogEntry> p, CancellationToken ct)
    {
        var topo = await s.GetTopologyInfoAsync(db.Name, ct);
        if (!topo.IsRisky) return false;

        Report(p, db.Name, RepairLogLevel.Error, "Operação bloqueada por segurança: " + topo.Summary);
        foreach (var w in topo.Warnings)
            Report(p, db.Name, RepairLogLevel.Error, "  ⛔ " + w);
        Report(p, db.Name, RepairLogLevel.Info,
            "Remova o banco dessa configuração (failover/remover do AG ou do espelhamento, desativar log shipping/replicação) antes de reparar, ou acione o DBA.");
        return true;
    }

    /// <summary>
    /// Rede de segurança antes de um reparo destrutivo, tudo best-effort:
    /// 1) tail-log WITH NO_TRUNCATE (captura as transações ainda não copiadas mesmo com os
    ///    arquivos de DADOS danificados — junto com a cadeia de backups permite restaurar sem perda);
    /// 2) backup COPY_ONLY do banco;
    /// 3) cópia física dos arquivos (quando locais) — porque o BACKUP costuma falhar com o banco
    ///    em EMERGENCY (Msg 3033).
    /// </summary>
    private async Task<SafetyNet> TrySafetyNetAsync(SqlSession s, DatabaseInfo db, IProgress<RepairLogEntry> p, CancellationToken ct)
    {
        // Relógio do SERVIDOR: é com ele que o msdb data os backups (a ferramenta pode rodar noutra máquina).
        var started = await s.GetServerTimeAsync(ct) ?? DateTime.Now;
        var tail = await TryCaptureTailLogAsync(s, db, p, ct);
        await TryBackupAsync(s, db, p, ct);
        await TryCopyPhysicalFilesAsync(s, db, p, ct);
        return new SafetyNet(started, tail);
    }

    /// <summary>O que a rede de segurança conseguiu preservar antes de um reparo destrutivo.</summary>
    private sealed record SafetyNet(DateTime StartedAt, bool TailLogCaptured);

    /// <summary>
    /// Captura o tail-log com NO_TRUNCATE + CONTINUE_AFTER_ERROR: funciona mesmo com os arquivos
    /// de dados corrompidos/inacessíveis, desde que o .ldf esteja legível. É o que garante perda
    /// MÍNIMA: as transações posteriores ao último backup ficam preservadas num .trn que pode ser
    /// aplicado numa restauração da cadeia. Não altera o estado do banco. Best-effort.
    /// </summary>
    private async Task<bool> TryCaptureTailLogAsync(SqlSession s, DatabaseInfo db, IProgress<RepairLogEntry> p, CancellationToken ct)
    {
        try
        {
            var model = await s.GetRecoveryModelAsync(db.Name, ct);
            if (model.Length == 0 || model.Equals("SIMPLE", StringComparison.OrdinalIgnoreCase))
                return false; // em SIMPLE não há cadeia de log a preservar

            var file = $"{AppConstants.SanitizeToken(db.Name, "banco")}_tail_{DateTime.Now:yyyyMMdd_HHmmss}.trn";
            var sql =
                "DECLARE @cmd nvarchar(max);\n" +
                "SET @cmd = N'BACKUP LOG ' + QUOTENAME(@db) + N' TO DISK = @f WITH NO_TRUNCATE, CONTINUE_AFTER_ERROR, INIT, NAME = @n;';\n" +
                "EXEC sys.sp_executesql @cmd, N'@f nvarchar(4000), @n nvarchar(256)', @f = @file, @n = @name;";
            var r = await RunStepAsync(s, db, p, "Preservando o tail-log (BACKUP LOG ... WITH NO_TRUNCATE)", sql,
                new Dictionary<string, object?> { ["@db"] = db.Name, ["@file"] = file, ["@name"] = $"RestoreDB tail-log de {db.Name}" }, ct);
            Report(p, db.Name, r.Success ? RepairLogLevel.Success : RepairLogLevel.Warning,
                r.Success
                    ? $"Tail-log preservado na pasta padrão de backups: {file} — junto com a cadeia de backups, permite restauração SEM perda."
                    : "Não foi possível preservar o tail-log (log inacessível). O reparo prosseguirá conforme autorizado.");
            return r.Success;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; /* best-effort */ }
    }

    /// <summary>
    /// Após um reparo DESTRUTIVO bem-sucedido (REPAIR_ALLOW_DATA_LOSS / reconstrução de log), a
    /// cadeia de backups anterior deixa de ser confiável (diferenciais/logs antigos não casam mais
    /// com o estado atual). Um backup FULL novo vira a base da nova cadeia. Best-effort.
    /// </summary>
    private async Task TryPostRepairFullBackupAsync(SqlSession s, DatabaseInfo db, IProgress<RepairLogEntry> p, CancellationToken ct)
    {
        try
        {
            Report(p, db.Name, RepairLogLevel.Warning,
                "O reparo alterou a estrutura do banco: a cadeia de backups anterior NÃO é mais confiável. Criando um backup FULL novo (nova base)...");
            var file = $"{AppConstants.SanitizeToken(db.Name, "banco")}_PosReparo_{DateTime.Now:yyyyMMdd_HHmmss}.bak";
            var comp = await s.SupportsBackupCompressionAsync(ct) ? ", COMPRESSION" : string.Empty;
            var sql =
                "DECLARE @cmd nvarchar(max);\n" +
                "SET @cmd = N'BACKUP DATABASE ' + QUOTENAME(@db) + N' TO DISK = @f WITH INIT" + comp + ", NAME = @n;';\n" +
                "EXEC sys.sp_executesql @cmd, N'@f nvarchar(4000), @n nvarchar(256)', @f = @file, @n = @name;";
            var r = await RunStepAsync(s, db, p, "Backup FULL pós-reparo (nova base da cadeia)", sql,
                new Dictionary<string, object?> { ["@db"] = db.Name, ["@file"] = file, ["@name"] = $"RestoreDB pós-reparo de {db.Name}" }, ct);
            Report(p, db.Name, r.Success ? RepairLogLevel.Success : RepairLogLevel.Warning,
                r.Success
                    ? $"Backup FULL pós-reparo criado na pasta padrão de backups: {file}"
                    : "Não foi possível criar o backup pós-reparo — faça um BACKUP DATABASE ... FULL assim que possível.");
        }
        catch (OperationCanceledException) { throw; }
        catch { /* best-effort */ }
    }

    /// <summary>Reajusta as sementes de IDENTITY após remoção de linhas (best-effort).</summary>
    private async Task TryReseedIdentitiesAsync(SqlSession s, DatabaseInfo db, IProgress<RepairLogEntry> p, CancellationToken ct)
    {
        try
        {
            var n = await s.ReseedIdentitiesAsync(db.Name, ct);
            if (n > 0)
                Report(p, db.Name, RepairLogLevel.Info, $"Sementes de IDENTITY reajustadas em {n} tabela(s) (evita saltos/colisões após remoção de linhas).");
        }
        catch (OperationCanceledException) { throw; }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// OCUPA a vaga SINGLE_USER: o ALTER ... SET SINGLE_USER não deixa esta conexão "dentro" do
    /// banco (a sessão opera a partir de master), então entre um comando e outro um processo
    /// qualquer pode tomar a única vaga e travar o reparo. Um USE muda o contexto da conexão de
    /// forma durável, garantindo a exclusividade real durante a sequência. Os comandos seguintes
    /// não dependem do contexto (todos qualificam o banco explicitamente). Best-effort: se o USE
    /// falhar, o comportamento é o mesmo de antes. Usar APENAS quando o banco permanece ONLINE
    /// durante todo o fluxo (transições ONLINE/EMERGENCY invalidariam o contexto da conexão).
    /// </summary>
    private async Task TryOccupySingleUserSlotAsync(SqlSession s, DatabaseInfo db, IProgress<RepairLogEntry> p, CancellationToken ct)
    {
        var use = "USE [" + db.Name.Replace("]", "]]") + "];";
        var r = await s.ExecuteAsync("Ocupar a vaga SINGLE_USER (USE)", use, null, AppConstants.QueryCommandTimeoutSeconds, ct);
        if (r.Success)
            Report(p, db.Name, RepairLogLevel.Info, "Vaga SINGLE_USER ocupada por esta sessão (exclusividade garantida).");
        else
            Report(p, db.Name, RepairLogLevel.Warning, "Não foi possível ocupar a vaga SINGLE_USER — outro processo pode disputá-la durante o reparo.");
    }

    /// <summary>Devolve o contexto da conexão a master (par do <see cref="TryOccupySingleUserSlotAsync"/>).</summary>
    private static async Task TryReleaseSingleUserSlotAsync(SqlSession s, CancellationToken ct)
    {
        try { await s.ExecuteAsync("Liberar contexto (USE master)", "USE [master];", null, AppConstants.QueryCommandTimeoutSeconds, ct); }
        catch { /* best-effort */ }
    }

    private async Task TryCopyPhysicalFilesAsync(SqlSession s, DatabaseInfo db, IProgress<RepairLogEntry> p, CancellationToken ct)
    {
        try
        {
            var files = await s.GetDatabaseFilesAsync(db.Name, ct);
            var local = files.Where(f => File.Exists(f.PhysicalName)).ToList();
            if (local.Count == 0)
            {
                Report(p, db.Name, RepairLogLevel.Info,
                    "Cópia física de segurança não realizada (arquivos não acessíveis nesta máquina).");
                return;
            }
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            foreach (var f in local)
            {
                var dest = f.PhysicalName + $".pre_repair_{stamp}";
                File.Copy(f.PhysicalName, dest, overwrite: false);
                Report(p, db.Name, RepairLogLevel.Success, $"Cópia de segurança do arquivo criada: {dest}");
            }
        }
        catch (Exception ex)
        {
            Report(p, db.Name, RepairLogLevel.Warning,
                "Não foi possível copiar os arquivos como rede de segurança (o SQL Server pode mantê-los bloqueados): " + ex.Message);
        }
    }

    private void CopyFilesAside(IEnumerable<DatabaseFile> files, IProgress<RepairLogEntry> p, DatabaseInfo db)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        foreach (var f in files)
        {
            try
            {
                if (!File.Exists(f.PhysicalName)) continue;
                var dest = f.PhysicalName + $".pre_rebuild_{stamp}";
                File.Copy(f.PhysicalName, dest, overwrite: false);
                Report(p, db.Name, RepairLogLevel.Success, $"Cópia de segurança criada: {dest}");
            }
            catch (Exception ex)
            {
                Report(p, db.Name, RepairLogLevel.Warning, $"Não foi possível copiar '{f.PhysicalName}': {ex.Message}");
            }
        }
    }

    // ----------------------------------------------------------------------------------
    // Pré-visualização (dry-run): script T-SQL planejado para o estado atual
    // ----------------------------------------------------------------------------------

    /// <summary>
    /// Monta o T-SQL que a ferramenta executaria para o estado atual do banco, SEM executar nada.
    /// Útil para revisão por um DBA ou para rodar manualmente em um servidor remoto.
    /// </summary>
    public static string BuildPlannedScript(DatabaseInfo db, bool allowDataLoss)
    {
        var n = "[" + db.Name.Replace("]", "]]") + "]";
        var sb = new StringBuilder();
        sb.AppendLine($"/* Script planejado para [{db.Name}] — estado atual: {db.StateDesc}.");
        sb.AppendLine("   REVISE antes de executar. Compatível com SQL Server 2008 R2. */");
        sb.AppendLine();

        switch (db.StateDesc.Trim().ToUpperInvariant())
        {
            case "ONLINE":
                // Espelha OnlineRepairFlowAsync: um banco ONLINE pode ter corrupção que o estado não mostra.
                sb.AppendLine($"DBCC CHECKDB ({n}) WITH NO_INFOMSGS, ALL_ERRORMSGS;   -- decide: sem erros = nada a fazer");
                sb.AppendLine($"ALTER DATABASE {n} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; -- desconecta quem estiver usando");
                sb.AppendLine("-- Se a corrupção estiver só em índices não-clusterizados: ALTER INDEX <índice> ON <tabela> REBUILD;");
                sb.AppendLine($"DBCC CHECKDB ({n}, REPAIR_REBUILD) WITH ALL_ERRORMSGS;  -- SEM perda de dados");
                sb.AppendLine($"DBCC CHECKDB ({n}) WITH NO_INFOMSGS, ALL_ERRORMSGS;     -- verificação");
                if (allowDataLoss)
                    AppendDataLossSteps(sb, db, n, emergency: false);
                sb.AppendLine($"ALTER DATABASE {n} SET MULTI_USER;");
                break;
            case "OFFLINE":
                sb.AppendLine($"ALTER DATABASE {n} SET ONLINE;");
                sb.AppendLine($"ALTER DATABASE {n} SET MULTI_USER;");
                break;
            case "RESTORING":
            case "RECOVERING":
                sb.AppendLine($"RESTORE DATABASE {n} WITH RECOVERY;");
                break;
            case "RECOVERY_PENDING":
                sb.AppendLine($"ALTER DATABASE {n} SET ONLINE;   -- se falhar, seguir para o fluxo EMERGENCY abaixo");
                sb.AppendLine();
                goto case "SUSPECT";
            case "SUSPECT":
            case "EMERGENCY":
                sb.AppendLine($"ALTER DATABASE {n} SET EMERGENCY;");
                sb.AppendLine($"ALTER DATABASE {n} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;");
                sb.AppendLine($"DBCC CHECKDB ({n}) WITH NO_INFOMSGS, ALL_ERRORMSGS;   -- diagnóstico");
                sb.AppendLine($"ALTER DATABASE {n} SET ONLINE;                          -- recuperação normal");
                sb.AppendLine($"DBCC CHECKDB ({n}, REPAIR_REBUILD) WITH ALL_ERRORMSGS;  -- SEM perda de dados");
                sb.AppendLine($"DBCC CHECKDB ({n}) WITH NO_INFOMSGS, ALL_ERRORMSGS;     -- verificação");
                if (allowDataLoss)
                    AppendDataLossSteps(sb, db, n, emergency: true);
                sb.AppendLine($"ALTER DATABASE {n} SET MULTI_USER;");
                break;
            default:
                sb.AppendLine("-- Estado sem reparo automático planejado (ex.: desconhecido): avalie manualmente.");
                break;
        }
        return sb.ToString();
    }

    /// <summary>Parte destrutiva do script planejado: rede de segurança (TrySafetyNetAsync) + REPAIR_ALLOW_DATA_LOSS.</summary>
    private static void AppendDataLossSteps(StringBuilder sb, DatabaseInfo db, string n, bool emergency)
    {
        var token = AppConstants.SanitizeToken(db.Name, "banco");
        sb.AppendLine();
        sb.AppendLine("/* DESTRUTIVO — somente com confirmação explícita: */");
        if (emergency)
        {
            sb.AppendLine($"ALTER DATABASE {n} SET EMERGENCY;");
            sb.AppendLine($"ALTER DATABASE {n} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;");
        }
        sb.AppendLine($"BACKUP LOG {n} TO DISK = N'{token}_tail.trn' WITH NO_TRUNCATE, CONTINUE_AFTER_ERROR, INIT; -- preserva o tail-log (modelo FULL)");
        sb.AppendLine($"BACKUP DATABASE {n} TO DISK = N'{token}_RestoreDB.bak' WITH COPY_ONLY, INIT, CONTINUE_AFTER_ERROR; -- backup de segurança (tentativa)");
        sb.AppendLine($"DBCC CHECKDB ({n}, REPAIR_ALLOW_DATA_LOSS) WITH ALL_ERRORMSGS;");
        sb.AppendLine($"DBCC CHECKDB ({n}) WITH NO_INFOMSGS, ALL_ERRORMSGS;");
        sb.AppendLine("/* Se a verificação ficar limpa: a cadeia de backups anterior deixa de ser confiável — backup FULL novo: */");
        sb.AppendLine($"BACKUP DATABASE {n} TO DISK = N'{token}_PosReparo.bak' WITH INIT;");
    }

    private async Task TryBackupAsync(SqlSession s, DatabaseInfo db, IProgress<RepairLogEntry> p, CancellationToken ct)
    {
        Report(p, db.Name, RepairLogLevel.Info, "Tentando backup de segurança (COPY_ONLY) antes do reparo destrutivo...");

        var fileName = $"{AppConstants.SanitizeToken(db.Name, "banco")}_RestoreDB_{DateTime.Now:yyyyMMdd_HHmmss}.bak";
        var backupName = $"RestoreDB COPY_ONLY backup de {db.Name}";

        // O caminho relativo faz o SQL Server gravar na pasta padrão de backups da instância.
        // CONTINUE_AFTER_ERROR: com 'backup checksum default' ligado (2014+), páginas com checksum
        // inválido abortariam justamente o backup de um banco corrompido — que é quando ele mais importa.
        var comp = await s.SupportsBackupCompressionAsync(ct) ? ", COMPRESSION" : string.Empty;
        var sql =
            "DECLARE @cmd nvarchar(max);\n" +
            "SET @cmd = N'BACKUP DATABASE ' + QUOTENAME(@db) + N' TO DISK = @bkfile WITH COPY_ONLY, INIT, CONTINUE_AFTER_ERROR" + comp + ", NAME = @bkname';\n" +
            "EXEC sys.sp_executesql @cmd, N'@bkfile nvarchar(4000), @bkname nvarchar(256)', @bkfile = @file, @bkname = @name;";

        var prm = new Dictionary<string, object?>
        {
            ["@db"] = db.Name,
            ["@file"] = fileName,
            ["@name"] = backupName
        };

        var r = await RunStepAsync(s, db, p, "Backup de segurança (COPY_ONLY)", sql, prm, ct);
        if (r.Success)
            Report(p, db.Name, RepairLogLevel.Success, $"Backup criado na pasta padrão de backups do servidor: {fileName}");
        else
            Report(p, db.Name, RepairLogLevel.Warning,
                "Não foi possível efetuar o backup (o banco pode estar inacessível). O reparo prosseguirá conforme autorizado.");
    }

    /// <summary>
    /// Estado do banco depois que ele "assenta". Um ALTER DATABASE (SET ONLINE / SET MULTI_USER) num banco SUSPECT
    /// o reinicia: ele aparece ONLINE por alguns segundos e a recuperação o devolve a SUSPECT. Ler o estado nesse
    /// instante faria a tela mostrar ONLINE para um banco inacessível — espera o mesmo estado por 5 s (máx. 60 s).
    /// </summary>
    private static async Task<string> GetSettledStateAsync(SqlSession s, string dbName, CancellationToken ct)
    {
        var last = await s.GetDatabaseStateAsync(dbName, ct);
        var since = DateTime.UtcNow;
        var deadline = since.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(500, ct);
            var now = await s.GetDatabaseStateAsync(dbName, ct);
            if (!now.Equals(last, StringComparison.OrdinalIgnoreCase)) { last = now; since = DateTime.UtcNow; }
            else if (DateTime.UtcNow - since >= TimeSpan.FromSeconds(5)
                     && !last.Equals("RECOVERING", StringComparison.OrdinalIgnoreCase)) break;
        }
        return last;
    }

    private async Task TrySetMultiUserAsync(SqlSession s, DatabaseInfo db, IProgress<RepairLogEntry> p, CancellationToken ct)
    {
        var (sql, prm) = AlterDatabase(db.Name, "SET MULTI_USER");
        var r = await s.ExecuteAsync("SET MULTI_USER", sql, prm, AppConstants.RepairCommandTimeoutSeconds, ct);
        _log.Write(new RepairLogEntry { Database = db.Name, Level = RepairLogLevel.Command, Message = "Restaurando acesso normal (SET MULTI_USER)" });
        Report(p, db.Name, r.Success ? RepairLogLevel.Info : RepairLogLevel.Warning,
            r.Success ? "Acesso normal restaurado (MULTI_USER)." : "Não foi possível restaurar o MULTI_USER automaticamente.");
    }

    // ----------------------------------------------------------------------------------
    // Execução + log de um passo
    // ----------------------------------------------------------------------------------

    private Task<CommandExecutionResult> RunStepAsync(
        SqlSession s, DatabaseInfo db, IProgress<RepairLogEntry> p,
        string description, (string sql, Dictionary<string, object?> prm) cmd, CancellationToken ct)
        => RunStepAsync(s, db, p, description, cmd.sql, cmd.prm, ct);

    private async Task<CommandExecutionResult> RunStepAsync(
        SqlSession s, DatabaseInfo db, IProgress<RepairLogEntry> p,
        string description, string sql, Dictionary<string, object?> prm, CancellationToken ct)
    {
        Report(p, db.Name, RepairLogLevel.Command, "» " + description);

        var r = await s.ExecuteAsync(description, sql, prm, AppConstants.RepairCommandTimeoutSeconds, ct);

        foreach (var m in r.InfoMessages)
            Report(p, db.Name, RepairLogLevel.Info, "    " + m);
        foreach (var m in r.ErrorMessages)
            Report(p, db.Name, RepairLogLevel.Error, "    " + m);

        Report(p, db.Name, r.Success ? RepairLogLevel.Success : RepairLogLevel.Warning,
            $"    → {(r.Success ? "OK" : "Retornou erros / não concluído")}: {description}");
        return r;
    }

    // ----------------------------------------------------------------------------------
    // Construção do T-SQL (compatível com SQL Server 2008 R2; nome via QUOTENAME)
    // ----------------------------------------------------------------------------------

    private static (string sql, Dictionary<string, object?> prm) AlterDatabase(string dbName, string setClause)
    {
        var sql =
            "DECLARE @cmd nvarchar(max);\n" +
            "SET @cmd = N'ALTER DATABASE ' + QUOTENAME(@db) + N' " + setClause + ";';\n" +
            "EXEC sys.sp_executesql @cmd;";
        return (sql, new Dictionary<string, object?> { ["@db"] = dbName });
    }

    private static (string sql, Dictionary<string, object?> prm) RestoreRecovery(string dbName)
    {
        var sql =
            "DECLARE @cmd nvarchar(max);\n" +
            "SET @cmd = N'RESTORE DATABASE ' + QUOTENAME(@db) + N' WITH RECOVERY;';\n" +
            "EXEC sys.sp_executesql @cmd;";
        return (sql, new Dictionary<string, object?> { ["@db"] = dbName });
    }

    private static (string sql, Dictionary<string, object?> prm) Dbcc(string dbName, string? repairOption, bool noInfoMsgs, bool tablock = false)
    {
        var repairPart = repairOption is null ? string.Empty : ", " + repairOption;
        var withPart = noInfoMsgs ? "WITH NO_INFOMSGS, ALL_ERRORMSGS" : "WITH ALL_ERRORMSGS";
        if (tablock && repairOption is null) withPart += ", TABLOCK";
        var sql =
            "DECLARE @cmd nvarchar(max);\n" +
            "SET @cmd = N'DBCC CHECKDB (' + QUOTENAME(@db) + N'" + repairPart + ") " + withPart + ";';\n" +
            "EXEC sys.sp_executesql @cmd;";
        return (sql, new Dictionary<string, object?> { ["@db"] = dbName });
    }

    /// <summary>
    /// CHECKDB somente-leitura (diagnóstico/verificação) com fallback: se o snapshot interno não
    /// puder ser criado (Msg 1823/7928 — disco cheio ou sem permissão na pasta de dados), repete
    /// WITH TABLOCK, que usa bloqueios em vez do snapshot. Comum em cenários de emergência, onde
    /// o disco cheio costuma ser justamente a causa do problema.
    /// </summary>
    private async Task<CommandExecutionResult> RunCheckDbStepAsync(
        SqlSession s, DatabaseInfo db, IProgress<RepairLogEntry> p, string description, string dbName, CancellationToken ct)
    {
        var r = await RunStepAsync(s, db, p, description, Dbcc(dbName, repairOption: null, noInfoMsgs: true), ct);
        if (!r.Success && r.ErrorMessages.Any(m => m.Contains("Msg 1823") || m.Contains("Msg 7928")))
        {
            Report(p, db.Name, RepairLogLevel.Warning,
                "O CHECKDB não conseguiu criar o snapshot interno (disco cheio/sem permissão). Repetindo WITH TABLOCK...");
            r = await RunStepAsync(s, db, p, description + " (TABLOCK)",
                Dbcc(dbName, repairOption: null, noInfoMsgs: true, tablock: true), ct);
        }
        return r;
    }

    /// <summary>
    /// Runbook (somente leitura) para corrupção de banco de SISTEMA — que esta ferramenta não repara
    /// automaticamente. Devolve os passos manuais a executar no servidor, conforme o banco.
    /// </summary>
    public static string BuildSystemDbRunbook(string dbName)
    {
        var n = dbName.Trim().ToLowerInvariant();
        var sb = new StringBuilder();
        sb.AppendLine($"Procedimento manual para o banco de SISTEMA [{dbName}] — execute no servidor, como administrador.");
        sb.AppendLine("⚠ Não automatizado: bancos de sistema exigem cuidado e parada do serviço. Acione o DBA se possível.");
        sb.AppendLine();
        switch (n)
        {
            case "master":
                sb.AppendLine("master corrompido (o SQL não inicia):");
                sb.AppendLine("1) Inicie a instância em modo de usuário único: net start MSSQLSERVER /m\"SQLCMD\"  (ou via /m).");
                sb.AppendLine("2) Conecte com sqlcmd -S .\\INSTANCIA e RESTORE DATABASE master FROM DISK='...' WITH REPLACE; (precisa de backup do master).");
                sb.AppendLine("3) Sem backup do master: reconstrua os bancos de sistema com o setup do SQL Server (/ACTION=REBUILDDATABASE) e restaure msdb/model do backup.");
                break;
            case "model":
                sb.AppendLine("model corrompido (tempdb não é criado, o SQL não sobe):");
                sb.AppendLine("1) Inicie com flags de trace 3608 e 3609 e em /m para pular a recriação do tempdb.");
                sb.AppendLine("2) RESTORE DATABASE model FROM DISK='...' WITH REPLACE; reinicie normalmente.");
                break;
            case "msdb":
                sb.AppendLine("msdb corrompido (Agent/histórico de backup afetados; a instância sobe):");
                sb.AppendLine("1) Pare o SQL Server Agent.");
                sb.AppendLine("2) RESTORE DATABASE msdb FROM DISK='...' WITH REPLACE; reinicie o Agent.");
                break;
            case "tempdb":
                sb.AppendLine("tempdb corrompido: o tempdb é recriado a cada inicialização.");
                sb.AppendLine("1) Reinicie o serviço do SQL Server — o tempdb será recriado limpo.");
                sb.AppendLine("2) Se persistir, mova os arquivos do tempdb para um volume saudável (ALTER DATABASE tempdb MODIFY FILE ...).");
                break;
            default:
                sb.AppendLine("Banco de sistema não reconhecido — acione o DBA/suporte.");
                break;
        }
        return sb.ToString();
    }

    private void Report(IProgress<RepairLogEntry> p, string db, RepairLogLevel level, string message)
    {
        var entry = new RepairLogEntry { Level = level, Message = message, Database = db };
        _log.Write(entry);
        p.Report(entry);
    }
}
