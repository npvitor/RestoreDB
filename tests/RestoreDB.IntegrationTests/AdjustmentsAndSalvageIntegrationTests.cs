using Microsoft.Data.SqlClient;
using RestoreDB.DataAccess;
using RestoreDB.Domain.Models;
using RestoreDB.Services;
using Xunit;

namespace RestoreDB.IntegrationTests;

/// <summary>
/// Ajustes recomendados, acesso travado e salvamento sem backup — contra uma instância SQL real.
/// O salvamento corrompe de verdade uma página de dados (DBCC WRITEPAGE) num banco RestoreDB_IT_* próprio.
/// </summary>
public sealed class AdjustmentsAndSalvageIntegrationTests
{
    private static RepairService Repair()
    {
        var log = new LogService();
        return new(log, new RestoreService(log));
    }

    private static Task InDbAsync(string db, string sql)
        => It.ExecAsync($"DECLARE @p nvarchar(300) = QUOTENAME(@db) + N'.sys.sp_executesql'; EXEC @p @s;", ("@db", db), ("@s", sql));

    private static async Task<object?> InDbScalarAsync(string db, string sql)
    {
        await using var c = new SqlConnection(It.Conn);
        await c.OpenAsync();
        c.ChangeDatabase(db);
        await using var cmd = new SqlCommand(sql, c) { CommandTimeout = 0 };
        try { return await cmd.ExecuteScalarAsync(); }
        // A conexão volta ao pool ainda "dentro" do banco e disputaria a vaga de um SINGLE_USER posterior.
        finally { if (c.State == System.Data.ConnectionState.Open) c.ChangeDatabase("master"); }
    }

    // ------------------------------------------------------------------ Ajustes recomendados

    [IntegrationFact]
    public async Task Ajustes_DetectaEAplicaSemAlterarDados()
    {
        await using var db = await TestDatabase.CreateAsync("ajustes");
        await It.ExecAsync($"ALTER DATABASE {It.Q(db.Name)} SET PAGE_VERIFY NONE; ALTER DATABASE {It.Q(db.Name)} SET AUTO_SHRINK ON; ALTER DATABASE {It.Q(db.Name)} SET AUTO_CLOSE ON;");
        var dataFile = (string)(await It.ScalarAsync("SELECT name FROM sys.master_files WHERE database_id = DB_ID(@n) AND type = 0;", ("@n", db.Name)))!;
        await It.ExecAsync($"ALTER DATABASE {It.Q(db.Name)} MODIFY FILE (NAME = {It.Q(dataFile)}, FILEGROWTH = 10%);");
        await InDbAsync(db.Name,
            "CREATE TABLE dbo.Cliente(Id int PRIMARY KEY); INSERT dbo.Cliente VALUES (1),(2),(3); " +
            "ALTER TABLE dbo.NotaFiscal ADD ClienteId int NULL; " +
            "ALTER TABLE dbo.NotaFiscal WITH NOCHECK ADD CONSTRAINT FK_NF_Cliente FOREIGN KEY (ClienteId) REFERENCES dbo.Cliente(Id); " +
            "ALTER INDEX IX_NotaFiscal_Cliente ON dbo.NotaFiscal DISABLE;");

        var diag = await Repair().DiagnoseAsync(db.AsInfo("ONLINE"), It.Conn, It.NoProgress);
        var kinds = diag.Adjustments.Select(a => a.Kind).ToList();
        foreach (var k in new[] { HealthIssueKind.PageVerify, HealthIssueKind.AutoShrink, HealthIssueKind.AutoClose,
                                   HealthIssueKind.FileGrowth, HealthIssueKind.UntrustedConstraints, HealthIssueKind.DisabledIndexes })
            Assert.Contains(k, kinds);
        Assert.Equal(RecommendedActionKind.NoAction, diag.RecommendedAction); // não é corrupção

        var rowsBefore = await It.RowCountAsync(db.Name);
        var r = await new AdjustmentService(new LogService()).ApplyAsync(db.AsInfo("ONLINE"),
            diag.Adjustments.Where(a => a.CanFix).Select(a => a.Kind).ToList(), It.Conn, It.NoProgress);
        Assert.Equal(RepairStatus.Success, r.Status);

        Assert.Equal("CHECKSUM", await It.ScalarAsync("SELECT page_verify_option_desc FROM sys.databases WHERE name = @n;", ("@n", db.Name)));
        Assert.Equal(false, await It.ScalarAsync("SELECT CAST(is_auto_shrink_on AS bit) FROM sys.databases WHERE name = @n;", ("@n", db.Name)));
        Assert.Equal(false, await It.ScalarAsync("SELECT CAST(is_auto_close_on AS bit) FROM sys.databases WHERE name = @n;", ("@n", db.Name)));
        Assert.Equal(false, await It.ScalarAsync("SELECT CAST(is_percent_growth AS bit) FROM sys.master_files WHERE database_id = DB_ID(@n) AND type = 0;", ("@n", db.Name)));
        Assert.Equal(0, Convert.ToInt32(await InDbScalarAsync(db.Name, "SELECT COUNT(*) FROM sys.indexes WHERE is_disabled = 1;")));
        Assert.Equal(0, Convert.ToInt32(await InDbScalarAsync(db.Name, "SELECT COUNT(*) FROM sys.foreign_keys WHERE is_not_trusted = 1;")));
        Assert.Equal(rowsBefore, await It.RowCountAsync(db.Name));

        var again = await Repair().DiagnoseAsync(db.AsInfo("ONLINE"), It.Conn, It.NoProgress);
        Assert.DoesNotContain(again.Adjustments, a => a.CanFix);
    }

