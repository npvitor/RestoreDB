using RestoreDB.Domain.Models;
using Xunit;

namespace RestoreDB.Tests;

public class MaintenanceTests
{
    [Theory]
    [InlineData(45, "REBUILD")]
    [InlineData(31, "REBUILD")]
    [InlineData(30, "REORGANIZE")]
    [InlineData(10, "REORGANIZE")]
    [InlineData(9, "OK")]
    [InlineData(0, "OK")]
    public void Fragmentation_RecommendedAction(double pct, string expected)
        => Assert.Equal(expected, new IndexFragmentation { FragmentationPercent = pct }.RecommendedAction);

    [Theory]
    [InlineData(15, true)]
    [InlineData(9.9, false)]
    public void Fragmentation_NeedsMaintenance(double pct, bool expected)
        => Assert.Equal(expected, new IndexFragmentation { FragmentationPercent = pct }.NeedsMaintenance);

    [Fact]
    public void TableLoss_ComputesRowsLost()
    {
        var t = new TableLoss { Table = "dbo.X", RowsBefore = 100, RowsAfter = 70 };
        Assert.Equal(30, t.RowsLost);
        var none = new TableLoss { Table = "dbo.Y", RowsBefore = 50, RowsAfter = 50 };
        Assert.Equal(0, none.RowsLost);
    }

    [Fact]
    public void RepairSimulation_AggregatesLoss()
    {
        var sim = new RepairSimulationResult { DatabaseName = "DB", Executed = true };
        sim.TableLosses.Add(new TableLoss { Table = "dbo.A", RowsBefore = 10, RowsAfter = 8 });
        sim.TableLosses.Add(new TableLoss { Table = "dbo.B", RowsBefore = 5, RowsAfter = 5 });
        Assert.Equal(2, sim.TotalRowsLost);
        Assert.Single(sim.AffectedTables);
    }
}
