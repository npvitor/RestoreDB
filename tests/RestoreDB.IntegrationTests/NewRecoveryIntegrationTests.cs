using Microsoft.Data.SqlClient;
using RestoreDB.DataAccess;
using RestoreDB.Domain.Constants;
using RestoreDB.Domain.Models;
using RestoreDB.Services;
using Xunit;

namespace RestoreDB.IntegrationTests;

/// <summary>
/// Novos caminhos de recuperação e correções de segurança, contra uma instância real: quarentena de
/// órfãos, constraints desabilitadas, gatilhos durante o salvamento, FKs após reconstruir índices,
/// exclusão acidental desfeita sem sobrescrever, restauração com a aplicação conectada, backups
/// apagados no plano e log cheio (9002).
/// </summary>
public sealed class NewRecoveryIntegrationTests
{
    private static RepairService Repair() { var log = new LogService(); return new(log, new RestoreService(log)); }
    private static RestoreService Restore() => new(new LogService());
    private static AvailabilityService Availability() => new(new LogService());

    private static async Task<int> CountAsync(string sql, params (string, object?)[] ps)
        => Convert.ToInt32(await It.ScalarAsync(sql, ps));

    private static async Task CriarPaiFilhoComOrfaoAsync(string db)
    {
        var inner =
            $"USE {It.Q(db)}; " +
            "CREATE TABLE dbo.Cliente(Id int PRIMARY KEY); " +
            "CREATE TABLE dbo.Pedido(Id int IDENTITY PRIMARY KEY, ClienteId int NOT NULL, Obs nvarchar(50) NULL, Versao rowversion, " +
            "  CONSTRAINT FK_Pedido_Cliente FOREIGN KEY (ClienteId) REFERENCES dbo.Cliente(Id)); " +
            "INSERT INTO dbo.Cliente(Id) VALUES (1),(2); " +
            "INSERT INTO dbo.Pedido(ClienteId, Obs) VALUES (1, N''a''),(2, N''b''); " +
            "ALTER TABLE dbo.Pedido NOCHECK CONSTRAINT FK_Pedido_Cliente; " +
            "INSERT INTO dbo.Pedido(ClienteId, Obs) VALUES (999, N''orfao''); " +
            "ALTER TABLE dbo.Pedido WITH NOCHECK CHECK CONSTRAINT FK_Pedido_Cliente;";
        await It.ExecAsync($"EXEC(N'{inner}');");
    }

    [IntegrationFact]
    public async Task CorrigirIntegridade_MoveOrfaoParaQuarentena_EmVezDeApagar()
    {
        await using var db = await TestDatabase.CreateAsync("quarentena", withData: false);
        await CriarPaiFilhoComOrfaoAsync(db.Name);
        var qTable = $"{AppConstants.SanitizeToken(db.Name)}__dbo__Pedido";
        try
        {
            var r = await Repair().FixConstraintViolationsAsync(db.AsInfo("ONLINE"), It.Conn, It.NoProgress);

            Assert.Equal(RepairStatus.Success, r.Status);
            Assert.Equal(2, await CountAsync($"SELECT COUNT(*) FROM {It.Q(db.Name)}.dbo.Pedido;"));
            // A linha órfã NÃO se perdeu: está na quarentena, com a constraint de origem.
            Assert.Equal(1, await CountAsync(
                $"SELECT COUNT(*) FROM {It.Q(AppConstants.QuarantineDatabaseName)}.dbo.{It.Q(qTable)} " +
                "WHERE ClienteId = 999 AND Obs = N'orfao' AND [_RestoreDB_Constraint] LIKE N'%FK_Pedido_Cliente%';"));
        }
        finally
        {
            await It.ExecAsync($"IF OBJECT_ID(N'{AppConstants.QuarantineDatabaseName}.dbo.{qTable}') IS NOT NULL " +
                               $"DROP TABLE {It.Q(AppConstants.QuarantineDatabaseName)}.dbo.{It.Q(qTable)};");
        }
    }