    [IntegrationFact]
    public async Task Ajustes_RegraComDadosViolando_FicaPendenteSemApagarNada()
    {
        await using var db = await TestDatabase.CreateAsync("ajusteviol");
        await InDbAsync(db.Name,
            "CREATE TABLE dbo.Cliente(Id int PRIMARY KEY); INSERT dbo.Cliente VALUES (1); ALTER TABLE dbo.NotaFiscal ADD ClienteId int NULL;");
        await InDbAsync(db.Name,
            "UPDATE dbo.NotaFiscal SET ClienteId = 99; " +
            "ALTER TABLE dbo.NotaFiscal WITH NOCHECK ADD CONSTRAINT FK_NF_Cliente FOREIGN KEY (ClienteId) REFERENCES dbo.Cliente(Id);");

        var r = await new AdjustmentService(new LogService()).ApplyAsync(db.AsInfo("ONLINE"),
            new[] { HealthIssueKind.UntrustedConstraints }, It.Conn, It.NoProgress);
        Assert.Contains("violam", r.Message);
        Assert.Equal(3, await It.RowCountAsync(db.Name));
    }

    [IntegrationFact]
    public async Task Ajustes_LogFragmentado_ReorganizaSemPerda()
    {
        await using var db = await TestDatabase.CreateAsync("vlf");
        await It.ExecAsync($"ALTER DATABASE {It.Q(db.Name)} SET RECOVERY SIMPLE;");
        var logFile = (string)(await It.ScalarAsync("SELECT name FROM sys.master_files WHERE database_id = DB_ID(@n) AND type = 1;", ("@n", db.Name)))!;
        var sizeMb = Convert.ToInt32(await It.ScalarAsync("SELECT size * 8 / 1024 FROM sys.master_files WHERE database_id = DB_ID(@n) AND type = 1;", ("@n", db.Name)));
        // Cresce o log de 1 em 1 MB, 1100 vezes: cada crescimento cria ao menos 1 VLF (4 no 2008 R2).
        await It.ExecAsync(
            "DECLARE @i int = @start + 1, @c nvarchar(max); WHILE @i <= @start + 1100 BEGIN " +
            "SET @c = N'ALTER DATABASE ' + QUOTENAME(@db) + N' MODIFY FILE (NAME = ' + QUOTENAME(@f, '''') + N', SIZE = ' + CAST(@i AS nvarchar(10)) + N'MB);'; " +
            "EXEC(@c); SET @i += 1; END",
            ("@db", db.Name), ("@f", logFile), ("@start", Math.Max(sizeMb, 1)));

        await using var s = await new SqlDataAccess().OpenSessionAsync(It.Conn);
        var before = await s.CountVlfsAsync(db.Name);
        Assert.True(before > HealthEvaluator.VlfWarnThreshold, $"preparação: {before} VLFs");

        var r = await new AdjustmentService(new LogService()).ApplyAsync(db.AsInfo("ONLINE"),
            new[] { HealthIssueKind.TooManyVlfs }, It.Conn, It.NoProgress);
        var after = await s.CountVlfsAsync(db.Name);
        Assert.True(after < 100, $"VLFs {before} → {after}: {r.Message}");
        Assert.Equal(3, await It.RowCountAsync(db.Name));
    }

