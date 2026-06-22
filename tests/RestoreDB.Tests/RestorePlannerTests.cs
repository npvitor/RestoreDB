using RestoreDB.Domain.Models;
using RestoreDB.Services;
using Xunit;

namespace RestoreDB.Tests;

public class RestorePlannerTests
{
    private static BackupFileInfo Data(string logical, string physical) =>
        new() { LogicalName = logical, PhysicalName = physical, Type = "D" };
    private static BackupFileInfo Log(string logical, string physical) =>
        new() { LogicalName = logical, PhysicalName = physical, Type = "L" };

    private static readonly string DataDir = @"C:\SQLDATA";
    private static readonly string LogDir = @"C:\SQLLOG";

    [Fact]
    public void KeepsFiles_WhenOriginalDirExists_AndNotForced()
    {
        var files = new List<BackupFileInfo>
        {
            Data("DB", @"D:\dados\DB.mdf"),
            Log("DB_log", @"D:\dados\DB_log.ldf"),
        };
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"D:\dados" };

        var map = RestorePlanner.BuildMoveMap(files, existing, DataDir, LogDir, "DB", forceRelocate: false);

        Assert.Empty(map); // pastas existem → nada a mover
    }

    [Fact]
    public void RelocatesMissingDir_ToDefaultDirs()
    {
        var files = new List<BackupFileInfo>
        {
            Data("DB", @"X:\inexistente\DB.mdf"),
            Log("DB_log", @"X:\inexistente\DB_log.ldf"),
        };
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // nenhuma pasta existe

        var map = RestorePlanner.BuildMoveMap(files, existing, DataDir, LogDir, "DB", forceRelocate: false);

        Assert.Equal(2, map.Count);
        Assert.StartsWith(DataDir, map["DB"]);
        Assert.EndsWith(".mdf", map["DB"]);
        Assert.StartsWith(LogDir, map["DB_log"]);
        Assert.EndsWith(".ldf", map["DB_log"]);
    }

    [Fact]
    public void ForceRelocate_MovesEverything_EvenIfDirExists()
    {
        var files = new List<BackupFileInfo>
        {
            Data("DB", @"D:\dados\DB.mdf"),
            Data("DB2", @"D:\dados\DB2.ndf"),
            Log("DB_log", @"D:\dados\DB_log.ldf"),
        };
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"D:\dados" };

        var map = RestorePlanner.BuildMoveMap(files, existing, DataDir, LogDir, "Copia", forceRelocate: true);

        Assert.Equal(3, map.Count);
        Assert.EndsWith(".mdf", map["DB"]);
        Assert.EndsWith(".ndf", map["DB2"]);   // segundo arquivo de dados
        Assert.EndsWith(".ldf", map["DB_log"]);
        Assert.All(map.Values, v => Assert.Contains("Copia_", v)); // nome do destino inclui o alvo
    }

    [Fact]
    public void GeneratesUniqueNames_NoCollision()
    {
        var files = new List<BackupFileInfo>
        {
            Data("a", @"X:\x\a.mdf"),
            Data("b", @"X:\x\b.ndf"),
            Log("a_log", @"X:\x\a.ldf"),
        };
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var map = RestorePlanner.BuildMoveMap(files, existing, DataDir, LogDir, "DB", forceRelocate: true);

        Assert.Equal(3, map.Count);
        Assert.Equal(3, map.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