    [IntegrationFact]
    public async Task ConstraintDesabilitada_NaoEhCorrupcao_ENadaERemovido()
    {
        await using var db = await TestDatabase.CreateAsync("fkoff", withData: false);
        // FK DESLIGADA de propósito pelo sistema (NOCHECK sem reativar) com uma linha que a viola.
        var inner =
            $"USE {It.Q(db.Name)}; " +
            "CREATE TABLE dbo.Cliente(Id int PRIMARY KEY); " +
            "CREATE TABLE dbo.Pedido(Id int IDENTITY PRIMARY KEY, ClienteId int NOT NULL CONSTRAINT FK_Off FOREIGN KEY REFERENCES dbo.Cliente(Id)); " +
            "INSERT INTO dbo.Cliente VALUES (1); INSERT INTO dbo.Pedido(ClienteId) VALUES (1); " +
            "ALTER TABLE dbo.Pedido NOCHECK CONSTRAINT FK_Off; INSERT INTO dbo.Pedido(ClienteId) VALUES (777);";
        await It.ExecAsync($"EXEC(N'{inner}');");

        var diag = await Repair().DiagnoseAsync(db.AsInfo("ONLINE"), It.Conn, It.NoProgress);
        Assert.False(diag.LogicalCorruptionSuspected);
        Assert.Equal(1, diag.DisabledConstraintHits);
        Assert.Equal(RecommendedActionKind.NoAction, diag.RecommendedAction);

        var fix = await Repair().FixConstraintViolationsAsync(db.AsInfo("ONLINE"), It.Conn, It.NoProgress);
        Assert.Equal(RepairStatus.Success, fix.Status);
        Assert.Equal(2, await CountAsync($"SELECT COUNT(*) FROM {It.Q(db.Name)}.dbo.Pedido;")); // nada removido
    }

    [IntegrationFact]
    public async Task Salvamento_NaoDisparaGatilhos_EOsMantemAtivos()
    {
        await using var db = await TestDatabase.CreateAsync("trg");
        await It.ExecAsync($"EXEC(N'USE {It.Q(db.Name)}; CREATE TABLE dbo.Estoque(Qtd int NOT NULL); INSERT dbo.Estoque VALUES (100);');");
        await It.ExecAsync($"EXEC(N'USE {It.Q(db.Name)}; EXEC(''CREATE TRIGGER dbo.trg_Baixa ON dbo.NotaFiscal AFTER INSERT AS UPDATE dbo.Estoque SET Qtd = Qtd - (SELECT COUNT(*) FROM inserted);'')');");
        await db.BackupAsync();
        var copy = db.Name + "_copia";
        try
        {
            Assert.Equal(RepairStatus.Success, (await Restore().RestoreAsCopyAsync(db.Name, copy, It.Conn, It.NoProgress)).Status);
            await It.ExecAsync($"DELETE FROM {It.Q(db.Name)}.dbo.NotaFiscal WHERE Cliente IN (N'A', N'B');");

            var r = await Restore().SalvageTableAsync(copy, db.Name, "dbo.NotaFiscal", It.Conn, It.NoProgress);

            Assert.Equal(RepairStatus.Success, r.Status);
            Assert.Equal(3, await It.RowCountAsync(db.Name));
            Assert.Equal(100, await CountAsync($"SELECT Qtd FROM {It.Q(db.Name)}.dbo.Estoque;"));       // gatilho NÃO disparou
            Assert.Equal(0, await CountAsync($"SELECT COUNT(*) FROM {It.Q(db.Name)}.sys.triggers WHERE is_disabled = 1;")); // e continua ativo
        }
        finally { await It.ForceDropAsync(copy); }
    }

    [IntegrationFact]
    public async Task ReconstruirIndice_ReativaAsFksQueODisableDerrubou()
    {
        await using var db = await TestDatabase.CreateAsync("idxfk", withData: false);
        var inner =
            $"USE {It.Q(db.Name)}; " +
            "CREATE TABLE dbo.Cliente(Id int NOT NULL CONSTRAINT PK_Cliente PRIMARY KEY NONCLUSTERED, Nome nvarchar(20)); " +
            "CREATE TABLE dbo.Pedido(Id int PRIMARY KEY, ClienteId int NOT NULL CONSTRAINT FK_Ped_Cli FOREIGN KEY REFERENCES dbo.Cliente(Id)); " +
            "INSERT dbo.Cliente VALUES (1,N''x''); INSERT dbo.Pedido VALUES (1,1);";
        await It.ExecAsync($"EXEC(N'{inner}');");
        var objectId = await CountAsync($"SELECT OBJECT_ID(N'{db.Name}.dbo.Cliente');");
        var indexId = await CountAsync($"SELECT index_id FROM {It.Q(db.Name)}.sys.indexes WHERE name = N'PK_Cliente';");

        var (sql, prm) = RepairService.RebuildIndex(db.Name, objectId, indexId);
        await using (var s = await new SqlDataAccess().OpenSessionAsync(It.Conn))
            Assert.True((await s.ExecuteAsync("rebuild", sql, prm)).Success);

        Assert.Equal(0, await CountAsync(
            $"SELECT COUNT(*) FROM {It.Q(db.Name)}.sys.foreign_keys WHERE name = N'FK_Ped_Cli' AND (is_disabled = 1 OR is_not_trusted = 1);"));
        Assert.Equal(0, await CountAsync($"SELECT COUNT(*) FROM {It.Q(db.Name)}.sys.indexes WHERE is_disabled = 1;"));
    }

