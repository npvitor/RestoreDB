using Microsoft.Data.SqlClient;
using RestoreDB.Domain.Constants;

namespace RestoreDB.IntegrationTests;

/// <summary>
/// Corrupção REAL para os testes de ponta a ponta: a tabela dbo.Item (2000 linhas de ~420 bytes, ~18 por página,
/// índice clusterizado em Id + não-clusterizado IX_Item_Cliente), páginas localizadas por DBCC IND (funciona do
/// 2008 R2 ao 2025) e alteradas com DBCC WRITEPAGE — direto no disco (checksum inválido → erro 824) ou pelo
/// buffer (checksum recalculado → inconsistência lógica que só o CHECKDB vê).
/// </summary>
internal static class Corruption
{
    public const int Rows = 2000;

    public static Task InDbAsync(string db, string sql)
        => It.ExecAsync("DECLARE @p nvarchar(300) = QUOTENAME(@db) + N'.sys.sp_executesql'; EXEC @p @s;", ("@db", db), ("@s", sql));

    public static async Task<object?> InDbScalarAsync(string db, string sql)
    {
        await using var c = new SqlConnection(It.Conn);
        await c.OpenAsync();
        c.ChangeDatabase(db);
        await using var cmd = new SqlCommand(sql, c) { CommandTimeout = 0 };
        try { return await cmd.ExecuteScalarAsync(); }
        // A conexão volta ao pool ainda "dentro" do banco e disputaria a vaga de um SINGLE_USER posterior.
        finally { if (c.State == System.Data.ConnectionState.Open) c.ChangeDatabase("master"); }
    }

    public static async Task CreateItemTableAsync(string db, string recovery)
    {
        await It.ExecAsync($"ALTER DATABASE {It.Q(db)} SET RECOVERY {recovery}; ALTER DATABASE {It.Q(db)} SET PAGE_VERIFY CHECKSUM;");
        await InDbAsync(db,
            "CREATE TABLE dbo.Item(Id int NOT NULL PRIMARY KEY CLUSTERED, Cliente nvarchar(50) NOT NULL, Pad char(400) NOT NULL DEFAULT 'x'); " +
            "CREATE INDEX IX_Item_Cliente ON dbo.Item(Cliente); " +
            $"INSERT dbo.Item(Id, Cliente) SELECT TOP ({Rows}) n, N'Cliente ' + CAST(n AS nvarchar(10)) FROM " +
            "(SELECT ROW_NUMBER() OVER (ORDER BY (SELECT 1)) n FROM sys.all_objects a CROSS JOIN sys.all_objects b) x; CHECKPOINT;");
    }

    /// <summary>Linhas de dbo.Item lidas por um índice escolhido (1 = clusterizado: passa por TODAS as páginas de dados).</summary>
    public static async Task<int> CountItemsAsync(string db, string index = "1")
        => Convert.ToInt32(await InDbScalarAsync(db, $"SELECT COUNT(*) FROM dbo.Item WITH (INDEX({index}));"));

    /// <summary>Página de dados (arquivo, página) onde está a linha <paramref name="id"/> e quantas linhas ela guarda.</summary>
    public static async Task<(int File, int Page, int RowsOnPage)> DataPageOfAsync(string db, int id)
    {
        var loc = (string)(await InDbScalarAsync(db, $"SELECT sys.fn_PhysLocFormatter(%%physloc%%) FROM dbo.Item WHERE Id = {id};"))!;
        var parts = loc.Trim('(', ')').Split(':');
        var (file, page) = (int.Parse(parts[0]), int.Parse(parts[1]));
        var onPage = Convert.ToInt32(await InDbScalarAsync(db,
            $"SELECT COUNT(*) FROM dbo.Item WHERE sys.fn_PhysLocFormatter(%%physloc%%) LIKE '({file}:{page}:%';"));
        return (file, page, onPage);
    }

