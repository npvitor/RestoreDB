using System.Diagnostics;
using System.IO;
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

    public RepairService(LogService log) => _log = log;

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
        CancellationToken ct = default)
    {
        var result = new RepairResult();
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
                "ONLINE" => await OnlineRepairFlowAsync(session, db, allowDataLoss, progress, ct),
                "OFFLINE" => await BringOnlineAsync(session, db, progress, ct),
                "RESTORING" => await RestoreWithRecoveryAsync(session, db, progress, ct),
                "RECOVERING" => await RestoreWithRecoveryAsync(session, db, progress, ct),
                "RECOVERY_PENDING" => await RecoveryPendingAsync(session, db, allowDataLoss, progress, ct),
                "SUSPECT" => await EmergencyRepairFlowAsync(session, db, allowDataLoss, progress, ct),
                "EMERGENCY" => await EmergencyRepairFlowAsync(session, db, allowDataLoss, progress, ct),
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
            finalState = await session.GetDatabaseStateAsync(db.Name, ct);
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
            RepairStatus.NoActionNeeded => "O banco já está ONLINE; nenhuma ação foi necessária.",
            RepairStatus.NeedsDataLossConfirmation =>
                "O reparo SEM perda de dados não foi suficiente. É possível tentar o reparo COM possível perda de dados (requer confirmação).",
            _ => $"Não foi possível reparar automaticamente. Estado atual: {finalState}."
        };

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

            // Topologia (espelhamento/AG/log shipping/replicação) — afeta a segurança do reparo.
            diag.Topology = await session.GetTopologyInfoAsync(db.Name, ct);
            if (diag.Topology.IsRisky)
            {
                Report(progress, db.Name, RepairLogLevel.Warning, "TOPOLOGIA: " + diag.Topology.Summary);
                foreach (var w in diag.Topology.Warnings)
                    Report(progress, db.Name, RepairLogLevel.Warning, "  ⚠ " + w);
            }

            // Recomendação preventiva: PAGE_VERIFY = CHECKSUM detecta corrupção futura.
            var pageVerify = await session.GetPageVerifyOptionAsync(db.Name, ct);
            if (pageVerify.Length > 0 && !pageVerify.Equals("CHECKSUM", StringComparison.OrdinalIgnoreCase))
                Report(progress, db.Name, RepairLogLevel.Warning,
                    $"PAGE_VERIFY está como {pageVerify}. Recomenda-se: ALTER DATABASE [{db.Name}] SET PAGE_VERIFY CHECKSUM; (detecta corrupção futura).");

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
            var lines = await session.ReadErrorLogAsync(db.Name, ct);

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

            // Saúde lógica adicional (B4): índices desabilitados, dependências quebradas, constraints não confiáveis.
            await TryLogicalHealthChecksAsync(session, db, diag, progress, ct);

            // Causa-raiz: erros de E/S e corrupção recorrente (suspeita de hardware/volume).
            TryRootCauseAnalysis(db, diag, progress);

            BuildConclusion(diag);

            // Histórico (B5): compara com a última verificação para apontar o que é NOVO (detecção precoce).
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
        var report = await session.CheckDbWithResultsAsync(db.Name, repairOption: null, ct, physicalOnly: physicalOnly);
        diag.CheckDb = report;

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

        // Constraints não confiáveis: integridade nunca validada (causa comum de FK órfã silenciosa).
        var untrusted = await session.GetUntrustedConstraintsAsync(db.Name, ct);
        if (untrusted.Count > 0)
        {
            diag.UntrustedConstraints.AddRange(untrusted);
            Report(progress, db.Name, RepairLogLevel.Info,
                $"{untrusted.Count} constraint(s) NÃO confiável(is) (is_not_trusted): {string.Join(", ", untrusted.Take(10))}.");
        }

        // Violações de FK/CHECK (o caso do registro órfão).
        var violations = await session.CheckConstraintsAsync(db.Name, ct);
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
    /// Saúde lógica adicional (B4), somente leitura: índices DESABILITADOS, dependências QUEBRADAS
    /// (views/procs referenciando objetos inexistentes) e constraints NÃO confiáveis. Advisory.
    /// </summary>
    private async Task TryLogicalHealthChecksAsync(SqlSession session, DatabaseInfo db, DiagnosisResult diag, IProgress<RepairLogEntry> progress, CancellationToken ct)
    {
        if (diag.CheckDb is not { Executed: true }) return; // banco precisa estar acessível
        try
        {
            var disabled = await session.GetDisabledIndexesAsync(db.Name, ct);
            if (disabled.Count > 0)
            {
                diag.HealthFindings.Add(new PolicyFinding { Severity = FindingSeverity.Medium,
                    Message = $"{disabled.Count} índice(s) DESABILITADO(s) (consultas podem estar lentas): {string.Join(", ", disabled.Take(8))}." });
            }

            var broken = await session.GetBrokenDependenciesAsync(db.Name, ct);
            if (broken.Count > 0)
            {
                diag.HealthFindings.Add(new PolicyFinding { Severity = FindingSeverity.High,
                    Message = $"{broken.Count} dependência(s) QUEBRADA(s) (objeto referenciado não existe): {string.Join("; ", broken.Take(6))}." });
            }

            if (diag.UntrustedConstraints.Count > 0)
                diag.HealthFindings.Add(new PolicyFinding { Severity = FindingSeverity.Low,
                    Message = $"{diag.UntrustedConstraints.Count} constraint(s) NÃO confiável(is) (is_not_trusted)." });

            foreach (var f in diag.HealthFindings)
                Report(progress, db.Name, SeverityToLevel(f.Severity), "  • Saúde lógica: " + f.Message);
        }
        catch { /* best-effort */ }
    }

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

    /// <summary>Persiste o snapshot do diagnóstico e reporta o que é NOVO desde a última verificação (B5).</summary>
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

    private static void BuildConclusion(DiagnosisResult d)
    {
        // Estado íntegro: CHECKDB ao vivo limpo, integridade lógica OK e sem flags ativas.
        if (d.CheckDb is { Executed: true, HasErrors: false }
            && !d.DataCorruptionSuspected && !d.LogCorruptionSuspected && !d.LogFull
            && !d.LogicalCorruptionSuspected)
        {
            d.RecommendedAction = RecommendedActionKind.NoAction;
            d.Summary = "Nenhuma corrupção detectada — DBCC CHECKDB e integridade lógica íntegros.";
            d.Recommendation = string.Empty;
            return;
        }

        var topoNote = d.Topology is { IsRisky: true }
            ? " ATENÇÃO: " + d.Topology.Summary + " Reparos destrutivos/restore podem afetar essa configuração."
            : string.Empty;

        // 1) Corrupção restrita a índices não-clusterizados → reconstrução SEM perda de dados.
        if (d.IndexOnlyRepairPossible)
        {
            d.RecommendedAction = RecommendedActionKind.RebuildIndexesOnly;
            d.Summary = "A corrupção está restrita a índices NÃO clusterizados.";
            d.Recommendation = "Use 'Reparar índices (sem perda de dados)': basta reconstruir os índices afetados (ALTER INDEX REBUILD). Nenhum dado é perdido." + topoNote;
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
            d.Recommendation = (fk > 0
                ? $"Há {fk} violação(ões) de chave estrangeira (registros órfãos). Use 'Corrigir integridade (remover registros órfãos)' — operação destrutiva, com backup de segurança antes. "
                : "Há violações de CHECK e/ou inconsistências de catálogo; revise os dados. ")
                + "Para preservar 100% dos registros, prefira RESTAURAR de backup." + topoNote;
            return;
        }

        // 2) Log de transações cheio (erro 9002) — não é corrupção.
        if (d.LogFull && !d.DataCorruptionSuspected && !d.LogCorruptionSuspected)
        {
            d.RecommendedAction = RecommendedActionKind.FreeDiskSpace;
            d.Summary = "O LOG de transações está CHEIO (erro 9002) — não é corrupção.";
            d.Recommendation = "Libere espaço no volume do log, faça BACKUP LOG e/ou adicione espaço ao arquivo de log. Depois religue o banco." + topoNote;
            return;
        }

        // 3) Corrupção de DADOS → restaurar de backup (ou restauração de página, se poucas páginas).
        if (d.DataCorruptionSuspected && !d.LogCorruptionSuspected)
        {
            var fewPages = d.SuspectPages.Count(p => p.IsActiveCorruption) is > 0 and <= 100;
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
            d.Recommendation = "Reconstruir o log: 'reparo COM perda de dados' (reconstrói o log no servidor, sem Administrador) ou a opção 'Reconstruir log'. Os dados devem ser preservados." + topoNote;
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
    private async Task<RepairStatus> RecoveryPendingAsync(SqlSession s, DatabaseInfo db, bool allowDataLoss, IProgress<RepairLogEntry> p, CancellationToken ct)
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
        return await EmergencyRepairFlowAsync(s, db, allowDataLoss, p, ct);
    }

    /// <summary>
    /// SUSPECT / EMERGENCY (e fallback de RECOVERY_PENDING). Sequência validada em engine real:
    /// EMERGENCY → SINGLE_USER → diagnóstico → recuperação normal (SET ONLINE) → REPAIR_REBUILD
    /// (sem perda, exige o banco ONLINE) → verificação. Se não bastar e for autorizado:
    /// REPAIR_ALLOW_DATA_LOSS (em EMERGENCY).
    /// Importante: o SQL Server NÃO permite REPAIR_REBUILD com o banco em modo EMERGENCY
    /// (Msg 7901); por isso a recuperação normal é tentada primeiro.
    /// </summary>
    private async Task<RepairStatus> EmergencyRepairFlowAsync(SqlSession s, DatabaseInfo db, bool allowDataLoss, IProgress<RepairLogEntry> p, CancellationToken ct)
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
        var diag = await RunStepAsync(s, db, p, "Diagnóstico DBCC CHECKDB", Dbcc(db.Name, repairOption: null, noInfoMsgs: true), ct);
        Report(p, db.Name, diag.Success ? RepairLogLevel.Success : RepairLogLevel.Warning,
            diag.Success
                ? "Diagnóstico não encontrou erros de consistência."
                : "Diagnóstico encontrou inconsistências. Tentando reparo SEM perda de dados...");

        // 4. Reparo SEM perda de dados:
        //    (a) recuperação normal (SET ONLINE); (b) se ONLINE, REPAIR_REBUILD; (c) verificação.
        bool safeOk = false;
        Report(p, db.Name, RepairLogLevel.Info, "Tentando recuperação SEM perda de dados (SET ONLINE)...");
        await RunStepAsync(s, db, p, "Recuperação normal (SET ONLINE)", AlterDatabase(db.Name, "SET ONLINE"), ct);

        var stateAfterOnline = await s.GetDatabaseStateAsync(db.Name, ct);
        if (stateAfterOnline.Equals("ONLINE", StringComparison.OrdinalIgnoreCase))
        {
            Report(p, db.Name, RepairLogLevel.Success,
                "Banco recuperado para ONLINE. Aplicando REPAIR_REBUILD (sem perda) e verificando...");
            var rebuild = await RunStepAsync(s, db, p, "Reparo SEM perda de dados (REPAIR_REBUILD)",
                Dbcc(db.Name, "REPAIR_REBUILD", noInfoMsgs: false), ct);
            var verify = await RunStepAsync(s, db, p, "Verificação pós-reparo (DBCC CHECKDB)",
                Dbcc(db.Name, repairOption: null, noInfoMsgs: true), ct);
            safeOk = rebuild.Success && verify.Success;
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
                var verifyIdx = await RunStepAsync(s, db, p, "Verificação pós-reconstrução de índices (DBCC CHECKDB)",
                    Dbcc(db.Name, repairOption: null, noInfoMsgs: true), ct);
                if (rebuilt && verifyIdx.Success)
                {
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

        await TrySafetyNetAsync(s, db, p, ct);

        await RunStepAsync(s, db, p, "Reparo COM possível PERDA DE DADOS (REPAIR_ALLOW_DATA_LOSS)",
            Dbcc(db.Name, "REPAIR_ALLOW_DATA_LOSS", noInfoMsgs: false), ct);

        var verifyFinal = await RunStepAsync(s, db, p, "Verificação final (DBCC CHECKDB)",
            Dbcc(db.Name, repairOption: null, noInfoMsgs: true), ct);

        // O REPAIR_ALLOW_DATA_LOSS desaloca páginas SEM respeitar constraints — pode deixar
        // chaves estrangeiras órfãs. Como a perda de dados já foi autorizada, reconciliamos.
        await TryReconcileConstraintsAsync(s, db, p, allowDataLoss: true, ct);

        await TrySetMultiUserAsync(s, db, p, ct);
        return verifyFinal.Success ? RepairStatus.Success : RepairStatus.Failed;
    }

    /// <summary>
    /// Banco ONLINE: pode estar realmente saudável OU corrompido sem o estado indicar (corrupção
    /// física que só o CHECKDB revela, ou FK órfã que só o CHECKCONSTRAINTS revela). Verifica e,
    /// se houver corrupção, repara em SINGLE_USER (sem precisar de EMERGENCY, pois já está ONLINE):
    /// índices não-clusterizados / REPAIR_REBUILD (sem perda) → e, só com confirmação,
    /// REPAIR_ALLOW_DATA_LOSS / remoção de órfãos (com perda).
    /// </summary>
    private async Task<RepairStatus> OnlineRepairFlowAsync(SqlSession s, DatabaseInfo db, bool allowDataLoss, IProgress<RepairLogEntry> p, CancellationToken ct)
    {
        Report(p, db.Name, RepairLogLevel.Info, "Banco ONLINE: verificando consistência física e lógica antes de decidir...");
        var report = await s.CheckDbWithResultsAsync(db.Name, repairOption: null, ct);
        var hasPhysical = report is { Executed: true, HasErrors: true };

        var violations = await s.CheckConstraintsAsync(db.Name, ct);
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

            try
            {
                // a) Corrupção só em índices não-clusterizados → reconstrução SEM perda.
                if (report.AllErrorsAreNonClusteredIndex)
                {
                    Report(p, db.Name, RepairLogLevel.Info,
                        "Corrupção restrita a índices não-clusterizados — reconstruindo (SEM perda de dados)...");
                    var rebuilt = await RebuildNonClusteredIndexesAsync(s, db, report, p, ct);
                    var verifyIdx = await RunStepAsync(s, db, p, "Verificação pós-reconstrução de índices (DBCC CHECKDB)",
                        Dbcc(db.Name, repairOption: null, noInfoMsgs: true), ct);
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
                var verify = await RunStepAsync(s, db, p, "Verificação pós-reparo (DBCC CHECKDB)",
                    Dbcc(db.Name, repairOption: null, noInfoMsgs: true), ct);
                if (rebuild.Success && verify.Success)
                    return RepairStatus.Success;

                // c) Precisa de REPAIR_ALLOW_DATA_LOSS.
                if (!allowDataLoss)
                {
                    Report(p, db.Name, RepairLogLevel.Warning,
                        "O reparo SEM perda não foi suficiente. É necessária CONFIRMAÇÃO para o reparo COM possível perda de dados.");
                    return RepairStatus.NeedsDataLossConfirmation;
                }

                Report(p, db.Name, RepairLogLevel.Warning, "ATENÇÃO: reparo COM POSSÍVEL PERDA DE DADOS (autorizado).");
                await TrySafetyNetAsync(s, db, p, ct);
                await RunStepAsync(s, db, p, "Reparo COM possível PERDA DE DADOS (REPAIR_ALLOW_DATA_LOSS)",
                    Dbcc(db.Name, "REPAIR_ALLOW_DATA_LOSS", noInfoMsgs: false), ct);
                var verifyFinal = await RunStepAsync(s, db, p, "Verificação final (DBCC CHECKDB)",
                    Dbcc(db.Name, repairOption: null, noInfoMsgs: true), ct);
                await TryReconcileConstraintsAsync(s, db, p, allowDataLoss: true, ct);
                return verifyFinal.Success ? RepairStatus.Success : RepairStatus.Failed;
            }
            finally
            {
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
        try
        {
            await TrySafetyNetAsync(s, db, p, ct);
            await TryReconcileConstraintsAsync(s, db, p, allowDataLoss: true, ct);
            var remaining = await s.CheckConstraintsAsync(db.Name, ct);
            return remaining.Count(v => v.IsForeignKey) == 0 ? RepairStatus.Success : RepairStatus.Failed;
        }
        finally
        {
            await TrySetMultiUserAsync(s, db, p, ct);
        }
    }

    /// <summary>
    /// Reconciliação de integridade referencial após um reparo com perda de dados: localiza as
    /// linhas que violam chaves estrangeiras (registros órfãos) e, quando a perda já foi
    /// autorizada, remove-as. Best-effort — nunca derruba o fluxo de reparo.
    /// </summary>
    private async Task TryReconcileConstraintsAsync(SqlSession s, DatabaseInfo db, IProgress<RepairLogEntry> p, bool allowDataLoss, CancellationToken ct)
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

            var total = 0;
            foreach (var v in fk)
            {
                try
                {
                    var removed = await s.DeleteViolatingRowsAsync(db.Name, v, ct);
                    total += removed;
                    Report(p, db.Name, RepairLogLevel.Success, $"  ✔ {removed} registro(s) órfão(s) removido(s) — {v.Display}");
                }
                catch (Exception ex)
                {
                    Report(p, db.Name, RepairLogLevel.Warning, $"  ⚠ Não foi possível remover órfãos de {v.Constraint}: {ex.Message}");
                }
            }
            Report(p, db.Name, total > 0 ? RepairLogLevel.Success : RepairLogLevel.Info,
                $"Reconciliação de integridade concluída: {total} registro(s) órfão(s) removido(s).");
        }
        catch
        {
            // best-effort: a reconciliação nunca deve derrubar o reparo.
        }
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

        var check = await RunStepAsync(session, db, progress,
            "Verificação de consistência (DBCC CHECKDB)", Dbcc(db.Name, repairOption: null, noInfoMsgs: true), ct);

        if (finalState.Equals("ONLINE", StringComparison.OrdinalIgnoreCase))
        {
            result.Status = RepairStatus.Success;
            result.FinalStateDesc = finalState;
            result.Message = check.Success
                ? "Log de transações reconstruído com sucesso. Banco ONLINE e consistente."
                : "Log reconstruído e banco ONLINE, mas o DBCC CHECKDB ainda acusa inconsistências. Considere o reparo COM perda de dados.";
            Report(progress, db.Name, check.Success ? RepairLogLevel.Success : RepairLogLevel.Warning, result.Message);
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
                result.Status = RepairStatus.NeedsDataLossConfirmation;
                result.Message = "A corrupção NÃO está restrita a índices não-clusterizados. Use o reparo padrão (e, se necessário, RESTAURAR de backup).";
                Report(progress, db.Name, RepairLogLevel.Warning, result.Message);
                return result;
            }

            var ok = await RebuildNonClusteredIndexesAsync(session, db, report, progress, ct);
            var verify = await RunStepAsync(session, db, progress, "Verificação pós-reconstrução (DBCC CHECKDB)",
                Dbcc(db.Name, repairOption: null, noInfoMsgs: true), ct);

            result.Status = ok && verify.Success ? RepairStatus.Success : RepairStatus.Failed;
            if (result.Status == RepairStatus.Success)
            {
                await session.ClearSuspectPagesAsync(db.Name, ct);
                result.Message = "Índices reconstruídos com sucesso — SEM perda de dados. Banco consistente.";
                Report(progress, db.Name, RepairLogLevel.Success, result.Message);
            }
            else
            {
                result.Message = "A reconstrução de índices não eliminou todas as inconsistências. Considere o reparo padrão.";
                Report(progress, db.Name, RepairLogLevel.Warning, result.Message);
            }
            return result;
        }
        finally
        {
            // 1) Religa na PRÓPRIA sessão (que é a dona do slot SINGLE_USER quando viva).
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
        DatabaseInfo db, string connectionString, IProgress<RepairLogEntry> progress, CancellationToken ct = default)
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

            var total = 0;
            var ok = true;
            foreach (var v in fk)
            {
                try
                {
                    var removed = await session.DeleteViolatingRowsAsync(db.Name, v, ct);
                    total += removed;
                    Report(progress, db.Name, RepairLogLevel.Success, $"Removidos {removed} registro(s) órfão(s) — {v.Display}");
                }
                catch (Exception ex)
                {
                    ok = false;
                    Report(progress, db.Name, RepairLogLevel.Error, $"Falha ao remover órfãos de {v.Constraint}: {ex.Message}");
                }
            }

            var remaining = await session.CheckConstraintsAsync(db.Name, ct);
            var remainingFk = remaining.Count(v => v.IsForeignKey);

            result.Status = ok && remainingFk == 0 ? RepairStatus.Success : RepairStatus.Failed;
            result.Message = result.Status == RepairStatus.Success
                ? $"Integridade referencial restaurada: {total} registro(s) órfão(s) removido(s)."
                : $"Removidos {total} registro(s), mas ainda restam {remainingFk} violação(ões) de FK. Verifique o log.";
            Report(progress, db.Name, result.Status == RepairStatus.Success ? RepairLogLevel.Success : RepairLogLevel.Warning, result.Message);
            return result;
        }
        finally
        {
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
                "SET @cmd = N'BACKUP DATABASE ' + QUOTENAME(@db) + N' TO DISK = @f WITH COPY_ONLY, INIT, NAME = @n;';\n" +
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
                "SET @cmd = N'RESTORE DATABASE ' + QUOTENAME(@s) + N' FROM DISK = @f WITH " + moveLiteral + ", REPLACE, RECOVERY;';\n" +
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
    // Checagens granulares (B1): CHECKTABLE dirigido e reseed de IDENTITY
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

    private static (string sql, Dictionary<string, object?> prm) RebuildIndex(string db, int objectId, int indexId)
    {
        // O ALTER INDEX roda NO CONTEXTO do banco alvo (EXEC [db].sys.sp_executesql), montando o
        // nome qualificado (schema.tabela) a partir do object_id já lá dentro.
        //
        // IMPORTANTE: primeiro DISABLE, depois REBUILD. Um REBUILD direto LÊ as páginas do índice
        // — se elas estiverem fisicamente corrompidas (erro 824), o comando falha e derruba a
        // conexão. O DISABLE desaloca a estrutura SEM ler as páginas corrompidas, e o REBUILD
        // reconstrói o índice a partir do índice clusterizado/heap (íntegro), sem perda de dados.
        var sql =
            "DECLARE @inner nvarchar(max) = N'" +
            "DECLARE @q nvarchar(300) = QUOTENAME(OBJECT_SCHEMA_NAME(@o)) + N''.'' + QUOTENAME(OBJECT_NAME(@o)); " +
            "DECLARE @ix sysname = (SELECT name FROM sys.indexes WHERE object_id = @o AND index_id = @i); " +
            "IF @ix IS NOT NULL BEGIN " +
            "DECLARE @s1 nvarchar(max) = N''ALTER INDEX '' + QUOTENAME(@ix) + N'' ON '' + @q + N'' DISABLE;''; " +
            "DECLARE @s2 nvarchar(max) = N''ALTER INDEX '' + QUOTENAME(@ix) + N'' ON '' + @q + N'' REBUILD;''; " +
            "EXEC sys.sp_executesql @s1; EXEC sys.sp_executesql @s2; " +
            "END';\n" +
            "DECLARE @proc nvarchar(300) = QUOTENAME(@db) + N'.sys.sp_executesql';\n" +
            "EXEC @proc @inner, N'@o int, @i int', @o = @oid, @i = @iid;";
        return (sql, new Dictionary<string, object?> { ["@db"] = db, ["@oid"] = objectId, ["@iid"] = indexId });
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
    /// Tenta o backup COPY_ONLY e, em seguida, uma cópia física dos arquivos (quando locais) —
    /// porque o BACKUP costuma falhar com o banco em EMERGENCY (Msg 3033). Tudo best-effort.
    /// </summary>
    private async Task TrySafetyNetAsync(SqlSession s, DatabaseInfo db, IProgress<RepairLogEntry> p, CancellationToken ct)
    {
        await TryBackupAsync(s, db, p, ct);
        await TryCopyPhysicalFilesAsync(s, db, p, ct);
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
                {
                    sb.AppendLine();
                    sb.AppendLine("/* DESTRUTIVO — somente com confirmação explícita: */");
                    sb.AppendLine($"ALTER DATABASE {n} SET EMERGENCY;");
                    sb.AppendLine($"ALTER DATABASE {n} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;");
                    sb.AppendLine($"DBCC CHECKDB ({n}, REPAIR_ALLOW_DATA_LOSS) WITH ALL_ERRORMSGS;");
                    sb.AppendLine($"DBCC CHECKDB ({n}) WITH NO_INFOMSGS, ALL_ERRORMSGS;");
                }
                sb.AppendLine($"ALTER DATABASE {n} SET MULTI_USER;");
                break;
            default:
                sb.AppendLine("-- Estado saudável ou não problemático: nenhuma ação planejada.");
                break;
        }
        return sb.ToString();
    }

    private async Task TryBackupAsync(SqlSession s, DatabaseInfo db, IProgress<RepairLogEntry> p, CancellationToken ct)
    {
        Report(p, db.Name, RepairLogLevel.Info, "Tentando backup de segurança (COPY_ONLY) antes do reparo destrutivo...");

        var fileName = $"{AppConstants.SanitizeToken(db.Name, "banco")}_RestoreDB_{DateTime.Now:yyyyMMdd_HHmmss}.bak";
        var backupName = $"RestoreDB COPY_ONLY backup de {db.Name}";

        // O caminho relativo faz o SQL Server gravar na pasta padrão de backups da instância.
        var comp = await s.SupportsBackupCompressionAsync(ct) ? ", COMPRESSION" : string.Empty;
        var sql =
            "DECLARE @cmd nvarchar(max);\n" +
            "SET @cmd = N'BACKUP DATABASE ' + QUOTENAME(@db) + N' TO DISK = @bkfile WITH COPY_ONLY, INIT" + comp + ", NAME = @bkname';\n" +
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

    private static (string sql, Dictionary<string, object?> prm) Dbcc(string dbName, string? repairOption, bool noInfoMsgs)
    {
        var repairPart = repairOption is null ? string.Empty : ", " + repairOption;
        var withPart = noInfoMsgs ? "WITH NO_INFOMSGS, ALL_ERRORMSGS" : "WITH ALL_ERRORMSGS";
        var sql =
            "DECLARE @cmd nvarchar(max);\n" +
            "SET @cmd = N'DBCC CHECKDB (' + QUOTENAME(@db) + N'" + repairPart + ") " + withPart + ";';\n" +
            "EXEC sys.sp_executesql @cmd;";
        return (sql, new Dictionary<string, object?> { ["@db"] = dbName });
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
