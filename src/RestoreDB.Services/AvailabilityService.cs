using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using RestoreDB.DataAccess;
using RestoreDB.Domain.Constants;
using RestoreDB.Domain.Models;

namespace RestoreDB.Services;

/// <summary>
/// Resolve as causas de indisponibilidade que NÃO são corrupção — e, portanto, NÃO justificam reparo
/// com perda de dados — mas que deixam o banco em RECOVERY_PENDING/SUSPECT ou recusando gravações:
///  • LOG CHEIO (erro 9002): backup de log, crescimento/limite do arquivo, arquivo de log extra em
///    outro disco e, por fim, religar o banco;
///  • ARQUIVOS INACESSÍVEIS: arquivo movido (novo caminho via MODIFY FILE), sem permissão para a conta
///    de serviço (concessão via icacls quando local e como Administrador) ou preso por outro processo.
/// Nada aqui descarta dados. Todo o T-SQL é compatível com o SQL Server 2008 R2.
/// </summary>
public sealed class AvailabilityService
{
    private readonly SqlDataAccess _data = new();
    private readonly LogService _log;

    public AvailabilityService(LogService log) => _log = log;

    // ----------------------------------------------------------------------------------
    // Log de transações cheio (9002)
    // ----------------------------------------------------------------------------------

    /// <summary>
    /// Libera o log de transações SEM perda de dados, na ordem do menos ao mais invasivo:
    /// (1) LOG_BACKUP → BACKUP LOG (o espaço volta a ser reutilizável e a cadeia de backups continua);
    ///     CHECKPOINT → CHECKPOINT; ACTIVE_TRANSACTION/replicação → informa quem segura o log (não mata
    ///     sessões por conta própria);
    /// (2) arquivo no limite ou sem crescimento, com disco disponível → libera o crescimento;
    /// (3) disco do log cheio → adiciona um arquivo de log EXTRA no volume com mais espaço livre;
    /// (4) banco fora do ar (RECOVERY_PENDING/SUSPECT por log cheio na recuperação) → SET ONLINE.
    /// </summary>
    public async Task<RepairResult> ResolveLogFullAsync(
        DatabaseInfo db, string connectionString, IProgress<RepairLogEntry> progress, CancellationToken ct = default)
    {
        var result = new RepairResult();
        Report(progress, db.Name, RepairLogLevel.Info, $"════════ Resolver log de transações cheio de [{db.Name}] (sem perda de dados) ════════");
        await using var s = await _data.OpenSessionAsync(connectionString, ct);

        var state = await s.GetDatabaseStateAsync(db.Name, ct);
        var online = state.Equals("ONLINE", StringComparison.OrdinalIgnoreCase);
        var info = await s.GetLogSpaceInfoAsync(db.Name, ct);
        Report(progress, db.Name, RepairLogLevel.Info, $"Estado: {state}. {info.Summary}");
        foreach (var f in info.Files) Report(progress, db.Name, RepairLogLevel.Info, "  • " + f.Display);

        var actions = 0;

        // (1) O que segura o log.
        if (online)
        {
            switch (info.ReuseWait)
            {
                case "LOG_BACKUP" when !info.RecoveryModel.Equals("SIMPLE", StringComparison.OrdinalIgnoreCase):
                    if (await BackupLogAsync(s, db.Name, progress, ct)) actions++;
                    break;
                case "CHECKPOINT":
                case "NOTHING":
                    var cp = await RunStepAsync(s, db.Name, progress, "CHECKPOINT (libera o log já gravado)",
                        "DECLARE @proc nvarchar(300) = QUOTENAME(@db) + N'.sys.sp_executesql'; EXEC @proc N'CHECKPOINT;';",
                        new() { ["@db"] = db.Name }, ct);
                    if (cp.Success) actions++;
                    break;
                case "ACTIVE_TRANSACTION":
                    await ReportOldestTransactionAsync(s, db.Name, progress, ct);
                    break;
                case "REPLICATION":
                case "LOG_SCAN":
                    Report(progress, db.Name, RepairLogLevel.Warning,
                        "A replicação/CDC ainda não leu o log. Verifique o Log Reader Agent / a captura de dados (não é seguro descartar isso automaticamente).");
                    break;
                case "DATABASE_MIRRORING":
                case "AVAILABILITY_REPLICA":
                    Report(progress, db.Name, RepairLogLevel.Warning,
                        "Um espelho/réplica está atrasado ou desconectado. Restabeleça a sincronização (ou remova a réplica) para liberar o log.");
                    break;
            }
        }
        else if (!info.RecoveryModel.Equals("SIMPLE", StringComparison.OrdinalIgnoreCase))
        {
            // Fora do ar não há BACKUP LOG normal; a saída é dar espaço ao log e religar.
            Report(progress, db.Name, RepairLogLevel.Info, "Banco fora do ar: o espaço será dado ao log antes de religar.");
        }

        // (2)/(3) Espaço físico para o log.
        info = await s.GetLogSpaceInfoAsync(db.Name, ct);
        var needsRoom = !online || info.UsedPercent is null or >= 85;
        if (needsRoom)
        {
            foreach (var f in info.Files.Where(f => f.VolumeFreeMb is null or >= 1024 && (f.AtMaxSize || f.AutoGrowthDisabled)))
            {
                var r = await RunStepAsync(s, db.Name, progress, $"Liberando o crescimento do log {f.LogicalName} (MAXSIZE = UNLIMITED, FILEGROWTH = 256MB)",
                    "DECLARE @cmd nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@db) + N' MODIFY FILE (NAME = ' + QUOTENAME(@f) + N', MAXSIZE = UNLIMITED, FILEGROWTH = 256MB);';\n" +
                    "EXEC sys.sp_executesql @cmd;",
                    new() { ["@db"] = db.Name, ["@f"] = f.LogicalName }, ct);
                if (r.Success) actions++;
            }

            var diskFull = info.Files.Count > 0 && info.Files.All(f => f.VolumeFreeMb is < 1024);
            if (diskFull || (info.CannotGrow && info.Files.All(f => f.VolumeFreeMb is not null)))
            {
                var logVolumes = info.Files.Select(f => Path.GetPathRoot(f.PhysicalName) ?? string.Empty).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var folders = await s.GetInstanceFoldersByFreeSpaceAsync(ct);
                var target = folders.FirstOrDefault(x => x.FreeMb >= 2048
                    && !logVolumes.Contains(Path.GetPathRoot(x.Directory) ?? string.Empty));
                if (target.Directory is null)
                {
                    Report(progress, db.Name, RepairLogLevel.Warning,
                        "Nenhum outro disco da instância tem espaço livre suficiente (≥ 2 GB) para um arquivo de log extra. " +
                        "Libere espaço no disco do log (arquivos antigos, backups velhos) e tente de novo.");
                }
                else
                {
                    var logical = $"{AppConstants.SanitizeToken(db.Name, "banco")}_log_extra_{DateTime.Now:yyyyMMddHHmmss}";
                    var path = Path.Combine(target.Directory, logical + ".ldf");
                    var r = await RunStepAsync(s, db.Name, progress, $"Adicionando arquivo de log extra em {target.Directory} ({target.FreeMb:N0} MB livres)",
                        "DECLARE @cmd nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@db) + N' ADD LOG FILE (NAME = ' + QUOTENAME(@n) + " +
                        "N', FILENAME = N''' + REPLACE(@p, N'''', N'''''') + N''', SIZE = 512MB, FILEGROWTH = 256MB);';\n" +
                        "EXEC sys.sp_executesql @cmd;",
                        new() { ["@db"] = db.Name, ["@n"] = logical, ["@p"] = path }, ct);
                    if (r.Success)
                    {
                        actions++;
                        Report(progress, db.Name, RepairLogLevel.Success,
                            $"Arquivo de log extra criado: {path}. Depois que a situação normalizar (backup de log feito), ele pode ser removido com " +
                            $"ALTER DATABASE [{db.Name}] REMOVE FILE [{logical}];");
                    }
                }
            }
        }

        // (4) Religa se estava fora do ar.
        if (!online)
        {
            var r = await RunStepAsync(s, db.Name, progress, "Religando o banco (SET ONLINE)",
                "DECLARE @cmd nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@db) + N' SET ONLINE;'; EXEC sys.sp_executesql @cmd;",
                new() { ["@db"] = db.Name }, ct);
            if (r.Success) actions++;
        }

        var finalState = await s.GetDatabaseStateAsync(db.Name, ct);
        var after = await s.GetLogSpaceInfoAsync(db.Name, ct);
        result.FinalStateDesc = finalState;
        db.StateDesc = finalState;
        var ok = finalState.Equals("ONLINE", StringComparison.OrdinalIgnoreCase) && !after.IsCritical;
        result.Status = ok ? RepairStatus.Success : RepairStatus.Failed;
        result.Message = ok
            ? $"Log de transações liberado sem perda de dados. Banco {finalState}. {after.Summary}"
            : actions == 0
                ? $"Não foi possível liberar o log automaticamente ({after.ReuseWaitFriendly}). Veja o registro para a orientação."
                : $"Ações aplicadas, mas o log ainda requer atenção. Estado: {finalState}. {after.Summary}";
        Report(progress, db.Name, ok ? RepairLogLevel.Success : RepairLogLevel.Warning, result.Message);
        return result;
    }