    /// <summary>Uma página de índice de dbo.Item (DBCC IND): <paramref name="indexId"/> 1 = clusterizado, 2 = IX_Item_Cliente.</summary>
    public static async Task<int> IndexPageAsync(string db, int indexId, int level)
    {
        var sql =
            "CREATE TABLE #ind(PageFID int, PagePID int, IAMFID int, IAMPID int, ObjectID int, IndexID int, PartitionNumber int, PartitionID bigint, " +
            "iam_chain_type varchar(50), PageType int, IndexLevel int, NextPageFID int, NextPagePID int, PrevPageFID int, PrevPagePID int); " +
            "DECLARE @c nvarchar(400) = N'DBCC IND (' + QUOTENAME(@db, '''') + N', ''dbo.Item'', ' + CAST(@ix AS nvarchar(10)) + N') WITH NO_INFOMSGS'; " +
            "INSERT #ind EXEC (@c); " +
            "SELECT MIN(PagePID) FROM #ind WHERE IndexID = @ix AND PageType = 2 AND IndexLevel = @lvl;";
        await using var c = new SqlConnection(It.Conn);
        await c.OpenAsync();
        await using var cmd = new SqlCommand(sql, c) { CommandTimeout = 0 };
        cmd.Parameters.AddWithValue("@db", db);
        cmd.Parameters.AddWithValue("@ix", indexId);
        cmd.Parameters.AddWithValue("@lvl", level);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static string Literal(string db) => "N'" + db.Replace("'", "''") + "'";

    /// <summary>Lixo DIRETO no arquivo (checksum inválido) + descarte do cache: a próxima leitura da página dá 824.</summary>
    public static Task WriteGarbageOnDiskAsync(string db, int file, int page)
        => It.ExecAsync(
            $"ALTER DATABASE {It.Q(db)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
            $"DBCC WRITEPAGE ({Literal(db)}, {file}, {page}, 2000, 4, 0x41414141, 1); " +
            $"ALTER DATABASE {It.Q(db)} SET MULTI_USER; " +
            $"ALTER DATABASE {It.Q(db)} SET OFFLINE WITH ROLLBACK IMMEDIATE; ALTER DATABASE {It.Q(db)} SET ONLINE;");

    /// <summary>Altera bytes pelo buffer (checksum recalculado): a página lê normalmente, mas o conteúdo fica inconsistente.</summary>
    public static Task WriteBytesAsync(string db, int file, int page, int offset, string hex)
        => It.ExecAsync(
            $"ALTER DATABASE {It.Q(db)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
            $"DBCC WRITEPAGE ({Literal(db)}, {file}, {page}, {offset}, {(hex.Length - 2) / 2}, {hex}); " +
            $"ALTER DATABASE {It.Q(db)} SET MULTI_USER;");

    public static async Task<bool> CheckDbCleanAsync(string db)
    {
        try { await It.ExecAsync($"DBCC CHECKDB ({It.Q(db)}) WITH NO_INFOMSGS;"); return true; }
        catch (SqlException) { return false; }
    }

    /// <summary>
    /// Deixa o banco SUSPECT como acontece de verdade: uma transação altera a linha 1000, a página vai ao disco
    /// (CHECKPOINT), o disco a corrompe e o ROLLBACK precisa relê-la → 824 durante o desfazer (Msg 3314) e o
    /// SQL Server reinicia o banco, cuja recuperação falha. Num banco FULL com backup, as versões novas
    /// (Enterprise/Developer) adiam a transação e deixam o banco ONLINE em vez de SUSPECT. Devolve a página
    /// danificada e quantas linhas ela guarda. O DBCC DROPCLEANBUFFERS vale para a instância toda (só esvazia cache).
    /// </summary>
    public static async Task<(int File, int Page, int RowsOnPage)> MakeSuspectAsync(string db, int id = 1000)
    {
        var where = await DataPageOfAsync(db, id);
        var conn = new SqlConnectionStringBuilder(It.Conn) { Pooling = false }.ConnectionString;
        await using (var c = new SqlConnection(conn))
        {
            await c.OpenAsync();
            var sql =
                $"ALTER DATABASE {It.Q(db)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; USE {It.Q(db)}; " +
                $"BEGIN TRAN; UPDATE dbo.Item SET Pad = 'y' WHERE Id = {id}; CHECKPOINT; " +
                $"DBCC WRITEPAGE ({Literal(db)}, {where.File}, {where.Page}, 2000, 4, 0x41414141, 1); " +
                "DBCC DROPCLEANBUFFERS; ROLLBACK;";
            try
            {
                await using var cmd = new SqlCommand(sql, c) { CommandTimeout = 0 };
                await cmd.ExecuteNonQueryAsync();
            }
            catch (SqlException) { /* esperado: 824 + 3314, a conexão cai */ }
        }

        // O banco reinicia em segundo plano. Um ALTER DATABASE num banco SUSPECT o reinicia de novo (ele aparece
        // ONLINE por alguns segundos e volta a SUSPECT) — por isso a espera vem antes e depois do MULTI_USER.
        await WaitSettledStateAsync(db);
        try { await It.ExecAsync($"ALTER DATABASE {It.Q(db)} SET MULTI_USER;"); } catch (SqlException) { /* best-effort */ }
        await WaitSettledStateAsync(db);
        return where;
    }

    /// <summary>
    /// Espera, SEM entrar no banco (uma sessão lá dentro muda o desfecho do reinício), o estado ficar igual por 8 s:
    /// SUSPECT, ou ONLINE (nas versões novas, com a transação adiada). Devolve o estado estável.
    /// </summary>
    public static async Task<string> WaitSettledStateAsync(string db)
    {
        var last = string.Empty;
        var stableSince = DateTime.UtcNow;
        for (var i = 0; i < 480; i++)
        {
            var state = await It.GetStateAsync(db);
            if (state != last) { last = state; stableSince = DateTime.UtcNow; }
            else if (state is not ("RECOVERING" or "RECOVERY_PENDING") && DateTime.UtcNow - stableSince > TimeSpan.FromSeconds(8)) break;
            await Task.Delay(250);
        }
        return last;
    }

    /// <summary>Descarta os bancos derivados que a ferramenta cria para um banco de teste e o histórico de páginas suspeitas.</summary>
    public static async Task CleanupAsync(string db)
    {
        try
        {
            await It.ExecAsync("DELETE FROM msdb.dbo.suspect_pages WHERE database_id = DB_ID(@n);", ("@n", db));
            var token = AppConstants.SanitizeToken(db, "banco");
            var names = new List<string>();
            await using (var c = new SqlConnection(It.Conn))
            {
                await c.OpenAsync();
                foreach (var suffix in new[] { "_Salvamento_", "_LostRows_", "_SimRepair_", "_OrphanRecovery_", "_AntesExclusao_", "_RestoreDB_TEST_" })
                {
                    await using var cmd = new SqlCommand("SELECT name FROM sys.databases WHERE name LIKE @p ESCAPE '\\';", c);
                    cmd.Parameters.AddWithValue("@p", (token + suffix).Replace("_", "\\_") + "%");
                    await using var r = await cmd.ExecuteReaderAsync();
                    while (await r.ReadAsync()) names.Add(r.GetString(0));
                }
            }
            foreach (var n in names) await It.ForceDropAsync(n);
        }
        catch { /* best-effort */ }
    }

    /// <summary>Quantos bancos derivados (cópias de salvamento/recuperação/simulação) ainda existem para o banco de teste.</summary>
    public static async Task<int> LeftoverCopiesAsync(string db)
    {
        var token = AppConstants.SanitizeToken(db, "banco").Replace("_", "\\_");
        return Convert.ToInt32(await It.ScalarAsync(
            "SELECT COUNT(*) FROM sys.databases WHERE name LIKE @p ESCAPE '\\' AND name <> @n;", ("@p", token + "\\_%"), ("@n", db)));
    }
}
