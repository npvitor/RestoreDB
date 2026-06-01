using RestoreDB.DataAccess;
using RestoreDB.Domain.Models;
using RestoreDB.Services;

// Runner mínimo que invoca os SERVIÇOS REAIS da ferramenta contra um banco, para validar
// o comportamento sob corrupção real. Usa autenticação do Windows (sem senha).
//
// uso:
//   diagnose <db>
//   repair-indexes <db>
//   repair <db> [dataloss]
//   restore <db> <bakPath> [replace]
//   state <db>

if (args.Length < 2)
{
    Console.Error.WriteLine("uso: <diagnose|repair-indexes|repair|restore|state> <db> [extra]");
    return 2;
}

var server = Environment.GetEnvironmentVariable("PROBE_SERVER") ?? "localhost";
var info = new ConnectionInfo { Server = server, UseWindowsAuth = true };
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
        var diag = await new RepairService(log).DiagnoseAsync(db, conn, progress);
        Console.WriteLine($"RESULT: action={diag.RecommendedAction} indexOnly={diag.IndexOnlyRepairPossible} " +
                          $"data={diag.DataCorruptionSuspected} log={diag.LogCorruptionSuspected} " +
                          $"suspectPages={diag.SuspectPages.Count} checkdbErros={(diag.CheckDb?.ErrorCount ?? -1)} " +
                          $"minRepair={(diag.CheckDb?.MinRepairLevel ?? "")}");
        Console.WriteLine($"SUMMARY: {diag.Summary}");
        break;
    }

    case "repair-indexes":
    {
        var r = await new RepairService(log).RebuildIndexesAsync(db, conn, progress);
        Console.WriteLine($"RESULT: {r.Status} | {r.Message}");
        return r.Status is RepairStatus.Success or RepairStatus.NoActionNeeded ? 0 : 1;
    }

    case "repair":
    {
        var dataloss = args.Length > 2 && args[2].Equals("dataloss", StringComparison.OrdinalIgnoreCase);
        var r = await new RepairService(log).RepairAsync(db, conn, dataloss, progress);
        Console.WriteLine($"RESULT: {r.Status} | {r.Message}");
        return r.Status is RepairStatus.Success or RepairStatus.NoActionNeeded ? 0 : 1;
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
