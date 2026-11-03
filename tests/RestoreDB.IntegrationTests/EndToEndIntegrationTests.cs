using System.Diagnostics;
using Microsoft.Data.SqlClient;
using RestoreDB.DataAccess;
using RestoreDB.Domain.Models;
using RestoreDB.Services;
using Xunit;

namespace RestoreDB.IntegrationTests;

/// <summary>
/// Matriz de ponta a ponta: cada cenário do fluxo guiado contra corrupção/estado REAL, conferindo no fim o
/// estado do banco (sys.databases, DBCC CHECKDB, linhas) e não só o que o serviço diz.
/// </summary>
public sealed class EndToEndIntegrationTests
{
    private static RepairService Repair() { var log = new LogService(); return new(log, new RestoreService(log)); }
    private static RestoreService Restore() => new(new LogService());

    private static async Task<string> UserAccessAsync(string db)
        => (string)(await It.ScalarAsync("SELECT user_access_desc FROM sys.databases WHERE name = @n;", ("@n", db)))!;

    private static async Task<int> CountAsync(string sql, params (string, object?)[] ps)
        => Convert.ToInt32(await It.ScalarAsync(sql, ps));

    // ------------------------------------------------------------------ Estados

    [IntegrationFact]
    public async Task RecoveryPending_ReparoSeguroNaoInventaSucesso_EAoLocalizarOArquivoVoltaSemPerda()
    {
        await using var db = await TestDatabase.CreateAsync("recpending");
        string logical, realPath;
        await using (var c = new SqlConnection(It.Conn))
        {
            await c.OpenAsync();
            await using var cmd = new SqlCommand("SELECT name, physical_name FROM sys.master_files WHERE database_id = DB_ID(@n) AND type = 1;", c);
            cmd.Parameters.AddWithValue("@n", db.Name);
            await using var r = await cmd.ExecuteReaderAsync();
            await r.ReadAsync();
            (logical, realPath) = (r.GetString(0), r.GetString(1));
        }
        await It.ExecAsync($"ALTER DATABASE {It.Q(db.Name)} SET OFFLINE WITH ROLLBACK IMMEDIATE;");
        var bogus = Path.Combine(Path.GetDirectoryName(realPath)!, "sumiu_" + Guid.NewGuid().ToString("N")[..6] + ".ldf");
        await It.ExecAsync($"ALTER DATABASE {It.Q(db.Name)} MODIFY FILE (NAME = {It.Q(logical)}, FILENAME = N'{bogus.Replace("'", "''")}');");
        try { await It.ExecAsync($"ALTER DATABASE {It.Q(db.Name)} SET ONLINE;"); } catch (SqlException) { /* esperado */ }
        Assert.Equal("RECOVERY_PENDING", await It.GetStateAsync(db.Name));

        var diag = await Repair().DiagnoseAsync(db.AsInfo("RECOVERY_PENDING"), It.Conn, It.NoProgress);
        Assert.Equal(RecommendedActionKind.FixFileAccess, diag.RecommendedAction);

        // O reparo SEGURO não tem como recriar um log que não está lá: não pode dizer que resolveu.
        var safe = await Repair().RepairAsync(db.AsInfo("RECOVERY_PENDING"), It.Conn, allowDataLoss: false, It.NoProgress);
        Assert.NotEqual(RepairStatus.Success, safe.Status);
        Assert.NotEqual(RepairStatus.NoActionNeeded, safe.Status);
        Assert.NotEqual("ONLINE", await It.GetStateAsync(db.Name));

        // Nada foi destruído: apontar o arquivo verdadeiro traz o banco de volta inteiro.
        var state = await It.GetStateAsync(db.Name);
        var fix = await new AvailabilityService(new LogService()).FixFileAccessAsync(db.AsInfo(state), It.Conn, It.NoProgress, default,
            new Dictionary<string, string> { [logical] = realPath });
        Assert.Equal(RepairStatus.Success, fix.Status);
        Assert.Equal("ONLINE", await It.GetStateAsync(db.Name));
        Assert.Equal("MULTI_USER", await UserAccessAsync(db.Name));
        Assert.Equal(3, await It.RowCountAsync(db.Name));
        Assert.True(await Corruption.CheckDbCleanAsync(db.Name));
    }

