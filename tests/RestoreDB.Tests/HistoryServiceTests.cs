using RestoreDB.Domain.Models;
using RestoreDB.Services;
using Xunit;

namespace RestoreDB.Tests;

public class HistoryServiceTests
{
    private static DiagnosisSnapshot Snap(int checkdb = 0, int fk = 0, bool physical = false, bool logical = false, int suspect = 0) => new()
    {
        DatabaseName = "DB",
        TimestampUtc = DateTime.UtcNow,
        CheckDbErrors = checkdb,
        ForeignKeyViolations = fk,
        PhysicalCorruption = physical,
        LogicalCorruption = logical,
        SuspectPages = suspect
    };

    [Fact]
    public void Compare_NoPrevious_ReturnsEmpty()
        => Assert.Empty(HistoryService.Compare(null, Snap(checkdb: 5, physical: true)));

    [Fact]
    public void Compare_NewPhysicalCorruption_IsReported()
    {
        var news = HistoryService.Compare(Snap(), Snap(checkdb: 3, physical: true));
        Assert.Contains(news, n => n.Contains("FÍSICA"));
    }

    [Fact]
    public void Compare_NewFkViolations_IsReported()
    {
        var news = HistoryService.Compare(Snap(fk: 1), Snap(fk: 4));
        Assert.Contains(news, n => n.Contains("chave estrangeira"));
    }

    [Fact]
    public void Compare_NothingWorse_ReturnsEmpty()
        => Assert.Empty(HistoryService.Compare(Snap(checkdb: 5, fk: 2, physical: true), Snap(checkdb: 5, fk: 2, physical: true)));

    [Fact]
    public void SaveAndLoad_RoundTrips_AndDetectsNew()
    {
        var dir = Path.Combine(Path.GetTempPath(), "RestoreDB_HistTest_" + Guid.NewGuid().ToString("N"));
        try
        {
            var svc = new HistoryService(dir);
            Assert.Null(svc.LoadLatest("DB"));

            svc.Save("DB", Snap());                       // saudável
            var prev = svc.LoadLatest("DB");
            Assert.NotNull(prev);

            var current = Snap(checkdb: 2, physical: true);
            var news = HistoryService.Compare(prev, current);
            Assert.NotEmpty(news);

            svc.Save("DB", current);
            Assert.Equal(2, svc.Load("DB").Count);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
