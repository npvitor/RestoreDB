using Microsoft.Data.SqlClient;
using RestoreDB.Domain.Constants;
using RestoreDB.Domain.Models;

namespace RestoreDB.DataAccess;

/// <summary>
/// Sessão de banco com UMA conexão aberta, mantida durante toda a sequência de reparo.
/// Isso é essencial: o bloqueio SINGLE_USER WITH ROLLBACK IMMEDIATE precisa ser detido
/// pela mesma conexão que executa o DBCC CHECKDB, evitando que outro processo "roube"
/// a vaga de usuário único. Conecta-se sempre em master (nunca no banco quebrado).
/// </summary>
public sealed class SqlSession : IAsyncDisposable, IDisposable
{
    private readonly SqlConnection _connection;

    internal SqlSession(SqlConnection connection) => _connection = connection;

    /// <summary>
    /// Garante a conexão aberta antes de um comando. Erros SEVEROS (ex.: 824 ao ler uma página
    /// corrompida) DERRUBAM a conexão — sem isso, todos os passos seguintes do reparo falhariam
    /// com "connection closed". Reabrir volta o contexto a master (perde USE/vaga SINGLE_USER,
    /// mas as opções do banco persistem e os comandos qualificam o banco explicitamente).
    /// </summary>
    private async Task EnsureOpenAsync(CancellationToken ct)
    {
        if (_connection.State == System.Data.ConnectionState.Open) return;
        try { _connection.Close(); } catch { /* estado Broken */ }
        await _connection.OpenAsync(ct);
    }

    /// <summary>
    /// Executa um comando T-SQL capturando mensagens informativas (PRINT / DBCC) e de erro.
    /// Não lança em erros do servidor: devolve Success=false e a lista de mensagens, para
    /// que a camada de serviço decida o escalonamento. Cancelamento é propagado.
    /// </summary>
    public async Task<CommandExecutionResult> ExecuteAsync(
        string description,
        string sql,
        IReadOnlyDictionary<string, object?>? parameters = null,
        int timeoutSeconds = AppConstants.QueryCommandTimeoutSeconds,
        CancellationToken ct = default)
    {
        var result = new CommandExecutionResult { Description = description, Sql = sql };
        var infos = new List<string>();

        void OnInfoMessage(object sender, SqlInfoMessageEventArgs e)
        {
            foreach (SqlError err in e.Errors)
                infos.Add(FormatMessage(err));
        }

        _connection.InfoMessage += OnInfoMessage;
        try
        {
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand(sql, _connection) { CommandTimeout = timeoutSeconds };
            if (parameters is not null)
            {
                foreach (var kv in parameters)
                    cmd.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
            }

            await cmd.ExecuteNonQueryAsync(ct);
            result.Success = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SqlException ex)
        {
            result.Success = false;
            result.Exception = ex;
            // A exceção de um DBCC traz TODAS as mensagens misturadas (ex.: milhares de
            // "DBCC results for..."). Severidade <= 10 é informativa — vai para InfoMessages,
            // para o registro da UI não pintar tudo de vermelho e esconder os erros reais.
            foreach (SqlError err in ex.Errors)
            {
                if (err.Class <= 10) infos.Add(FormatMessage(err));
                else result.ErrorMessages.Add(FormatMessage(err));
            }
            if (result.ErrorMessages.Count == 0)
                result.ErrorMessages.Add(ex.Message);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Exception = ex;
            result.ErrorMessages.Add(ex.Message);
        }
        finally
        {
            _connection.InfoMessage -= OnInfoMessage;
            result.InfoMessages.AddRange(infos);
        }

        return result;
    }