    // ------------------------------------------------------------------ Índices e reparo seguro

    [IntegrationFact]
    public async Task IndiceNaoClusterizadoCorrompido_DiagnosticoApontaIndices_EReconstrucaoNaoPerdeNada()
    {
        await using var db = await TestDatabase.CreateAsync("ncidx", withData: false);
        try
        {
            await Corruption.CreateItemTableAsync(db.Name, "SIMPLE");
            await Corruption.WriteGarbageOnDiskAsync(db.Name, 1, await Corruption.IndexPageAsync(db.Name, indexId: 2, level: 0));

            var diag = await Repair().DiagnoseAsync(db.AsInfo("ONLINE"), It.Conn, It.NoProgress);
            Assert.True(diag.CheckDb!.HasErrors);
            Assert.Equal(RecommendedActionKind.RebuildIndexesOnly, diag.RecommendedAction);

            var r = await Repair().RebuildIndexesAsync(db.AsInfo("ONLINE"), It.Conn, It.NoProgress);

            Assert.Equal(RepairStatus.Success, r.Status);
            Assert.True(await Corruption.CheckDbCleanAsync(db.Name));
            Assert.Equal(Corruption.Rows, await Corruption.CountItemsAsync(db.Name));
            Assert.Equal(Corruption.Rows, await Corruption.CountItemsAsync(db.Name, "IX_Item_Cliente"));
            Assert.Equal("MULTI_USER", await UserAccessAsync(db.Name));
        }
        finally { await Corruption.CleanupAsync(db.Name); }
    }

    [IntegrationFact]
    public async Task ChavesForaDeOrdem_DiagnosticoRecomendaReparoSeguro_EORepairRebuildConsertaSemPerda()
    {
        await using var db = await TestDatabase.CreateAsync("rebuild", withData: false);
        try
        {
            await Corruption.CreateItemTableAsync(db.Name, "SIMPLE");
            // 2ª linha da raiz do índice clusterizado (offset 96 + 11 bytes da 1ª): chave trocada por um valor enorme.
            var root = await Corruption.IndexPageAsync(db.Name, indexId: 1, level: 1);
            await Corruption.WriteBytesAsync(db.Name, 1, root, 108, "0xFFFFFF7F");

            var diag = await Repair().DiagnoseAsync(db.AsInfo("ONLINE"), It.Conn, It.NoProgress);
            Assert.Equal("REPAIR_REBUILD", diag.CheckDb!.MinRepairLevel);
            Assert.Equal(RecommendedActionKind.SafeRepair, diag.RecommendedAction);   // e NÃO "restaurar de backup"

            var r = await Repair().RepairAsync(db.AsInfo("ONLINE"), It.Conn, allowDataLoss: false, It.NoProgress);

            Assert.Equal(RepairStatus.Success, r.Status);                             // não pede confirmação de perda à toa
            Assert.True(await Corruption.CheckDbCleanAsync(db.Name));
            Assert.Equal(Corruption.Rows, await Corruption.CountItemsAsync(db.Name));
            Assert.Equal("MULTI_USER", await UserAccessAsync(db.Name));
        }
        finally { await Corruption.CleanupAsync(db.Name); }
    }

    [IntegrationFact]
    public async Task PaginaDeDadosIlegivel_ReparoSeguroPedeConfirmacao_SemDescartarNada()
    {
        await using var db = await TestDatabase.CreateAsync("needsloss", withData: false);
        try
        {
            await Corruption.CreateItemTableAsync(db.Name, "SIMPLE");
            var (file, page, _) = await Corruption.DataPageOfAsync(db.Name, 1000);
            await Corruption.WriteGarbageOnDiskAsync(db.Name, file, page);

            var r = await Repair().RepairAsync(db.AsInfo("ONLINE"), It.Conn, allowDataLoss: false, It.NoProgress);

            Assert.Equal(RepairStatus.NeedsDataLossConfirmation, r.Status);
            Assert.Equal("ONLINE", await It.GetStateAsync(db.Name));
            Assert.Equal("MULTI_USER", await UserAccessAsync(db.Name));
            Assert.False(await Corruption.CheckDbCleanAsync(db.Name));                // a página continua lá, intocada
            Assert.Equal(Corruption.Rows, await Corruption.CountItemsAsync(db.Name, "IX_Item_Cliente"));
        }
        finally { await Corruption.CleanupAsync(db.Name); }
    }

