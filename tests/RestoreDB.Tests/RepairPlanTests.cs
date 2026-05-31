using RestoreDB.Domain.Models;
using RestoreDB.Services;
using Xunit;

namespace RestoreDB.Tests;

public class RepairPlanTests
{
    private static DatabaseInfo Db(string name, string state)
    {
        var db = new DatabaseInfo { Name = name };
        db.StateDesc = state;
        return db;
    }

    [Fact]
    public void Offline_PlansSetOnline()
    {
        var script = RepairService.BuildPlannedScript(Db("Faturamento", "OFFLINE"), allowDataLoss: false);
        Assert.Contains("SET ONLINE", script);
        Assert.Contains("SET MULTI_USER", script);
        Assert.DoesNotContain("REPAIR_ALLOW_DATA_LOSS", script);
    }

    [Fact]
    public void Restoring_PlansRestoreWithRecovery()
    {
        var script = RepairService.BuildPlannedScript(Db("Faturamento", "RESTORING"), allowDataLoss: false);
        Assert.Contains("RESTORE DATABASE", script);
        Assert.Contains("WITH RECOVERY", script);
    }

    [Fact]
    public void Suspect_Safe_DoesNotIncludeDataLoss()
    {
        var script = RepairService.BuildPlannedScript(Db("Faturamento", "SUSPECT"), allowDataLoss: false);
        Assert.Contains("REPAIR_REBUILD", script);
        Assert.DoesNotContain("REPAIR_ALLOW_DATA_LOSS", script);
    }

    [Fact]
    public void Suspect_WithDataLoss_IncludesDataLossRepair()
    {
        var script = RepairService.BuildPlannedScript(Db("Faturamento", "SUSPECT"), allowDataLoss: true);
        Assert.Contains("REPAIR_ALLOW_DATA_LOSS", script);
    }

    [Fact]
    public void DatabaseNameWithBracket_IsEscaped()
    {
        var script = RepairService.BuildPlannedScript(Db("a]b", "OFFLINE"), allowDataLoss: false);
        Assert.Contains("[a]]b]", script);
    }

    [Theory]
    [InlineData("1:24;drop table x", "1:24")]
    [InlineData("1:24,1:56", "1:24,1:56")]
    [InlineData("abc", "")]
    public void SanitizePages_KeepsOnlyDigitsColonComma(string input, string expected)
    {
        Assert.Equal(expected, RestoreService.SanitizePages(input));
    }
}
