using RestoreDB.DataAccess;
using RestoreDB.Domain.Models;
using RestoreDB.Services;

// Runner mínimo que invoca os SERVIÇOS REAIS da ferramenta contra um banco, para validar
// o comportamento sob corrupção real. Autenticação do Windows, ou SQL com PROBE_USER/PROBE_PASS.
//
// uso:
//   diagnose <db>
//   repair-indexes <db>
//   repair <db> [dataloss]
//   fix-constraints <db> [bakPath]   (bakPath opcional: .bak para recuperar registros-pai)
//   restore <db> <bakPath> [replace]
//   restore-pit <db> [yyyy-MM-ddTHH:mm:ss | now] [notail]
//   test-restore <db> <bakPath>
//   restore-copy <db> [copyName]
//   salvage-table <fromDb> <toDb> <schema.table>
//   verify-backups <db>
//   restore-standby <db> [yyyy-MM-ddTHH:mm:ss | now]
//   finalize-recovery <db>
//   check-table <db> <schema.table>
//   reseed <db>
//   history <db>
//   rebuild-log <db>
//   resolve-log <db>                          (log cheio / 9002, sem perda de dados)
//   fix-files <db> [logico=caminho ...]       (arquivo ausente/sem permissão; religa sem perda)
//   recover-deleted <db> <yyyy-MM-ddTHH:mm:ss> <schema.tabela>[,...]  (desfaz DELETE sem sobrescrever)
//   state <db>

if (args.Length < 2)
{
    Console.Error.WriteLine("uso: <diagnose|repair|repair-indexes|fix-constraints|simulate-repair|monitor|maintain|fragmentation|forensic|attach|restore|restore-pit|restore-pages|restore-standby|finalize-recovery|test-restore|restore-copy|salvage-table|verify-backups|check-table|reseed|history|rebuild-log|resolve-log|fix-files|recover-deleted|state> <db> [extra]");
    return 2;
}

var server = Environment.GetEnvironmentVariable("PROBE_SERVER") ?? "localhost";
// Autenticação SQL opcional (PROBE_USER/PROBE_PASS); sem elas, autenticação do Windows.
var probeUser = Environment.GetEnvironmentVariable("PROBE_USER");
var info = string.IsNullOrWhiteSpace(probeUser)
    ? new ConnectionInfo { Server = server, UseWindowsAuth = true }
    : new ConnectionInfo { Server = server, User = probeUser, Password = Environment.GetEnvironmentVariable("PROBE_PASS") ?? string.Empty };
var conn = new SqlDataAccess().BuildConnectionString(info);
var log = new LogService();
var progress = new Progress<RepairLogEntry>(e => Console.WriteLine($"  [{e.Level,-7}] {e.Message}"));

var cmd = args[0].ToLowerInvariant();
var dbName = args[1];

DatabaseInfo db;
try
{
    var list = await new DatabaseService().ListDatabasesAsync(conn);
    db = list.FirstOrDefault(d => string.Equals(d.Name, dbName, StringComparison.OrdinalIgnoreCase))
         ?? new DatabaseInfo { Name = dbName };
}
catch (Exception ex)
{
    Console.Error.WriteLine("Falha ao conectar/listar: " + ex.Message);
    return 3;
}

Console.WriteLine($"== {cmd} [{dbName}] (estado atual: {db.StateDesc}) ==");

