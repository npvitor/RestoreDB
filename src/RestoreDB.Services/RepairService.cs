using System.Diagnostics;
using System.IO;
using System.Security.Principal;
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
                "ONLINE"           => RepairStatus.NoActionNeeded,
                "OFFLINE"          => await BringOnlineAsync(session, db, progress, ct),
                "RESTORING"        => await RestoreWithRecoveryAsync(session, db, progress, ct),
                "RECOVERING"       => await RestoreWithRecoveryAsync(session, db, progress, ct),
                "RECOVERY_PENDING" => await RecoveryPendingAsync(session, db, allowDataLoss, progress, ct),
                "SUSPECT"          => await EmergencyRepairFlowAsync(session, db, allowDataLoss, progress, ct),
                "EMERGENCY"        => await EmergencyRepairFlowAsync(session, db, allowDataLoss, progress, ct),
                _                  => RepairStatus.Failed
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
        DatabaseInfo db, string connectionString, IProgress<RepairLogEntry> progress, CancellationToken ct = default)
    {
        var diag = new DiagnosisResult { DatabaseName = db.Name, StateDesc = db.StateDesc };
        Report(progress, db.Name, RepairLogLevel.Info, $"──── Diagnóstico (somente leitura) de [{db.Name}] ────");

        try
        {
            await using var session = await _data.OpenSessionAsync(connectionString, ct);
            diag.StateDesc = await session.GetDatabaseStateAsync(db.Name, ct);

            var files = await session.GetDatabaseFilesAsync(db.Name, ct);
            foreach (var f in files)
                Report(progress, db.Name, RepairLogLevel.Info, $"Arquivo {f.TypeDesc}: {f.PhysicalName}");

            var logNames = files.Where(f => f.IsLog).Select(f => Path.GetFileName(f.PhysicalName))
                                .Where(n => !string.IsNullOrEmpty(n)).ToList();
            var dataNames = files.Where(f => f.IsData).Select(f => Path.GetFileName(f.PhysicalName))
                                 .Where(n => !string.IsNullOrEmpty(n)).ToList();

            Report(progress, db.Name, RepairLogLevel.Info, "Lendo o log de erros do SQL Server (sp_readerrorlog)...");
            var lines = await session.ReadErrorLogAsync(db.Name, ct);

            foreach (var line in lines)
            {
                if (!LooksLikeCorruptionLine(line)) continue;

                var refLog = logNames.Any(n => line.Contains(n, StringComparison.OrdinalIgnoreCase))
                             || line.Contains(".ldf", StringComparison.OrdinalIgnoreCase);
                var refData = dataNames.Any(n => line.Contains(n, StringComparison.OrdinalIgnoreCase))
                              || line.Contains(".mdf", StringComparison.OrdinalIgnoreCase)
                              || line.Contains(".ndf", StringComparison.OrdinalIgnoreCase);

                diag.Evidence.Add(line);
                if (refLog) diag.LogCorruptionSuspected = true;
                if (refData) diag.DataCorruptionSuspected = true;
            }

            BuildConclusion(diag);

            foreach (var e in diag.Evidence.Take(8))
                Report(progress, db.Name, RepairLogLevel.Warning, "  • " + e);
            if (!diag.HasEvidence)
                Report(progress, db.Name, RepairLogLevel.Info, "Sem entradas de corrupção encontradas no log de erros (ou sem permissão para lê-lo).");

            Report(progress, db.Name,
                diag.LogCorruptionSuspected || diag.DataCorruptionSuspected ? RepairLogLevel.Warning : RepairLogLevel.Success,
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

    private static void BuildConclusion(DiagnosisResult d)
    {
        if (d.LogCorruptionSuspected && !d.DataCorruptionSuspected)
        {
            d.Summary = "Os arquivos de DADOS parecem íntegros e o LOG de transações (.ldf) apresenta corrupção.";
            d.Recommendation = "Reconstruir o log: 'Reparar (modo seguro)' → 'reparo COM perda de dados' (reconstrói o log no servidor, sem Administrador) ou a opção 'Reconstruir log'. Os dados devem ser preservados.";
        }
        else if (d.DataCorruptionSuspected && !d.LogCorruptionSuspected)
        {
            d.Summary = "Há indícios de corrupção nos arquivos de DADOS (.mdf/.ndf).";
            d.Recommendation = "Prefira RESTAURAR de backup. Sem backup, use o reparo (DBCC CHECKDB), possivelmente COM perda de dados.";
        }
        else if (d.DataCorruptionSuspected && d.LogCorruptionSuspected)
        {
            d.Summary = "Há indícios de corrupção tanto nos DADOS quanto no LOG.";
            d.Recommendation = "Prefira RESTAURAR de backup. Caso contrário, reparo COM perda de dados.";
        }
        else
        {
            d.Summary = "Não foram encontradas evidências claras de corrupção no log de erros. A causa pode ser outra (arquivo/recurso indisponível, permissão, etc.).";
            d.Recommendation = "Tente 'Reparar (modo seguro)' e verifique se os arquivos do banco existem e estão acessíveis ao SQL Server.";
        }
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

        await TryBackupAsync(s, db, p, ct);

        await RunStepAsync(s, db, p, "Reparo COM possível PERDA DE DADOS (REPAIR_ALLOW_DATA_LOSS)",
            Dbcc(db.Name, "REPAIR_ALLOW_DATA_LOSS", noInfoMsgs: false), ct);

        var verifyFinal = await RunStepAsync(s, db, p, "Verificação final (DBCC CHECKDB)",
            Dbcc(db.Name, repairOption: null, noInfoMsgs: true), ct);

        await TrySetMultiUserAsync(s, db, p, ct);
        return verifyFinal.Success ? RepairStatus.Success : RepairStatus.Failed;
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

    private async Task TryBackupAsync(SqlSession s, DatabaseInfo db, IProgress<RepairLogEntry> p, CancellationToken ct)
    {
        Report(p, db.Name, RepairLogLevel.Info, "Tentando backup de segurança (COPY_ONLY) antes do reparo destrutivo...");

        var fileName = $"{SanitizeFileToken(db.Name)}_RestoreDB_{DateTime.Now:yyyyMMdd_HHmmss}.bak";
        var backupName = $"RestoreDB COPY_ONLY backup de {db.Name}";

        // O caminho relativo faz o SQL Server gravar na pasta padrão de backups da instância.
        const string sql =
            "DECLARE @cmd nvarchar(max);\n" +
            "SET @cmd = N'BACKUP DATABASE ' + QUOTENAME(@db) + N' TO DISK = @bkfile WITH COPY_ONLY, INIT, NAME = @bkname';\n" +
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

    private static string SanitizeFileToken(string name)
    {
        var chars = name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        var token = new string(chars).Trim('_');
        return string.IsNullOrEmpty(token) ? "banco" : token;
    }

    private void Report(IProgress<RepairLogEntry> p, string db, RepairLogLevel level, string message)
    {
        var entry = new RepairLogEntry { Level = level, Message = message, Database = db };
        _log.Write(entry);
        p.Report(entry);
    }
}