    /// <summary>
    /// Descarta um banco descartável (cópia de teste/simulação): força SINGLE_USER e DROP DATABASE.
    /// Best-effort — usado nas limpezas em finally; nunca lança. Devolve true se o banco não existe mais.
    /// </summary>
    public async Task<bool> DropDatabaseBestEffortAsync(string databaseName, CancellationToken ct = default)
    {
        const string sql =
            "IF DB_ID(@db) IS NOT NULL\n" +
            "BEGIN\n" +
            "  DECLARE @cmd nvarchar(max);\n" +
            "  BEGIN TRY SET @cmd = N'ALTER DATABASE ' + QUOTENAME(@db) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE;'; EXEC sys.sp_executesql @cmd; END TRY BEGIN CATCH END CATCH\n" +
            "  SET @cmd = N'DROP DATABASE ' + QUOTENAME(@db) + N';'; EXEC sys.sp_executesql @cmd;\n" +
            "END";
        try
        {
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand(sql, _connection) { CommandTimeout = AppConstants.RepairCommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@db", databaseName);
            await cmd.ExecuteNonQueryAsync(ct);
            return (await GetDatabaseStateAsync(databaseName, ct)) == "DESCONHECIDO";
        }
        catch { return false; }
    }

    /// <summary>Lê o state_desc atual de um banco em sys.databases.</summary>
    public async Task<string> GetDatabaseStateAsync(string databaseName, CancellationToken ct = default)
    {
        const string sql = "SELECT state_desc FROM sys.databases WHERE name = @db;";
        await EnsureOpenAsync(ct);
        await using var cmd = new SqlCommand(sql, _connection) { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
        cmd.Parameters.AddWithValue("@db", databaseName);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value as string ?? "DESCONHECIDO";
    }

    /// <summary>
    /// Remove as entradas de msdb.dbo.suspect_pages do banco após um reparo bem-sucedido e
    /// verificado. Necessário porque o SQL Server não limpa essa tabela automaticamente quando
    /// a corrupção é resolvida fora do DBCC repair (ex.: reconstrução de índice). Best-effort.
    /// </summary>
    public async Task ClearSuspectPagesAsync(string databaseName, CancellationToken ct = default)
    {
        try
        {
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand(
                "DELETE FROM msdb.dbo.suspect_pages WHERE database_id = DB_ID(@db);", _connection)
            { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@db", databaseName);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch { /* best-effort */ }
    }

    /// <summary>Data/hora atual do SERVIDOR (GETDATE()) — a mesma referência das datas do msdb. Best-effort.</summary>
    public async Task<DateTime?> GetServerTimeAsync(CancellationToken ct = default)
    {
        try
        {
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand("SELECT GETDATE();", _connection) { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
            return (DateTime?)await cmd.ExecuteScalarAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>
    /// Situação do LOG de transações: motivo de espera (log_reuse_wait_desc), % usado (DBCC SQLPERF —
    /// funciona no 2008 R2; só para bancos ONLINE) e, por arquivo, tamanho/limite/crescimento e espaço
    /// livre no volume (sys.dm_os_volume_stats, 2008 R2 SP1+). Best-effort: campos indisponíveis ficam nulos.
    /// </summary>
    public async Task<LogSpaceInfo> GetLogSpaceInfoAsync(string databaseName, CancellationToken ct = default)
    {
        string model = string.Empty, wait = string.Empty;
        double? used = null, total = null;
        try
        {
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand(
                "SELECT recovery_model_desc, log_reuse_wait_desc FROM sys.databases WHERE name = @db;", _connection)
            { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@db", databaseName);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                model = r.IsDBNull(0) ? string.Empty : r.GetString(0);
                wait = r.IsDBNull(1) ? string.Empty : r.GetString(1);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* best-effort */ }

        // 2012+: sys.dm_db_log_space_usage no contexto do banco (abre o banco mesmo com AUTO_CLOSE, que o
        // DBCC SQLPERF omite). No 2008 R2 a DMV não existe e o SQLPERF abaixo cobre.
        try
        {
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand(
                "DECLARE @proc nvarchar(300) = QUOTENAME(@db) + N'.sys.sp_executesql';\n" +
                "EXEC @proc N'SELECT total_log_size_in_bytes / 1048576.0, used_log_space_in_percent FROM sys.dm_db_log_space_usage;';",
                _connection) { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@db", databaseName);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                total = r.IsDBNull(0) ? null : Convert.ToDouble(r.GetValue(0));
                used = r.IsDBNull(1) ? null : Convert.ToDouble(r.GetValue(1));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* 2008 R2 ou banco inacessível */ }

        if (used is null)
        try
        {
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand(
                "DECLARE @t TABLE (DbName sysname, LogSizeMb float, LogUsedPct float, Status int);\n" +
                "INSERT INTO @t EXEC (N'DBCC SQLPERF(LOGSPACE) WITH NO_INFOMSGS');\n" +
                "SELECT LogSizeMb, LogUsedPct FROM @t WHERE DbName = @db;", _connection)
            { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@db", databaseName);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                total = r.IsDBNull(0) ? null : Convert.ToDouble(r.GetValue(0));
                used = r.IsDBNull(1) ? null : Convert.ToDouble(r.GetValue(1));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* best-effort */ }

        var info = new LogSpaceInfo { RecoveryModel = model, ReuseWait = wait, UsedPercent = used, TotalSizeMb = total };

        // Arquivos de log: size/max_size em páginas de 8 KB; max_size -1 = ilimitado; growth em páginas ou %.
        const string filesSql =
            "SELECT mf.name, mf.physical_name, mf.size, mf.max_size, mf.growth, mf.is_percent_growth, mf.file_id " +
            "FROM sys.master_files mf WHERE mf.database_id = DB_ID(@db) AND mf.type = 1 ORDER BY mf.file_id;";
        var fileIds = new List<(int id, LogFileInfo f)>();
        try
        {
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand(filesSql, _connection) { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@db", databaseName);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var size = Convert.ToDouble(r.GetValue(2)) * 8 / 1024;
                var max = Convert.ToInt64(r.GetValue(3));
                var pct = !r.IsDBNull(5) && r.GetBoolean(5);
                var growth = Convert.ToDouble(r.GetValue(4));
                fileIds.Add((Convert.ToInt32(r.GetValue(6)), new LogFileInfo
                {
                    LogicalName = r.GetString(0),
                    PhysicalName = r.GetString(1),
                    SizeMb = size,
                    // max_size: -1 = ilimitado; 268435456 = 2 TB (o "ilimitado" do log).
                    MaxSizeMb = max is -1 or >= 268435456 ? null : max * 8.0 / 1024,
                    Growth = pct ? growth : growth * 8 / 1024,
                    GrowthIsPercent = pct
                }));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* best-effort */ }

        foreach (var (id, f) in fileIds)
        {
            double? free = null;
            try
            {
                await EnsureOpenAsync(ct);
                await using var cmd = new SqlCommand(
                    "SELECT available_bytes FROM sys.dm_os_volume_stats(DB_ID(@db), @fid);", _connection)
                { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
                cmd.Parameters.AddWithValue("@db", databaseName);
                cmd.Parameters.AddWithValue("@fid", id);
                var v = await cmd.ExecuteScalarAsync(ct);
                if (v is not null && v != DBNull.Value) free = Convert.ToDouble(v) / 1024 / 1024;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* DMF indisponível (2008 R2 sem SP1) / banco inacessível */ }
            info.Files.Add(new LogFileInfo
            {
                LogicalName = f.LogicalName, PhysicalName = f.PhysicalName, SizeMb = f.SizeMb, MaxSizeMb = f.MaxSizeMb,
                Growth = f.Growth, GrowthIsPercent = f.GrowthIsPercent, VolumeFreeMb = free
            });
        }
        return info;
    }

    /// <summary>
    /// A transação ABERTA mais antiga do banco (a que segura o log em ACTIVE_TRANSACTION), com quem a
    /// abriu. Somente leitura (DMVs de transação, 2005+). Null quando não há/sem permissão.
    /// </summary>
    public async Task<(int SessionId, DateTime Started, string Login, string Host, string Program)?> GetOldestOpenTransactionAsync(
        string databaseName, CancellationToken ct = default)
    {
        try
        {
            const string sql =
                "SELECT TOP (1) st.session_id, dt.database_transaction_begin_time, ISNULL(es.login_name, N''), " +
                "       ISNULL(es.host_name, N''), ISNULL(es.program_name, N'') " +
                "FROM sys.dm_tran_database_transactions dt " +
                "JOIN sys.dm_tran_session_transactions st ON st.transaction_id = dt.transaction_id " +
                "LEFT JOIN sys.dm_exec_sessions es ON es.session_id = st.session_id " +
                "WHERE dt.database_id = DB_ID(@db) AND dt.database_transaction_begin_time IS NOT NULL " +
                "ORDER BY dt.database_transaction_begin_time;";
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand(sql, _connection) { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@db", databaseName);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            return (Convert.ToInt32(r.GetValue(0)), r.GetDateTime(1), r.GetString(2), r.GetString(3), r.GetString(4));
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>
    /// Pastas candidatas para acomodar um arquivo de log EXTRA: as pastas dos arquivos da instância
    /// (todos os bancos), com o espaço livre do volume de cada uma, da mais livre para a menos livre.
    /// Best-effort (sys.dm_os_volume_stats, 2008 R2 SP1+).
    /// </summary>
    public async Task<List<(string Directory, double FreeMb)>> GetInstanceFoldersByFreeSpaceAsync(CancellationToken ct = default)
    {
        var list = new List<(string, double)>();
        try
        {
            const string sql =
                "SELECT mf.physical_name, vs.available_bytes, vs.volume_mount_point FROM sys.master_files mf " +
                "CROSS APPLY sys.dm_os_volume_stats(mf.database_id, mf.file_id) vs WHERE mf.type IN (0, 1);";
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand(sql, _connection) { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (await r.ReadAsync(ct))
            {
                var dir = System.IO.Path.GetDirectoryName(r.GetString(0)) ?? string.Empty;
                if (dir.Length == 0 || !seen.Add(dir)) continue;
                list.Add((dir, Convert.ToDouble(r.GetValue(1)) / 1024 / 1024));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* best-effort */ }
        return list.OrderByDescending(x => x.Item2).ToList();
    }

    /// <summary>
    /// Conta de serviço do SQL Server (sys.dm_server_services, 2008 R2 SP1+) — quem precisa de permissão
    /// sobre os arquivos do banco. Fallback: a identidade virtual do serviço ("NT SERVICE\MSSQLSERVER" ou
    /// "NT SERVICE\MSSQL$INSTANCIA"), que é a conta padrão desde o 2012 e sempre existe como SID de serviço.
    /// </summary>
    public async Task<List<string>> GetSqlServiceAccountsAsync(CancellationToken ct = default)
    {
        var accounts = new List<string>();
        try
        {
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand(
                "SELECT service_account FROM sys.dm_server_services WHERE servicename LIKE N'SQL Server (%';", _connection)
            { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
            var v = await cmd.ExecuteScalarAsync(ct) as string;
            if (!string.IsNullOrWhiteSpace(v)) accounts.Add(v.Trim());
        }
        catch (OperationCanceledException) { throw; }
        catch { /* DMV indisponível */ }

        try
        {
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand("SELECT CAST(SERVERPROPERTY('InstanceName') AS nvarchar(128));", _connection)
            { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
            var inst = await cmd.ExecuteScalarAsync(ct) as string;
            var svc = string.IsNullOrEmpty(inst) ? @"NT SERVICE\MSSQLSERVER" : @"NT SERVICE\MSSQL$" + inst;
            if (!accounts.Contains(svc, StringComparer.OrdinalIgnoreCase)) accounts.Add(svc);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* best-effort */ }
        return accounts;
    }

    /// <summary>Lê o modelo de recuperação do banco (FULL / BULK_LOGGED / SIMPLE). Best-effort.</summary>
    public async Task<string> GetRecoveryModelAsync(string databaseName, CancellationToken ct = default)
    {
        try
        {
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand(
                "SELECT recovery_model_desc FROM sys.databases WHERE name = @db;", _connection)
            { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@db", databaseName);
            return (await cmd.ExecuteScalarAsync(ct)) as string ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    /// <summary>
    /// Diretório utilizável para arquivos de um banco DESCARTÁVEL de teste — a pasta de dados do
    /// tempdb, que existe e é gravável pela conta de serviço do SQL em qualquer versão (2008 R2+).
    /// Devolve vazio se não for possível determinar.
    /// </summary>
    public async Task<string> GetScratchDataDirAsync(CancellationToken ct = default)
    {
        try
        {
            const string sql =
                "SELECT TOP (1) physical_name FROM sys.master_files " +
                "WHERE database_id = DB_ID(N'tempdb') AND type = 0 ORDER BY file_id;";
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand(sql, _connection) { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
            var path = (await cmd.ExecuteScalarAsync(ct)) as string;
            return string.IsNullOrEmpty(path) ? string.Empty : (System.IO.Path.GetDirectoryName(path) ?? string.Empty);
        }
        catch { return string.Empty; }
    }

    /// <summary>
    /// Progresso de uma operação demorada em andamento no banco (recuperação/RESTORE/ROLLBACK), de
    /// sys.dm_exec_requests. Devolve null quando não há nada em andamento. Best-effort.
    /// </summary>
    public async Task<RecoveryProgress?> GetRecoveryProgressAsync(string databaseName, CancellationToken ct = default)
    {
        try
        {
            const string sql =
                "SELECT TOP (1) r.command, r.percent_complete, CAST(r.estimated_completion_time AS bigint) " +
                "FROM sys.dm_exec_requests r " +
                "WHERE r.database_id = DB_ID(@db) AND r.percent_complete > 0 " +
                "ORDER BY r.percent_complete DESC;";
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand(sql, _connection) { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@db", databaseName);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                return new RecoveryProgress
                {
                    Command = r.IsDBNull(0) ? string.Empty : r.GetString(0),
                    PercentComplete = r.IsDBNull(1) ? 0 : Convert.ToDouble(r.GetValue(1)),
                    EstimatedRemainingMs = r.IsDBNull(2) ? 0 : Convert.ToInt64(r.GetValue(2))
                };
            }
        }
        catch { /* best-effort */ }
        return null;
    }

    /// <summary>true se o banco está criptografado (TDE) — relevante para restaurar (exige o certificado).</summary>
    public async Task<bool> IsEncryptedAsync(string databaseName, CancellationToken ct = default)
    {
        try
        {
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand("SELECT is_encrypted FROM sys.databases WHERE name = @db;", _connection)
            { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@db", databaseName);
            var v = await cmd.ExecuteScalarAsync(ct);
            return v is not null && v != DBNull.Value && Convert.ToInt32(v) == 1;
        }
        catch { return false; }
    }

    /// <summary>Lê a opção PAGE_VERIFY do banco (NONE / TORN_PAGE_DETECTION / CHECKSUM). Best-effort.</summary>
    public async Task<string> GetPageVerifyOptionAsync(string databaseName, CancellationToken ct = default)
    {
        try
        {
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand(
                "SELECT page_verify_option_desc FROM sys.databases WHERE name = @db;", _connection)
            { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@db", databaseName);
            return (await cmd.ExecuteScalarAsync(ct)) as string ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    /// <summary>Lê os arquivos físicos de um banco (sys.master_files). Compatível com 2008 R2.</summary>
    public async Task<List<DatabaseFile>> GetDatabaseFilesAsync(string databaseName, CancellationToken ct = default)
    {
        const string sql =
            "SELECT CAST(mf.type AS int) AS [type], mf.type_desc, mf.physical_name, mf.name " +
            "FROM sys.master_files AS mf WHERE mf.database_id = DB_ID(@db) ORDER BY mf.type, mf.file_id;";

        var list = new List<DatabaseFile>();
        await EnsureOpenAsync(ct);
        await using var cmd = new SqlCommand(sql, _connection) { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
        cmd.Parameters.AddWithValue("@db", databaseName);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new DatabaseFile
            {
                Type = reader.GetInt32(0),
                TypeDesc = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                PhysicalName = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                LogicalName = reader.IsDBNull(3) ? string.Empty : reader.GetString(3)
            });
        }
        return list;
    }

    /// <summary>
    /// Lê as páginas suspeitas registradas em msdb.dbo.suspect_pages para o banco — a fonte
    /// autoritativa do SQL Server para corrupção (erros 823/824/825). Best-effort.
    /// </summary>
    public async Task<List<SuspectPageInfo>> GetSuspectPagesAsync(string databaseName, CancellationToken ct = default)
    {
        const string sql =
            "SELECT file_id, page_id, event_type, error_count, last_update_date " +
            "FROM msdb.dbo.suspect_pages WHERE database_id = DB_ID(@db) ORDER BY last_update_date DESC;";

        var list = new List<SuspectPageInfo>();
        try
        {
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand(sql, _connection) { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@db", databaseName);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new SuspectPageInfo
                {
                    FileId = reader.GetInt32(0),
                    PageId = Convert.ToInt64(reader.GetValue(1)),
                    EventType = reader.GetInt32(2),
                    ErrorCount = reader.GetInt32(3),
                    LastUpdateDate = reader.IsDBNull(4) ? null : reader.GetDateTime(4)
                });
            }
        }
        catch
        {
            // sem permissão em msdb ou tabela indisponível: ignora.
        }
        return list;
    }

    /// <summary>
    /// Lê o log de erros atual do SQL Server filtrando pelas linhas que mencionam o banco
    /// (sp_readerrorlog). Somente leitura. Best-effort: se não houver permissão/sp, devolve vazio.
    /// Compatível com SQL Server 2008 R2.
    /// </summary>
    public async Task<List<string>> ReadErrorLogAsync(string databaseName, CancellationToken ct = default, int maxArchives = 3,
        IEnumerable<string>? extraSearchTerms = null)
    {
        // Muitas mensagens de E/S citam só o CAMINHO do arquivo ("FCB::Open failed: Could not open file
        // D:\dados\x.mdf ... OS error: 5"), não o nome do banco: procura também pelos nomes de arquivo.
        var terms = new List<string> { databaseName };
        foreach (var t in extraSearchTerms ?? Enumerable.Empty<string>())
            if (!string.IsNullOrWhiteSpace(t) && !terms.Any(x => t.Contains(x, StringComparison.OrdinalIgnoreCase)))
                terms.Add(t);

        var lines = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var term in terms)
            foreach (var l in await ReadErrorLogTermAsync(term, ct, maxArchives))
                if (seen.Add(l)) lines.Add(l);
        return lines;
    }

    private async Task<List<string>> ReadErrorLogTermAsync(string databaseName, CancellationToken ct, int maxArchives)
    {
        var lines = new List<string>();
        // Varre o log atual (0) e os arquivos arquivados mais recentes, pois um rollover pode
        // ter movido as evidências de corrupção para um arquivo anterior.
        for (var arch = 0; arch < maxArchives; arch++)
        {
            try
            {
                // @arch (0 = atual), @logtype = 1 (SQL Server, não Agent), @search = texto a filtrar.
                await EnsureOpenAsync(ct);
                await using var cmd = new SqlCommand("EXEC sys.sp_readerrorlog @arch, @logtype, @search;", _connection)
                {
                    CommandTimeout = AppConstants.QueryCommandTimeoutSeconds
                };
                cmd.Parameters.AddWithValue("@arch", arch);
                cmd.Parameters.AddWithValue("@logtype", 1);
                cmd.Parameters.AddWithValue("@search", databaseName);

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    // Colunas: LogDate, ProcessInfo, Text
                    var date = reader.IsDBNull(0) ? string.Empty : Convert.ToString(reader.GetValue(0)) ?? string.Empty;
                    var text = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                    lines.Add(string.IsNullOrEmpty(date) ? text : $"{date}  {text}");
                }
            }
            catch
            {
                // best-effort: sem permissão (não-sysadmin), arquivo inexistente, sp indisponível, etc.
                break;
            }
        }
        return lines;
    }

    /// <summary>
    /// Executa DBCC CHECKDB (...) WITH TABLERESULTS e devolve as linhas estruturadas. Quando
    /// <paramref name="repairOption"/> é informado (REPAIR_REBUILD/REPAIR_ALLOW_DATA_LOSS), o
    /// banco precisa estar ONLINE + SINGLE_USER. Sem opção, é somente leitura (diagnóstico).
    /// Lê colunas por nome (tolerante às variações entre versões do SQL Server).
    /// </summary>
    public async Task<CheckDbReport> CheckDbWithResultsAsync(string databaseName, string? repairOption, CancellationToken ct = default,
        bool physicalOnly = false, bool dataPurity = false)
    {
        var report = new CheckDbReport();
        var repairPart = repairOption is null ? string.Empty : ", " + repairOption;

        // PHYSICAL_ONLY (varredura rápida) e DATA_PURITY só fazem sentido no diagnóstico (sem reparo).
        var withOpts = "TABLERESULTS, ALL_ERRORMSGS, NO_INFOMSGS";
        if (repairOption is null && physicalOnly) withOpts += ", PHYSICAL_ONLY";
        else if (repairOption is null && dataPurity) withOpts += ", DATA_PURITY";

        try
        {
            await RunCheckDbReaderAsync(databaseName, repairPart, withOpts, report, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SqlException ex) when (repairOption is null && ex.Errors.Cast<SqlError>().Any(e => e.Number is 1823 or 7928))
        {
            // O CHECKDB sem reparo usa um snapshot interno do banco; em disco cheio/sem permissão
            // na pasta de dados ele falha (Msg 1823/7928). O TABLOCK dispensa o snapshot (usa
            // bloqueios), permitindo verificar mesmo assim. TABLOCK não aceita DATA_PURITY.
            try
            {
                report = new CheckDbReport();
                await RunCheckDbReaderAsync(databaseName, repairPart, "TABLERESULTS, ALL_ERRORMSGS, NO_INFOMSGS, TABLOCK"
                    + (physicalOnly ? ", PHYSICAL_ONLY" : string.Empty), report, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* segue com relatório vazio */ }
        }
        catch
        {
            // Banco inacessível / sem permissão: relatório fica vazio (Executed = false).
        }
        return report;
    }

    private async Task RunCheckDbReaderAsync(string databaseName, string repairPart, string withOpts, CheckDbReport report, CancellationToken ct)
    {
        var sql =
            "DECLARE @cmd nvarchar(max);\n" +
            "SET @cmd = N'DBCC CHECKDB (' + QUOTENAME(@db) + N'" + repairPart + ") WITH " + withOpts + ";';\n" +
            "EXEC sys.sp_executesql @cmd;";

        await EnsureOpenAsync(ct);
        await using var cmd = new SqlCommand(sql, _connection) { CommandTimeout = AppConstants.RepairCommandTimeoutSeconds };
        cmd.Parameters.AddWithValue("@db", databaseName);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var oErr = Ordinal(reader, "Error");
        var oLvl = Ordinal(reader, "Level");
        var oState = Ordinal(reader, "State");
        var oMsg = Ordinal(reader, "MessageText");
        var oRepair = Ordinal(reader, "RepairLevel");
        var oObj = Ordinal(reader, "ObjectId", "Id");
        var oIdx = Ordinal(reader, "IndexId", "IndId");

        report.Executed = true;
        while (await reader.ReadAsync(ct))
        {
            report.Details.Add(new CorruptionDetail
            {
                Error = GetInt(reader, oErr),
                Level = GetInt(reader, oLvl),
                State = GetInt(reader, oState),
                MessageText = GetStr(reader, oMsg),
                RepairLevel = GetStr(reader, oRepair),
                ObjectId = GetInt(reader, oObj),
                IndexId = GetInt(reader, oIdx)
            });
        }
    }

    /// <summary>
    /// DBCC CHECKTABLE de UM objeto (verificação dirigida, mais rápida que o CHECKDB completo),
    /// no contexto do banco alvo, com TABLERESULTS. <paramref name="schemaTable"/> ex.: "dbo.NotaFiscal".
    /// </summary>
    public async Task<CheckDbReport> CheckTableAsync(string databaseName, string schemaTable, CancellationToken ct = default)
    {
        var report = new CheckDbReport();
        try
        {
            // O nome do objeto é validado/aspeado pelo chamador; ainda assim escapamos o literal.
            var inner = $"DBCC CHECKTABLE ('{schemaTable.Replace("'", "''")}') WITH TABLERESULTS, ALL_ERRORMSGS, NO_INFOMSGS;";
            const string sql =
                "DECLARE @proc nvarchar(300) = QUOTENAME(@db) + N'.sys.sp_executesql';\n" +
                "EXEC @proc @stmt;";
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand(sql, _connection) { CommandTimeout = AppConstants.RepairCommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@db", databaseName);
            cmd.Parameters.AddWithValue("@stmt", inner);
            await using var reader = await cmd.ExecuteReaderAsync(ct);

            var oErr = Ordinal(reader, "Error");
            var oLvl = Ordinal(reader, "Level");
            var oState = Ordinal(reader, "State");
            var oMsg = Ordinal(reader, "MessageText");
            var oRepair = Ordinal(reader, "RepairLevel");
            var oObj = Ordinal(reader, "ObjectId", "Id");
            var oIdx = Ordinal(reader, "IndexId", "IndId");

            report.Executed = true;
            while (await reader.ReadAsync(ct))
            {
                report.Details.Add(new CorruptionDetail
                {
                    Error = GetInt(reader, oErr),
                    Level = GetInt(reader, oLvl),
                    State = GetInt(reader, oState),
                    MessageText = GetStr(reader, oMsg),
                    RepairLevel = GetStr(reader, oRepair),
                    ObjectId = GetInt(reader, oObj),
                    IndexId = GetInt(reader, oIdx)
                });
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* banco/objeto inacessível */ }
        return report;
    }

    /// <summary>
    /// Lista as transações DESTRUTIVAS recentes (DELETE em massa / DROP / TRUNCATE) do log de
    /// transações via fn_dblog — para investigar exclusões acidentais. Somente leitura; enxerga só a
    /// parte ATIVA do log. Best-effort (função não documentada, porém disponível no 2008 R2).
    /// </summary>
    public async Task<List<ForensicTransaction>> GetRecentDestructiveTransactionsAsync(string databaseName, int max = 50, CancellationToken ct = default)
    {
        var list = new List<ForensicTransaction>();
        try
        {
            // Uma única leitura de fn_dblog (cara) para uma tabela temporária. A contagem de linhas usa a
            // unidade de alocação com MAIS exclusões (a tabela base): somar todas contaria também as
            // entradas de cada índice não-clusterizado. A tabela afetada vem do AllocUnitName
            // ("schema.tabela.índice"); objetos de sistema (sys.*) são ignorados.
            const string sql =
                "DECLARE @proc nvarchar(300) = QUOTENAME(@db) + N'.sys.sp_executesql';\n" +
                "DECLARE @stmt nvarchar(max) = N'" +
                "SELECT [Transaction ID] AS tid, [Transaction Name] AS tname, [Begin Time] AS btime, Operation AS op, AllocUnitName AS au " +
                "INTO #l FROM fn_dblog(NULL, NULL) WHERE Operation IN (''LOP_BEGIN_XACT'', ''LOP_DELETE_ROWS''); " +
                "SELECT au_tid = tid, au, n = COUNT(*) INTO #d FROM #l " +
                "WHERE op = ''LOP_DELETE_ROWS'' AND au IS NOT NULL AND au NOT LIKE ''sys.%'' GROUP BY tid, au; " +
                "SELECT TOP (@m) b.tid, MAX(b.tname), MIN(b.btime), " +
                "  ISNULL((SELECT MAX(d.n) FROM #d d WHERE d.au_tid = b.tid), 0), " +
                "  (SELECT TOP (1) d.au FROM #d d WHERE d.au_tid = b.tid ORDER BY d.n DESC) " +
                "FROM #l b WHERE b.op = ''LOP_BEGIN_XACT'' " +
                "GROUP BY b.tid " +
                "HAVING EXISTS (SELECT 1 FROM #d d WHERE d.au_tid = b.tid) " +
                "    OR MAX(b.tname) = ''DROPOBJ'' OR MAX(b.tname) LIKE ''TRUNCATE%'' " +
                "ORDER BY MIN(b.btime) DESC;';\n" +
                "EXEC @proc @stmt, N'@m int', @m = @max;";
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand(sql, _connection) { CommandTimeout = AppConstants.RepairCommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@db", databaseName);
            cmd.Parameters.AddWithValue("@max", max);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var txName = r.IsDBNull(1) ? string.Empty : r.GetString(1);
                var beginRaw = r.IsDBNull(2) ? string.Empty : Convert.ToString(r.GetValue(2)) ?? string.Empty;
                var deletes = r.IsDBNull(3) ? 0 : Convert.ToInt32(r.GetValue(3));
                var allocUnit = r.IsDBNull(4) ? string.Empty : r.GetString(4);
                var kind = txName.Equals("DROPOBJ", StringComparison.OrdinalIgnoreCase) ? "DROP"
                         : txName.StartsWith("TRUNCATE", StringComparison.OrdinalIgnoreCase) ? "TRUNCATE"
                         : "DELETE";
                list.Add(new ForensicTransaction
                {
                    TransactionId = r.IsDBNull(0) ? string.Empty : Convert.ToString(r.GetValue(0)) ?? string.Empty,
                    Kind = kind,
                    BeginTimeRaw = beginRaw,
                    BeginTime = ForensicTransaction.ParseLogTime(beginRaw),
                    RowsAffected = deletes,
                    Table = ForensicTransaction.TableFromAllocUnit(allocUnit)
                });
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* fn_dblog indisponível / sem permissão */ }
        return list;
    }

    /// <summary>Lista os índices DESABILITADOS do banco (tabela.índice). Best-effort.</summary>
    public async Task<List<string>> GetDisabledIndexesAsync(string databaseName, CancellationToken ct = default)
    {
        var list = new List<string>();
        try
        {
            const string sql =
                "DECLARE @proc nvarchar(300) = QUOTENAME(@db) + N'.sys.sp_executesql';\n" +
                "DECLARE @stmt nvarchar(max) = N'SELECT OBJECT_NAME(i.object_id) + N''.'' + i.name " +
                "FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id " +
                "WHERE i.is_disabled = 1 AND t.is_ms_shipped = 0;';\n" +
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

    /// <summary>
    /// Lista referências QUEBRADAS de objetos (views/procedures que referenciam um objeto que não
    /// existe mais), via sys.sql_expression_dependencies. Best-effort (2008+).
    /// </summary>
    public async Task<List<string>> GetBrokenDependenciesAsync(string databaseName, CancellationToken ct = default)
    {
        var list = new List<string>();
        try
        {
            const string sql =
                "DECLARE @proc nvarchar(300) = QUOTENAME(@db) + N'.sys.sp_executesql';\n" +
                "DECLARE @stmt nvarchar(max) = N'SELECT DISTINCT OBJECT_NAME(d.referencing_id) + N'' → '' + " +
                "ISNULL(d.referenced_schema_name + N''.'', N'''') + d.referenced_entity_name " +
                "FROM sys.sql_expression_dependencies d " +
                "WHERE d.referenced_id IS NULL AND d.referenced_entity_name IS NOT NULL " +
                "AND d.is_ambiguous = 0 AND OBJECT_NAME(d.referencing_id) IS NOT NULL " +
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
        catch { /* DMV ausente / sem permissão */ }
        return list;
    }

    /// <summary>
    /// Fragmentação dos índices do banco (sys.dm_db_index_physical_stats, modo LIMITED) com pelo menos
    /// <paramref name="minPages"/> páginas. Nomes já vêm com QUOTENAME. Best-effort.
    /// </summary>
    public async Task<List<IndexFragmentation>> GetFragmentationAsync(string databaseName, int minPages = 100, CancellationToken ct = default)
    {
        var list = new List<IndexFragmentation>();
        try
        {
            const string sql =
                "DECLARE @proc nvarchar(300) = QUOTENAME(@db) + N'.sys.sp_executesql';\n" +
                "DECLARE @stmt nvarchar(max) = N'" +
                "SELECT QUOTENAME(s.name) + N''.'' + QUOTENAME(o.name), i.name, ips.avg_fragmentation_in_percent, ips.page_count " +
                "FROM sys.dm_db_index_physical_stats(DB_ID(), NULL, NULL, NULL, ''LIMITED'') ips " +
                "JOIN sys.indexes i ON i.object_id = ips.object_id AND i.index_id = ips.index_id " +
                "JOIN sys.objects o ON o.object_id = ips.object_id " +
                "JOIN sys.schemas s ON s.schema_id = o.schema_id " +
                "WHERE ips.index_id > 0 AND ips.page_count >= @min AND i.name IS NOT NULL AND o.is_ms_shipped = 0 " +
                "ORDER BY ips.avg_fragmentation_in_percent DESC;';\n" +
                "EXEC @proc @stmt, N'@min int', @min = @minp;";
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand(sql, _connection) { CommandTimeout = AppConstants.RepairCommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@db", databaseName);
            cmd.Parameters.AddWithValue("@minp", minPages);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                list.Add(new IndexFragmentation
                {
                    Table = r.IsDBNull(0) ? string.Empty : r.GetString(0),
                    Index = r.IsDBNull(1) ? string.Empty : r.GetString(1),
                    FragmentationPercent = r.IsDBNull(2) ? 0 : Convert.ToDouble(r.GetValue(2)),
                    PageCount = r.IsDBNull(3) ? 0 : Convert.ToInt64(r.GetValue(3))
                });
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* DMF indisponível / sem permissão */ }
        return list;
    }

    /// <summary>true quando a edição da instância suporta CRIAR backups com compressão (não-Express).</summary>
    public async Task<bool> SupportsBackupCompressionAsync(CancellationToken ct = default)
    {
        try
        {
            // EngineEdition: 2 = Standard, 3 = Enterprise/Developer (suportam). 4 = Express (não).
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand("SELECT CAST(SERVERPROPERTY('EngineEdition') AS int);", _connection)
            { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
            var v = await cmd.ExecuteScalarAsync(ct);
            var edition = v is null || v == DBNull.Value ? 0 : Convert.ToInt32(v);
            return edition == 2 || edition == 3;
        }
        catch { return false; }
    }

    /// <summary>
    /// Contagem de linhas por tabela (schema.tabela → linhas) a partir de sys.dm_db_partition_stats —
    /// usa METADADOS (não lê páginas), então funciona mesmo num banco corrompido. Usado pela
    /// simulação de reparo para medir a perda (antes × depois). Best-effort.
    /// </summary>
    public async Task<Dictionary<string, long>> GetTableRowCountsAsync(string databaseName, CancellationToken ct = default)
    {
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            const string sql =
                "DECLARE @proc nvarchar(300) = QUOTENAME(@db) + N'.sys.sp_executesql';\n" +
                "DECLARE @stmt nvarchar(max) = N'SELECT s.name + N''.'' + t.name, SUM(ps.row_count) " +
                "FROM sys.dm_db_partition_stats ps " +
                "JOIN sys.tables t ON t.object_id = ps.object_id " +
                "JOIN sys.schemas s ON s.schema_id = t.schema_id " +
                "WHERE ps.index_id IN (0,1) AND t.is_ms_shipped = 0 GROUP BY s.name, t.name;';\n" +
                "EXEC @proc @stmt;";
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand(sql, _connection) { CommandTimeout = AppConstants.RepairCommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@db", databaseName);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                if (!r.IsDBNull(0)) map[r.GetString(0)] = r.IsDBNull(1) ? 0 : Convert.ToInt64(r.GetValue(1));
        }
        catch (OperationCanceledException) { throw; }
        catch { /* best-effort */ }
        return map;
    }

    /// <summary>Lista as tabelas de usuário (schema.tabela) do banco alvo. Best-effort.</summary>
    public async Task<List<string>> ListUserTablesAsync(string databaseName, CancellationToken ct = default)
    {
        var list = new List<string>();
        try
        {
            const string sql =
                "DECLARE @proc nvarchar(300) = QUOTENAME(@db) + N'.sys.sp_executesql';\n" +
                "DECLARE @stmt nvarchar(max) = N'SELECT s.name + N''.'' + t.name FROM sys.tables t " +
                "JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE t.is_ms_shipped = 0 ORDER BY s.name, t.name;';\n" +
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

    /// <summary>
    /// SALVAMENTO de dados: copia da tabela de <paramref name="fromDb"/> para a mesma tabela em
    /// <paramref name="toDb"/> APENAS as linhas que ainda não existem (por chave primária). Preserva
    /// IDENTITY (SET IDENTITY_INSERT) e não duplica registros. Exige PK. Devolve as linhas inseridas.
    /// Roda no contexto de <paramref name="toDb"/>, referenciando a origem por nome de 3 partes.
    /// </summary>
    public async Task<int> SalvageTableAsync(string fromDb, string toDb, string schemaTable, CancellationToken ct = default)
    {
        var (schema, table) = SplitSchemaTable(schemaTable);
        var qTarget = $"{Quote(schema)}.{Quote(table)}";                          // no contexto de toDb
        var qSource = $"{Quote(fromDb)}.{Quote(schema)}.{Quote(table)}";          // 3 partes

        // Colunas não-computadas (na ordem) + se há IDENTITY.
        var (cols, hasIdentity) = await GetInsertableColumnsAsync(toDb, schema, table, ct);
        if (cols.Count == 0)
            throw new InvalidOperationException($"Tabela '{schemaTable}' não encontrada em [{toDb}].");

        // Colunas da chave primária (para não duplicar).
        var pk = await GetPrimaryKeyColumnsAsync(toDb, schema, table, ct);
        if (pk.Count == 0)
            throw new InvalidOperationException($"A tabela '{schemaTable}' não tem chave primária — o salvamento seguro (sem duplicar) não é possível.");

        var colList = string.Join(", ", cols.Select(Quote));
        var joinOn = string.Join(" AND ", pk.Select(p => $"dst.{Quote(p)} = src.{Quote(p)}"));
        var insert =
            $"INSERT INTO {qTarget} ({colList}) SELECT {colList} FROM {qSource} src " +
            $"WHERE NOT EXISTS (SELECT 1 FROM {qTarget} dst WHERE {joinOn});";

        var (rows, err, msg) = await RunRecoveryWriteAsync(toDb, qTarget, insert, hasIdentity, ct);
        if (err != 0)
            throw new InvalidOperationException($"Msg {err}: {msg}");
        return rows;
    }

    /// <summary>Colunas da chave primária de uma tabela (na ordem da chave). Vazio = sem PK.</summary>
    public async Task<List<string>> GetPrimaryKeyColumnsAsync(string db, string schema, string table, CancellationToken ct = default)
    {
        var pk = new List<string>();
        var sql =
            $"SELECT c.name FROM {Quote(db)}.sys.indexes i " +
            $"JOIN {Quote(db)}.sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id " +
            $"JOIN {Quote(db)}.sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id " +
            $"WHERE i.is_primary_key = 1 AND i.object_id = OBJECT_ID(@o) ORDER BY ic.key_ordinal;";
        await EnsureOpenAsync(ct);
        await using var cmd = new SqlCommand(sql, _connection) { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
        cmd.Parameters.AddWithValue("@o", $"{Quote(db)}.{Quote(schema)}.{Quote(table)}");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            pk.Add(r.GetString(0));
        return pk;
    }

    /// <summary>
    /// Executa uma escrita de RECUPERAÇÃO (reinserir linhas salvas de um backup, mover órfãos para a
    /// quarentena) no contexto de <paramref name="db"/> com os GATILHOS da tabela alvo DESLIGADOS.
    ///
    /// Por quê: essas linhas JÁ existiram — os efeitos dos gatilhos (baixa de estoque, auditoria,
    /// lançamentos...) já aconteceram quando foram gravadas pela aplicação. Deixar um gatilho de INSERT/
    /// DELETE disparar de novo duplicaria regras de negócio. Tudo roda numa transação: o DISABLE TRIGGER
    /// segura o bloqueio de esquema até o COMMIT, então nenhuma outra sessão grava na tabela sem os
    /// gatilhos nesse intervalo; em erro, o ROLLBACK desfaz tudo (inclusive o DISABLE).
    ///
    /// <paramref name="statement"/> é o DML (pode ter vários comandos); o nº de linhas do ÚLTIMO comando
    /// é devolvido. Erros do servidor não lançam: voltam como (−1, número, mensagem) para o chamador
    /// distinguir, p.ex., violação de PK (2627/2601). Compatível com 2008 R2 (sem THROW).
    /// </summary>
    internal async Task<(int Rows, int ErrorNumber, string ErrorMessage)> RunRecoveryWriteAsync(
        string db, string quotedTargetTable, string statement, bool identityInsert, CancellationToken ct)
    {
        var inner =
            (identityInsert ? $"SET IDENTITY_INSERT {quotedTargetTable} ON; " : string.Empty) +
            statement + " SET @rc = @@ROWCOUNT;" +
            (identityInsert ? $" SET IDENTITY_INSERT {quotedTargetTable} OFF;" : string.Empty);

        const string stmt =
            "SET XACT_ABORT ON; " +
            "DECLARE @rc int = 0, @dis nvarchar(max) = N'', @en nvarchar(max) = N''; " +
            "SELECT @dis = @dis + N'DISABLE TRIGGER ' + QUOTENAME(name) + N' ON ' + @tbl + N'; ', " +
            "       @en  = @en  + N'ENABLE TRIGGER '  + QUOTENAME(name) + N' ON ' + @tbl + N'; ' " +
            "FROM sys.triggers WHERE parent_id = OBJECT_ID(@tbl) AND is_disabled = 0; " +
            "BEGIN TRY " +
            "  BEGIN TRAN; " +
            "  IF @dis <> N'' EXEC sys.sp_executesql @dis; " +
            "  EXEC sys.sp_executesql @ins, N'@rc int OUTPUT', @rc = @rc OUTPUT; " +
            "  IF @en <> N'' EXEC sys.sp_executesql @en; " +
            "  COMMIT; " +
            "  SELECT @rc, 0, N''; " +
            "END TRY " +
            "BEGIN CATCH " +
            "  IF @@TRANCOUNT > 0 ROLLBACK; " +
            "  SELECT -1, ERROR_NUMBER(), ERROR_MESSAGE(); " +
            "END CATCH";
        const string runner =
            "DECLARE @proc nvarchar(300) = QUOTENAME(@db) + N'.sys.sp_executesql';\n" +
            "EXEC @proc @stmt, N'@tbl nvarchar(600), @ins nvarchar(max)', @tbl = @target, @ins = @inner;";

        await EnsureOpenAsync(ct);
        await using var cmd = new SqlCommand(runner, _connection) { CommandTimeout = AppConstants.RepairCommandTimeoutSeconds };
        cmd.Parameters.AddWithValue("@db", db);
        cmd.Parameters.AddWithValue("@stmt", stmt);
        cmd.Parameters.AddWithValue("@target", quotedTargetTable);
        cmd.Parameters.AddWithValue("@inner", inner);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return (0, 0, string.Empty);
        return (Convert.ToInt32(r.GetValue(0)), Convert.ToInt32(r.GetValue(1)), r.IsDBNull(2) ? string.Empty : r.GetString(2));
    }

    /// <summary>
    /// Descobre as colunas INSERÍVEIS de uma tabela (não-computadas, na ordem de column_id) e se a
    /// tabela tem coluna IDENTITY. Consulta o catálogo do banco alvo por nome de 3 partes (OBJECT_ID
    /// resolve cross-database). Reutilizado por <see cref="SalvageTableAsync"/> e pela recuperação
    /// de registros-pai a partir de backup.
    /// </summary>
    private async Task<(List<string> Columns, bool HasIdentity)> GetInsertableColumnsAsync(
        string db, string schema, string table, CancellationToken ct)
    {
        var cols = new List<string>();
        var hasIdentity = false;
        var objRef = $"{Quote(db)}.{Quote(schema)}.{Quote(table)}";
        // Exclui colunas COMPUTADAS e rowversion/timestamp (system_type_id 189) — nenhuma delas aceita
        // valor explícito num INSERT; incluí-las derrubaria o INSERT ... SELECT (erro 273).
        var sql =
            $"SELECT c.name, c.is_identity FROM {Quote(db)}.sys.columns c " +
            $"WHERE c.object_id = OBJECT_ID(@o) AND c.is_computed = 0 AND c.system_type_id <> 189 " +
            $"ORDER BY c.column_id;";
        await EnsureOpenAsync(ct);
        await using var cmd = new SqlCommand(sql, _connection) { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
        cmd.Parameters.AddWithValue("@o", objRef);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            cols.Add(r.GetString(0));
            if (!r.IsDBNull(1) && r.GetBoolean(1)) hasIdentity = true;
        }
        return (cols, hasIdentity);
    }

    private static (string schema, string table) SplitSchemaTable(string schemaTable)
    {
        var s = schemaTable.Trim();
        // remove colchetes externos se vierem
        var idx = s.IndexOf('.');
        if (idx <= 0) return ("dbo", Unbracket(s));
        return (Unbracket(s[..idx]), Unbracket(s[(idx + 1)..]));
    }

    private static string Quote(string ident) => "[" + ident.Replace("]", "]]") + "]";

    /// <summary>
    /// Reseta a semente de IDENTITY de todas as tabelas do banco (DBCC CHECKIDENT ... RESEED) — útil
    /// após perda de linhas, para evitar saltos/colisões de identidade. Best-effort; devolve quantas
    /// tabelas foram processadas. Roda no contexto do banco alvo.
    /// </summary>
    public async Task<int> ReseedIdentitiesAsync(string databaseName, CancellationToken ct = default)
    {
        // 1) Tabelas com coluna IDENTITY (nome simples schema.tabela, para o literal do CHECKIDENT).
        var tables = new List<string>();
        try
        {
            const string q =
                "DECLARE @proc nvarchar(300) = QUOTENAME(@db) + N'.sys.sp_executesql';\n" +
                "DECLARE @stmt nvarchar(max) = N'SELECT s.name + N''.'' + t.name FROM sys.tables t " +
                "JOIN sys.schemas s ON s.schema_id = t.schema_id " +
                "WHERE t.is_ms_shipped = 0 AND EXISTS (SELECT 1 FROM sys.identity_columns ic WHERE ic.object_id = t.object_id);';\n" +
                "EXEC @proc @stmt;";
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand(q, _connection) { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@db", databaseName);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                if (!r.IsDBNull(0)) tables.Add(r.GetString(0));
        }
        catch (OperationCanceledException) { throw; }
        catch { return 0; }

        // 2) RESEED de cada uma (CHECKIDENT recebe o nome como string literal).
        var done = 0;
        foreach (var t in tables)
        {
            try
            {
                var inner = $"DBCC CHECKIDENT ('{t.Replace("'", "''")}', RESEED) WITH NO_INFOMSGS;";
                const string sql =
                    "DECLARE @proc nvarchar(300) = QUOTENAME(@db) + N'.sys.sp_executesql';\n" +
                    "EXEC @proc @stmt;";
                await EnsureOpenAsync(ct);
                await using var cmd = new SqlCommand(sql, _connection) { CommandTimeout = AppConstants.RepairCommandTimeoutSeconds };
                cmd.Parameters.AddWithValue("@db", databaseName);
                cmd.Parameters.AddWithValue("@stmt", inner);
                await cmd.ExecuteNonQueryAsync(ct);
                done++;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* tabela específica falhou: segue */ }
        }
        return done;
    }

    /// <summary>Pasta de DADOS padrão da instância (derivada do master). Robusto em qualquer versão.</summary>
    public Task<string> GetDefaultDataDirAsync(CancellationToken ct = default) => GetMasterFileDirAsync(0, ct);

    /// <summary>Pasta de LOG padrão da instância (derivada do master).</summary>
    public Task<string> GetDefaultLogDirAsync(CancellationToken ct = default) => GetMasterFileDirAsync(1, ct);

    private async Task<string> GetMasterFileDirAsync(int fileType, CancellationToken ct)
    {
        try
        {
            const string sql =
                "SELECT TOP (1) physical_name FROM sys.master_files " +
                "WHERE database_id = DB_ID(N'master') AND type = @t ORDER BY file_id;";
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand(sql, _connection) { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@t", fileType);
            var path = (await cmd.ExecuteScalarAsync(ct)) as string;
            return string.IsNullOrEmpty(path) ? string.Empty : (System.IO.Path.GetDirectoryName(path) ?? string.Empty);
        }
        catch { return string.Empty; }
    }

    /// <summary>
    /// Verifica se um arquivo OU pasta existe no sistema de arquivos DO SERVIDOR (xp_fileexist).
    /// Usado para decidir realocação de arquivos numa restauração. Best-effort (sysadmin).
    /// </summary>
    public async Task<bool> PathExistsOnServerAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            // xp_fileexist devolve: [File Exists], [File is a Directory], [Parent Directory Exists]
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand(
                "DECLARE @r TABLE (FileExists int, IsDir int, ParentExists int);\n" +
                "INSERT INTO @r EXEC master.dbo.xp_fileexist @p;\n" +
                "SELECT CASE WHEN FileExists = 1 OR IsDir = 1 THEN 1 ELSE 0 END FROM @r;", _connection)
            { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@p", path);
            var v = await cmd.ExecuteScalarAsync(ct);
            return v is not null && v != DBNull.Value && Convert.ToInt32(v) == 1;
        }
        catch { return false; }
    }

    /// <summary>
    /// Verifica, no sistema de arquivos DO SERVIDOR, se um ARQUIVO existe. Diferente de
    /// <see cref="PathExistsOnServerAsync"/>, distingue "não existe" (false) de "não foi possível
    /// verificar" (null — sem permissão para xp_fileexist, caminho de rede inacessível à sonda...),
    /// para que quem chama não descarte um backup válido só porque a checagem falhou.
    /// </summary>
    public async Task<bool?> FileExistsOnServerAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand(
                "DECLARE @r TABLE (FileExists int, IsDir int, ParentExists int);\n" +
                "INSERT INTO @r EXEC master.dbo.xp_fileexist @p;\n" +
                "SELECT FileExists, ParentExists FROM @r;", _connection)
            { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@p", path);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            var exists = !r.IsDBNull(0) && Convert.ToInt32(r.GetValue(0)) == 1;
            var parent = !r.IsDBNull(1) && Convert.ToInt32(r.GetValue(1)) == 1;
            if (exists) return true;
            // Pasta-mãe inalcançável num caminho de REDE (compartilhamento fora do ar / sem permissão da
            // conta de serviço) = não dá para afirmar. Em disco local, pasta ausente = arquivo ausente.
            return parent || !path.StartsWith(@"\\", StringComparison.Ordinal) ? false : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>
    /// Executa DBCC CHECKCONSTRAINTS WITH ALL_CONSTRAINTS, ALL_ERRORMSGS no contexto
    /// do banco alvo e devolve as violações de integridade LÓGICA (FK/CHECK) — que o CHECKDB NÃO
    /// detecta. Resolve, para cada violação, se a constraint é uma chave estrangeira (sys.foreign_keys),
    /// pois só FKs têm remoção automática de linhas órfãs. Best-effort. Compatível com 2008 R2.
    /// </summary>
    public async Task<List<ConstraintViolation>> CheckConstraintsAsync(string databaseName, CancellationToken ct = default,
        bool includeDisabled = false)
    {
        var list = new List<ConstraintViolation>();
        try
        {
            // CHECKCONSTRAINTS roda no banco CORRENTE; executamos no contexto do banco alvo.
            // IMPORTANTE: diferente do CHECKDB, o CHECKCONSTRAINTS NÃO aceita TABLERESULTS
            // (Msg 2532 no 2008 R2) — ele já devolve o conjunto Table/Constraint/Where por padrão.
            const string sql =
                "DECLARE @proc nvarchar(300) = QUOTENAME(@db) + N'.sys.sp_executesql';\n" +
                "DECLARE @stmt nvarchar(max) = N'DBCC CHECKCONSTRAINTS WITH ALL_CONSTRAINTS, ALL_ERRORMSGS;';\n" +
                "EXEC @proc @stmt;";
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand(sql, _connection) { CommandTimeout = AppConstants.RepairCommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@db", databaseName);
            await using var reader = await cmd.ExecuteReaderAsync(ct);

            var oTable = Ordinal(reader, "Table");
            var oConstraint = Ordinal(reader, "Constraint");
            var oWhere = Ordinal(reader, "Where");
            while (await reader.ReadAsync(ct))
            {
                list.Add(new ConstraintViolation
                {
                    Table = GetStr(reader, oTable),
                    Constraint = GetStr(reader, oConstraint),
                    Where = GetStr(reader, oWhere)
                });
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { return list; /* banco inacessível / sem permissão */ }

        if (list.Count > 0)
            await ResolveConstraintFlagsAsync(databaseName, list, ct);
        // Constraints DESABILITADAS (NOCHECK) não são aplicadas por decisão do próprio sistema: as linhas
        // que as violam são dados legítimos, não corrupção. Por padrão ficam FORA da lista — assim nenhum
        // fluxo de correção as remove. O diagnóstico pede includeDisabled=true apenas para informar.
        return includeDisabled ? list : list.Where(v => !v.IsDisabled).ToList();
    }

    /// <summary>
    /// Marca quais violações pertencem a chaves estrangeiras e quais constraints estão DESABILITADAS
    /// (sys.foreign_keys / sys.check_constraints do banco alvo).
    /// </summary>
    private async Task ResolveConstraintFlagsAsync(string databaseName, List<ConstraintViolation> violations, CancellationToken ct)
    {
        var fkNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            const string sql =
                "DECLARE @proc nvarchar(300) = QUOTENAME(@db) + N'.sys.sp_executesql';\n" +
                "DECLARE @stmt nvarchar(max) = N'SELECT name, 1, is_disabled FROM sys.foreign_keys " +
                "UNION ALL SELECT name, 0, is_disabled FROM sys.check_constraints;';\n" +
                "EXEC @proc @stmt;";
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand(sql, _connection) { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@db", databaseName);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (r.IsDBNull(0)) continue;
                var name = r.GetString(0);
                if (Convert.ToInt32(r.GetValue(1)) == 1) fkNames.Add(name);
                if (!r.IsDBNull(2) && r.GetBoolean(2)) disabled.Add(name);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* best-effort */ }

        foreach (var v in violations)
        {
            var name = Unbracket(v.Constraint);
            v.IsForeignKey = fkNames.Contains(name);
            v.IsDisabled = disabled.Contains(name);
        }
    }

    /// <summary>Remove um nível de QUOTENAME: "[Nome]]X]" → "Nome]X". A saída do CHECKCONSTRAINTS
    /// devolve os nomes já entre colchetes (ex.: "[FK_Pedido_Cliente]").</summary>
    private static string Unbracket(string name)
    {
        if (name.Length >= 2 && name[0] == '[' && name[^1] == ']')
            return name[1..^1].Replace("]]", "]");
        return name;
    }

    /// <summary>Lista os nomes de constraints (FK/CHECK) marcadas como NÃO confiáveis no banco alvo. Best-effort.</summary>
    public async Task<List<string>> GetUntrustedConstraintsAsync(string databaseName, CancellationToken ct = default)
    {
        var list = new List<string>();
        try
        {
            const string sql =
                "DECLARE @proc nvarchar(300) = QUOTENAME(@db) + N'.sys.sp_executesql';\n" +
                "DECLARE @stmt nvarchar(max) = N'SELECT name FROM sys.foreign_keys WHERE is_not_trusted = 1 " +
                "UNION ALL SELECT name FROM sys.check_constraints WHERE is_not_trusted = 1;';\n" +
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

    /// <summary>
    /// Executa DBCC CHECKCATALOG (consistência de metadados) no banco alvo. Devolve as mensagens
    /// de ERRO encontradas (vazio = catálogo íntegro). CHECKCATALOG não suporta TABLERESULTS;
    /// capturamos as mensagens via InfoMessage/SqlException. Best-effort.
    /// </summary>
    public async Task<List<string>> CheckCatalogAsync(string databaseName, CancellationToken ct = default)
    {
        var errors = new List<string>();
        void OnInfoMessage(object sender, SqlInfoMessageEventArgs e)
        {
            foreach (SqlError err in e.Errors)
                if (err.Class > 10) errors.Add(FormatMessage(err)); // Class <= 10 = informativo (PRINT/DBCC ok)
        }

        _connection.InfoMessage += OnInfoMessage;
        try
        {
            // CHECKCATALOG aceita o nome do banco diretamente (parametrizado via QUOTENAME).
            const string sql =
                "DECLARE @cmd nvarchar(max) = N'DBCC CHECKCATALOG (' + QUOTENAME(@db) + N') WITH NO_INFOMSGS;';\n" +
                "EXEC sys.sp_executesql @cmd;";
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand(sql, _connection) { CommandTimeout = AppConstants.RepairCommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@db", databaseName);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (SqlException ex)
        {
            foreach (SqlError err in ex.Errors)
                errors.Add(FormatMessage(err));
        }
        catch { /* sem permissão / banco inacessível */ }
        finally
        {
            _connection.InfoMessage -= OnInfoMessage;
        }
        return errors;
    }

    /// <summary>
    /// Remove as linhas violadoras de uma constraint (DELETE FROM tabela WHERE predicado), no
    /// contexto do banco alvo, usando o predicado gerado pelo próprio SQL Server (CHECKCONSTRAINTS).
    /// Operação DESTRUTIVA — use apenas após confirmação. Devolve o nº de linhas removidas.
    /// </summary>
    public async Task<int> DeleteViolatingRowsAsync(string databaseName, ConstraintViolation violation, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(violation.Table) || string.IsNullOrWhiteSpace(violation.Where))
            return 0;

        // O DELETE roda NO CONTEXTO do banco alvo. Tabela e predicado vêm de metadados do servidor
        // (DBCC CHECKCONSTRAINTS), não de entrada do usuário. Gatilhos desligados: é um reparo de dados,
        // não uma exclusão de negócio (um gatilho de DELETE poderia, p.ex., estornar estoque).
        var (rows, err, msg) = await RunRecoveryWriteAsync(databaseName, violation.Table,
            $"DELETE FROM {violation.Table} WHERE {violation.Where};", identityInsert: false, ct);
        if (err != 0) throw new InvalidOperationException($"Msg {err}: {msg}");
        return rows;
    }

    /// <summary>
    /// Remove as linhas violadoras de uma FK MOVENDO-AS para a QUARENTENA — uma tabela no banco
    /// <see cref="AppConstants.QuarantineDatabaseName"/> (mesma instância), com a data e a constraint de
    /// origem — numa ÚNICA transação (cópia + exclusão; nunca uma sem a outra). A remoção deixa de ser
    /// irreversível: as linhas podem ser consultadas e devolvidas depois (ex.: quando o registro-pai for
    /// recadastrado). Se a quarentena não puder ser usada (tipo de coluna não copiável, sem espaço...), cai
    /// para a exclusão simples de antes. Devolve (linhas removidas, tabela de quarentena ou null).
    /// </summary>
    public async Task<(int Rows, string? QuarantineTable)> QuarantineViolatingRowsAsync(
        string databaseName, ConstraintViolation violation, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(violation.Table) || string.IsNullOrWhiteSpace(violation.Where))
            return (0, null);

        string? qTable = null;
        try { qTable = await PrepareQuarantineTableAsync(databaseName, violation.Table, forceNew: false, ct); }
        catch (OperationCanceledException) { throw; }
        catch { /* sem quarentena: exclusão simples abaixo */ }

        for (var attempt = 0; qTable is not null && attempt < 2; attempt++)
        {
            var (selectList, colNames) = await GetQuarantineColumnsAsync(databaseName, violation.Table, ct);
            var move =
                $"INSERT INTO {qTable} ([_RestoreDB_QuarentenadoEm], [_RestoreDB_Constraint], {colNames}) " +
                $"SELECT GETDATE(), N'{violation.Constraint.Replace("'", "''")}', {selectList} FROM {violation.Table} t WHERE {violation.Where}; " +
                $"DELETE FROM {violation.Table} WHERE {violation.Where};";
            var (rows, err, _) = await RunRecoveryWriteAsync(databaseName, violation.Table, move, identityInsert: false, ct);
            if (err == 0) return (rows, qTable);

            // A tabela de quarentena existente pode ter o formato de uma versão ANTERIOR da tabela
            // (colunas alteradas desde então): cria uma nova, com sufixo de data, e tenta mais uma vez.
            qTable = attempt == 0
                ? await TryOrNullAsync(() => PrepareQuarantineTableAsync(databaseName, violation.Table, forceNew: true, ct))
                : null;
        }

        return (await DeleteViolatingRowsAsync(databaseName, violation, ct), null);
    }

    private static async Task<string?> TryOrNullAsync(Func<Task<string>> f)
    {
        try { return await f(); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>
    /// Garante o banco de quarentena e a tabela para a tabela de origem, com as colunas de controle
    /// (_RestoreDB_QuarentenadoEm / _RestoreDB_Constraint) + as colunas da origem. O CROSS JOIN no
    /// SELECT INTO evita herdar a propriedade IDENTITY (junto da expressão na coluna identidade — ver GetQuarantineColumnsAsync).
    /// Devolve o nome de 3 partes já entre colchetes.
    /// </summary>
    private async Task<string> PrepareQuarantineTableAsync(string databaseName, string quotedTable, bool forceNew, CancellationToken ct)
    {
        var (schema, table) = SplitSchemaTable(quotedTable);
        var baseName = $"{AppConstants.SanitizeToken(databaseName)}__{schema}__{table}";
        if (forceNew) baseName += "_" + DateTime.Now.ToString("yyyyMMddHHmmss");
        var qdb = Quote(AppConstants.QuarantineDatabaseName);
        var target = $"{qdb}.[dbo].{Quote(baseName)}";

        await EnsureOpenAsync(ct);
        await using (var mk = new SqlCommand(
            "IF DB_ID(@q) IS NULL\n" +
            "BEGIN\n" +
            "  DECLARE @c nvarchar(max) = N'CREATE DATABASE ' + QUOTENAME(@q) + N';'; EXEC sys.sp_executesql @c;\n" +
            "  SET @c = N'ALTER DATABASE ' + QUOTENAME(@q) + N' SET RECOVERY SIMPLE;'; EXEC sys.sp_executesql @c;\n" +
            "END", _connection) { CommandTimeout = AppConstants.RepairCommandTimeoutSeconds })
        {
            mk.Parameters.AddWithValue("@q", AppConstants.QuarantineDatabaseName);
            await mk.ExecuteNonQueryAsync(ct);
        }

        var (selectList, _) = await GetQuarantineColumnsAsync(databaseName, quotedTable, ct);
        var create =
            $"IF OBJECT_ID(N'{target.Replace("'", "''")}') IS NULL " +
            $"SELECT TOP (0) CAST(NULL AS datetime) AS [_RestoreDB_QuarentenadoEm], CAST(NULL AS nvarchar(256)) AS [_RestoreDB_Constraint], {selectList} " +
            $"INTO {target} FROM {Quote(databaseName)}.{Quote(schema)}.{Quote(table)} t CROSS JOIN (SELECT 1 AS _x) AS _j;";
        await using (var cmd = new SqlCommand(create, _connection) { CommandTimeout = AppConstants.RepairCommandTimeoutSeconds })
            await cmd.ExecuteNonQueryAsync(ct);
        return target;
    }

    /// <summary>
    /// Lista de SELECT (t.[col] ..., rowversion convertido em binary(8) — não aceita valor explícito)
    /// e lista de nomes de coluna da tabela de origem, para copiar linhas para a quarentena.
    /// </summary>
    private async Task<(string SelectList, string ColumnNames)> GetQuarantineColumnsAsync(string databaseName, string quotedTable, CancellationToken ct)
    {
        var (schema, table) = SplitSchemaTable(quotedTable);
        var sel = new List<string>();
        var names = new List<string>();
        var sql =
            $"SELECT c.name, c.system_type_id, c.is_identity FROM {Quote(databaseName)}.sys.columns c " +
            $"WHERE c.object_id = OBJECT_ID(@o) ORDER BY c.column_id;";
        await EnsureOpenAsync(ct);
        await using var cmd = new SqlCommand(sql, _connection) { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
        cmd.Parameters.AddWithValue("@o", $"{Quote(databaseName)}.{Quote(schema)}.{Quote(table)}");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var n = Quote(r.GetString(0));
            var type = Convert.ToInt32(r.GetValue(1));
            var identity = !r.IsDBNull(2) && r.GetBoolean(2);
            // rowversion não aceita valor explícito; a IDENTITY numa expressão não é herdada pelo SELECT INTO
            // (a quarentena guarda o valor original, sem gerar novos).
            sel.Add(type == 189 ? $"CAST(t.{n} AS binary(8)) AS {n}" : identity ? $"t.{n} * 1 AS {n}" : $"t.{n}");
            names.Add(n);
        }
        if (names.Count == 0) throw new InvalidOperationException($"Tabela {quotedTable} não encontrada.");
        return (string.Join(", ", sel), string.Join(", ", names));
    }

    /// <summary>
    /// Resolve, para uma chave estrangeira, o schema/tabela do lado PAI (referenciado) e os pares
    /// ordenados (coluna-filha → coluna-pai) — cobre chaves compostas. Consulta o catálogo do banco
    /// alvo por nomes de 3 partes (estilo 2008 R2-safe, sem INFORMATION_SCHEMA). Devolve null se a FK
    /// não existir ou em caso de erro. Best-effort.
    ///
    /// Atenção à terminologia do SQL Server em sys.foreign_key_columns: "parent" = tabela
    /// referenciadora (a FILHA, que contém a FK); "referenced" = tabela referenciada (o PAI, que
    /// contém a PK). Aqui recuperamos a linha do lado "referenced".
    /// </summary>
    public async Task<ForeignKeyParentMapping?> GetForeignKeyParentMappingAsync(
        string databaseName, string constraintName, CancellationToken ct = default)
    {
        try
        {
            var q = Quote(databaseName);
            var sql =
                $"SELECT sch.name, rt.name, pc.name, rc.name\n" +
                $"FROM {q}.sys.foreign_keys fk\n" +
                $"JOIN {q}.sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id\n" +
                $"JOIN {q}.sys.tables rt ON rt.object_id = fk.referenced_object_id\n" +
                $"JOIN {q}.sys.schemas sch ON sch.schema_id = rt.schema_id\n" +
                $"JOIN {q}.sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id\n" +
                $"JOIN {q}.sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id\n" +
                $"WHERE fk.name = @fk\n" +
                $"ORDER BY fkc.constraint_column_id;";
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand(sql, _connection) { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@fk", constraintName);
            await using var r = await cmd.ExecuteReaderAsync(ct);

            ForeignKeyParentMapping? map = null;
            while (await r.ReadAsync(ct))
            {
                map ??= new ForeignKeyParentMapping
                {
                    ParentSchema = r.IsDBNull(0) ? "dbo" : r.GetString(0),
                    ParentTable = r.IsDBNull(1) ? string.Empty : r.GetString(1)
                };
                map.ColumnPairs.Add(new ForeignKeyColumnPair
                {
                    ChildColumn = r.IsDBNull(2) ? string.Empty : r.GetString(2),
                    ParentColumn = r.IsDBNull(3) ? string.Empty : r.GetString(3)
                });
            }
            return map;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>
    /// Tenta reinserir no banco VIVO (<paramref name="liveDb"/>) a linha-pai localizada na CÓPIA do
    /// backup (<paramref name="scratchDb"/>) pelo <paramref name="predicate"/> — que carrega o valor
    /// exato da chave que falta. Preserva a chave (SET IDENTITY_INSERT quando há identidade). Distingue:
    /// inserida / já existia (violação de PK 2627/2601, sucesso silencioso) / não encontrada na cópia /
    /// erro inesperado. Best-effort — nunca lança (exceto cancelamento). Roda no contexto de liveDb
    /// (a mesma sessão que detém o SINGLE_USER), lendo a origem por nome de 3 partes — o mesmo idioma
    /// cross-database de <see cref="SalvageTableAsync"/>, sem linked server.
    /// </summary>
    public async Task<ParentRecoveryOutcome> TryInsertRecoveredParentRowAsync(
        string liveDb, string scratchDb, string schema, string parentTable, string predicate, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(parentTable) || string.IsNullOrWhiteSpace(predicate))
            return ParentRecoveryOutcome.Failed;

        List<string> cols;
        bool hasIdentity;
        try { (cols, hasIdentity) = await GetInsertableColumnsAsync(liveDb, schema, parentTable, ct); }
        catch (OperationCanceledException) { throw; }
        catch { return ParentRecoveryOutcome.Failed; }
        if (cols.Count == 0) return ParentRecoveryOutcome.Failed;

        var qTarget = $"{Quote(schema)}.{Quote(parentTable)}";                     // no contexto de liveDb
        var qSource = $"{Quote(scratchDb)}.{Quote(schema)}.{Quote(parentTable)}";  // 3 partes
        var colList = string.Join(", ", cols.Select(Quote));

        // O predicado usa as colunas do PAI (= valor exato da PK ausente), reescrito na camada de serviço.
        // Gatilhos desligados: o registro-pai JÁ existiu; seus efeitos de negócio já foram aplicados.
        var insert = $"INSERT INTO {qTarget} ({colList}) SELECT {colList} FROM {qSource} src WHERE {predicate};";
        try
        {
            var (rows, err, _) = await RunRecoveryWriteAsync(liveDb, qTarget, insert, hasIdentity, ct);
            // Violação de PK: a linha-pai já existe no banco vivo — a FK já não está órfã por essa chave.
            if (err is 2627 or 2601) return ParentRecoveryOutcome.AlreadyPresent;
            if (err != 0) return ParentRecoveryOutcome.Failed;
            return rows > 0 ? ParentRecoveryOutcome.Inserted : ParentRecoveryOutcome.NotFoundInBackup;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return ParentRecoveryOutcome.Failed;
        }
    }

    /// <summary>Resolve o nome de um objeto (tabela/índice) a partir do object_id, dentro do banco alvo.</summary>
    public async Task<string> ResolveObjectNameAsync(string databaseName, int objectId, CancellationToken ct = default)
    {
        if (objectId == 0) return string.Empty;
        try
        {
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand("SELECT OBJECT_NAME(@oid, DB_ID(@db));", _connection)
            { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@oid", objectId);
            cmd.Parameters.AddWithValue("@db", databaseName);
            return (await cmd.ExecuteScalarAsync(ct)) as string ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    /// <summary>"schema.tabela" (sem colchetes) de um object_id no banco alvo; vazio se não resolver.</summary>
    public async Task<string> ResolveSchemaTableAsync(string databaseName, int objectId, CancellationToken ct = default)
    {
        if (objectId == 0) return string.Empty;
        try
        {
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand(
                "SELECT OBJECT_SCHEMA_NAME(@oid, DB_ID(@db)) + N'.' + OBJECT_NAME(@oid, DB_ID(@db));", _connection)
            { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@oid", objectId);
            cmd.Parameters.AddWithValue("@db", databaseName);
            return (await cmd.ExecuteScalarAsync(ct)) as string ?? string.Empty;
        }
        catch (OperationCanceledException) { throw; }
        catch { return string.Empty; }
    }

    /// <summary>Resolve o nome de um índice (object_id + index_id) no banco alvo. Best-effort.</summary>
    public async Task<string> ResolveIndexNameAsync(string databaseName, int objectId, int indexId, CancellationToken ct = default)
    {
        if (objectId == 0) return string.Empty;
        try
        {
            var sql =
                "DECLARE @cmd nvarchar(max);\n" +
                "SET @cmd = N'SELECT name FROM ' + QUOTENAME(@db) + N'.sys.indexes WHERE object_id=@o AND index_id=@i';\n" +
                "EXEC sys.sp_executesql @cmd, N'@o int, @i int', @o = @oid, @i = @iid;";
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand(sql, _connection) { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@db", databaseName);
            cmd.Parameters.AddWithValue("@oid", objectId);
            cmd.Parameters.AddWithValue("@iid", indexId);
            return (await cmd.ExecuteScalarAsync(ct)) as string ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    /// <summary>
    /// Detecta topologia de alta disponibilidade/replicação do banco (espelhamento, Always On,
    /// log shipping, replicação). Cada sonda é isolada: DMVs inexistentes em versões antigas
    /// (ex.: Always On no 2008 R2) apenas não contribuem.
    /// </summary>
    public async Task<TopologyInfo> GetTopologyInfoAsync(string databaseName, CancellationToken ct = default)
    {
        var topo = new TopologyInfo();

        // Espelhamento (existe desde o 2005)
        await ProbeAsync(async () =>
        {
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand(
                "SELECT mirroring_role_desc, mirroring_state_desc FROM sys.database_mirroring " +
                "WHERE database_id = DB_ID(@db) AND mirroring_guid IS NOT NULL;", _connection)
            { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@db", databaseName);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                topo.IsMirrored = true;
                topo.MirroringRole = r.IsDBNull(0) ? string.Empty : r.GetString(0);
                topo.MirroringState = r.IsDBNull(1) ? string.Empty : r.GetString(1);
                topo.Warnings.Add("Banco espelhado: religar/desanexar quebra o espelhamento. Faça failover/remova o espelhamento primeiro.");
            }
        });

        // Always On AG (2012+)
        await ProbeAsync(async () =>
        {
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand(
                "SELECT ag.name FROM sys.databases d " +
                "JOIN sys.availability_databases_cluster adc ON adc.database_name = d.name " +
                "JOIN sys.availability_groups ag ON ag.group_id = adc.group_id " +
                "WHERE d.name = @db;", _connection)
            { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@db", databaseName);
            var name = await cmd.ExecuteScalarAsync(ct) as string;
            if (!string.IsNullOrEmpty(name))
            {
                topo.IsInAvailabilityGroup = true;
                topo.AvailabilityGroupName = name;
                topo.Warnings.Add("Banco em grupo de disponibilidade (Always On): remova-o do AG antes de qualquer reparo destrutivo.");
            }
        });

        // Replicação (colunas existentes no 2008 R2)
        await ProbeAsync(async () =>
        {
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand(
                "SELECT is_published, is_subscribed, is_merge_published FROM sys.databases WHERE name = @db;", _connection)
            { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@db", databaseName);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                topo.IsPublished = !r.IsDBNull(0) && r.GetBoolean(0);
                topo.IsSubscribed = !r.IsDBNull(1) && r.GetBoolean(1);
                topo.IsMergePublished = !r.IsDBNull(2) && r.GetBoolean(2);
                if (topo.IsPublished || topo.IsSubscribed || topo.IsMergePublished)
                    topo.Warnings.Add("Banco em replicação: reparo destrutivo/restore pode exigir reconfigurar a replicação.");
            }
        });

        // Log shipping (msdb)
        await ProbeAsync(async () =>
        {
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand(
                "SELECT (SELECT COUNT(*) FROM msdb.dbo.log_shipping_primary_databases WHERE primary_database = @db), " +
                "       (SELECT COUNT(*) FROM msdb.dbo.log_shipping_secondary_databases WHERE secondary_database = @db);", _connection)
            { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@db", databaseName);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                topo.IsLogShippingPrimary = !r.IsDBNull(0) && r.GetInt32(0) > 0;
                topo.IsLogShippingSecondary = !r.IsDBNull(1) && r.GetInt32(1) > 0;
                if (topo.IsLogShippingPrimary || topo.IsLogShippingSecondary)
                    topo.Warnings.Add("Banco em log shipping: RESTORE WITH RECOVERY o remove da cadeia de log shipping.");
            }
        });

        return topo;
    }

    /// <summary>
    /// Espaço livre dos volumes que hospedam os arquivos do banco (sys.dm_os_volume_stats,
    /// 2008 R2 SP1+). Devolve vazio quando a função não está disponível.
    /// </summary>
    public async Task<List<DiskSpaceInfo>> GetDiskSpaceAsync(string databaseName, CancellationToken ct = default)
    {
        var list = new List<DiskSpaceInfo>();
        try
        {
            const string sql =
                "SELECT DISTINCT vs.volume_mount_point, vs.logical_volume_name, vs.total_bytes, vs.available_bytes " +
                "FROM sys.master_files AS mf " +
                "CROSS APPLY sys.dm_os_volume_stats(mf.database_id, mf.file_id) AS vs " +
                "WHERE mf.database_id = DB_ID(@db);";
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand(sql, _connection) { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@db", databaseName);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                list.Add(new DiskSpaceInfo
                {
                    VolumeMountPoint = r.IsDBNull(0) ? string.Empty : r.GetString(0),
                    LogicalVolumeName = r.IsDBNull(1) ? string.Empty : r.GetString(1),
                    TotalBytes = r.IsDBNull(2) ? 0 : Convert.ToInt64(r.GetValue(2)),
                    AvailableBytes = r.IsDBNull(3) ? 0 : Convert.ToInt64(r.GetValue(3))
                });
            }
        }
        catch
        {
            // função indisponível (versão antiga) ou sem permissão.
        }
        return list;
    }

    /// <summary>Lista os conjuntos de backup dentro de um arquivo físico (RESTORE HEADERONLY).</summary>
    public async Task<List<BackupSetInfo>> RestoreHeaderOnlyAsync(string diskPath, CancellationToken ct = default)
    {
        var list = new List<BackupSetInfo>();
        await EnsureOpenAsync(ct);
        await using var cmd = new SqlCommand("RESTORE HEADERONLY FROM DISK = @path;", _connection)
        { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
        cmd.Parameters.AddWithValue("@path", diskPath);
        await using var r = await cmd.ExecuteReaderAsync(ct);

        var oName = Ordinal(r, "DatabaseName");
        var oType = Ordinal(r, "BackupType");
        var oPos = Ordinal(r, "Position");
        var oStart = Ordinal(r, "BackupStartDate");
        var oFinish = Ordinal(r, "BackupFinishDate");
        var oCopy = Ordinal(r, "IsCopyOnly");
        var oSize = Ordinal(r, "BackupSize");
        var oRec = Ordinal(r, "RecoveryModel");
        var oServer = Ordinal(r, "ServerName");
        var oFirst = Ordinal(r, "FirstLSN");
        var oLast = Ordinal(r, "LastLSN");
        var oDbBak = Ordinal(r, "DatabaseBackupLSN");
        var oChk = Ordinal(r, "CheckpointLSN");
        var oCompressed = Ordinal(r, "Compressed");
        var oBackupName = Ordinal(r, "BackupName");

        while (await r.ReadAsync(ct))
        {
            var typeInt = GetInt(r, oType);
            list.Add(new BackupSetInfo
            {
                Name = GetStr(r, oBackupName),
                DatabaseName = GetStr(r, oName),
                BackupTypeCode = BackupTypeIntToCode(typeInt),
                Position = GetInt(r, oPos),
                BackupStartDate = GetDate(r, oStart),
                BackupFinishDate = GetDate(r, oFinish),
                IsCopyOnly = GetInt(r, oCopy) == 1,
                BackupSizeBytes = GetLong(r, oSize),
                RecoveryModel = GetStr(r, oRec),
                ServerName = GetStr(r, oServer),
                PhysicalDevice = diskPath,
                FirstLsn = GetDecimal(r, oFirst),
                LastLsn = GetDecimal(r, oLast),
                DatabaseBackupLsn = GetDecimal(r, oDbBak),
                CheckpointLsn = GetDecimal(r, oChk),
                IsCompressed = GetInt(r, oCompressed) == 1
            });
        }
        return list;
    }

    /// <summary>Lista os arquivos lógicos de um conjunto de backup (RESTORE FILELISTONLY).</summary>
    public async Task<List<BackupFileInfo>> RestoreFileListOnlyAsync(string diskPath, int position, CancellationToken ct = default)
    {
        var list = new List<BackupFileInfo>();
        await EnsureOpenAsync(ct);
        await using var cmd = new SqlCommand("RESTORE FILELISTONLY FROM DISK = @path WITH FILE = @pos;", _connection)
        { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
        cmd.Parameters.AddWithValue("@path", diskPath);
        cmd.Parameters.AddWithValue("@pos", position);
        await using var r = await cmd.ExecuteReaderAsync(ct);

        var oLogical = Ordinal(r, "LogicalName");
        var oPhysical = Ordinal(r, "PhysicalName");
        var oType = Ordinal(r, "Type");
        var oFg = Ordinal(r, "FileGroupName");

        while (await r.ReadAsync(ct))
        {
            list.Add(new BackupFileInfo
            {
                LogicalName = GetStr(r, oLogical),
                PhysicalName = GetStr(r, oPhysical),
                Type = GetStr(r, oType),
                FileGroupName = GetStr(r, oFg)
            });
        }
        return list;
    }

    /// <summary>Histórico de backups do banco (msdb.dbo.backupset/backupmediafamily). Best-effort.</summary>
    public async Task<List<BackupSetInfo>> GetBackupHistoryAsync(string databaseName, CancellationToken ct = default)
    {
        var list = new List<BackupSetInfo>();
        // Um backup STRIPED tem várias linhas em backupmediafamily (uma por arquivo); agrupamos por
        // backup_set_id e reunimos todos os dispositivos. compressed_backup_size < backup_size = comprimido.
        //
        // Janela: tudo desde o 3º FULL agendado (não COPY_ONLY) mais recente (ou todo o histórico, se houver menos). Um corte fixo
        // (ex.: TOP 300) QUEBRAVA a cadeia em bancos com backup de log frequente — 15 em 15 min são ~100
        // logs/dia, e uma FULL semanal ficava fora da janela, fazendo a restauração "achar" uma quebra que
        // não existe. O teto de segurança (5000 conjuntos) só protege contra um msdb nunca expurgado.
        const string sql = @"
DECLARE @since datetime;
SELECT @since = MIN(x.backup_finish_date)
FROM (SELECT TOP (3) backup_finish_date FROM msdb.dbo.backupset
      WHERE database_name = @db AND type = 'D' AND is_copy_only = 0 ORDER BY backup_finish_date DESC) AS x;
SELECT TOP (5000)
       bs.backup_set_id, bs.database_name, bs.type, bmf.physical_device_name,
       bs.backup_start_date, bs.backup_finish_date, bs.is_copy_only,
       bs.backup_size, ISNULL(bs.recovery_model, N''), ISNULL(bs.server_name, N''),
       bs.first_lsn, bs.last_lsn, bs.database_backup_lsn, bs.checkpoint_lsn,
       bs.position, ISNULL(bs.compressed_backup_size, 0), ISNULL(bs.name, N'')
FROM   msdb.dbo.backupset AS bs
JOIN   msdb.dbo.backupmediafamily AS bmf ON bmf.media_set_id = bs.media_set_id
WHERE  bs.database_name = @db AND bmf.device_type IN (2, 102)  /* disco */
  AND  (@since IS NULL OR bs.backup_finish_date >= @since)
ORDER BY bs.backup_finish_date DESC, bs.backup_set_id, bmf.family_sequence_number;";
        try
        {
            // Acumula por backup_set_id preservando a ordem de chegada (mais recente primeiro).
            var order = new List<int>();
            var groups = new Dictionary<int, (BackupSetInfo info, List<string> devices)>();

            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand(sql, _connection) { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@db", databaseName);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var setId = r.IsDBNull(0) ? 0 : Convert.ToInt32(r.GetValue(0));
                var device = r.IsDBNull(3) ? string.Empty : r.GetString(3);

                if (!groups.TryGetValue(setId, out var g))
                {
                    var size = r.IsDBNull(7) ? 0 : Convert.ToInt64(r.GetValue(7));
                    var comp = r.IsDBNull(15) ? 0 : Convert.ToInt64(r.GetValue(15));
                    var info = new BackupSetInfo
                    {
                        DatabaseName = r.IsDBNull(1) ? string.Empty : r.GetString(1),
                        BackupTypeCode = r.IsDBNull(2) ? string.Empty : r.GetString(2),
                        PhysicalDevice = device,
                        BackupStartDate = r.IsDBNull(4) ? null : r.GetDateTime(4),
                        BackupFinishDate = r.IsDBNull(5) ? null : r.GetDateTime(5),
                        IsCopyOnly = !r.IsDBNull(6) && r.GetBoolean(6),
                        BackupSizeBytes = size,
                        RecoveryModel = r.IsDBNull(8) ? string.Empty : r.GetString(8),
                        ServerName = r.IsDBNull(9) ? string.Empty : r.GetString(9),
                        FirstLsn = r.IsDBNull(10) ? 0 : Convert.ToDecimal(r.GetValue(10)),
                        LastLsn = r.IsDBNull(11) ? 0 : Convert.ToDecimal(r.GetValue(11)),
                        DatabaseBackupLsn = r.IsDBNull(12) ? 0 : Convert.ToDecimal(r.GetValue(12)),
                        CheckpointLsn = r.IsDBNull(13) ? 0 : Convert.ToDecimal(r.GetValue(13)),
                        Position = r.IsDBNull(14) ? 1 : Convert.ToInt32(r.GetValue(14)),
                        IsCompressed = comp > 0 && comp < size,
                        Name = r.IsDBNull(16) ? string.Empty : r.GetString(16)
                    };
                    g = (info, new List<string>());
                    groups[setId] = g;
                    order.Add(setId);
                }
                if (!string.IsNullOrEmpty(device)) g.devices.Add(device);
            }

            foreach (var id in order)
            {
                var (info, devices) = groups[id];
                info.Devices.AddRange(devices.Distinct(StringComparer.OrdinalIgnoreCase));
                list.Add(info);
            }
        }
        catch
        {
            // sem permissão em msdb: histórico fica vazio (usuário pode escolher o arquivo manualmente).
        }
        return list;
    }

    // ---- helpers de leitura tolerantes a versão/idioma ----

    private static async Task ProbeAsync(Func<Task> probe)
    {
        try { await probe(); } catch { /* DMV/tabela inexistente nesta versão: ignora */ }
    }

    private static int Ordinal(SqlDataReader r, params string[] candidates)
    {
        foreach (var name in candidates)
        {
            for (var i = 0; i < r.FieldCount; i++)
                if (string.Equals(r.GetName(i), name, StringComparison.OrdinalIgnoreCase))
                    return i;
        }
        return -1;
    }

    private static int GetInt(SqlDataReader r, int ord)
        => ord < 0 || r.IsDBNull(ord) ? 0 : Convert.ToInt32(r.GetValue(ord));

    private static long GetLong(SqlDataReader r, int ord)
        => ord < 0 || r.IsDBNull(ord) ? 0 : Convert.ToInt64(r.GetValue(ord));

    private static decimal GetDecimal(SqlDataReader r, int ord)
        => ord < 0 || r.IsDBNull(ord) ? 0 : Convert.ToDecimal(r.GetValue(ord));

    private static string GetStr(SqlDataReader r, int ord)
        => ord < 0 || r.IsDBNull(ord) ? string.Empty : Convert.ToString(r.GetValue(ord)) ?? string.Empty;

    private static DateTime? GetDate(SqlDataReader r, int ord)
        => ord < 0 || r.IsDBNull(ord) ? null : Convert.ToDateTime(r.GetValue(ord));

    private static string BackupTypeIntToCode(int type) => type switch
    {
        1 => "D",  // Database (full)
        2 => "L",  // Transaction log
        4 => "F",  // File/Filegroup
        5 => "I",  // Differential database
        6 => "G",  // Differential file
        7 => "P",  // Partial
        8 => "Q",  // Differential partial
        _ => type.ToString()
    };

    private static string FormatMessage(SqlError err)
    {
        // Severidade (Class) 0-10 e número 0 => mensagem puramente informativa (PRINT / DBCC).
        if (err.Class <= 10 && err.Number == 0)
            return err.Message;
        return $"Msg {err.Number}, Nível {err.Class}, Linha {err.LineNumber}: {err.Message}";
    }

    public void Dispose() => _connection.Dispose();

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
}