    private async Task<bool> BackupLogAsync(SqlSession s, string db, IProgress<RepairLogEntry> p, CancellationToken ct)
    {
        var file = $"{AppConstants.SanitizeToken(db, "banco")}_log_{DateTime.Now:yyyyMMdd_HHmmss}.trn";
        var comp = await s.SupportsBackupCompressionAsync(ct) ? ", COMPRESSION" : string.Empty;
        var r = await RunStepAsync(s, db, p, "Backup de log (libera o espaço do log mantendo a cadeia de backups)",
            "DECLARE @cmd nvarchar(max) = N'BACKUP LOG ' + QUOTENAME(@db) + N' TO DISK = @f WITH INIT" + comp + ", NAME = @n;';\n" +
            "EXEC sys.sp_executesql @cmd, N'@f nvarchar(4000), @n nvarchar(256)', @f = @file, @n = @name;",
            new() { ["@db"] = db, ["@file"] = file, ["@name"] = $"RestoreDB log (liberar espaço) de {db}" }, ct);
        if (r.Success)
            Report(p, db, RepairLogLevel.Success,
                $"Backup de log gravado na pasta padrão de backups: {file}. GUARDE-O: ele faz parte da cadeia de restauração.");
        return r.Success;
    }

    private async Task ReportOldestTransactionAsync(SqlSession s, string db, IProgress<RepairLogEntry> p, CancellationToken ct)
    {
        var r = await s.GetOldestOpenTransactionAsync(db, ct);
        Report(p, db, RepairLogLevel.Warning, r is null
            ? "Uma transação aberta segura o log, mas não foi possível identificá-la."
            : $"Transação aberta segurando o log: sessão {r.Value.SessionId} ({r.Value.Program} em {r.Value.Host}, login {r.Value.Login}), " +
              $"aberta desde {r.Value.Started:dd/MM HH:mm:ss}. Peça ao usuário para concluir a operação ou, se estiver travada, encerre-a (KILL {r.Value.SessionId}) — " +
              "o que ela não confirmou será desfeito. A ferramenta NÃO encerra sessões por conta própria.");
    }