    // ------------------------------------------------------------------ Reparo com perda / restauração de página

    [IntegrationFact]
    public async Task ReparoComPerda_ComBackupELog_DevolveDoBackupTudoOQueOReparoDescartou()
    {
        await using var db = await TestDatabase.CreateAsync("losswithbak", withData: false);
        try
        {
            await Corruption.CreateItemTableAsync(db.Name, "FULL");
            await db.BackupAsync();
            await Corruption.InDbAsync(db.Name, "INSERT dbo.Item(Id, Cliente) SELECT Id + 2000, Cliente FROM dbo.Item WHERE Id <= 100;");
            await It.ExecAsync($"BACKUP LOG {It.Q(db.Name)} TO DISK = @f WITH INIT;", ("@f", db.Name + "_e2e.trn"));
            // Só no tail-log: nem o FULL nem o log agendado têm estas 50.
            await Corruption.InDbAsync(db.Name, "INSERT dbo.Item(Id, Cliente) SELECT Id + 2100, Cliente FROM dbo.Item WHERE Id <= 50;");
            const int total = Corruption.Rows + 150;
            var (file, page, onPage) = await Corruption.DataPageOfAsync(db.Name, 1000);
            Assert.InRange(onPage, 1, 40);
            await Corruption.WriteGarbageOnDiskAsync(db.Name, file, page);

            var r = await Repair().RepairAsync(db.AsInfo("ONLINE"), It.Conn, allowDataLoss: true, It.NoProgress,
                recovery: new ParentRecoveryOptions { RecoverLostRows = true });

            Assert.Equal(RepairStatus.Success, r.Status);
            Assert.Contains("Todas as linhas que o reparo descartou foram recuperadas", r.Message);
            Assert.True(await Corruption.CheckDbCleanAsync(db.Name));
            Assert.Equal(total, await Corruption.CountItemsAsync(db.Name));
            Assert.Equal("MULTI_USER", await UserAccessAsync(db.Name));
            Assert.Equal(0, await Corruption.LeftoverCopiesAsync(db.Name));          // _LostRows_ / _Salvamento_ descartados
        }
        finally
        {
            await Corruption.CleanupAsync(db.Name);
            await It.TryDeleteBackupFileAsync(db.Name + "_e2e.trn");
        }
    }

    [IntegrationFact]
    public async Task RestauracaoDePagina_PaginaCorrompidaDeVerdade_VoltaSemPerda_ESemFicarPendente()
    {
        await using var db = await TestDatabase.CreateAsync("pagecorrupt", withData: false);
        try
        {
            await Corruption.CreateItemTableAsync(db.Name, "FULL");
            await db.BackupAsync();
            await Corruption.InDbAsync(db.Name, "UPDATE dbo.Item SET Pad = 'depois do backup' WHERE Id = 1000;");
            var (file, page, _) = await Corruption.DataPageOfAsync(db.Name, 1000);
            await Corruption.WriteGarbageOnDiskAsync(db.Name, file, page);
            Assert.False(await Corruption.CheckDbCleanAsync(db.Name));

            var diag = await Repair().DiagnoseAsync(db.AsInfo("ONLINE"), It.Conn, It.NoProgress);
            Assert.Equal(RecommendedActionKind.PageRestore, diag.RecommendedAction);

            var r = await Restore().RestorePagesFromHistoryAsync(db.Name, $"{file}:{page}", It.Conn, It.NoProgress);

            Assert.Equal(RepairStatus.Success, r.Status);
            Assert.True(await Corruption.CheckDbCleanAsync(db.Name));                 // nada em RestorePending (Msg 829)
            Assert.Equal(Corruption.Rows, await Corruption.CountItemsAsync(db.Name));
            Assert.Equal("depois do backup", ((string)(await Corruption.InDbScalarAsync(db.Name, "SELECT Pad FROM dbo.Item WHERE Id = 1000;"))!).TrimEnd());
        }
        finally { await Corruption.CleanupAsync(db.Name); }
    }

    // ------------------------------------------------------------------ Restauração / anexar / log