    [IntegrationFact]
    public async Task ExclusaoAcidental_DevolveSoAsLinhas_SemPerderOQueVeioDepois()
    {
        await using var db = await TestDatabase.CreateAsync("undodel");
        await It.ExecAsync($"ALTER DATABASE {It.Q(db.Name)} SET RECOVERY FULL;");
        await db.BackupAsync();
        await It.ExecAsync($"EXEC(N'USE {It.Q(db.Name)}; INSERT dbo.NotaFiscal(Cliente,Valor) VALUES (N''D'',40);');");
        await Task.Delay(2500);

        await It.ExecAsync($"EXEC(N'USE {It.Q(db.Name)}; DELETE FROM dbo.NotaFiscal WHERE Cliente IN (N''B'', N''D'');');");  // o "acidente"
        await Task.Delay(1500);
        await It.ExecAsync($"EXEC(N'USE {It.Q(db.Name)}; INSERT dbo.NotaFiscal(Cliente,Valor) VALUES (N''E'',50);');");       // trabalho posterior

        var tx = (await Restore().ListRecentDestructiveTransactionsAsync(db.Name, It.Conn))
            .First(t => t.Kind == "DELETE" && t.CanRecoverRowsInPlace);
        Assert.Equal("dbo.NotaFiscal", tx.Table);
        Assert.Equal(2, tx.RowsAffected);
        Assert.NotNull(tx.BeginTime);

        var r = await Restore().RecoverDeletedRowsAsync(db.Name, tx.BeginTime!.Value.AddSeconds(-1), new[] { tx.Table }, It.Conn, It.NoProgress);
        try
        {
            Assert.Equal(RepairStatus.Success, r.Status);
            Assert.Equal(5, await It.RowCountAsync(db.Name));   // A, C, E + B, D devolvidos
            Assert.Equal(1, await CountAsync($"SELECT COUNT(*) FROM {It.Q(db.Name)}.dbo.NotaFiscal WHERE Cliente = N'E';")); // posterior preservado
            Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM sys.databases WHERE name LIKE @p;", ("@p", AppConstants.SanitizeToken(db.Name) + "_AntesExclusao_%")));
        }
        finally
        {
            var leftover = await It.ScalarAsync("SELECT TOP 1 name FROM sys.databases WHERE name LIKE @p;", ("@p", AppConstants.SanitizeToken(db.Name) + "_AntesExclusao_%")) as string;
            if (leftover is not null) await It.ForceDropAsync(leftover);
        }
    }

    [IntegrationFact]
    public async Task Restaurar_ComAAplicacaoConectada_EncerraConexoesERestaura()
    {
        await using var db = await TestDatabase.CreateAsync("inuse");
        var bak = await db.BackupAsync();
        await It.ExecAsync($"DELETE FROM {It.Q(db.Name)}.dbo.NotaFiscal;");

        // "Aplicação" conectada ao banco durante a restauração.
        var appConn = new SqlConnectionStringBuilder(It.Conn) { InitialCatalog = db.Name, Pooling = false }.ConnectionString;
        await using var app = new SqlConnection(appConn);
        await app.OpenAsync();

        var r = await Restore().RestoreDatabaseAsync(new RestoreRequest
        {
            DatabaseName = db.Name, Replace = true, Pieces = { new RestorePiece { DiskPath = bak, Position = 1 } }
        }, It.Conn, It.NoProgress);

        Assert.Equal(RepairStatus.Success, r.Status);
        Assert.Equal(3, await It.RowCountAsync(db.Name));
        Assert.Equal("MULTI_USER", await It.ScalarAsync("SELECT user_access_desc FROM sys.databases WHERE name = @n;", ("@n", db.Name)) as string);
    }