switch (cmd)
{
    case "state":
        Console.WriteLine($"RESULT: estado={db.StateDesc} problematico={db.IsProblematic}");
        break;

    case "diagnose":
    {
        var diag = await new RepairService(log, new RestoreService(log)).DiagnoseAsync(db, conn, progress);
        Console.WriteLine($"RESULT: action={diag.RecommendedAction} indexOnly={diag.IndexOnlyRepairPossible} " +
                          $"data={diag.DataCorruptionSuspected} log={diag.LogCorruptionSuspected} " +
                          $"logical={diag.LogicalCorruptionSuspected} fkViolacoes={diag.ForeignKeyViolations.Count} " +
                          $"untrusted={diag.UntrustedConstraints.Count} catalogErros={diag.CatalogErrors.Count} " +
                          $"suspectPages={diag.SuspectPages.Count} checkdbErros={(diag.CheckDb?.ErrorCount ?? -1)} " +
                          $"minRepair={(diag.CheckDb?.MinRepairLevel ?? "")}");
        Console.WriteLine($"SUMMARY: {diag.Summary}");
        if (diag.BackupPolicy is { } bp)
            Console.WriteLine($"POLICY: modelo={bp.RecoveryModel} maxSev={bp.MaxSeverity} achados={bp.Findings.Count} — {bp.Summary}");
        break;
    }

    case "monitor":
    {
        var svc = new MonitorService(log);
        var targets = new List<DatabaseInfo>();
        if (dbName.Equals("all", StringComparison.OrdinalIgnoreCase))
            targets.AddRange((await new DatabaseService().ListDatabasesAsync(conn)).Where(d => !d.IsSystemDatabase));
        else
            targets.Add(db);

        var anyNew = false;
        foreach (var t in targets)
        {
            var m = await svc.RunCheckAsync(t, conn, progress);
            Console.WriteLine($"  [{t.Name}] novoProblema={m.HasNewIssue} corrupcao={m.CorruptionPresent} acao={m.RecommendedAction}");
            anyNew |= m.HasNewIssue;
        }
        Console.WriteLine($"RESULT: novidades={anyNew}");
        return anyNew ? 2 : 0;   // código 2 = há novidade (Agendador pode disparar e-mail)
    }

    case "simulate-repair":
    {
        var r = await new RepairService(log, new RestoreService(log)).SimulateRepairAsync(db, conn, progress);
        Console.WriteLine($"RESULT: executed={r.Executed} perdaTotal={r.TotalRowsLost} tabelasAfetadas={r.AffectedTables.Count} checkdbLimpoDepois={r.CheckDbCleanAfter}");
        Console.WriteLine($"SUMMARY: {r.Message}");
        return r.Executed ? 0 : 1;
    }

    case "fix-constraints":
    {
        // arg opcional [bakPath]: força a recuperação de registros-pai a partir desse .bak (senão, automático).
        var recoveryBak = args.Length > 2 ? args[2] : null;
        var opts = recoveryBak is null ? null : new ParentRecoveryOptions { InitialBackupPath = recoveryBak };
        var r = await new RepairService(log, new RestoreService(log)).FixConstraintViolationsAsync(db, conn, progress, default, opts);
        Console.WriteLine($"RESULT: {r.Status} | {r.Message}");
        return r.Status is RepairStatus.Success or RepairStatus.NoActionNeeded ? 0 : 1;
    }

    case "repair-indexes":
    {
        var r = await new RepairService(log, new RestoreService(log)).RebuildIndexesAsync(db, conn, progress);
        Console.WriteLine($"RESULT: {r.Status} | {r.Message}");
        return r.Status is RepairStatus.Success or RepairStatus.NoActionNeeded ? 0 : 1;
    }

    case "repair":
    {
        var dataloss = args.Length > 2 && args[2].Equals("dataloss", StringComparison.OrdinalIgnoreCase);
        var r = await new RepairService(log, new RestoreService(log)).RepairAsync(db, conn, dataloss, progress);
        Console.WriteLine($"RESULT: {r.Status} | {r.Message}");
        return r.Status is RepairStatus.Success or RepairStatus.NoActionNeeded ? 0 : 1;
    }

    case "restore-copy":
    {
        var copyName = args.Length > 2 ? args[2] : null;
        var r = await new RestoreService(log).RestoreAsCopyAsync(dbName, copyName, conn, progress);
        Console.WriteLine($"RESULT: {r.Status} | {r.Message}");
        return r.Status == RepairStatus.Success ? 0 : 1;
    }

    case "salvage-table":
    {
        if (args.Length < 4) { Console.Error.WriteLine("salvage-table exige <toDb> <schema.table>"); return 2; }
        // args: [0]=cmd [1]=fromDb [2]=toDb [3]=schema.table
        var r = await new RestoreService(log).SalvageTableAsync(dbName, args[2], args[3], conn, progress);
        Console.WriteLine($"RESULT: {r.Status} | {r.Message}");
        return r.Status == RepairStatus.Success ? 0 : 1;
    }

    case "verify-backups":
    {
        var items = await new RestoreService(log).VerifyBackupHealthAsync(dbName, conn, progress);
        foreach (var it in items) Console.WriteLine("  " + it.Display);
        Console.WriteLine($"RESULT: {items.Count(i => i.Verified)}/{items.Count} verificado(s)");
        return items.All(i => i.Verified) ? 0 : 1;
    }

    case "attach":
    {
        if (args.Length < 3) { Console.Error.WriteLine("attach exige <mdfPath> [ldfPath]"); return 2; }
        var ldf = args.Length > 3 ? args[3] : null;
        var rebuild = string.IsNullOrEmpty(ldf);
        var r = await new RestoreService(log).AttachDatabaseAsync(dbName, args[2], ldf, rebuild, conn, progress);
        Console.WriteLine($"RESULT: {r.Status} | {r.Message}");
        return r.Status == RepairStatus.Success ? 0 : 1;
    }

    case "restore-pages":
    {
        if (args.Length < 3) { Console.Error.WriteLine("restore-pages exige <fileId:pageId,...>"); return 2; }
        var r = await new RestoreService(log).RestorePagesFromHistoryAsync(dbName, args[2], conn, progress);
        Console.WriteLine($"RESULT: {r.Status} | {r.Message}");
        return r.Status == RepairStatus.Success ? 0 : 1;
    }

    case "restore-standby":
    {
        DateTime? target = null;
        if (args.Length > 2 && !args[2].Equals("now", StringComparison.OrdinalIgnoreCase))
            target = DateTime.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
        var r = await new RestoreService(log).RestoreToStandbyAsync(dbName, target, conn, progress);
        Console.WriteLine($"RESULT: {r.Status} | {r.Message}");
        return r.Status == RepairStatus.Success ? 0 : 1;
    }

    case "finalize-recovery":
    {
        var r = await new RestoreService(log).FinalizeRecoveryAsync(dbName, conn, progress);
        Console.WriteLine($"RESULT: {r.Status} | {r.Message}");
        return r.Status == RepairStatus.Success ? 0 : 1;
    }

    case "forensic":
    {
        var txs = await new RestoreService(log).ListRecentDestructiveTransactionsAsync(dbName, conn);
        foreach (var t in txs.Take(30)) Console.WriteLine("  " + t.Display);
        Console.WriteLine($"RESULT: {txs.Count} transação(ões) destrutiva(s) recente(s) no log ativo");
        return 0;
    }

    case "history":
    {
        var snaps = new HistoryService().Load(dbName);
        foreach (var s in snaps.TakeLast(10))
            Console.WriteLine($"  {s.TimestampUtc:yyyy-MM-dd HH:mm:ss}Z  estado={s.StateDesc}  checkdbErros={s.CheckDbErrors}  fk={s.ForeignKeyViolations}  acao={s.RecommendedAction}");
        Console.WriteLine($"RESULT: {snaps.Count} snapshot(s) no histórico");
        return 0;
    }

    case "fragmentation":
    {
        var frags = await new MaintenanceService(log).GetFragmentationAsync(dbName, conn);
        foreach (var f in frags.Take(30)) Console.WriteLine("  " + f.Display);
        Console.WriteLine($"RESULT: {frags.Count(f => f.NeedsMaintenance)}/{frags.Count} índice(s) precisam de manutenção");
        return 0;
    }

    case "maintain":
    {
        var r = await new MaintenanceService(log).RunMaintenanceAsync(db, conn, progress);
        Console.WriteLine($"RESULT: {r.Status} | {r.Message}");
        return r.Status == RepairStatus.Success ? 0 : 1;
    }

    case "check-table":
    {
        if (args.Length < 3) { Console.Error.WriteLine("check-table exige <schema.table>"); return 2; }
        var rep = await new RepairService(log, new RestoreService(log)).CheckTableAsync(db, args[2], conn, progress);
        Console.WriteLine($"RESULT: executed={rep.Executed} erros={rep.ErrorCount} minRepair={rep.MinRepairLevel}");
        return rep.Executed && !rep.HasErrors ? 0 : 1;
    }

    case "reseed":
    {
        var n = await new RepairService(log, new RestoreService(log)).ReseedIdentitiesAsync(db, conn, progress);
        Console.WriteLine($"RESULT: tabelas_reseed={n}");
        return 0;
    }

    case "resolve-log":
    {
        var r = await new AvailabilityService(log).ResolveLogFullAsync(db, conn, progress);
        Console.WriteLine($"RESULT: {r.Status} | {r.Message}");
        return r.Status == RepairStatus.Success ? 0 : 1;
    }

    case "fix-files":
    {
        // args opcionais: nomeLogico=novoCaminho (como o SERVIDOR enxerga o caminho)
        var moves = args.Skip(2).Select(a => a.Split('=', 2)).Where(p => p.Length == 2)
                        .ToDictionary(p => p[0], p => p[1], StringComparer.OrdinalIgnoreCase);
        var r = await new AvailabilityService(log).FixFileAccessAsync(db, conn, progress, default, moves);
        Console.WriteLine($"RESULT: {r.Status} | {r.Message}");
        return r.Status == RepairStatus.Success ? 0 : 1;
    }

    case "recover-deleted":
    {
        if (args.Length < 4) { Console.Error.WriteLine("recover-deleted exige <yyyy-MM-ddTHH:mm:ss> <schema.tabela>[,...]"); return 2; }
        var target = DateTime.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
        var tables = args[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var r = await new RestoreService(log).RecoverDeletedRowsAsync(dbName, target, tables, conn, progress);
        Console.WriteLine($"RESULT: {r.Status} | {r.Message}");
        return r.Status == RepairStatus.Success ? 0 : 1;
    }

    case "rebuild-log":
    {
        var r = await new RepairService(log, new RestoreService(log)).RebuildTransactionLogAsync(db, conn, progress);
        Console.WriteLine($"RESULT: {r.Status} | {r.Message}");
        return r.Status == RepairStatus.Success ? 0 : 1;
    }

    case "restore-pit":
    {
        DateTime? target = null;
        if (args.Length > 2 && !args[2].Equals("now", StringComparison.OrdinalIgnoreCase))
            target = DateTime.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
        var tail = !(args.Length > 3 && args[3].Equals("notail", StringComparison.OrdinalIgnoreCase));
        var r = await new RestoreService(log).RestoreToPointInTimeAsync(dbName, target, tail, conn, progress);
        Console.WriteLine($"RESULT: {r.Status} | {r.Message}");
        return r.Status == RepairStatus.Success ? 0 : 1;
    }

    case "test-restore":
    {
        if (args.Length < 3) { Console.Error.WriteLine("test-restore exige <bakPath>"); return 2; }
        var pieces = new List<RestorePiece> { new() { DiskPath = args[2], Position = 1 } };
        var r = await new RestoreService(log).TestRestoreAsync(dbName, pieces, conn, progress);
        Console.WriteLine($"RESULT: {r.Status} | {r.Message}");
        return r.Status == RepairStatus.Success ? 0 : 1;
    }

    case "restore":
    {
        if (args.Length < 3) { Console.Error.WriteLine("restore exige <bakPath>"); return 2; }
        var bak = args[2];
        var replace = args.Length > 3 && args[3].Equals("replace", StringComparison.OrdinalIgnoreCase);
        var req = new RestoreRequest
        {
            DatabaseName = dbName,
            Replace = replace,
            Pieces = { new RestorePiece { DiskPath = bak, Position = 1 } }
        };
        var r = await new RestoreService(log).RestoreDatabaseAsync(req, conn, progress);
        Console.WriteLine($"RESULT: {r.Status} | {r.Message}");
        return r.Status == RepairStatus.Success ? 0 : 1;
    }

    default:
        Console.Error.WriteLine("comando desconhecido: " + cmd);
        return 2;
}

return 0;
