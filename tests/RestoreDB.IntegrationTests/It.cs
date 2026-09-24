using Microsoft.Data.SqlClient;
using RestoreDB.DataAccess;
using RestoreDB.Domain.Models;
using Xunit;

namespace RestoreDB.IntegrationTests;

/// <summary>
/// Marca um teste que só roda quando há uma instância SQL configurada via variáveis de ambiente
/// (RESTOREDB_IT_SERVER / _USER / _PASS). Sem elas, o teste é IGNORADO — assim o `dotnet test`
/// padrão (sem banco) continua verde.
/// </summary>
public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RESTOREDB_IT_SERVER")))
            Skip = "Defina RESTOREDB_IT_SERVER/_USER/_PASS para executar os testes de integração.";
    }
}

/// <summary>Acesso à instância de teste + utilitários de setup/teardown (sempre via master).</summary>
internal static class It
{
    public static string Server => Environment.GetEnvironmentVariable("RESTOREDB_IT_SERVER") ?? "localhost";
    public static string User => Environment.GetEnvironmentVariable("RESTOREDB_IT_USER") ?? "sa";
    public static string Pass => Environment.GetEnvironmentVariable("RESTOREDB_IT_PASS") ?? string.Empty;

    /// <summary>Usa autenticação do Windows quando RESTOREDB_IT_USE_WINDOWS=1 ou quando não há usuário.</summary>
    public static bool UseWindowsAuth =>
        Environment.GetEnvironmentVariable("RESTOREDB_IT_USE_WINDOWS") == "1"
        || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RESTOREDB_IT_USER"));

    public static ConnectionInfo Info => UseWindowsAuth
        ? new() { Server = Server, UseWindowsAuth = true }
        : new() { Server = Server, User = User, Password = Pass };
    public static string Conn => new SqlDataAccess().BuildConnectionString(Info);

    /// <summary>Progresso no-op para passar aos serviços (eles relatam via IProgress).</summary>
    public static IProgress<RepairLogEntry> NoProgress { get; } = new Progress<RepairLogEntry>(_ => { });

    public static string Q(string name) => "[" + name.Replace("]", "]]") + "]";

    public static async Task ExecAsync(string sql, params (string name, object? val)[] ps)
    {
        await using var c = new SqlConnection(Conn);
        await c.OpenAsync();
        await using var cmd = new SqlCommand(sql, c) { CommandTimeout = 0 };
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<object?> ScalarAsync(string sql, params (string name, object? val)[] ps)
    {
        await using var c = new SqlConnection(Conn);
        await c.OpenAsync();
        await using var cmd = new SqlCommand(sql, c) { CommandTimeout = 0 };
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return await cmd.ExecuteScalarAsync();
    }

    public static async Task<string> GetStateAsync(string db)
        => (await ScalarAsync("SELECT state_desc FROM sys.databases WHERE name = @n;", ("@n", db))) as string ?? "DESCONHECIDO";

    public static async Task<int> RowCountAsync(string db)
        => Convert.ToInt32(await ScalarAsync($"SELECT COUNT(*) FROM {Q(db)}.dbo.NotaFiscal;"));

    public static async Task ForceDropAsync(string name)
    {
        const string sql = @"
IF DB_ID(@n) IS NOT NULL
BEGIN
  DECLARE @c nvarchar(max);
  BEGIN TRY SET @c = N'ALTER DATABASE ' + QUOTENAME(@n) + N' SET ONLINE;'; EXEC(@c); END TRY BEGIN CATCH END CATCH
  BEGIN TRY SET @c = N'ALTER DATABASE ' + QUOTENAME(@n) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE;'; EXEC(@c); END TRY BEGIN CATCH END CATCH
  BEGIN TRY SET @c = N'DROP DATABASE ' + QUOTENAME(@n) + N';'; EXEC(@c); END TRY BEGIN CATCH END CATCH
END";
        await ExecAsync(sql, ("@n", name));
    }

    /// <summary>Apaga um arquivo de backup pela própria instância (best-effort).</summary>
    public static async Task TryDeleteBackupFileAsync(string path)
    {
        try { await ExecAsync("EXEC master.sys.xp_delete_files @p;", ("@p", path)); } catch { /* best-effort */ }
    }

    /// <summary>Remove o histórico de backup do banco em msdb (best-effort).</summary>
    public static async Task TryPurgeBackupHistoryAsync(string name)
    {
        try { await ExecAsync("EXEC msdb.dbo.sp_delete_database_backuphistory @database_name = @n;", ("@n", name)); }
        catch { /* best-effort */ }
    }
}

/// <summary>Cria um banco de teste (com tabela + índice + linhas) e o derruba no Dispose.</summary>
internal sealed class TestDatabase : IAsyncDisposable
{
    public string Name { get; }
    private readonly List<string> _backups = new();

