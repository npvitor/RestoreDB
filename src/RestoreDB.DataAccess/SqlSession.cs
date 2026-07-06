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
            foreach (SqlError err in ex.Errors)
                result.ErrorMessages.Add(FormatMessage(err));
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
    public async Task<List<string>> ReadErrorLogAsync(string databaseName, CancellationToken ct = default, int maxArchives = 3)
    {
        var lines = new List<string>();
        // Varre o log atual (0) e os arquivos arquivados mais recentes, pois um rollover pode
        // ter movido as evidências de corrupção para um arquivo anterior.
        for (var arch = 0; arch < maxArchives; arch++)
        {
            try
            {
                // @arch (0 = atual), @logtype = 1 (SQL Server, não Agent), @search = texto a filtrar.
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
            const string sql =
                "DECLARE @proc nvarchar(300) = QUOTENAME(@db) + N'.sys.sp_executesql';\n" +
                "DECLARE @stmt nvarchar(max) = N'" +
                "SELECT TOP (@m) [Transaction ID], MAX([Transaction Name]), MIN([Begin Time]), " +
                "SUM(CASE WHEN Operation = ''LOP_DELETE_ROWS'' THEN 1 ELSE 0 END) " +
                "FROM fn_dblog(NULL, NULL) " +
                "GROUP BY [Transaction ID] " +
                "HAVING SUM(CASE WHEN Operation = ''LOP_DELETE_ROWS'' THEN 1 ELSE 0 END) > 0 " +
                "   OR MAX([Transaction Name]) IN (''DROPOBJ'', ''TRUNCATE'') " +
                "ORDER BY MIN([Begin Time]) DESC;';\n" +
                "EXEC @proc @stmt, N'@m int', @m = @max;";
            await using var cmd = new SqlCommand(sql, _connection) { CommandTimeout = AppConstants.RepairCommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@db", databaseName);
            cmd.Parameters.AddWithValue("@max", max);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var txName = r.IsDBNull(1) ? string.Empty : r.GetString(1);
                var beginRaw = r.IsDBNull(2) ? string.Empty : Convert.ToString(r.GetValue(2)) ?? string.Empty;
                var deletes = r.IsDBNull(3) ? 0 : Convert.ToInt32(r.GetValue(3));
                var kind = txName.Equals("DROPOBJ", StringComparison.OrdinalIgnoreCase) ? "DROP"
                         : txName.Equals("TRUNCATE", StringComparison.OrdinalIgnoreCase) ? "TRUNCATE"
                         : "DELETE";
                DateTime? begin = DateTime.TryParse(beginRaw.Replace('/', '-'), out var dt) ? dt : null;
                list.Add(new ForensicTransaction
                {
                    TransactionId = r.IsDBNull(0) ? string.Empty : Convert.ToString(r.GetValue(0)) ?? string.Empty,
                    Kind = kind,
                    BeginTimeRaw = beginRaw,
                    BeginTime = begin,
                    RowsAffected = deletes
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
        var objRef = $"{schema}.{table}";

        // Colunas não-computadas (na ordem) + se há IDENTITY.
        var cols = new List<string>();
        var hasIdentity = false;
        {
            var sql =
                $"SELECT c.name, c.is_identity FROM {Quote(toDb)}.sys.columns c " +
                $"WHERE c.object_id = OBJECT_ID(@o) AND c.is_computed = 0 ORDER BY c.column_id;";
            await using var cmd = new SqlCommand(sql, _connection) { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@o", $"{toDb}.{objRef}");
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                cols.Add(r.GetString(0));
                if (!r.IsDBNull(1) && r.GetBoolean(1)) hasIdentity = true;
            }
        }
        if (cols.Count == 0)
            throw new InvalidOperationException($"Tabela '{schemaTable}' não encontrada em [{toDb}].");

        // Colunas da chave primária (para não duplicar).
        var pk = new List<string>();
        {
            var sql =
                $"SELECT c.name FROM {Quote(toDb)}.sys.indexes i " +
                $"JOIN {Quote(toDb)}.sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id " +
                $"JOIN {Quote(toDb)}.sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id " +
                $"WHERE i.is_primary_key = 1 AND i.object_id = OBJECT_ID(@o) ORDER BY ic.key_ordinal;";
            await using var cmd = new SqlCommand(sql, _connection) { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@o", $"{toDb}.{objRef}");
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                pk.Add(r.GetString(0));
        }
        if (pk.Count == 0)
            throw new InvalidOperationException($"A tabela '{schemaTable}' não tem chave primária — o salvamento seguro (sem duplicar) não é possível.");

        var colList = string.Join(", ", cols.Select(Quote));
        var joinOn = string.Join(" AND ", pk.Select(p => $"dst.{Quote(p)} = src.{Quote(p)}"));
        var idOn = hasIdentity ? $"SET IDENTITY_INSERT {qTarget} ON; " : string.Empty;
        var idOff = hasIdentity ? $" SET IDENTITY_INSERT {qTarget} OFF;" : string.Empty;

        var inner =
            idOn +
            $"INSERT INTO {qTarget} ({colList}) SELECT {colList} FROM {qSource} src " +
            $"WHERE NOT EXISTS (SELECT 1 FROM {qTarget} dst WHERE {joinOn}); " +
            "DECLARE @rc int = @@ROWCOUNT;" + idOff + " SELECT @rc;";

        const string runner =
            "DECLARE @proc nvarchar(300) = QUOTENAME(@db) + N'.sys.sp_executesql';\n" +
            "EXEC @proc @stmt;";
        await using var run = new SqlCommand(runner, _connection) { CommandTimeout = AppConstants.RepairCommandTimeoutSeconds };
        run.Parameters.AddWithValue("@db", toDb);
        run.Parameters.AddWithValue("@stmt", inner);
        var n = await run.ExecuteScalarAsync(ct);
        return n is null || n == DBNull.Value ? 0 : Convert.ToInt32(n);
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
    /// Executa DBCC CHECKCONSTRAINTS WITH ALL_CONSTRAINTS, ALL_ERRORMSGS no contexto
    /// do banco alvo e devolve as violações de integridade LÓGICA (FK/CHECK) — que o CHECKDB NÃO
    /// detecta. Resolve, para cada violação, se a constraint é uma chave estrangeira (sys.foreign_keys),
    /// pois só FKs têm remoção automática de linhas órfãs. Best-effort. Compatível com 2008 R2.
    /// </summary>
    public async Task<List<ConstraintViolation>> CheckConstraintsAsync(string databaseName, CancellationToken ct = default)
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
            await ResolveForeignKeyFlagsAsync(databaseName, list, ct);
        return list;
    }

    /// <summary>Marca quais violações pertencem a chaves estrangeiras (sys.foreign_keys do banco alvo).</summary>
    private async Task ResolveForeignKeyFlagsAsync(string databaseName, List<ConstraintViolation> violations, CancellationToken ct)
    {
        var fkNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            const string sql =
                "DECLARE @proc nvarchar(300) = QUOTENAME(@db) + N'.sys.sp_executesql';\n" +
                "DECLARE @stmt nvarchar(max) = N'SELECT name FROM sys.foreign_keys;';\n" +
                "EXEC @proc @stmt;";
            await using var cmd = new SqlCommand(sql, _connection) { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@db", databaseName);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                if (!r.IsDBNull(0)) fkNames.Add(r.GetString(0));
        }
        catch (OperationCanceledException) { throw; }
        catch { /* best-effort */ }

        foreach (var v in violations)
            v.IsForeignKey = fkNames.Contains(Unbracket(v.Constraint));
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
        // (DBCC CHECKCONSTRAINTS), não de entrada do usuário. Aspas simples são escapadas para o literal.
        var inner = $"DELETE FROM {violation.Table} WHERE {violation.Where}";
        var sql =
            "DECLARE @proc nvarchar(300) = QUOTENAME(@db) + N'.sys.sp_executesql';\n" +
            "EXEC @proc @inner;";
        await using var cmd = new SqlCommand(sql, _connection) { CommandTimeout = AppConstants.RepairCommandTimeoutSeconds };
        cmd.Parameters.AddWithValue("@db", databaseName);
        cmd.Parameters.AddWithValue("@inner", inner);
        return await cmd.ExecuteNonQueryAsync(ct);
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

        while (await r.ReadAsync(ct))
        {
            var typeInt = GetInt(r, oType);
            list.Add(new BackupSetInfo
            {
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
        const string sql = @"
SELECT TOP (300)
       bs.backup_set_id, bs.database_name, bs.type, bmf.physical_device_name,
       bs.backup_start_date, bs.backup_finish_date, bs.is_copy_only,
       bs.backup_size, ISNULL(bs.recovery_model, N''), ISNULL(bs.server_name, N''),
       bs.first_lsn, bs.last_lsn, bs.database_backup_lsn, bs.checkpoint_lsn,
       bs.position, ISNULL(bs.compressed_backup_size, 0)
FROM   msdb.dbo.backupset AS bs
JOIN   msdb.dbo.backupmediafamily AS bmf ON bmf.media_set_id = bs.media_set_id
WHERE  bs.database_name = @db AND bmf.device_type IN (2, 102)  /* disco */
ORDER BY bs.backup_finish_date DESC, bs.backup_set_id, bmf.family_sequence_number;";
        try
        {
            // Acumula por backup_set_id preservando a ordem de chegada (mais recente primeiro).
            var order = new List<int>();
            var groups = new Dictionary<int, (BackupSetInfo info, List<string> devices)>();

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
                        IsCompressed = comp > 0 && comp < size
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
