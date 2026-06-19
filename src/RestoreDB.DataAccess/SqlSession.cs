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

    /// <summary>Lê o state_desc atual de um banco em sys.databases.</summary>
    public async Task<string> GetDatabaseStateAsync(string databaseName, CancellationToken ct = default)
    {
        const string sql = "SELECT state_desc FROM sys.databases WHERE name = @db;";
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
    public async Task<CheckDbReport> CheckDbWithResultsAsync(string databaseName, string? repairOption, CancellationToken ct = default)
    {
        var report = new CheckDbReport();
        var repairPart = repairOption is null ? string.Empty : ", " + repairOption;
        var sql =
            "DECLARE @cmd nvarchar(max);\n" +
            "SET @cmd = N'DBCC CHECKDB (' + QUOTENAME(@db) + N'" + repairPart + ") WITH TABLERESULTS, ALL_ERRORMSGS, NO_INFOMSGS;';\n" +
            "EXEC sys.sp_executesql @cmd;";

        try
        {
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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Banco inacessível / sem permissão: relatório fica vazio (Executed = false).
        }
        return report;
    }

    /// <summary>
    /// Executa DBCC CHECKCONSTRAINTS WITH ALL_CONSTRAINTS, ALL_ERRORMSGS, TABLERESULTS no contexto
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
            const string sql =
                "DECLARE @proc nvarchar(300) = QUOTENAME(@db) + N'.sys.sp_executesql';\n" +
                "DECLARE @stmt nvarchar(max) = N'DBCC CHECKCONSTRAINTS WITH ALL_CONSTRAINTS, ALL_ERRORMSGS, TABLERESULTS;';\n" +
                "EXEC @proc @stmt;";
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
            v.IsForeignKey = fkNames.Contains(v.Constraint);
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
                CheckpointLsn = GetDecimal(r, oChk)
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
        const string sql = @"
SELECT TOP (100)
       bs.database_name, bs.type, bmf.physical_device_name,
       bs.backup_start_date, bs.backup_finish_date, bs.is_copy_only,
       bs.backup_size, ISNULL(bs.recovery_model, N''), ISNULL(bs.server_name, N''),
       bs.first_lsn, bs.last_lsn, bs.database_backup_lsn, bs.checkpoint_lsn,
       bs.position
FROM   msdb.dbo.backupset AS bs
JOIN   msdb.dbo.backupmediafamily AS bmf ON bmf.media_set_id = bs.media_set_id
WHERE  bs.database_name = @db AND bmf.device_type IN (2, 102)  /* disco */
ORDER BY bs.backup_finish_date DESC;";
        try
        {
            await using var cmd = new SqlCommand(sql, _connection) { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@db", databaseName);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                list.Add(new BackupSetInfo
                {
                    DatabaseName = r.IsDBNull(0) ? string.Empty : r.GetString(0),
                    BackupTypeCode = r.IsDBNull(1) ? string.Empty : r.GetString(1),
                    PhysicalDevice = r.IsDBNull(2) ? string.Empty : r.GetString(2),
                    BackupStartDate = r.IsDBNull(3) ? null : r.GetDateTime(3),
                    BackupFinishDate = r.IsDBNull(4) ? null : r.GetDateTime(4),
                    IsCopyOnly = !r.IsDBNull(5) && r.GetBoolean(5),
                    BackupSizeBytes = r.IsDBNull(6) ? 0 : Convert.ToInt64(r.GetValue(6)),
                    RecoveryModel = r.IsDBNull(7) ? string.Empty : r.GetString(7),
                    ServerName = r.IsDBNull(8) ? string.Empty : r.GetString(8),
                    FirstLsn = r.IsDBNull(9) ? 0 : Convert.ToDecimal(r.GetValue(9)),
                    LastLsn = r.IsDBNull(10) ? 0 : Convert.ToDecimal(r.GetValue(10)),
                    DatabaseBackupLsn = r.IsDBNull(11) ? 0 : Convert.ToDecimal(r.GetValue(11)),
                    CheckpointLsn = r.IsDBNull(12) ? 0 : Convert.ToDecimal(r.GetValue(12)),
                    Position = r.IsDBNull(13) ? 1 : Convert.ToInt32(r.GetValue(13))
                });
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