    [IntegrationFact]
    public async Task Ajustes_UsuarioOrfao_ReligaAoLogin()
    {
        await using var db = await TestDatabase.CreateAsync("orfao");
        var login = "RestoreDB_IT_login_" + Guid.NewGuid().ToString("N")[..8];
        var pwd = "It#" + Guid.NewGuid().ToString("N");
        try
        {
            await It.ExecAsync($"CREATE LOGIN {It.Q(login)} WITH PASSWORD = N'{pwd}', CHECK_POLICY = OFF;");
            await InDbAsync(db.Name, $"CREATE USER {It.Q(login)} FOR LOGIN {It.Q(login)};");
            // Recria o login: mesmo nome, SID novo — o usuário fica órfão (como ao mover o banco de servidor).
            await It.ExecAsync($"DROP LOGIN {It.Q(login)}; CREATE LOGIN {It.Q(login)} WITH PASSWORD = N'{pwd}', CHECK_POLICY = OFF;");

            var diag = await Repair().DiagnoseAsync(db.AsInfo("ONLINE"), It.Conn, It.NoProgress);
            var issue = Assert.Single(diag.Adjustments, a => a.Kind == HealthIssueKind.OrphanedUsers);
            Assert.True(issue.CanFix);

            await new AdjustmentService(new LogService()).ApplyAsync(db.AsInfo("ONLINE"), new[] { HealthIssueKind.OrphanedUsers }, It.Conn, It.NoProgress);
            var orphans = Convert.ToInt32(await InDbScalarAsync(db.Name,
                $"SELECT COUNT(*) FROM sys.database_principals dp WHERE dp.name = N'{login}' AND NOT EXISTS (SELECT 1 FROM sys.server_principals sp WHERE sp.sid = dp.sid);"));
            Assert.Equal(0, orphans);
        }
        finally
        {
            await It.ForceDropAsync(db.Name);
            try { await It.ExecAsync($"IF SUSER_ID(N'{login}') IS NOT NULL DROP LOGIN {It.Q(login)};"); } catch { }
        }
    }

    // ------------------------------------------------------------------ Contadores / verificação em partes

