using RestoreDB.Domain.Models;
using RestoreDB.Services;
using Xunit;

namespace RestoreDB.Tests;

public class MonitorServiceTests
{
    [Fact]
    public void ShouldAlert_WhenSomethingNew()
    {
        var d = new DiagnosisResult { DatabaseName = "DB" };
        d.NewSinceLastRun.Add("Corrupção FÍSICA nova (DBCC CHECKDB).");
        Assert.True(MonitorService.ShouldAlert(d));
    }

    [Fact]
    public void ShouldNotAlert_WhenNothingNew()
    {
        var d = new DiagnosisResult { DatabaseName = "DB" };
        Assert.False(MonitorService.ShouldAlert(d));
    }
}
