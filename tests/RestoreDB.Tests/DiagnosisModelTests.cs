using RestoreDB.Domain.Models;
using Xunit;

namespace RestoreDB.Tests;

public class DiagnosisModelTests
{
    private static CorruptionDetail Err(int objectId, int indexId, string repairLevel) => new()
    {
        Error = 8928, Level = 16, State = 1, MessageText = "erro", RepairLevel = repairLevel,
        ObjectId = objectId, IndexId = indexId
    };

    [Fact]
    public void CheckDbReport_AllNonClusteredIndex_IsIndexOnly()
    {
        var r = new CheckDbReport { Executed = true };
        r.Details.Add(Err(100, 2, "repair_rebuild"));
        r.Details.Add(Err(100, 3, "repair_rebuild"));

        Assert.True(r.HasErrors);
        Assert.True(r.AllErrorsAreNonClusteredIndex);
        Assert.Equal("REPAIR_REBUILD", r.MinRepairLevel);
        Assert.Equal(2, r.NonClusteredIndexesToRebuild.Count);
    }

    [Fact]
    public void CheckDbReport_ClusteredOrHeap_NotIndexOnly()
    {
        var r = new CheckDbReport { Executed = true };
        r.Details.Add(Err(100, 2, "repair_rebuild"));
        r.Details.Add(Err(200, 1, "repair_allow_data_loss")); // índice clusterizado

        Assert.True(r.HasErrors);
        Assert.False(r.AllErrorsAreNonClusteredIndex);
        Assert.Equal("REPAIR_ALLOW_DATA_LOSS", r.MinRepairLevel);
    }

    [Fact]
    public void CheckDbReport_NoErrors()
    {
        var r = new CheckDbReport { Executed = true };
        Assert.False(r.HasErrors);
        Assert.False(r.AllErrorsAreNonClusteredIndex);
        Assert.Equal(string.Empty, r.MinRepairLevel);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(5, false)]
    [InlineData(7, false)]
    public void SuspectPage_ActiveCorruptionClassification(int eventType, bool active)
    {
        var p = new SuspectPageInfo { EventType = eventType };
        Assert.Equal(active, p.IsActiveCorruption);
        Assert.False(string.IsNullOrEmpty(p.EventTypeDescription));
    }

    [Fact]
    public void DiskSpace_LowDetection()
    {
        var ok = new DiskSpaceInfo { TotalBytes = 100L * 1024 * 1024 * 1024, AvailableBytes = 10L * 1024 * 1024 * 1024 };
        var lowByPercent = new DiskSpaceInfo { TotalBytes = 100L * 1024 * 1024 * 1024, AvailableBytes = 2L * 1024 * 1024 * 1024 };
        var lowByAbsolute = new DiskSpaceInfo { TotalBytes = 100L * 1024 * 1024 * 1024, AvailableBytes = 500L * 1024 * 1024 };

        Assert.False(ok.IsLow);
        Assert.True(lowByPercent.IsLow);   // 2% < 5%
        Assert.True(lowByAbsolute.IsLow);   // < 1 GB
    }

    [Fact]
    public void Topology_RiskyWhenMirrored()
    {
        var topo = new TopologyInfo { IsMirrored = true, MirroringRole = "PRINCIPAL", MirroringState = "SYNCHRONIZED" };
        Assert.True(topo.IsRisky);
        Assert.Contains("Espelhamento", topo.Summary);
    }

    [Fact]
    public void Topology_NotRiskyByDefault()
    {
        var topo = new TopologyInfo();
        Assert.False(topo.IsRisky);
        Assert.Contains("Sem topologia", topo.Summary);
    }

    [Theory]
    [InlineData(5, true)]   // Azure SQL DB
    [InlineData(3, false)]  // Enterprise/Developer
    [InlineData(4, false)]  // Express
    public void ServerInfo_EditionFlags(int engineEdition, bool azure)
    {
        var s = new ServerInfo { EngineEdition = engineEdition };
        Assert.Equal(azure, s.IsAzureSqlDatabase);
    }

    [Theory]
    [InlineData("D", "Completo (Full)")]
    [InlineData("I", "Diferencial")]
    [InlineData("L", "Log de transações")]
    public void BackupSet_TypeFriendly(string code, string friendly)
    {
        var b = new BackupSetInfo { BackupTypeCode = code, Position = 1 };
        Assert.Equal(friendly, b.BackupTypeFriendly);
        Assert.Contains("pos 1", b.Display);
    }
}