    [IntegrationFact]
    public async Task RestaurarDeArquivoEscolhido_SemHistoricoNoMsdb_LeOArquivoERestaura()
    {
        await using var db = await TestDatabase.CreateAsync("bakfile");
        var bak = await db.BackupAsync();
        await It.ExecAsync($"DELETE FROM {It.Q(db.Name)}.dbo.NotaFiscal;");
        await It.TryPurgeBackupHistoryAsync(db.Name);                                // backup trazido de outro servidor

        var sets = await Restore().ListBackupSetsInFileAsync(bak, It.Conn);
        var full = Assert.Single(sets, s => s.BackupTypeCode == "D");
        var r = await Restore().RestoreDatabaseAsync(new RestoreRequest
        {
            DatabaseName = db.Name, Replace = true, Pieces = { new RestorePiece { DiskPath = bak, Position = full.Position } }
        }, It.Conn, It.NoProgress);

        Assert.Equal(RepairStatus.Success, r.Status);
        Assert.Equal("ONLINE", await It.GetStateAsync(db.Name));
        Assert.Equal(3, await It.RowCountAsync(db.Name));
    }

    [IntegrationFact]
    public async Task ReconstrucaoDoLog_SemAdministrador_AbortaSemTocarNoBanco()
    {
        await using var db = await TestDatabase.CreateAsync("rebuildlog");

        var r = await Repair().RebuildTransactionLogAsync(db.AsInfo("ONLINE"), It.Conn, It.NoProgress);

        if (new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            Assert.Equal(RepairStatus.Success, r.Status);                            // elevado: reconstrói de verdade
        else
        {
            Assert.Equal(RepairStatus.Failed, r.Status);
            Assert.Contains("ADMINISTRADOR", r.Message);
            Assert.Contains("NÃO foi alterado", r.Message);
        }
        Assert.Equal("ONLINE", await It.GetStateAsync(db.Name));
        Assert.Equal("MULTI_USER", await UserAccessAsync(db.Name));
        Assert.Equal(3, await It.RowCountAsync(db.Name));
    }

    [IntegrationFact]
    public async Task Anexar_ArquivoDeLogPerdido_RecriaOLogEVoltaComOsDados()
    {
        // Pasta própria, gravável pela conta de serviço do SQL (no 2025 ela não grava em C:\) e por este processo.
        var dir = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "RestoreDB_IT_attach_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        using (var icacls = Process.Start(new ProcessStartInfo("icacls", $"\"{dir}\" /grant *S-1-1-0:(OI)(CI)F") { CreateNoWindow = true, UseShellExecute = false })!)
            await icacls.WaitForExitAsync();
        var name = "RestoreDB_IT_attachlog";
        var (mdf, ldf) = (Path.Combine(dir, name + ".mdf"), Path.Combine(dir, name + "_log.ldf"));
        await It.ForceDropAsync(name);
        try
        {
            await It.ExecAsync($"CREATE DATABASE {It.Q(name)} ON (NAME = N'{name}', FILENAME = N'{mdf}') LOG ON (NAME = N'{name}_log', FILENAME = N'{ldf}');");
            await It.ExecAsync($"EXEC(N'USE {It.Q(name)}; CREATE TABLE dbo.T(Id int PRIMARY KEY); INSERT dbo.T VALUES (1),(2),(3);');");
            await It.ExecAsync("EXEC master.dbo.sp_detach_db @dbname = @n;", ("@n", name));
            File.Delete(ldf);                                                        // o .ldf se perdeu

            var r = await Restore().AttachDatabaseAsync(name, mdf, ldfPath: null, rebuildLog: true, It.Conn, It.NoProgress);

            Assert.Equal(RepairStatus.Success, r.Status);
            Assert.Equal("ONLINE", await It.GetStateAsync(name));
            Assert.Equal(3, await CountAsync($"SELECT COUNT(*) FROM {It.Q(name)}.dbo.T;"));
            Assert.True(await Corruption.CheckDbCleanAsync(name));
        }
        finally
        {
            await It.ForceDropAsync(name);
            try { Directory.Delete(dir, true); } catch { /* best-effort */ }
        }
    }

    // ------------------------------------------------------------------ Integridade lógica

    [IntegrationFact]
    public async Task ViolacaoDeCheck_DiagnosticaSemOferecerRemocao_ENaoApagaNada()
    {
        await using var db = await TestDatabase.CreateAsync("checkviol");
        // Regra criada sem verificar os dados existentes: A (10) e B (20) a violam.
        await Corruption.InDbAsync(db.Name, "ALTER TABLE dbo.NotaFiscal WITH NOCHECK ADD CONSTRAINT CK_NF_Valor CHECK (Valor > 25);");

        var diag = await Repair().DiagnoseAsync(db.AsInfo("ONLINE"), It.Conn, It.NoProgress);
        Assert.True(diag.LogicalCorruptionSuspected);
        Assert.Equal(RecommendedActionKind.FixConstraints, diag.RecommendedAction);
        Assert.Contains("não há correção automática", diag.Recommendation);

        var r = await Repair().FixConstraintViolationsAsync(db.AsInfo("ONLINE"), It.Conn, It.NoProgress);

        Assert.Equal(RepairStatus.Failed, r.Status);                                 // CHECK exige revisão humana
        Assert.Equal(3, await It.RowCountAsync(db.Name));
        Assert.Equal("MULTI_USER", await UserAccessAsync(db.Name));
    }

    // ------------------------------------------------------------------ Forense: TRUNCATE / DROP

    [IntegrationFact]
    public async Task Forense_Truncate_ListaEPontoNoTempoDevolveAsLinhas()
    {
        await using var db = await TestDatabase.CreateAsync("truncate");
        await It.ExecAsync($"ALTER DATABASE {It.Q(db.Name)} SET RECOVERY FULL;");
        await db.BackupAsync();
        await Corruption.InDbAsync(db.Name, "INSERT dbo.NotaFiscal(Cliente,Valor) VALUES (N'D',40);");
        await Task.Delay(2500);
        await Corruption.InDbAsync(db.Name, "TRUNCATE TABLE dbo.NotaFiscal;");

        var tx = (await Restore().ListRecentDestructiveTransactionsAsync(db.Name, It.Conn)).First(t => t.Kind == "TRUNCATE");
        Assert.NotNull(tx.BeginTime);
        Assert.False(tx.CanRecoverRowsInPlace);                                      // a tela oferece o banco inteiro até antes

        var r = await Restore().RestoreToPointInTimeAsync(db.Name, tx.BeginTime!.Value.AddSeconds(-1), tailLogBackup: true, It.Conn, It.NoProgress);

        Assert.Equal(RepairStatus.Success, r.Status);
        Assert.Equal(4, await It.RowCountAsync(db.Name));                            // A, B, C + D (posterior ao backup, do tail-log)
        Assert.Equal("MULTI_USER", await UserAccessAsync(db.Name));
    }

    [IntegrationFact]
    public async Task Forense_DropTable_ListaEPontoNoTempoTrazATabelaDeVolta()
    {
        await using var db = await TestDatabase.CreateAsync("droptable");
        await It.ExecAsync($"ALTER DATABASE {It.Q(db.Name)} SET RECOVERY FULL;");
        await db.BackupAsync();
        await Corruption.InDbAsync(db.Name, "INSERT dbo.NotaFiscal(Cliente,Valor) VALUES (N'D',40);");
        await Task.Delay(2500);
        await Corruption.InDbAsync(db.Name, "DROP TABLE dbo.NotaFiscal;");

        var tx = (await Restore().ListRecentDestructiveTransactionsAsync(db.Name, It.Conn)).First(t => t.Kind == "DROP");
        Assert.NotNull(tx.BeginTime);

        var r = await Restore().RestoreToPointInTimeAsync(db.Name, tx.BeginTime!.Value.AddSeconds(-1), tailLogBackup: true, It.Conn, It.NoProgress);

        Assert.Equal(RepairStatus.Success, r.Status);
        Assert.Equal(4, await It.RowCountAsync(db.Name));
    }

    // ------------------------------------------------------------------ Ajustes que faltavam

    [IntegrationFact]
    public async Task Ajustes_SomenteLeituraComObjetoQuebrado_LiberaGravacaoAntesDeRevalidar_EInformativosFicam()
    {
        await using var db = await TestDatabase.CreateAsync("ajustes2");
        // vw_Quebrada cita uma tabela que não existe mais (depende do fornecedor — continua quebrada); o índice
        // desabilitado só se reconstrói com gravação liberada: prova que o READ_WRITE vem antes dos demais ajustes.
        await Corruption.InDbAsync(db.Name, "CREATE TABLE dbo.Temp(Id int);");
        await Corruption.InDbAsync(db.Name, "EXEC(N'CREATE VIEW dbo.vw_Quebrada AS SELECT Id FROM dbo.Temp;'); DROP TABLE dbo.Temp;");
        await Corruption.InDbAsync(db.Name, "ALTER INDEX IX_NotaFiscal_Cliente ON dbo.NotaFiscal DISABLE;");
        var major = (await new ServerService().GetServerInfoAsync(It.Conn)).MajorVersion;
        var lowestCompat = major <= 10 ? 80 : 100;
        await It.ExecAsync($"ALTER DATABASE {It.Q(db.Name)} SET COMPATIBILITY_LEVEL = {lowestCompat};");
        await It.ExecAsync($"ALTER DATABASE {It.Q(db.Name)} SET TRUSTWORTHY ON;");
        await It.ExecAsync($"ALTER DATABASE {It.Q(db.Name)} SET READ_ONLY WITH ROLLBACK IMMEDIATE;");

        var diag = await Repair().DiagnoseAsync(db.AsInfo("ONLINE"), It.Conn, It.NoProgress);
        Assert.Contains(diag.Adjustments, a => a.Kind == HealthIssueKind.ReadOnly && a.CanFix);
        Assert.Contains(diag.Adjustments, a => a.Kind == HealthIssueKind.DisabledIndexes && a.CanFix);
        var broken = Assert.Single(diag.Adjustments, a => a.Kind == HealthIssueKind.BrokenModules);
        Assert.Contains("vw_Quebrada", Assert.Single(broken.Items));
        Assert.Contains(diag.Adjustments, a => a.Kind == HealthIssueKind.Trustworthy && !a.CanFix);
        // Só avisa quando a compatibilidade está mais de 2 versões atrás (no 2008 R2, o mínimo — 80 — não está).
        Assert.Equal(lowestCompat < major * 10 - 20, diag.Adjustments.Any(a => a.Kind == HealthIssueKind.OldCompatibility && !a.CanFix));

        var r = await new AdjustmentService(new LogService()).ApplyAsync(db.AsInfo("ONLINE"),
            diag.Adjustments.Where(a => a.CanFix).Select(a => a.Kind).ToList(), It.Conn, It.NoProgress);

        Assert.Equal(RepairStatus.Success, r.Status);
        Assert.Contains("Pendências", r.Message);                                    // a view quebrada é relatada, não escondida
        Assert.Equal(false, await It.ScalarAsync("SELECT CAST(is_read_only AS bit) FROM sys.databases WHERE name = @n;", ("@n", db.Name)));
        Assert.Equal(0, await CountAsync($"SELECT COUNT(*) FROM {It.Q(db.Name)}.sys.indexes WHERE is_disabled = 1;"));
        await using (var s = await new SqlDataAccess().OpenSessionAsync(It.Conn))
            Assert.Single(await s.GetModulesWithBrokenReferencesAsync(db.Name));
        // Os informativos não são tocados.
        Assert.Equal(true, await It.ScalarAsync("SELECT CAST(is_trustworthy_on AS bit) FROM sys.databases WHERE name = @n;", ("@n", db.Name)));
        Assert.Equal(lowestCompat, Convert.ToInt32(await It.ScalarAsync("SELECT compatibility_level FROM sys.databases WHERE name = @n;", ("@n", db.Name))));
        Assert.Equal(3, await It.RowCountAsync(db.Name));
    }

    // ------------------------------------------------------------------ Monitoramento

    [IntegrationFact]
    public async Task Monitoramento_AlertaSoQuandoHaNovidade_EGravaOArquivoDeAlerta()
    {
        await using var db = await TestDatabase.CreateAsync("monitor", withData: false);
        var history = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RestoreDB", "History",
            RestoreDB.Domain.Constants.AppConstants.SanitizeToken(db.Name) + ".json");
        var alerts = Path.Combine(Path.GetTempPath(), "RestoreDB_IT_Alerts_" + Guid.NewGuid().ToString("N"));
        File.Delete(history);                                                        // o histórico é por NOME (comum às instâncias)
        try
        {
            await Corruption.InDbAsync(db.Name,
                "CREATE TABLE dbo.Cliente(Id int PRIMARY KEY); " +
                "CREATE TABLE dbo.Pedido(Id int IDENTITY PRIMARY KEY, ClienteId int NOT NULL CONSTRAINT FK_Pedido_Cliente FOREIGN KEY REFERENCES dbo.Cliente(Id)); " +
                "INSERT dbo.Cliente VALUES (1); INSERT dbo.Pedido(ClienteId) VALUES (1);");
            var monitor = new MonitorService(new LogService(), alerts);

            var first = await monitor.RunCheckAsync(db.AsInfo("ONLINE"), It.Conn, It.NoProgress);
            Assert.False(first.HasNewIssue);
            Assert.False(first.CorruptionPresent);
            Assert.Null(first.AlertFilePath);

            // Registro órfão aparece entre uma verificação e outra.
            await Corruption.InDbAsync(db.Name,
                "ALTER TABLE dbo.Pedido NOCHECK CONSTRAINT FK_Pedido_Cliente; INSERT dbo.Pedido(ClienteId) VALUES (999); " +
                "ALTER TABLE dbo.Pedido WITH NOCHECK CHECK CONSTRAINT FK_Pedido_Cliente;");
            var second = await monitor.RunCheckAsync(db.AsInfo("ONLINE"), It.Conn, It.NoProgress);
            Assert.True(second.HasNewIssue);
            Assert.True(second.CorruptionPresent);
            Assert.True(File.Exists(second.AlertFilePath));
            Assert.Contains(db.Name, await File.ReadAllTextAsync(second.AlertFilePath!));

            // Sem mudança desde a última vez: não repete o alerta.
            var third = await monitor.RunCheckAsync(db.AsInfo("ONLINE"), It.Conn, It.NoProgress);
            Assert.False(third.HasNewIssue);

            // O banco saiu do ar sem corrupção nova: também é novidade.
            await It.ExecAsync($"ALTER DATABASE {It.Q(db.Name)} SET OFFLINE WITH ROLLBACK IMMEDIATE;");
            var fourth = await monitor.RunCheckAsync(db.AsInfo("OFFLINE"), It.Conn, It.NoProgress);
            Assert.True(fourth.HasNewIssue);
            Assert.Contains(fourth.Messages, m => m.Contains("OFFLINE"));
        }
        finally
        {
            try { File.Delete(history); } catch { /* best-effort */ }
            try { Directory.Delete(alerts, true); } catch { /* best-effort */ }
        }
    }
}

