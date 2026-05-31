using RestoreDB.DataAccess;
using RestoreDB.Domain.Models;
using RestoreDB.Services;
using Xunit;

namespace RestoreDB.IntegrationTests;

/// <summary>
/// Testes de integração contra uma instância SQL real (configurada via RESTOREDB_IT_*).
/// Cada teste cria e derruba seu próprio banco RestoreDB_IT_* (isolado, ordem-independente)
/// e jamais toca em bancos existentes. Conecta-se sempre em master.
/// </summary>
public sealed class RepairIntegrationTests
{
    private static RepairService Repair() => new(new LogService());
    private static RestoreService Restore() => new(new LogService());

    [IntegrationFact]
    public async Task Conexao_E_ServerInfo()
    {
        var conn = await new ConnectionService().ConnectAsync(It.Info);
        Assert.False(string.IsNullOrEmpty(conn));

        var info = await new ServerService().GetServerInfoAsync(It.Conn);
        Assert.True(info.IsSysadmin, "o login de teste deveria ser sysadmin");
        Assert.True(info.MajorVersion >= 10, $"versão major inesperada: {info.MajorVersion}");
        Assert.False(info.IsAzureSqlDatabase);
        Assert.Null(ServerService.PreflightWarning(info)); // sysadmin + não-Azure => sem aviso
    }

    [IntegrationFact]
    public async Task Listagem_EncontraBancoSaudavel()
    {
        await using var db = await TestDatabase.CreateAsync("list");
        var list = await new DatabaseService().ListDatabasesAsync(It.Conn);
        var found = list.FirstOrDefault(d => d.Name == db.Name);
        Assert.NotNull(found);
        Assert.True(found!.IsHealthy);
    }

    [IntegrationFact]
    public async Task Offline_ReligaSemPerda()
    {
        await using var db = await TestDatabase.CreateAsync("offline");
        await It.ExecAsync($"ALTER DATABASE {It.Q(db.Name)} SET OFFLINE WITH ROLLBACK IMMEDIATE;");
        Assert.Equal("OFFLINE", await It.GetStateAsync(db.Name));

        var result = await Repair().RepairAsync(db.AsInfo("OFFLINE"), It.Conn, allowDataLoss: false, It.NoProgress);

        Assert.Equal(RepairStatus.Success, result.Status);
        Assert.Equal("ONLINE", await It.GetStateAsync(db.Name));
        Assert.Equal(3, await It.RowCountAsync(db.Name));
    }

    [IntegrationFact]
    public async Task Emergency_ReparoSeguro_VoltaOnlineSemPerda()
    {
        await using var db = await TestDatabase.CreateAsync("emergency");
        await It.ExecAsync($"ALTER DATABASE {It.Q(db.Name)} SET EMERGENCY;");
        Assert.Equal("EMERGENCY", await It.GetStateAsync(db.Name));

        var result = await Repair().RepairAsync(db.AsInfo("EMERGENCY"), It.Conn, allowDataLoss: false, It.NoProgress);

        Assert.Equal(RepairStatus.Success, result.Status);
        Assert.Equal("ONLINE", await It.GetStateAsync(db.Name));
        Assert.Equal(3, await It.RowCountAsync(db.Name)); // sem perda de dados
    }

    [IntegrationFact]
    public async Task Restoring_FinalizaRecuperacao()
    {
        await using var db = await TestDatabase.CreateAsync("restoring");
        var path = await db.BackupAsync();
        await It.ExecAsync($"RESTORE DATABASE {It.Q(db.Name)} FROM DISK = @f WITH NORECOVERY, REPLACE;", ("@f", path));
        Assert.Equal("RESTORING", await It.GetStateAsync(db.Name));

        var result = await Repair().RepairAsync(db.AsInfo("RESTORING"), It.Conn, allowDataLoss: false, It.NoProgress);

        Assert.Equal(RepairStatus.Success, result.Status);
        Assert.Equal("ONLINE", await It.GetStateAsync(db.Name));
    }

