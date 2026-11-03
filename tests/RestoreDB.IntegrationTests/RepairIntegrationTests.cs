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
    private static RepairService Repair()
    {
        var log = new LogService();
        return new(log, new RestoreService(log));
    }
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
    public async Task Diagnostico_DetectaRegistroOrfaoDeChaveEstrangeira()
    {
        await using var db = await TestDatabase.CreateAsync("fkdiag", withData: false);
        await CriarOrfaoFkAsync(db.Name);

        var diag = await Repair().DiagnoseAsync(db.AsInfo("ONLINE"), It.Conn, It.NoProgress);

        Assert.True(diag.CheckDb!.Executed);
        Assert.False(diag.CheckDb.HasErrors);                 // físico íntegro
        Assert.True(diag.LogicalCorruptionSuspected);         // mas integridade lógica quebrada
        Assert.NotEmpty(diag.ForeignKeyViolations);
        Assert.Equal(RecommendedActionKind.FixConstraints, diag.RecommendedAction);
    }

    [IntegrationFact]
    public async Task CorrigirIntegridade_RemoveOrfaoEPreservaValidos()
    {
        await using var db = await TestDatabase.CreateAsync("fkfix", withData: false);
        await CriarOrfaoFkAsync(db.Name);

        var result = await Repair().FixConstraintViolationsAsync(db.AsInfo("ONLINE"), It.Conn, It.NoProgress);

        Assert.Equal(RepairStatus.Success, result.Status);
        Assert.Equal("ONLINE", await It.GetStateAsync(db.Name));

        // O pedido órfão (ClienteId 999) foi removido; os dois válidos permanecem.
        var orfaos = Convert.ToInt32(await It.ScalarAsync(
            $"SELECT COUNT(*) FROM {It.Q(db.Name)}.dbo.Pedido WHERE ClienteId = 999;"));
        Assert.Equal(0, orfaos);
        var total = Convert.ToInt32(await It.ScalarAsync(
            $"SELECT COUNT(*) FROM {It.Q(db.Name)}.dbo.Pedido;"));
        Assert.Equal(2, total);

        // O órfão foi para a quarentena (ver NewRecoveryIntegrationTests); limpa o que o teste gerou.
        var q = $"{RestoreDB.Domain.Constants.AppConstants.QuarantineDatabaseName}.dbo.{db.Name}__dbo__Pedido";
        await It.ExecAsync($"IF OBJECT_ID(N'{q}') IS NOT NULL DROP TABLE {q};");
    }

    [IntegrationFact]
    public async Task CorrigirIntegridade_RecuperaPaiDoBackup_PreservaFilho()
    {
        await using var db = await TestDatabase.CreateAsync("fkrecover", withData: false);

        // Esquema pai/filho com dados VÁLIDOS (Cliente 1 e 2; Pedidos apontando 1 e 2).
        var setup =
            $"USE {It.Q(db.Name)}; " +
            "CREATE TABLE dbo.Cliente(Id int PRIMARY KEY, Nome nvarchar(100) NOT NULL); " +
            "CREATE TABLE dbo.Pedido(Id int IDENTITY PRIMARY KEY, ClienteId int NOT NULL " +
            "  CONSTRAINT FK_Pedido_Cliente FOREIGN KEY REFERENCES dbo.Cliente(Id)); " +
            "INSERT INTO dbo.Cliente(Id,Nome) VALUES (1,N''Cliente 1''),(2,N''Cliente 2''); " +
            "INSERT INTO dbo.Pedido(ClienteId) VALUES (1),(2);";
        await It.ExecAsync($"EXEC(N'{setup}');");

        // Backup FULL (agendado, NÃO copy-only): contém Cliente 1 e 2.
        await db.BackupAsync();

        // Simula o efeito do REPAIR: o PAI (Cliente 2) some, deixando o FILHO (Pedido→2) órfão.
        var orphan =
            $"USE {It.Q(db.Name)}; " +
            "ALTER TABLE dbo.Pedido NOCHECK CONSTRAINT FK_Pedido_Cliente; " +
            "DELETE FROM dbo.Cliente WHERE Id = 2; " +
            "ALTER TABLE dbo.Pedido WITH NOCHECK CHECK CONSTRAINT FK_Pedido_Cliente;";
        await It.ExecAsync($"EXEC(N'{orphan}');");

        // Estado órfão inicial: 1 Cliente, 2 Pedidos (um deles órfão).
        Assert.Equal(1, Convert.ToInt32(await It.ScalarAsync($"SELECT COUNT(*) FROM {It.Q(db.Name)}.dbo.Cliente;")));
        Assert.Equal(2, Convert.ToInt32(await It.ScalarAsync($"SELECT COUNT(*) FROM {It.Q(db.Name)}.dbo.Pedido;")));

        try
        {
            var result = await Repair().FixConstraintViolationsAsync(db.AsInfo("ONLINE"), It.Conn, It.NoProgress);

            Assert.Equal(RepairStatus.Success, result.Status);
            Assert.Equal("ONLINE", await It.GetStateAsync(db.Name));

            // O PAI foi RECUPERADO do backup (Cliente 2 voltou)...
            Assert.Equal(1, Convert.ToInt32(await It.ScalarAsync(
                $"SELECT COUNT(*) FROM {It.Q(db.Name)}.dbo.Cliente WHERE Id = 2;")));
            // ...e o FILHO saudável foi PRESERVADO (nenhum Pedido removido).
            Assert.Equal(2, Convert.ToInt32(await It.ScalarAsync($"SELECT COUNT(*) FROM {It.Q(db.Name)}.dbo.Pedido;")));
            Assert.Contains("recuperado", result.Message, StringComparison.OrdinalIgnoreCase);

            // O banco paralelo de recuperação foi descartado (sem lixo no servidor).
            Assert.Equal(0, Convert.ToInt32(await It.ScalarAsync(
                "SELECT COUNT(*) FROM sys.databases WHERE name LIKE @p;", ("@p", db.Name + "_OrphanRecovery_%"))));
        }
        finally
        {
            // Segurança: descarta qualquer banco paralelo remanescente.
            var leftover = (await It.ScalarAsync(
                "SELECT TOP 1 name FROM sys.databases WHERE name LIKE @p;", ("@p", db.Name + "_OrphanRecovery_%"))) as string;
            if (leftover is not null) await It.ForceDropAsync(leftover);
        }
    }

    [IntegrationFact]
    public async Task CorrigirIntegridade_RecuperaPaiDeBackupESCOLHIDO_MesmoSemHistoricoMsdb()
    {
        await using var db = await TestDatabase.CreateAsync("fkrecfile", withData: false);

        var setup =
            $"USE {It.Q(db.Name)}; " +
            "CREATE TABLE dbo.Cliente(Id int PRIMARY KEY, Nome nvarchar(100) NOT NULL); " +
            "CREATE TABLE dbo.Pedido(Id int IDENTITY PRIMARY KEY, ClienteId int NOT NULL " +
            "  CONSTRAINT FK_Pedido_Cliente FOREIGN KEY REFERENCES dbo.Cliente(Id)); " +
            "INSERT INTO dbo.Cliente(Id,Nome) VALUES (1,N''Cliente 1''),(2,N''Cliente 2''); " +
            "INSERT INTO dbo.Pedido(ClienteId) VALUES (1),(2);";
        await It.ExecAsync($"EXEC(N'{setup}');");

        // Backup FULL para um ARQUIVO conhecido (contém Cliente 2).
        var bakPath = await db.BackupAsync();

        // Órfão: remove o pai Cliente 2.
        var orphan =
            $"USE {It.Q(db.Name)}; " +
            "ALTER TABLE dbo.Pedido NOCHECK CONSTRAINT FK_Pedido_Cliente; " +
            "DELETE FROM dbo.Cliente WHERE Id = 2; " +
            "ALTER TABLE dbo.Pedido WITH NOCHECK CHECK CONSTRAINT FK_Pedido_Cliente;";
        await It.ExecAsync($"EXEC(N'{orphan}');");

        // Prova forte: apaga TODO o histórico do msdb — a recuperação AUTOMÁTICA não acharia backup.
        // Só o .bak ESCOLHIDO (lido direto via HEADERONLY) pode recuperar o pai.
        await It.TryPurgeBackupHistoryAsync(db.Name);

        try
        {
            var result = await Repair().FixConstraintViolationsAsync(db.AsInfo("ONLINE"), It.Conn, It.NoProgress, default,
                new ParentRecoveryOptions { InitialBackupPath = bakPath });

            Assert.Equal(RepairStatus.Success, result.Status);
            Assert.Equal(1, Convert.ToInt32(await It.ScalarAsync(
                $"SELECT COUNT(*) FROM {It.Q(db.Name)}.dbo.Cliente WHERE Id = 2;")));         // pai recuperado do .bak escolhido
            Assert.Equal(2, Convert.ToInt32(await It.ScalarAsync($"SELECT COUNT(*) FROM {It.Q(db.Name)}.dbo.Pedido;"))); // filho preservado
            Assert.Contains("recuperado", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            var leftover = (await It.ScalarAsync(
                "SELECT TOP 1 name FROM sys.databases WHERE name LIKE @p;", ("@p", db.Name + "_OrphanRecovery_%"))) as string;
            if (leftover is not null) await It.ForceDropAsync(leftover);
        }
    }

    [IntegrationFact]
    public async Task CorrigirIntegridade_RecuperacaoFalha_CallbackEscolheOutroBak_RecuperaAntesDeRemover()
    {
        await using var db = await TestDatabase.CreateAsync("fkcallback", withData: false);

        var setup =
            $"USE {It.Q(db.Name)}; " +
            "CREATE TABLE dbo.Cliente(Id int PRIMARY KEY, Nome nvarchar(100) NOT NULL); " +
            "CREATE TABLE dbo.Pedido(Id int IDENTITY PRIMARY KEY, ClienteId int NOT NULL " +
            "  CONSTRAINT FK_Pedido_Cliente FOREIGN KEY REFERENCES dbo.Cliente(Id)); " +
            "INSERT INTO dbo.Cliente(Id,Nome) VALUES (1,N''C1''),(2,N''C2''); " +
            "INSERT INTO dbo.Pedido(ClienteId) VALUES (1),(2);";
        await It.ExecAsync($"EXEC(N'{setup}');");

        var bakPath = await db.BackupAsync();   // contém Cliente 2

        await It.ExecAsync($"EXEC(N'USE {It.Q(db.Name)}; ALTER TABLE dbo.Pedido NOCHECK CONSTRAINT FK_Pedido_Cliente; " +
            "DELETE FROM dbo.Cliente WHERE Id=2; ALTER TABLE dbo.Pedido WITH NOCHECK CHECK CONSTRAINT FK_Pedido_Cliente;');");

        // Purga o histórico → a 1ª tentativa (automática) NÃO acha backup e o callback é acionado.
        await It.TryPurgeBackupHistoryAsync(db.Name);

        var prompts = 0;
        var options = new ParentRecoveryOptions
        {
            InitialBackupPath = null,                                    // 1ª tentativa automática → falha
            ChooseAnotherBackup = (prompt, ct) =>
            {
                prompts++;
                return Task.FromResult<string?>(prompts == 1 ? bakPath : null); // oferece o .bak bom só uma vez
            }
        };

        try
        {
            var result = await Repair().FixConstraintViolationsAsync(db.AsInfo("ONLINE"), It.Conn, It.NoProgress, default, options);

            Assert.True(prompts >= 1);                                   // callback acionado (recuperação incompleta)
            Assert.Equal(RepairStatus.Success, result.Status);
            Assert.Equal(1, Convert.ToInt32(await It.ScalarAsync(       // pai recuperado via callback
                $"SELECT COUNT(*) FROM {It.Q(db.Name)}.dbo.Cliente WHERE Id=2;")));
            Assert.Equal(2, Convert.ToInt32(await It.ScalarAsync(       // filho preservado (nada removido)
                $"SELECT COUNT(*) FROM {It.Q(db.Name)}.dbo.Pedido;")));
        }
        finally
        {
            var leftover = (await It.ScalarAsync(
                "SELECT TOP 1 name FROM sys.databases WHERE name LIKE @p;", ("@p", db.Name + "_OrphanRecovery_%"))) as string;
            if (leftover is not null) await It.ForceDropAsync(leftover);
        }
    }

    /// <summary>Cria um esquema pai/filho com FK e injeta uma linha filha órfã (FK desabilitada e
    /// reativada SEM verificar), reproduzindo o registro cuja chave estrangeira não existe na PK.</summary>
    private static async Task CriarOrfaoFkAsync(string name)
    {
        var inner =
            $"USE {It.Q(name)}; " +
            "CREATE TABLE dbo.Cliente(Id int PRIMARY KEY); " +
            "CREATE TABLE dbo.Pedido(Id int IDENTITY PRIMARY KEY, ClienteId int NOT NULL " +
            "  CONSTRAINT FK_Pedido_Cliente FOREIGN KEY REFERENCES dbo.Cliente(Id)); " +
            "INSERT INTO dbo.Cliente(Id) VALUES (1),(2); " +
            "INSERT INTO dbo.Pedido(ClienteId) VALUES (1),(2); " +
            "ALTER TABLE dbo.Pedido NOCHECK CONSTRAINT FK_Pedido_Cliente; " +
            "INSERT INTO dbo.Pedido(ClienteId) VALUES (999); " +
            "ALTER TABLE dbo.Pedido WITH NOCHECK CHECK CONSTRAINT FK_Pedido_Cliente;";
        await It.ExecAsync($"EXEC(N'{inner}');");
    }

    [IntegrationFact]
    public async Task Forense_ListaExclusaoRecenteNoLog()
    {
        await using var db = await TestDatabase.CreateAsync("forense");
        await It.ExecAsync($"ALTER DATABASE {It.Q(db.Name)} SET RECOVERY FULL;");
        await db.BackupAsync();
        await It.ExecAsync($"EXEC(N'USE {It.Q(db.Name)}; DELETE FROM dbo.NotaFiscal;');");

        var txs = await Restore().ListRecentDestructiveTransactionsAsync(db.Name, It.Conn);

        Assert.Contains(txs, t => t.Kind == "DELETE" && t.RowsAffected >= 1);
    }

    [IntegrationFact]
    public async Task Manutencao_ExecutaSemErro()
    {
        await using var db = await TestDatabase.CreateAsync("maint");
        var maint = new MaintenanceService(new LogService());

        var frags = await maint.GetFragmentationAsync(db.Name, It.Conn); // roda (pode vir vazio em tabela pequena)
        Assert.NotNull(frags);

        var r = await maint.RunMaintenanceAsync(db.AsInfo("ONLINE"), It.Conn, It.NoProgress);
        Assert.Equal(RepairStatus.Success, r.Status);
    }

    [IntegrationFact]
    public async Task Manutencao_ComFalha_NaoDizQueConcluiu()
    {
        await using var db = await TestDatabase.CreateAsync("maintfail", withData: false);
        // Chave GUID aleatória: índice bem fragmentado e com mais de 100 páginas.
        await It.ExecAsync($"EXEC(N'USE {It.Q(db.Name)}; " +
            "CREATE TABLE dbo.F(Id uniqueidentifier NOT NULL DEFAULT NEWID() PRIMARY KEY, Pad char(800) NOT NULL); " +
            "INSERT dbo.F(Pad) SELECT TOP (5000) ''x'' FROM sys.all_objects a CROSS JOIN sys.all_objects b;');");
        // Somente leitura: o REBUILD/REORGANIZE (e o sp_updatestats) falham.
        await It.ExecAsync($"ALTER DATABASE {It.Q(db.Name)} SET READ_ONLY WITH ROLLBACK IMMEDIATE;");

        var r = await new MaintenanceService(new LogService()).RunMaintenanceAsync(db.AsInfo("ONLINE"), It.Conn, It.NoProgress);

        Assert.Equal(RepairStatus.Failed, r.Status);
        Assert.DoesNotContain("Manutenção concluída", r.Message);
        Assert.Contains("INCOMPLETA", r.Message);
    }

    [IntegrationFact]
    public async Task Anexar_BancoDesanexado_VoltaOnline()
    {
        await using var db = await TestDatabase.CreateAsync("attach");
        var mdf = (await It.ScalarAsync("SELECT physical_name FROM sys.master_files WHERE database_id = DB_ID(@n) AND type = 0;", ("@n", db.Name))) as string;
        var ldf = (await It.ScalarAsync("SELECT physical_name FROM sys.master_files WHERE database_id = DB_ID(@n) AND type = 1;", ("@n", db.Name))) as string;
        Assert.False(string.IsNullOrEmpty(mdf));

        await It.ExecAsync("EXEC master.dbo.sp_detach_db @dbname = @n;", ("@n", db.Name));
        Assert.Equal("DESCONHECIDO", await It.GetStateAsync(db.Name)); // saiu do servidor

        var r = await Restore().AttachDatabaseAsync(db.Name, mdf!, ldf, rebuildLog: false, It.Conn, It.NoProgress);

        Assert.Equal(RepairStatus.Success, r.Status);
        Assert.Equal("ONLINE", await It.GetStateAsync(db.Name));
    }

    [IntegrationFact]
    public async Task SimulacaoDeReparo_NaoTocaOriginal_E_LimpaACopia()
    {
        await using var db = await TestDatabase.CreateAsync("simrep");
        var linhasAntes = await It.RowCountAsync(db.Name);

        var r = await Repair().SimulateRepairAsync(db.AsInfo("ONLINE"), It.Conn, It.NoProgress);

        Assert.True(r.Executed);
        Assert.True(r.CheckDbCleanAfter);               // banco saudável → cópia fica limpa
        Assert.Equal(0, r.TotalRowsLost);               // nada a perder num banco íntegro
        Assert.NotEmpty(r.TableLosses);                 // mediu as tabelas (NotaFiscal)
        // Original intacto e a cópia de simulação foi descartada.
        Assert.Equal("ONLINE", await It.GetStateAsync(db.Name));
        Assert.Equal(linhasAntes, await It.RowCountAsync(db.Name));
        var sobra = Convert.ToInt32(await It.ScalarAsync(
            "SELECT COUNT(*) FROM sys.databases WHERE name LIKE @p;", ("@p", db.Name + "_SimRepair_%")));
        Assert.Equal(0, sobra);
    }

    [IntegrationFact]
    public async Task RestauracaoDePagina_DeBackup_PreservaDados()
    {
        await using var db = await TestDatabase.CreateAsync("pagerest");
        await It.ExecAsync($"ALTER DATABASE {It.Q(db.Name)} SET RECOVERY FULL;");
        await db.BackupAsync();

        // Descobre uma página de DADOS real da tabela (formato "1:xxx").
        var raw = (await It.ScalarAsync($"SELECT TOP 1 sys.fn_PhysLocFormatter(%%physloc%%) FROM {It.Q(db.Name)}.dbo.NotaFiscal;")) as string ?? string.Empty;
        raw = raw.Trim('(', ')');
        var parts = raw.Split(':');
        var page = parts.Length >= 2 ? $"{parts[0]}:{parts[1]}" : string.Empty;
        Assert.False(string.IsNullOrEmpty(page));

        var r = await Restore().RestorePagesFromHistoryAsync(db.Name, page, It.Conn, It.NoProgress);

        Assert.Equal(RepairStatus.Success, r.Status);
        Assert.Equal("ONLINE", await It.GetStateAsync(db.Name));
        Assert.Equal(3, await It.RowCountAsync(db.Name));
        // O COUNT(*) pode ler só o índice não-clusterizado: o CHECKDB lê a página restaurada (RestorePending = Msg 829).
        Assert.True(await Corruption.CheckDbCleanAsync(db.Name));
        Assert.Equal(3, Convert.ToInt32(await It.ScalarAsync($"SELECT COUNT(*) FROM {It.Q(db.Name)}.dbo.NotaFiscal WITH (INDEX(1));")));
    }

    [IntegrationFact]
    public async Task RestauracaoDePagina_ModeloSimple_AbortaSemAlterar()
    {
        await using var db = await TestDatabase.CreateAsync("pagesimple");
        await It.ExecAsync($"ALTER DATABASE {It.Q(db.Name)} SET RECOVERY SIMPLE;");
        await db.BackupAsync();

        var r = await Restore().RestorePagesFromHistoryAsync(db.Name, "1:8", It.Conn, It.NoProgress);

        Assert.Equal(RepairStatus.Failed, r.Status);
        Assert.Equal("ONLINE", await It.GetStateAsync(db.Name)); // nada foi alterado
    }

    [IntegrationFact]
    public async Task Standby_RestauraSomenteLeitura_E_Finaliza()
    {
        await using var db = await TestDatabase.CreateAsync("standby");
        await It.ExecAsync($"ALTER DATABASE {It.Q(db.Name)} SET RECOVERY FULL;");
        await db.BackupAsync();
        await It.ExecAsync($"EXEC(N'USE {It.Q(db.Name)}; INSERT INTO dbo.NotaFiscal(Cliente,Valor) VALUES (N''D'',40);');");
        await It.ExecAsync($"BACKUP LOG {It.Q(db.Name)} TO DISK = @f WITH INIT, NAME = N'IT_LOG';", ("@f", db.Name + "_sb_log.trn"));

        var sb = await Restore().RestoreToStandbyAsync(db.Name, target: null, It.Conn, It.NoProgress);
        Assert.Equal(RepairStatus.Success, sb.Status);
        Assert.Equal(4, await It.RowCountAsync(db.Name)); // consultável em STANDBY (somente leitura)

        var fin = await Restore().FinalizeRecoveryAsync(db.Name, It.Conn, It.NoProgress);
        Assert.Equal(RepairStatus.Success, fin.Status);
        Assert.Equal("ONLINE", await It.GetStateAsync(db.Name));
        await It.TryDeleteBackupFileAsync(db.Name + "_sb_log.trn");
    }

    [IntegrationFact]
    public async Task Striped_BackupEmDoisArquivos_AgrupaERestaura()
    {
        await using var db = await TestDatabase.CreateAsync("striped");
        await It.ExecAsync($"BACKUP DATABASE {It.Q(db.Name)} TO DISK = @a, DISK = @b WITH INIT, FORMAT, NAME = N'IT_STRIPE';",
            ("@a", db.Name + "_s1.bak"), ("@b", db.Name + "_s2.bak"));

        // O histórico agrupa os dois arquivos do mesmo conjunto (striped).
        var hist = await Restore().ListBackupHistoryAsync(db.Name, It.Conn);
        var full = hist.First(h => h.IsFull);
        Assert.True(full.AllDevices.Count >= 2);

        await It.ExecAsync($"EXEC(N'USE {It.Q(db.Name)}; DELETE FROM dbo.NotaFiscal;');");
        var r = await Restore().RestoreToPointInTimeAsync(db.Name, target: null, tailLogBackup: false, It.Conn, It.NoProgress);
        Assert.Equal(RepairStatus.Success, r.Status);
        Assert.Equal(3, await It.RowCountAsync(db.Name)); // restaurado a partir do conjunto striped

        await It.TryDeleteBackupFileAsync(db.Name + "_s1.bak");
        await It.TryDeleteBackupFileAsync(db.Name + "_s2.bak");
    }

    [IntegrationFact]
    public async Task Diagnostico_SaudeLogica_DetectaIndiceDesabilitado()
    {
        await using var db = await TestDatabase.CreateAsync("saudelog");
        await It.ExecAsync($"EXEC(N'USE {It.Q(db.Name)}; ALTER INDEX IX_NotaFiscal_Cliente ON dbo.NotaFiscal DISABLE;');");

        var diag = await Repair().DiagnoseAsync(db.AsInfo("ONLINE"), It.Conn, It.NoProgress);

        Assert.Contains(diag.Adjustments, a => a.Kind == HealthIssueKind.DisabledIndexes && a.CanFix);
    }

    [IntegrationFact]
    public async Task Diagnostico_RegistraHistorico_SemNovidadesQuandoEstavel()
    {
        await using var db = await TestDatabase.CreateAsync("hist");
        await Repair().DiagnoseAsync(db.AsInfo("ONLINE"), It.Conn, It.NoProgress);
        var d2 = await Repair().DiagnoseAsync(db.AsInfo("ONLINE"), It.Conn, It.NoProgress);

        // Banco saudável e estável: a 2ª verificação não acusa nada NOVO.
        Assert.Empty(d2.NewSinceLastRun);
    }

    [IntegrationFact]
    public async Task RecuperarParaCopia_CriaCopiaComDados_SemTocarNoOriginal()
    {
        await using var db = await TestDatabase.CreateAsync("copia");
        await db.BackupAsync();
        var copia = db.Name + "_Recuperado";
        try
        {
            var result = await Restore().RestoreAsCopyAsync(db.Name, null, It.Conn, It.NoProgress);

            Assert.Equal(RepairStatus.Success, result.Status);
            Assert.Equal("ONLINE", await It.GetStateAsync(copia));
            // A cópia tem os dados; o original continua intacto.
            var copiaRows = Convert.ToInt32(await It.ScalarAsync($"SELECT COUNT(*) FROM {It.Q(copia)}.dbo.NotaFiscal;"));
            Assert.Equal(3, copiaRows);
            Assert.Equal("ONLINE", await It.GetStateAsync(db.Name));
            Assert.Equal(3, await It.RowCountAsync(db.Name));
        }
        finally { await It.ForceDropAsync(copia); }
    }

    [IntegrationFact]
    public async Task Salvamento_CopiaLinhasFaltantesDeVoltaAoOriginal()
    {
        await using var db = await TestDatabase.CreateAsync("salv");
        await db.BackupAsync();
        var copia = db.Name + "_Recuperado";
        try
        {
            // Cópia recuperada com as 3 linhas originais.
            var copy = await Restore().RestoreAsCopyAsync(db.Name, null, It.Conn, It.NoProgress);
            Assert.Equal(RepairStatus.Success, copy.Status);

            // Perde uma linha no banco VIVO (id 2).
            await It.ExecAsync($"EXEC(N'USE {It.Q(db.Name)}; DELETE FROM dbo.NotaFiscal WHERE Id = 2;');");
            Assert.Equal(2, await It.RowCountAsync(db.Name));

            // Salvamento: copia da cópia para o vivo apenas a linha que falta (preservando o Id).
            var salv = await Restore().SalvageTableAsync(copia, db.Name, "dbo.NotaFiscal", It.Conn, It.NoProgress);
            Assert.Equal(RepairStatus.Success, salv.Status);

            Assert.Equal(3, await It.RowCountAsync(db.Name));
            var temId2 = Convert.ToInt32(await It.ScalarAsync($"SELECT COUNT(*) FROM {It.Q(db.Name)}.dbo.NotaFiscal WHERE Id = 2;"));
            Assert.Equal(1, temId2);
        }
        finally { await It.ForceDropAsync(copia); }
    }

    [IntegrationFact]
    public async Task SaudeDosBackups_VerifyOnly_RetornaIntegro()
    {
        await using var db = await TestDatabase.CreateAsync("saude");
        await db.BackupAsync();

        var items = await Restore().VerifyBackupHealthAsync(db.Name, It.Conn, It.NoProgress);

        Assert.NotEmpty(items);
        Assert.All(items, i => Assert.True(i.Verified));
    }

    [IntegrationFact]
    public async Task DiagnosticoRapido_PhysicalOnly_Executa()
    {
        await using var db = await TestDatabase.CreateAsync("phys");
        var diag = await Repair().DiagnoseAsync(db.AsInfo("ONLINE"), It.Conn, It.NoProgress, physicalOnly: true);

        Assert.NotNull(diag.CheckDb);
        Assert.True(diag.CheckDb!.Executed);
        Assert.False(diag.CheckDb.HasErrors);
    }

    [IntegrationFact]
    public async Task CheckTable_E_Reseed_Funcionam()
    {
        await using var db = await TestDatabase.CreateAsync("granular");

        var rep = await Repair().CheckTableAsync(db.AsInfo("ONLINE"), "dbo.NotaFiscal", It.Conn, It.NoProgress);
        Assert.True(rep.Executed);
        Assert.False(rep.HasErrors);

        var n = await Repair().ReseedIdentitiesAsync(db.AsInfo("ONLINE"), It.Conn, It.NoProgress);
        Assert.True(n >= 1); // NotaFiscal tem coluna IDENTITY
    }

    [IntegrationFact]
    public async Task TestarBackup_RestauraEmCopiaDescartavel_SemTocarNoOriginal()
    {
        await using var db = await TestDatabase.CreateAsync("testrestore");
        await db.BackupAsync();
        var linhasAntes = await It.RowCountAsync(db.Name);

        var history = await Restore().ListBackupHistoryAsync(db.Name, It.Conn);
        var full = history.First(b => b.IsFull);
        var pieces = new List<RestorePiece> { new() { DiskPath = full.PhysicalDevice, Position = full.Position } };

        var result = await Restore().TestRestoreAsync(db.Name, pieces, It.Conn, It.NoProgress);

        Assert.Equal(RepairStatus.Success, result.Status);                 // backup válido e restaurável
        Assert.Equal("ONLINE", await It.GetStateAsync(db.Name));           // original intacto
        Assert.Equal(linhasAntes, await It.RowCountAsync(db.Name));        // original não foi tocado
        // O banco de teste foi descartado (não fica lixo no servidor).
        var sobra = Convert.ToInt32(await It.ScalarAsync(
            "SELECT COUNT(*) FROM sys.databases WHERE name LIKE @p;", ("@p", db.Name + "_RestoreDB_TEST_%")));
        Assert.Equal(0, sobra);
    }

    [IntegrationFact]
    public async Task RestaurarCadeiaCompleta_MontaFullMaisLog_ERecupera()
    {
        await using var db = await TestDatabase.CreateAsync("pit");
        await It.ExecAsync($"ALTER DATABASE {It.Q(db.Name)} SET RECOVERY FULL;");
        await db.BackupAsync();                                            // FULL base

        // Gera mais dados e um backup de LOG, formando uma cadeia full → log.
        await It.ExecAsync($"EXEC(N'USE {It.Q(db.Name)}; INSERT INTO dbo.NotaFiscal(Cliente,Valor) VALUES (N''D'',40);');");
        await It.ExecAsync($"BACKUP LOG {It.Q(db.Name)} TO DISK = @f WITH INIT, NAME = N'IT_LOG';",
            ("@f", db.Name + "_log.trn"));

        var result = await Restore().RestoreToPointInTimeAsync(
            db.Name, target: null, tailLogBackup: true, It.Conn, It.NoProgress);

        Assert.Equal(RepairStatus.Success, result.Status);
        Assert.Equal("ONLINE", await It.GetStateAsync(db.Name));
        Assert.Equal(4, await It.RowCountAsync(db.Name));                  // 3 originais + 1 do log
        await It.TryDeleteBackupFileAsync(db.Name + "_log.trn");
    }

    [IntegrationFact]
    public async Task RestaurarCadeia_SemHistorico_Aborta()
    {
        await using var db = await TestDatabase.CreateAsync("nochain");
        // Sem nenhum backup: a montagem da cadeia deve abortar com falha clara.
        await It.TryPurgeBackupHistoryAsync(db.Name);

        var result = await Restore().RestoreToPointInTimeAsync(
            db.Name, target: null, tailLogBackup: false, It.Conn, It.NoProgress);

        Assert.Equal(RepairStatus.Failed, result.Status);
        Assert.Equal("ONLINE", await It.GetStateAsync(db.Name));           // nada foi alterado
    }

    [IntegrationFact]
    public async Task Diagnostico_AvaliaPoliticaDeBackup_FullSemLog()
    {
        await using var db = await TestDatabase.CreateAsync("policy");
        await It.ExecAsync($"ALTER DATABASE {It.Q(db.Name)} SET RECOVERY FULL;");
        await db.BackupAsync(); // FULL, mas nenhum backup de LOG

        var diag = await Repair().DiagnoseAsync(db.AsInfo("ONLINE"), It.Conn, It.NoProgress);

        Assert.NotNull(diag.BackupPolicy);
        Assert.Equal("FULL", diag.BackupPolicy!.RecoveryModel);
        Assert.Contains(diag.BackupPolicy.Findings, f => f.Message.Contains("LOG"));
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

        await using var s = await new SqlDataAccess().OpenSessionAsync(It.Conn);
        var files = await s.RestoreFileListOnlyAsync(path, sets.First().Position);
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

        var settings = await s.GetDatabaseSettingsAsync(db.Name);
        Assert.False(string.IsNullOrEmpty(settings?.PageVerify));

        var disks = await s.GetDiskSpaceAsync(db.Name);
        Assert.NotEmpty(disks);
    }
}