    // ----------------------------------------------------------------------------------
    // Arquivos inacessíveis
    // ----------------------------------------------------------------------------------

    /// <summary>
    /// Devolve o banco ao ar quando o problema é de ACESSO aos arquivos, sem perda de dados:
    /// (1) arquivos realocados pelo usuário (<paramref name="relocations"/>: nome lógico → novo caminho,
    ///     que precisa existir NO SERVIDOR) → SET OFFLINE + MODIFY FILE (FILENAME) — o catálogo passa a
    ///     apontar para o lugar certo;
    /// (2) arquivos locais → concede Controle Total à conta de serviço do SQL Server (icacls) — exige
    ///     executar como Administrador; sem isso, registra os comandos para o suporte;
    /// (3) SET ONLINE (a recuperação normal do SQL Server roda com os arquivos acessíveis).
    /// </summary>
    public async Task<RepairResult> FixFileAccessAsync(
        DatabaseInfo db, string connectionString, IProgress<RepairLogEntry> progress, CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? relocations = null)
    {
        var result = new RepairResult();
        Report(progress, db.Name, RepairLogLevel.Info, $"════════ Corrigir acesso aos arquivos de [{db.Name}] (sem perda de dados) ════════");
        await using var s = await _data.OpenSessionAsync(connectionString, ct);

        var files = await s.GetDatabaseFilesAsync(db.Name, ct);
        var state = await s.GetDatabaseStateAsync(db.Name, ct);
        Report(progress, db.Name, RepairLogLevel.Info, $"Estado atual: {state}.");

        // (1) Novos caminhos.
        if (relocations is { Count: > 0 })
        {
            var valid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (logical, newPath) in relocations)
            {
                if (await s.FileExistsOnServerAsync(newPath, ct) != true)
                {
                    Report(progress, db.Name, RepairLogLevel.Error,
                        $"O novo caminho de {logical} NÃO existe no servidor SQL: {newPath}. (Se a ferramenta roda noutra máquina, use o caminho como o SERVIDOR o enxerga.)");
                    continue;
                }
                valid[logical] = newPath;
            }
            if (valid.Count > 0)
            {
                if (!state.Equals("OFFLINE", StringComparison.OrdinalIgnoreCase))
                    await RunStepAsync(s, db.Name, progress, "Colocando o banco OFFLINE para apontar os novos caminhos",
                        "DECLARE @cmd nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@db) + N' SET OFFLINE WITH ROLLBACK IMMEDIATE;'; EXEC sys.sp_executesql @cmd;",
                        new() { ["@db"] = db.Name }, ct);
                foreach (var (logical, newPath) in valid)
                {
                    await RunStepAsync(s, db.Name, progress, $"Apontando {logical} para {newPath} (MODIFY FILE)",
                        "DECLARE @cmd nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@db) + N' MODIFY FILE (NAME = ' + QUOTENAME(@f) + " +
                        "N', FILENAME = N''' + REPLACE(@p, N'''', N'''''') + N''');';\nEXEC sys.sp_executesql @cmd;",
                        new() { ["@db"] = db.Name, ["@f"] = logical, ["@p"] = newPath }, ct);
                }
                files = await s.GetDatabaseFilesAsync(db.Name, ct);
            }
        }