    [IntegrationFact]
    public async Task PontoNoTempo_AlvoCobertoPelosLogs_NaoAplicaOTailLog()
    {
        await using var db = await TestDatabase.CreateAsync("pitpast");
        await It.ExecAsync($"ALTER DATABASE {It.Q(db.Name)} SET RECOVERY FULL;");
        await db.BackupAsync();
        await It.ExecAsync($"EXEC(N'USE {It.Q(db.Name)}; INSERT dbo.NotaFiscal(Cliente,Valor) VALUES (N''D'',40);');");
        await Task.Delay(1500);
        var target = (DateTime)(await It.ScalarAsync("SELECT GETDATE();"))!;
        await Task.Delay(1500);
        await It.ExecAsync($"EXEC(N'USE {It.Q(db.Name)}; INSERT dbo.NotaFiscal(Cliente,Valor) VALUES (N''E'',50);');");
        var log = db.Name + "_pit.trn";
        await It.ExecAsync($"BACKUP LOG {It.Q(db.Name)} TO DISK = @f WITH INIT;", ("@f", log));
        try
        {
            var r = await Restore().RestoreToPointInTimeAsync(db.Name, target, tailLogBackup: true, It.Conn, It.NoProgress);
            Assert.Equal(RepairStatus.Success, r.Status);
            Assert.Equal(4, await It.RowCountAsync(db.Name)); // A, B, C, D — sem o E posterior ao alvo
        }
        finally { await It.TryDeleteBackupFileAsync(log); }
    }

    [IntegrationFact]
    public async Task RecuperarParaCopia_FalhaNoMeio_NaoDeixaACopiaPresaEmRestoring()
    {
        await using var db = await TestDatabase.CreateAsync("copyfail");
        await It.ExecAsync($"ALTER DATABASE {It.Q(db.Name)} SET RECOVERY FULL;");
        await db.BackupAsync();
        var log = db.Name + "_reuso.trn";
        await It.ExecAsync($"BACKUP LOG {It.Q(db.Name)} TO DISK = @f WITH INIT;", ("@f", log));     // log 1
        await It.ExecAsync($"EXEC(N'USE {It.Q(db.Name)}; INSERT dbo.NotaFiscal(Cliente,Valor) VALUES (N''D'',40);');");
        // O log 2 SOBRESCREVE o log 1 no mesmo arquivo; o msdb ainda lista o log 1 nele: o RESTORE do 2º elo falha.
        await It.ExecAsync($"BACKUP LOG {It.Q(db.Name)} TO DISK = @f WITH INIT;", ("@f", log));
        var copy = db.Name + "_copia";
        try
        {
            var r = await Restore().RestoreAsCopyAsync(db.Name, copy, It.Conn, It.NoProgress);

            Assert.Equal(RepairStatus.Failed, r.Status);
            // A cópia incompleta (RESTORING) foi descartada — uma nova tentativa com o mesmo nome não é bloqueada.
            Assert.Equal("DESCONHECIDO", await It.GetStateAsync(copy));
            Assert.Equal("ONLINE", await It.GetStateAsync(db.Name));
        }
        finally
        {
            await It.ForceDropAsync(copy);
            await It.TryDeleteBackupFileAsync(log);
        }
    }

    [IntegrationFact]
    public async Task Plano_IgnoraBackupCujoArquivoFoiApagado()
    {
        await using var db = await TestDatabase.CreateAsync("gonebak");
        await db.BackupAsync();                                                   // FULL 1 (existe)
        await It.ExecAsync($"EXEC(N'USE {It.Q(db.Name)}; INSERT dbo.NotaFiscal(Cliente,Valor) VALUES (N''D'',40);');");
        var gone = db.Name + "_apagado.bak";
        await It.ExecAsync($"BACKUP DATABASE {It.Q(db.Name)} TO DISK = @f WITH INIT, FORMAT;", ("@f", gone)); // FULL 2
        var gonePath = await It.ScalarAsync(
            "SELECT TOP 1 bmf.physical_device_name FROM msdb.dbo.backupset bs JOIN msdb.dbo.backupmediafamily bmf ON bmf.media_set_id = bs.media_set_id " +
            "WHERE bs.database_name = @n ORDER BY bs.backup_finish_date DESC;", ("@n", db.Name)) as string;
        await It.TryDeleteBackupFileAsync(gonePath!);                             // retenção apagou o arquivo; o msdb não sabe

        var r = await Restore().RestoreToPointInTimeAsync(db.Name, null, tailLogBackup: false, It.Conn, It.NoProgress);

        Assert.Equal(RepairStatus.Success, r.Status);                            // usou o FULL 1
        Assert.Equal(3, await It.RowCountAsync(db.Name));
    }

