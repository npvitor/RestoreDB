using RestoreDB.Domain.Models;
using RestoreDB.Services;
using Xunit;

namespace RestoreDB.Tests;

public class BackupChainPlannerTests
{
    private static BackupSetInfo Full(int day, decimal first, decimal last, decimal checkpoint, string device = "full.bak") => new()
    {
        BackupTypeCode = "D", PhysicalDevice = device, Position = 1,
        BackupFinishDate = new DateTime(2026, 1, day, 0, 0, 0),
        FirstLsn = first, LastLsn = last, CheckpointLsn = checkpoint, DatabaseBackupLsn = 0
    };

    private static BackupSetInfo Diff(int day, decimal first, decimal last, decimal dbBak, string device = "diff.bak") => new()
    {
        BackupTypeCode = "I", PhysicalDevice = device, Position = 1,
        BackupFinishDate = new DateTime(2026, 1, day, 0, 0, 0),
        FirstLsn = first, LastLsn = last, DatabaseBackupLsn = dbBak
    };

    private static BackupSetInfo Log(int dayHour, decimal first, decimal last, string device = "log.trn") => new()
    {
        BackupTypeCode = "L", PhysicalDevice = device, Position = 1,
        BackupFinishDate = new DateTime(2026, 1, 1, dayHour, 0, 0),
        FirstLsn = first, LastLsn = last
    };

    [Fact]
    public void Plan_PicksLatestFull_AndContiguousLogs_ToLatest()
    {
        var history = new List<BackupSetInfo>
        {
            Full(1, first: 100, last: 200, checkpoint: 150),
            Log(2, first: 200, last: 300),
            Log(3, first: 300, last: 400),
        };

        var plan = BackupChainPlanner.Plan("DB", history, target: null);

        Assert.True(plan.IsComplete);
        Assert.True(plan.ReachesTarget);
        Assert.NotNull(plan.BaseFull);
        Assert.Equal(3, plan.Pieces.Count);          // full + 2 logs
        Assert.False(plan.Pieces[0].IsLog);
        Assert.True(plan.Pieces[1].IsLog);
        Assert.True(plan.Pieces[2].IsLog);
    }

    [Fact]
    public void Plan_IncludesMatchingDifferential_ByCheckpointLsn()
    {
        var history = new List<BackupSetInfo>
        {
            Full(1, first: 100, last: 200, checkpoint: 150),
            Diff(2, first: 250, last: 350, dbBak: 150),   // database_backup_lsn casa com checkpoint do full
            Log(5, first: 350, last: 450),
        };

        var plan = BackupChainPlanner.Plan("DB", history, target: null);

        Assert.True(plan.IsComplete);
        Assert.NotNull(plan.Differential);
        Assert.Equal(3, plan.Pieces.Count);           // full + diff + 1 log
    }

    [Fact]
    public void Plan_IgnoresDifferential_FromAnotherFull()
    {
        var history = new List<BackupSetInfo>
        {
            Full(1, first: 100, last: 200, checkpoint: 150),
            Diff(2, first: 250, last: 350, dbBak: 999),   // não pertence a este full
        };

        var plan = BackupChainPlanner.Plan("DB", history, target: null);

        Assert.Null(plan.Differential);
        Assert.Single(plan.Pieces);                    // só o full
    }

    [Fact]
    public void Plan_DetectsBrokenLogChain_AndIsIncomplete()
    {
        var history = new List<BackupSetInfo>
        {
            Full(1, first: 100, last: 200, checkpoint: 150),
            Log(2, first: 200, last: 300),
            Log(4, first: 350, last: 450),   // GAP: começa em 350, mas a âncora está em 300
        };

        var plan = BackupChainPlanner.Plan("DB", history, target: null);

        Assert.False(plan.IsComplete);
        Assert.Contains(plan.Warnings, w => w.Contains("Quebra na cadeia"));
        Assert.Equal(2, plan.Pieces.Count);            // full + 1º log apenas
    }

    [Fact]
    public void Plan_NoFull_IsIncomplete()
    {
        var history = new List<BackupSetInfo> { Log(2, 200, 300) };
        var plan = BackupChainPlanner.Plan("DB", history, target: null);

        Assert.False(plan.IsComplete);
        Assert.Null(plan.BaseFull);
        Assert.Empty(plan.Pieces);
    }

    [Fact]
    public void Plan_StripedFull_CarriesAllDevicesIntoPiece()
    {
        var full = Full(1, first: 100, last: 200, checkpoint: 150);
        full.Devices.Add(@"C:\bk\DB_1.bak");
        full.Devices.Add(@"C:\bk\DB_2.bak");
        full.Devices.Add(@"C:\bk\DB_3.bak");

        var plan = BackupChainPlanner.Plan("DB", new List<BackupSetInfo> { full }, target: null);

        Assert.Single(plan.Pieces);
        var piece = plan.Pieces[0];
        Assert.Equal(3, piece.AllDevices.Count);
        Assert.Equal(@"C:\bk\DB_1.bak", piece.DiskPath);
        Assert.Equal(2, piece.AdditionalDevices.Count);
    }

    [Fact]
    public void Plan_PointInTime_StopsAtTargetLog()
    {
        var history = new List<BackupSetInfo>
        {
            Full(1, first: 100, last: 200, checkpoint: 150),
            Log(2, first: 200, last: 300),   // 02:00
            Log(4, first: 300, last: 400),   // 04:00 — contém o alvo (03:00)
            Log(6, first: 400, last: 500),   // 06:00 — depois do alvo, não deve entrar
        };
        var target = new DateTime(2026, 1, 1, 3, 0, 0);

        var plan = BackupChainPlanner.Plan("DB", history, target);

        Assert.True(plan.IsComplete);
        Assert.True(plan.ReachesTarget);
        Assert.Equal(3, plan.Pieces.Count);            // full + 2 logs (para 02:00 e 04:00)
    }
}