    [IntegrationFact]
    public async Task UpdateUsage_E_VerificacaoEmPartes_Funcionam()
    {
        await using var db = await TestDatabase.CreateAsync("partes");
        var log = new LogService();
        var u = await new MaintenanceService(log).UpdateUsageAsync(db.AsInfo("ONLINE"), It.Conn, It.NoProgress);
        Assert.Equal(RepairStatus.Success, u.Status);

        var dir = Path.Combine(Path.GetTempPath(), "RestoreDB_IT_Inc_" + Guid.NewGuid().ToString("N"));
        try
        {
            var inc = new IncrementalCheckService(log, dir);
            var o = await inc.RunAsync(db.AsInfo("ONLINE"), It.Conn, TimeSpan.FromMinutes(5), It.NoProgress);
            Assert.True(o.CycleCompleted);
            Assert.Empty(o.Errors);
            Assert.Equal(0, inc.Load(db.Name).NextIndex);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ------------------------------------------------------------------ Salvamento sem backup (corrupção real)

    /// <summary>
    /// Banco SIMPLE sem backup com 2000 linhas e UMA página de dados corrompida no disco (checksum inválido →
    /// erro 824 ao ler). Devolve o nome da tabela e quantas linhas estavam na página destruída.
    /// </summary>
    private static async Task<int> CreateCorruptedTableAsync(string dbName)
    {
        await It.ExecAsync($"ALTER DATABASE {It.Q(dbName)} SET RECOVERY SIMPLE; ALTER DATABASE {It.Q(dbName)} SET PAGE_VERIFY CHECKSUM;");
        await InDbAsync(dbName,
            "CREATE TABLE dbo.Item(Id int NOT NULL PRIMARY KEY CLUSTERED, Cliente nvarchar(50) NOT NULL, Pad char(400) NOT NULL DEFAULT 'x'); " +
            "CREATE INDEX IX_Item_Cliente ON dbo.Item(Cliente); " +
            "INSERT dbo.Item(Id, Cliente) SELECT TOP (2000) n, N'Cliente ' + CAST(n AS nvarchar(10)) FROM " +
            "(SELECT ROW_NUMBER() OVER (ORDER BY (SELECT 1)) n FROM sys.all_objects a CROSS JOIN sys.all_objects b) x; CHECKPOINT;");

        var loc = (string)(await InDbScalarAsync(dbName, "SELECT sys.fn_PhysLocFormatter(%%physloc%%) FROM dbo.Item WHERE Id = 1000;"))!;
        var parts = loc.Trim('(', ')').Split(':');
        var file = int.Parse(parts[0]); var page = int.Parse(parts[1]);
        var onPage = Convert.ToInt32(await InDbScalarAsync(dbName,
            $"SELECT COUNT(*) FROM dbo.Item WHERE sys.fn_PhysLocFormatter(%%physloc%%) LIKE '({file}:{page}:%';"));

        // Escreve lixo direto no arquivo (sem passar pelo buffer) e descarta o cache: a próxima leitura dá 824.
        await It.ExecAsync(
            $"ALTER DATABASE {It.Q(dbName)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
            $"DBCC WRITEPAGE ({It.Q(dbName).Replace("[", "'").Replace("]", "'")}, {file}, {page}, 2000, 4, 0x41414141, 1); " +
            $"ALTER DATABASE {It.Q(dbName)} SET MULTI_USER; " +
            $"ALTER DATABASE {It.Q(dbName)} SET OFFLINE WITH ROLLBACK IMMEDIATE; ALTER DATABASE {It.Q(dbName)} SET ONLINE;");
        return onPage;
    }

    private static async Task CleanupSalvageAsync(string dbName)
    {
        try
        {
            await It.ExecAsync("DELETE FROM msdb.dbo.suspect_pages WHERE database_id = DB_ID(@n);", ("@n", dbName));
            await using var c = new SqlConnection(It.Conn);
            await c.OpenAsync();
            var names = new List<string>();
            await using (var cmd = new SqlCommand("SELECT name FROM sys.databases WHERE name LIKE @p;", c))
            {
                cmd.Parameters.AddWithValue("@p", dbName + "_Salvamento_%");
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync()) names.Add(r.GetString(0));
            }
            foreach (var n in names) await It.ForceDropAsync(n);
        }
        catch { /* best-effort */ }
    }

    [IntegrationFact]
    public async Task Salvamento_ExtraiLegiveisPulaPaginaRuim_EDevolveOQueFaltar()
    {
        await using var db = await TestDatabase.CreateAsync("salvext", withData: false);
        try
        {
            var onPage = await CreateCorruptedTableAsync(db.Name);
            Assert.InRange(onPage, 1, 40);

            await using var s = await new SqlDataAccess().OpenSessionAsync(It.Conn);
            var rowsBefore = await s.GetTableRowCountsAsync(db.Name);
            var extractor = new PreRepairExtractor((_, _) => { });
            var ex = await extractor.ExtractAsync(s, db.Name, new[] { "dbo.Item" }, rowsBefore, CancellationToken.None);

            var t = Assert.Single(ex.Tables);
            Assert.Equal(ExtractionMode.KeyRanges, t.Mode);
            Assert.Equal(2000 - onPage, t.RowsExtracted);       // tudo menos a página destruída
            Assert.NotEmpty(t.UnreadableRanges);
            Assert.Equal(onPage, t.PartialRows);                 // as linhas da página ruim, parciais, pelo índice IX_Item_Cliente

            // Reparo de verdade (descarta a página destruída) e, para simular um reparo que levou também linhas
            // LEGÍVEIS, remove Id 1..100: a cópia devolve exatamente essas — e nada da página destruída.
            // O reparo relata os erros que encontrou como mensagens de erro (exceção no cliente) — esperado.
            try
            {
                await It.ExecAsync(
                    $"ALTER DATABASE {It.Q(db.Name)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
                    $"DBCC CHECKDB ({It.Q(db.Name)}, REPAIR_ALLOW_DATA_LOSS) WITH NO_INFOMSGS;");
            }
            catch (SqlException) { }
            await It.ExecAsync($"ALTER DATABASE {It.Q(db.Name)} SET MULTI_USER;");
            await InDbAsync(db.Name, "DELETE FROM dbo.Item WHERE Id BETWEEN 1 AND 100;");
            var (returned, notReturned) = await extractor.ReturnRowsAsync(s, db.Name, ex, new[] { "dbo.Item" }, CancellationToken.None);
            Assert.Equal(100, returned);
            Assert.Empty(notReturned);
            Assert.Equal(100, Convert.ToInt32(await InDbScalarAsync(db.Name, "SELECT COUNT(*) FROM dbo.Item WHERE Id BETWEEN 1 AND 100;")));
            Assert.Equal(2000 - onPage, Convert.ToInt32(await InDbScalarAsync(db.Name, "SELECT COUNT(*) FROM dbo.Item;")));
        }
        finally { await CleanupSalvageAsync(db.Name); }
    }

    [IntegrationFact]
    public async Task ReparoComPerda_SemBackup_PerdeSoAPaginaDestruida_EGuardaParciais()
    {
        await using var db = await TestDatabase.CreateAsync("salvrep", withData: false);
        try
        {
            var onPage = await CreateCorruptedTableAsync(db.Name);

            var r = await Repair().RepairAsync(db.AsInfo("ONLINE"), It.Conn, allowDataLoss: true, It.NoProgress,
                recovery: new ParentRecoveryOptions { RecoverLostRows = true });

            Assert.Equal(RepairStatus.Success, r.Status);
            var remaining = Convert.ToInt32(await InDbScalarAsync(db.Name, "SELECT COUNT(*) FROM dbo.Item;"));
            Assert.Equal(2000 - onPage, remaining);
            Assert.Contains("não puderam ser recuperadas", r.Message);

            // As linhas da página destruída sobrevivem PARCIALMENTE (Id + Cliente) na cópia mantida para revisão.
            Assert.False(string.IsNullOrEmpty(r.SalvageDatabase));
            var partial = Convert.ToInt32(await It.ScalarAsync($"SELECT COUNT(*) FROM {It.Q(r.SalvageDatabase!)}.dbo.[Item__parcial];"));
            Assert.Equal(onPage, partial);
        }
        finally { await CleanupSalvageAsync(db.Name); }
    }
}