/// <summary>
/// SUSPECT de verdade (falha ao desfazer uma transação sobre uma página danificada). Fica na coleção sem
/// paralelismo: o banco reinicia sozinho no meio do teste e o DBCC DROPCLEANBUFFERS esvazia o cache da instância.
/// </summary>
[Collection(ExclusiveCollection.Name)]
public sealed class SuspectIntegrationTests
{
    private static RepairService Repair() { var log = new LogService(); return new(log, new RestoreService(log)); }

    private static async Task<int> MajorAsync() => (await new ServerService().GetServerInfoAsync(It.Conn)).MajorVersion;

    [IntegrationFact]
    public async Task Suspect_SemBackup_ReparoSeguroPedeConfirmacao_EOComPerdaEhHonesto()
    {
        await using var db = await TestDatabase.CreateAsync("suspect", withData: false);
        try
        {
            await Corruption.CreateItemTableAsync(db.Name, "SIMPLE");
            var (_, _, onPage) = await Corruption.MakeSuspectAsync(db.Name);
            var state = await It.GetStateAsync(db.Name);
            // 2008 R2: SUSPECT. 2012+ (Enterprise/Developer): pode ficar ONLINE com a transação ADIADA.
            if (await MajorAsync() == 10) Assert.Equal("SUSPECT", state);
            else Assert.Contains(state, new[] { "SUSPECT", "ONLINE" });

            var diag = await Repair().DiagnoseAsync(db.AsInfo(state), It.Conn, It.NoProgress);
            Assert.Equal(state, diag.StateDesc);
            Assert.True(diag.DataCorruptionSuspected);
            Assert.Equal(RecommendedActionKind.RestoreFromBackup, diag.RecommendedAction); // SIMPLE: restaurar página não existe

            var safe = await Repair().RepairAsync(db.AsInfo(state), It.Conn, allowDataLoss: false, It.NoProgress);
            if (state == "ONLINE")
            {
                // Antes: o CHECKDB recusado virava "nada a reparar" num banco com página corrompida.
                Assert.Equal(RepairStatus.Failed, safe.Status);
                Assert.Contains("ADIADAS", safe.Message);
            }
            else Assert.Equal(RepairStatus.NeedsDataLossConfirmation, safe.Status);
            // O estado que o reparo informa (e a tela mostra) é o estado real do banco depois que ele assenta.
            Assert.Equal(await Corruption.WaitSettledStateAsync(db.Name), safe.FinalStateDesc);

            var r = await Repair().RepairAsync(db.AsInfo(await It.GetStateAsync(db.Name)), It.Conn, allowDataLoss: true, It.NoProgress,
                recovery: new ParentRecoveryOptions { RecoverLostRows = true });

            if (r.Message.Contains("ADIADAS"))
            {
                // SQL 2012+ (Enterprise/Developer): a recuperação ADIA a transação e o DBCC recusa reparar (Msg 7929).
                // A ferramenta precisa dizer isso, sem anunciar sucesso.
                Assert.True(await MajorAsync() > 10, "no 2008 R2 o reparo em EMERGENCY conserta este caso");
                Assert.Equal(RepairStatus.Failed, r.Status);
            }
            else
            {
                Assert.Equal(RepairStatus.Success, r.Status);
                Assert.Equal("ONLINE", await It.GetStateAsync(db.Name));
                Assert.True(await Corruption.CheckDbCleanAsync(db.Name));
                Assert.Equal(Corruption.Rows - onPage, await Corruption.CountItemsAsync(db.Name)); // só a página destruída
                Assert.Contains("não puderam ser recuperadas", r.Message);
            }
            Assert.Equal("MULTI_USER", await It.ScalarAsync("SELECT user_access_desc FROM sys.databases WHERE name = @n;", ("@n", db.Name)));
        }
        finally { await Corruption.CleanupAsync(db.Name); }
    }