    [IntegrationFact]
    public async Task Diagnostico_SomenteLeitura_BancoSaudavel()
    {
        await using var db = await TestDatabase.CreateAsync("diag");

        var diag = await Repair().DiagnoseAsync(db.AsInfo("ONLINE"), It.Conn, It.NoProgress);

        Assert.NotNull(diag.Server);
        Assert.True(diag.Server!.IsSysadmin);
        Assert.NotNull(diag.CheckDb);
        Assert.True(diag.CheckDb!.Executed);
        Assert.False(diag.CheckDb.HasErrors);
        Assert.Empty(diag.SuspectPages);
        Assert.NotNull(diag.Topology);
        Assert.False(diag.Topology!.IsRisky);
        Assert.Equal("ONLINE", diag.StateDesc);
    }

    [IntegrationFact]
    public async Task RestaurarDeBackup_RevertePerdaDeDados()
    {
        await using var db = await TestDatabase.CreateAsync("restore");
        await db.BackupAsync();

        // Apaga os dados APÓS o backup, para provar que a restauração os recupera.
        await It.ExecAsync($"EXEC(N'USE {It.Q(db.Name)}; DELETE FROM dbo.NotaFiscal;');");
        Assert.Equal(0, await It.RowCountAsync(db.Name));

        var history = await Restore().ListBackupHistoryAsync(db.Name, It.Conn);
        Assert.NotEmpty(history);
        var set = history.First();
        Assert.Equal("D", set.BackupTypeCode);

        var request = new RestoreRequest
        {
            DatabaseName = db.Name,
            Replace = true,
            Pieces = { new RestorePiece { DiskPath = set.PhysicalDevice, Position = set.Position } }
        };
        var result = await Restore().RestoreDatabaseAsync(request, It.Conn, It.NoProgress);

        Assert.Equal(RepairStatus.Success, result.Status);
        Assert.Equal("ONLINE", await It.GetStateAsync(db.Name));
        Assert.Equal(3, await It.RowCountAsync(db.Name)); // dados restaurados
    }

    [IntegrationFact]
    public async Task Backup_HeaderOnly_E_FileListOnly()
    {
        await using var db = await TestDatabase.CreateAsync("hdr");
        var path = await db.BackupAsync();

        var sets = await Restore().ListBackupSetsInFileAsync(path, It.Conn);
        Assert.NotEmpty(sets);
        Assert.Contains(sets, s => s.DatabaseName == db.Name && s.BackupTypeCode == "D");

        var files = await Restore().GetBackupFilesAsync(path, sets.First().Position, It.Conn);
        Assert.Contains(files, f => f.IsData);
        Assert.Contains(files, f => f.IsLog);
    }

    [IntegrationFact]
    public async Task ReparoDeIndices_SemErros_RetornaSucesso()
    {
        await using var db = await TestDatabase.CreateAsync("idx");

        var result = await Repair().RebuildIndexesAsync(db.AsInfo("ONLINE"), It.Conn, It.NoProgress);

        Assert.Equal(RepairStatus.Success, result.Status);
        Assert.Equal("ONLINE", await It.GetStateAsync(db.Name));
    }

    [IntegrationFact]
    public async Task Sessao_ConsultasDeLeitura_Funcionam()
    {
        await using var db = await TestDatabase.CreateAsync("session");
        var data = new SqlDataAccess();
        await using var s = await data.OpenSessionAsync(It.Conn);

        Assert.Equal("ONLINE", await s.GetDatabaseStateAsync(db.Name));

        var files = await s.GetDatabaseFilesAsync(db.Name);
        Assert.Contains(files, f => f.IsData);
        Assert.Contains(files, f => f.IsLog);

        var report = await s.CheckDbWithResultsAsync(db.Name, repairOption: null);
        Assert.True(report.Executed);
        Assert.False(report.HasErrors);

        Assert.Empty(await s.GetSuspectPagesAsync(db.Name));

        var topo = await s.GetTopologyInfoAsync(db.Name);
        Assert.False(topo.IsRisky);

        var pv = await s.GetPageVerifyOptionAsync(db.Name);
        Assert.False(string.IsNullOrEmpty(pv));

        var disks = await s.GetDiskSpaceAsync(db.Name);
        Assert.NotEmpty(disks);
    }
}