        // (2) Permissão para a conta de serviço nos arquivos visíveis desta máquina.
        var local = files.Where(f => (f.IsData || f.IsLog) && File.Exists(f.PhysicalName)).ToList();
        var accounts = await s.GetSqlServiceAccountsAsync(ct);
        if (local.Count > 0 && accounts.Count > 0)
        {
            if (!IsRunningElevated())
            {
                Report(progress, db.Name, RepairLogLevel.Warning,
                    "Para conceder permissão aos arquivos é preciso executar a ferramenta COMO ADMINISTRADOR. Comandos para o suporte executar no servidor:");
                foreach (var f in local)
                    foreach (var a in accounts)
                        Report(progress, db.Name, RepairLogLevel.Info, $"icacls \"{f.PhysicalName}\" /grant \"{a}\":(F)");
            }
            else
            {
                foreach (var f in local)
                {
                    var granted = accounts.Any(a => RunTool("icacls", $"\"{f.PhysicalName}\" /grant \"{a}\":(F)"));
                    Report(progress, db.Name, granted ? RepairLogLevel.Success : RepairLogLevel.Warning,
                        granted ? $"Permissão concedida à conta do SQL Server: {f.PhysicalName}"
                                : $"Não foi possível conceder permissão em {f.PhysicalName}.");
                }
            }
        }
        else if (local.Count == 0)
        {
            Report(progress, db.Name, RepairLogLevel.Info,
                "Os arquivos não são visíveis desta máquina (servidor remoto): a permissão, se for o caso, precisa ser concedida no servidor " +
                $"à conta do SQL Server ({string.Join(" / ", accounts)}).");
        }

        // (3) Religar.
        var cur = await s.GetDatabaseStateAsync(db.Name, ct);
        if (!cur.Equals("ONLINE", StringComparison.OrdinalIgnoreCase))
        {
            var r = await RunStepAsync(s, db.Name, progress, "Religando o banco (SET ONLINE — recuperação normal)",
                "DECLARE @cmd nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@db) + N' SET ONLINE;'; EXEC sys.sp_executesql @cmd;",
                new() { ["@db"] = db.Name }, ct);
            if (!r.Success && r.ErrorMessages.Any(m => m.Contains(" 32(") || m.Contains("another process") || m.Contains("outro processo")))
                Report(progress, db.Name, RepairLogLevel.Warning,
                    "O arquivo continua preso por outro processo: feche antivírus/backup de arquivos/cópias em andamento e tente de novo.");
        }

        var finalState = await s.GetDatabaseStateAsync(db.Name, ct);
        db.StateDesc = finalState;
        result.FinalStateDesc = finalState;
        result.Status = finalState.Equals("ONLINE", StringComparison.OrdinalIgnoreCase) ? RepairStatus.Success : RepairStatus.Failed;
        result.Message = result.Status == RepairStatus.Success
            ? "Arquivos acessíveis e banco ONLINE — nenhum dado foi perdido. Recomenda-se rodar o Diagnóstico para confirmar a consistência."
            : $"O banco continua {finalState}. Veja o registro: pode faltar arquivo, permissão (executar como Administrador) ou o problema é outro — rode o Diagnóstico de novo.";
        Report(progress, db.Name, result.Status == RepairStatus.Success ? RepairLogLevel.Success : RepairLogLevel.Warning, result.Message);
        return result;
    }

    // ----------------------------------------------------------------------------------
    // Utilitários
    // ----------------------------------------------------------------------------------

    private static bool IsRunningElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private static bool RunTool(string exe, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, arguments)
            {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            };
            using var process = Process.Start(psi);
            if (process is null) return false;
            process.WaitForExit(20000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch { return false; }
    }

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