    [IntegrationFact]
    public async Task Suspect_ComBackup_AAcaoRecomendadaRecuperaTudo()
    {
        await using var db = await TestDatabase.CreateAsync("suspectbak", withData: false);
        try
        {
            await Corruption.CreateItemTableAsync(db.Name, "FULL");
            await db.BackupAsync();
            var (file, page, _) = await Corruption.MakeSuspectAsync(db.Name);
            // Em FULL com backup, o reinício pode ADIAR a transação (banco ONLINE com a página pendente) — nas duas
            // versões, conforme o caminho do reinício — ou deixar o banco SUSPECT. Cada um tem sua ação recomendada.
            var state = await It.GetStateAsync(db.Name);
            Assert.Contains(state, new[] { "SUSPECT", "ONLINE" });

            var diag = await Repair().DiagnoseAsync(db.AsInfo(state), It.Conn, It.NoProgress);
            RepairResult r;
            if (state == "ONLINE")
            {
                Assert.Equal(RecommendedActionKind.PageRestore, diag.RecommendedAction);
                r = await new RestoreService(new LogService()).RestorePagesFromHistoryAsync(db.Name, $"{file}:{page}", It.Conn, It.NoProgress);
            }
            else
            {
                Assert.Equal(RecommendedActionKind.RestoreFromBackup, diag.RecommendedAction);
                r = await new RestoreService(new LogService()).RestoreToPointInTimeAsync(db.Name, target: null, tailLogBackup: true, It.Conn, It.NoProgress);
            }

            Assert.Equal(RepairStatus.Success, r.Status);
            Assert.Equal("ONLINE", await It.GetStateAsync(db.Name));
            Assert.True(await Corruption.CheckDbCleanAsync(db.Name));
            Assert.Equal(Corruption.Rows, await Corruption.CountItemsAsync(db.Name));
            Assert.Equal(0, Convert.ToInt32(await Corruption.InDbScalarAsync(db.Name, "SELECT COUNT(*) FROM dbo.Item WHERE Pad = 'y';"))); // a transação pendente foi desfeita
        }
        finally { await Corruption.CleanupAsync(db.Name); }
    }
}
