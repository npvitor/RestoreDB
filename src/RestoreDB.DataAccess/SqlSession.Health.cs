using Microsoft.Data.SqlClient;
using RestoreDB.Domain.Constants;
using RestoreDB.Domain.Models;

namespace RestoreDB.DataAccess;

/// <summary>
/// Leituras e correções de CONFIGURAÇÃO/SAÚDE do banco (ajustes recomendados) e do acesso travado
/// (SINGLE_USER/RESTRICTED_USER). Todo o T-SQL é compatível com SQL Server 2008 R2.
/// </summary>
public sealed partial class SqlSession
{
    /// <summary>
    /// Configurações do banco relevantes para os ajustes recomendados. As partes que dependem do banco
    /// estar acessível (VLFs, usuários órfãos) são best-effort: ficam vazias/−1 se não der para ler.
    /// </summary>
    public async Task<DatabaseSettings?> GetDatabaseSettingsAsync(string databaseName, CancellationToken ct = default)
    {
        var s = new DatabaseSettings();
        try
        {
            const string sql =
                "SELECT d.page_verify_option_desc, CAST(d.is_auto_shrink_on AS bit), CAST(d.is_auto_close_on AS bit), " +
                "       CAST(d.is_read_only AS bit), d.user_access_desc, CAST(d.is_trustworthy_on AS bit), d.compatibility_level, " +
                "       CASE WHEN EXISTS (SELECT 1 FROM sys.server_principals sp WHERE sp.sid = d.owner_sid) THEN SUSER_SNAME(d.owner_sid) ELSE N'' END, " +
                "       d.recovery_model_desc, " +
                "       CAST(PARSENAME(CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)), 4) AS int) " +
                "FROM sys.databases d WHERE d.name = @db;";
            await EnsureOpenAsync(ct);
            await using (var cmd = new SqlCommand(sql, _connection) { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds })
            {
                cmd.Parameters.AddWithValue("@db", databaseName);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return null;
                s.PageVerify = r.IsDBNull(0) ? string.Empty : r.GetString(0);
                s.AutoShrink = !r.IsDBNull(1) && r.GetBoolean(1);
                s.AutoClose = !r.IsDBNull(2) && r.GetBoolean(2);
                s.IsReadOnly = !r.IsDBNull(3) && r.GetBoolean(3);
                s.UserAccess = r.IsDBNull(4) ? string.Empty : r.GetString(4);
                s.Trustworthy = !r.IsDBNull(5) && r.GetBoolean(5);
                s.CompatibilityLevel = r.IsDBNull(6) ? 0 : Convert.ToInt32(r.GetValue(6));
                s.OwnerName = r.IsDBNull(7) ? string.Empty : r.GetString(7);
                s.RecoveryModel = r.IsDBNull(8) ? string.Empty : r.GetString(8);
                s.ServerCompatibilityLevel = r.IsDBNull(9) ? 0 : Convert.ToInt32(r.GetValue(9)) * 10;
            }

            // Crescimento dos arquivos (sys.master_files: size/growth em páginas de 8 KB, ou % quando is_percent_growth).
            const string files =
                "SELECT name, type, CAST(is_percent_growth AS bit), growth, size FROM sys.master_files " +
                "WHERE database_id = DB_ID(@db) AND type IN (0, 1) ORDER BY file_id;";
            await using (var cmd = new SqlCommand(files, _connection) { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds })
            {
                cmd.Parameters.AddWithValue("@db", databaseName);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    var pct = !r.IsDBNull(2) && r.GetBoolean(2);
                    var growth = r.IsDBNull(3) ? 0 : Convert.ToDouble(r.GetValue(3));
                    s.Files.Add(new FileGrowthSetting
                    {
                        LogicalName = r.GetString(0),
                        IsLog = Convert.ToInt32(r.GetValue(1)) == 1,
                        IsPercentGrowth = pct,
                        Growth = pct ? growth : growth * 8 / 1024,
                        SizeMb = (r.IsDBNull(4) ? 0 : Convert.ToDouble(r.GetValue(4))) * 8 / 1024
                    });
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }

        // Só faz sentido (e só funciona) com o banco ONLINE e acessível.
        s.VlfCount = await CountVlfsAsync(databaseName, ct);
        await ReadOrphanedUsersAsync(databaseName, s, ct);
        return s;
    }

    /// <summary>
    /// Quantidade de VLFs do log. DBCC LOGINFO existe em todas as versões (2008 R2+); só contamos as
    /// linhas, então a diferença de colunas entre versões não importa. −1 quando não dá para ler.
    /// </summary>
    public async Task<int> CountVlfsAsync(string databaseName, CancellationToken ct = default)
    {
        try
        {
            const string sql =
                "DECLARE @proc nvarchar(300) = QUOTENAME(@db) + N'.sys.sp_executesql';\n" +
                "EXEC @proc N'DBCC LOGINFO WITH NO_INFOMSGS;';";
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand(sql, _connection) { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@db", databaseName);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var n = 0;
            while (await r.ReadAsync(ct)) n++;
            return n;
        }
        catch (OperationCanceledException) { throw; }
        catch { return -1; }
    }

    /// <summary>
    /// Usuários SQL do banco cujo SID não corresponde a nenhum login (órfãos — típico após mover,
    /// anexar ou restaurar o banco em outro servidor). SIDs de 16 bytes = usuários de login SQL (exclui
    /// os criados WITHOUT LOGIN, que são intencionais). Marca se existe login de mesmo nome (reassociável).
    /// </summary>
    private async Task ReadOrphanedUsersAsync(string databaseName, DatabaseSettings s, CancellationToken ct)
    {
        try
        {
            const string sql =
                "DECLARE @proc nvarchar(300) = QUOTENAME(@db) + N'.sys.sp_executesql';\n" +
                "DECLARE @stmt nvarchar(max) = N'SELECT dp.name, CASE WHEN EXISTS (SELECT 1 FROM sys.server_principals sp " +
                "WHERE sp.name = dp.name AND sp.type = ''S'') THEN 1 ELSE 0 END " +
                "FROM sys.database_principals dp WHERE dp.type = ''S'' AND dp.principal_id > 4 AND DATALENGTH(dp.sid) = 16 " +
                "AND NOT EXISTS (SELECT 1 FROM sys.server_principals sp WHERE sp.sid = dp.sid) ORDER BY dp.name;';\n" +
                "EXEC @proc @stmt;";
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand(sql, _connection) { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@db", databaseName);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                s.OrphanedUsers.Add((r.GetString(0), Convert.ToInt32(r.GetValue(1)) == 1));
        }
        catch (OperationCanceledException) { throw; }
        catch { /* banco inacessível / sem permissão */ }
    }

    /// <summary>
    /// Sessões que estão USANDO o banco (sys.sysprocesses — no 2008 R2 a sys.dm_exec_sessions não tem
    /// database_id) ou que seguram um bloqueio de banco nele. Exclui a própria sessão e as de sistema.
    /// </summary>
    public async Task<List<DbSessionInfo>> GetDatabaseSessionsAsync(string databaseName, CancellationToken ct = default)
    {
        var list = new List<DbSessionInfo>();
        try
        {
            const string sql =
                "SELECT p.spid, RTRIM(p.loginame), RTRIM(p.hostname), RTRIM(p.program_name), p.last_batch " +
                "FROM sys.sysprocesses p " +
                "WHERE p.spid <> @@SPID AND p.spid > 50 AND p.ecid = 0 AND (p.dbid = DB_ID(@db) OR EXISTS (" +
                "  SELECT 1 FROM sys.dm_tran_locks l WHERE l.request_session_id = p.spid " +
                "  AND l.resource_type = N'DATABASE' AND l.resource_database_id = DB_ID(@db))) " +
                "ORDER BY p.spid;";
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand(sql, _connection) { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@db", databaseName);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                list.Add(new DbSessionInfo
                {
                    SessionId = Convert.ToInt32(r.GetValue(0)),
                    Login = r.IsDBNull(1) ? string.Empty : r.GetString(1),
                    Host = r.IsDBNull(2) ? string.Empty : r.GetString(2),
                    Program = r.IsDBNull(3) ? string.Empty : r.GetString(3),
                    LastBatch = r.IsDBNull(4) ? null : r.GetDateTime(4)
                });
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* sem permissão VIEW SERVER STATE */ }
        return list;
    }

    /// <summary>
    /// Devolve o banco ao acesso normal (MULTI_USER). Se outra sessão segura a vaga de SINGLE_USER, o
    /// ALTER falha (Msg 5064) — então encerra (KILL) SOMENTE as sessões conectadas a ESTE banco e tenta
    /// de novo no MESMO lote, para que um programa com reconexão automática não roube a vaga no meio.
    /// Devolve as sessões encerradas e o user_access final.
    /// </summary>
    public async Task<(List<int> Killed, string FinalAccess, string Error)> ReleaseDatabaseAccessAsync(
        string databaseName, bool killHolders, CancellationToken ct = default)
    {
        var killed = new List<int>();
        var error = string.Empty;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var access = await GetUserAccessAsync(databaseName, ct);
            if (access.Length == 0 || access.Equals("MULTI_USER", StringComparison.OrdinalIgnoreCase))
                return (killed, access, string.Empty);

            if (killHolders)
                foreach (var h in await GetDatabaseSessionsAsync(databaseName, ct))
                {
                    try
                    {
                        await EnsureOpenAsync(ct);
                        await using var k = new SqlCommand($"KILL {h.SessionId};", _connection) { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
                        await k.ExecuteNonQueryAsync(ct);
                        if (!killed.Contains(h.SessionId)) killed.Add(h.SessionId);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { /* sessão já saiu */ }
                }

            var r = await ExecuteAsync("Liberar acesso (MULTI_USER)",
                "DECLARE @c nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@db) + N' SET MULTI_USER WITH ROLLBACK IMMEDIATE;';\n" +
                "EXEC sys.sp_executesql @c;",
                new Dictionary<string, object?> { ["@db"] = databaseName }, AppConstants.QueryCommandTimeoutSeconds * 4, ct);
            if (!r.Success) error = string.Join(" ", r.ErrorMessages);
            if (!killHolders && !r.Success) break;
        }
        return (killed, await GetUserAccessAsync(databaseName, ct), error);
    }

    private async Task<string> GetUserAccessAsync(string databaseName, CancellationToken ct)
    {
        await EnsureOpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT user_access_desc FROM sys.databases WHERE name = @db;", _connection)
        { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
        cmd.Parameters.AddWithValue("@db", databaseName);
        return (await cmd.ExecuteScalarAsync(ct)) as string ?? string.Empty;
    }

    /// <summary>Executa um comando NO CONTEXTO do banco alvo (EXEC [db].sys.sp_executesql), capturando mensagens.</summary>
    public Task<CommandExecutionResult> ExecuteInDatabaseAsync(string description, string databaseName, string statement,
        int timeoutSeconds = AppConstants.RepairCommandTimeoutSeconds, CancellationToken ct = default)
        => ExecuteAsync(description,
            "DECLARE @proc nvarchar(300) = QUOTENAME(@db) + N'.sys.sp_executesql';\nEXEC @proc @stmt;",
            new Dictionary<string, object?> { ["@db"] = databaseName, ["@stmt"] = statement }, timeoutSeconds, ct);

    /// <summary>
    /// Lista "schema.tabela"/constraint de cada constraint não confiável (FK e CHECK habilitadas) — para
    /// revalidar com WITH CHECK CHECK CONSTRAINT. As DESABILITADAS ficam de fora: foram desligadas de propósito.
    /// </summary>
    public async Task<List<(string Table, string Constraint)>> GetUntrustedEnabledConstraintsAsync(string databaseName, CancellationToken ct = default)
    {
        var list = new List<(string, string)>();
        try
        {
            const string sql =
                "DECLARE @proc nvarchar(300) = QUOTENAME(@db) + N'.sys.sp_executesql';\n" +
                "DECLARE @stmt nvarchar(max) = N'" +
                "SELECT QUOTENAME(OBJECT_SCHEMA_NAME(parent_object_id)) + N''.'' + QUOTENAME(OBJECT_NAME(parent_object_id)), name " +
                "FROM sys.foreign_keys WHERE is_not_trusted = 1 AND is_disabled = 0 " +
                "UNION ALL SELECT QUOTENAME(OBJECT_SCHEMA_NAME(parent_object_id)) + N''.'' + QUOTENAME(OBJECT_NAME(parent_object_id)), name " +
                "FROM sys.check_constraints WHERE is_not_trusted = 1 AND is_disabled = 0;';\n" +
                "EXEC @proc @stmt;";
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand(sql, _connection) { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@db", databaseName);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add((r.GetString(0), r.GetString(1)));
        }
        catch (OperationCanceledException) { throw; }
        catch { /* best-effort */ }
        return list;
    }

    /// <summary>Índices desabilitados com tabela e nome já aspeados, clusterizados primeiro (reabrem a tabela).</summary>
    public async Task<List<(string Table, string Index, bool Clustered)>> GetDisabledIndexDetailsAsync(string databaseName, CancellationToken ct = default)
    {
        var list = new List<(string, string, bool)>();
        try
        {
            const string sql =
                "DECLARE @proc nvarchar(300) = QUOTENAME(@db) + N'.sys.sp_executesql';\n" +
                "DECLARE @stmt nvarchar(max) = N'SELECT QUOTENAME(s.name) + N''.'' + QUOTENAME(t.name), QUOTENAME(i.name), " +
                "CAST(CASE WHEN i.index_id = 1 THEN 1 ELSE 0 END AS bit) " +
                "FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id JOIN sys.schemas s ON s.schema_id = t.schema_id " +
                "WHERE i.is_disabled = 1 AND t.is_ms_shipped = 0 ORDER BY CASE WHEN i.index_id = 1 THEN 0 ELSE 1 END, t.name, i.name;';\n" +
                "EXEC @proc @stmt;";
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand(sql, _connection) { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@db", databaseName);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add((r.GetString(0), r.GetString(1), r.GetBoolean(2)));
        }
        catch (OperationCanceledException) { throw; }
        catch { /* best-effort */ }
        return list;
    }

    /// <summary>Módulos (views/procedures/funções, não schema-bound) que referenciam objetos inexistentes — "schema.nome".</summary>
    public async Task<List<string>> GetModulesWithBrokenReferencesAsync(string databaseName, CancellationToken ct = default)
    {
        var list = new List<string>();
        try
        {
            const string sql =
                "DECLARE @proc nvarchar(300) = QUOTENAME(@db) + N'.sys.sp_executesql';\n" +
                "DECLARE @stmt nvarchar(max) = N'SELECT DISTINCT QUOTENAME(OBJECT_SCHEMA_NAME(d.referencing_id)) + N''.'' + QUOTENAME(OBJECT_NAME(d.referencing_id)) " +
                "FROM sys.sql_expression_dependencies d JOIN sys.sql_modules m ON m.object_id = d.referencing_id " +
                "WHERE d.referenced_id IS NULL AND d.referenced_entity_name IS NOT NULL AND d.referenced_database_name IS NULL " +
                "AND d.is_ambiguous = 0 AND m.is_schema_bound = 0 " +
                "AND OBJECT_ID(ISNULL(d.referenced_schema_name + N''.'', N'''') + d.referenced_entity_name) IS NULL;';\n" +
                "EXEC @proc @stmt;";
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand(sql, _connection) { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@db", databaseName);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                if (!r.IsDBNull(0)) list.Add(r.GetString(0));
        }
        catch (OperationCanceledException) { throw; }
        catch { /* best-effort */ }
        return list;
    }

    /// <summary>Login com principal_id = 1 (o "sa", mesmo se renomeado) — dono padrão seguro para um banco sem dono válido.</summary>
    public async Task<string> GetSaLoginNameAsync(CancellationToken ct = default)
    {
        try
        {
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand("SELECT name FROM sys.server_principals WHERE principal_id = 1;", _connection)
            { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
            return (await cmd.ExecuteScalarAsync(ct)) as string ?? string.Empty;
        }
        catch (OperationCanceledException) { throw; }
        catch { return string.Empty; }
    }
}