    private TestDatabase(string name) => Name = name;

    public static async Task<TestDatabase> CreateAsync(string suffix, bool withData = true)
    {
        var name = "RestoreDB_IT_" + suffix;
        await It.ForceDropAsync(name);
        await It.ExecAsync($"CREATE DATABASE {It.Q(name)};");
        if (withData)
        {
            var inner =
                $"USE {It.Q(name)}; " +
                "CREATE TABLE dbo.NotaFiscal(Id int IDENTITY PRIMARY KEY, Cliente nvarchar(100) NOT NULL, Valor decimal(18,2) NOT NULL); " +
                "CREATE INDEX IX_NotaFiscal_Cliente ON dbo.NotaFiscal(Cliente); " +
                "INSERT INTO dbo.NotaFiscal(Cliente,Valor) VALUES (N''A'',10),(N''B'',20),(N''C'',30);";
            await It.ExecAsync($"EXEC(N'{inner}');");
        }
        return new TestDatabase(name);
    }

    /// <summary>Backup completo para a pasta padrão de backups da instância. Devolve o caminho físico.</summary>
    public async Task<string> BackupAsync()
    {
        var file = Name + ".bak";
        await It.ExecAsync($"BACKUP DATABASE {It.Q(Name)} TO DISK = @f WITH INIT, FORMAT, NAME = N'IT';", ("@f", file));
        // Caminho físico real (pasta padrão de backups).
        var path = await It.ScalarAsync(
            "SELECT TOP (1) bmf.physical_device_name FROM msdb.dbo.backupset bs " +
            "JOIN msdb.dbo.backupmediafamily bmf ON bmf.media_set_id = bs.media_set_id " +
            "WHERE bs.database_name = @n ORDER BY bs.backup_finish_date DESC;", ("@n", Name)) as string;
        var resolved = path ?? file;
        _backups.Add(resolved);
        return resolved;
    }

    public DatabaseInfo AsInfo(string stateDesc)
    {
        var d = new DatabaseInfo { Name = Name };
        d.StateDesc = stateDesc;
        return d;
    }

    public async ValueTask DisposeAsync()
    {
        await It.ForceDropAsync(Name);
        // Além dos backups do próprio teste, os que a FERRAMENTA gerou (tail-log, cópia de segurança,
        // log COPY_ONLY...) — todos registrados no msdb para este banco.
        try
        {
            await using var c = new SqlConnection(It.Conn);
            await c.OpenAsync();
            await using var cmd = new SqlCommand(
                "SELECT DISTINCT bmf.physical_device_name FROM msdb.dbo.backupset bs " +
                "JOIN msdb.dbo.backupmediafamily bmf ON bmf.media_set_id = bs.media_set_id WHERE bs.database_name = @n;", c);
            cmd.Parameters.AddWithValue("@n", Name);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                if (!r.IsDBNull(0)) _backups.Add(r.GetString(0));
        }
        catch { /* best-effort */ }
        foreach (var b in _backups.Distinct(StringComparer.OrdinalIgnoreCase))
            await It.TryDeleteBackupFileAsync(b);
        await It.TryPurgeBackupHistoryAsync(Name);
    }
}