    [IntegrationFact]
    public async Task ArquivoAusente_DiagnosticaEApontaNovoCaminho_VoltaOnlineSemPerda()
    {
        await using var db = await TestDatabase.CreateAsync("movedfile");
        var (logical, realPath) = await LogFileAsync(db.Name);

        // Simula "disco/letra trocados": o catálogo passa a apontar para um caminho que não existe.
        await It.ExecAsync($"ALTER DATABASE {It.Q(db.Name)} SET OFFLINE WITH ROLLBACK IMMEDIATE;");
        var bogus = Path.Combine(Path.GetDirectoryName(realPath)!, "movido_" + Guid.NewGuid().ToString("N")[..6] + ".ldf");
        await It.ExecAsync($"ALTER DATABASE {It.Q(db.Name)} MODIFY FILE (NAME = {It.Q(logical)}, FILENAME = N'{bogus.Replace("'", "''")}');");
        try { await It.ExecAsync($"ALTER DATABASE {It.Q(db.Name)} SET ONLINE;"); } catch (SqlException) { /* esperado: arquivo não encontrado */ }
        var state = await It.GetStateAsync(db.Name);
        Assert.NotEqual("ONLINE", state);

        var diag = await Repair().DiagnoseAsync(db.AsInfo(state), It.Conn, It.NoProgress);
        Assert.Equal(RecommendedActionKind.FixFileAccess, diag.RecommendedAction);
        Assert.Contains(diag.FileProblems, f => f.Kind == FileProblemKind.Missing && f.LogicalName == logical);

        // O usuário "localiza" o arquivo no lugar real.
        var r = await Availability().FixFileAccessAsync(db.AsInfo(state), It.Conn, It.NoProgress, default,
            new Dictionary<string, string> { [logical] = realPath });

        Assert.Equal(RepairStatus.Success, r.Status);
        Assert.Equal("ONLINE", await It.GetStateAsync(db.Name));
        Assert.Equal(3, await It.RowCountAsync(db.Name));
    }

    private static async Task<(string Logical, string Physical)> LogFileAsync(string db)
    {
        await using var c = new SqlConnection(It.Conn);
        await c.OpenAsync();
        await using var cmd = new SqlCommand("SELECT name, physical_name FROM sys.master_files WHERE database_id = DB_ID(@n) AND type = 1;", c);
        cmd.Parameters.AddWithValue("@n", db);
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        return (r.GetString(0), r.GetString(1));
    }

    [IntegrationFact]
    public async Task LogCheio_DiagnosticaEResolveSemPerda()
    {
        await using var db = await TestDatabase.CreateAsync("logfull");
        await It.ExecAsync($"ALTER DATABASE {It.Q(db.Name)} SET RECOVERY FULL;");
        var logName = await It.ScalarAsync($"SELECT name FROM {It.Q(db.Name)}.sys.database_files WHERE type = 1;") as string;
        // Trava o log no tamanho atual (o padrão varia por versão: ~1 MB no 2008, 8 MB no 2025).
        var sizeMb = await CountAsync($"SELECT CEILING(size * 8 / 1024.0) FROM {It.Q(db.Name)}.sys.database_files WHERE type = 1;");
        await It.ExecAsync($"ALTER DATABASE {It.Q(db.Name)} MODIFY FILE (NAME = {It.Q(logName!)}, FILEGROWTH = 0);");
        await It.ExecAsync($"ALTER DATABASE {It.Q(db.Name)} MODIFY FILE (NAME = {It.Q(logName!)}, MAXSIZE = {sizeMb}MB);");
        await db.BackupAsync();

        // Enche o log até o 9002.
        var hit9002 = false;
        for (var i = 0; i < 200 && !hit9002; i++)
        {
            try { await It.ExecAsync($"EXEC(N'USE {It.Q(db.Name)}; INSERT dbo.NotaFiscal(Cliente,Valor) SELECT TOP (200) REPLICATE(N''x'', 100), 1 FROM sys.all_objects;');"); }
            catch (SqlException ex) when (ex.Number == 9002) { hit9002 = true; }
        }
        Assert.True(hit9002, "o log deveria ter enchido");
        var rowsBefore = await It.RowCountAsync(db.Name);

        var diag = await Repair().DiagnoseAsync(db.AsInfo("ONLINE"), It.Conn, It.NoProgress);
        Assert.Equal(RecommendedActionKind.ResolveLogFull, diag.RecommendedAction);

        var r = await Availability().ResolveLogFullAsync(db.AsInfo("ONLINE"), It.Conn, It.NoProgress);

        Assert.Equal(RepairStatus.Success, r.Status);
        Assert.Equal(rowsBefore, await It.RowCountAsync(db.Name));                  // nada perdido
        await It.ExecAsync($"EXEC(N'USE {It.Q(db.Name)}; INSERT dbo.NotaFiscal(Cliente,Valor) VALUES (N''ok'',1);');"); // volta a gravar
    }
}
