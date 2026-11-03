using Microsoft.Data.SqlClient;
using RestoreDB.Domain.Constants;

namespace RestoreDB.DataAccess;

/// <summary>Coluna usada para ler uma tabela por faixas (1ª coluna do índice clusterizado ou da PK).</summary>
public sealed record RangeKey(string Column, string TypeName, bool Nullable)
{
    public bool IsInteger => TypeName is "tinyint" or "smallint" or "int" or "bigint";
    public bool IsDate => TypeName is "date" or "datetime" or "smalldatetime" or "datetime2";
    public bool CanBisect => IsInteger || IsDate;
}

/// <summary>Resultado de uma cópia de linhas (uma faixa) para o banco de salvamento.</summary>
public sealed record CopyOutcome(bool Ok, long Rows, int ErrorNumber, string Message, bool Corruption, bool Fatal);

/// <summary>
/// Extração de linhas LEGÍVEIS de tabelas danificadas para um banco de salvamento, antes do reparo
/// destrutivo. Erros de leitura de página (823/824/605...) derrubam a conexão (severidade ≥ 20) — a
/// sessão reconecta sozinha (<see cref="EnsureOpenAsync"/>) e a camada de serviço divide a faixa.
/// </summary>
public sealed partial class SqlSession
{
    /// <summary>Erros que indicam página/estrutura ilegível — a faixa deve ser dividida, não abandonada.</summary>
    private static readonly HashSet<int> CorruptionErrors = new()
    {
        605, 606, 608, 823, 824, 825, 829, 2511, 5242, 5243, 7105, 7987, 8909, 8928, 8939, 8944, 8976, 8978, 8984
    };

    /// <summary>Erros de falta de espaço no banco de salvamento — não adianta continuar.</summary>
    private static readonly HashSet<int> NoSpaceErrors = new() { 1101, 1105, 9002 };

    /// <summary>Cria o banco de salvamento (modelo SIMPLE, na pasta padrão da instância).</summary>
    public async Task<bool> CreateSalvageDatabaseAsync(string name, CancellationToken ct = default)
    {
        var r = await ExecuteAsync($"Criando o banco de salvamento [{name}]",
            "DECLARE @c nvarchar(max) = N'CREATE DATABASE ' + QUOTENAME(@n) + N'; ALTER DATABASE ' + QUOTENAME(@n) + N' SET RECOVERY SIMPLE;';\n" +
            "EXEC sys.sp_executesql @c;",
            new Dictionary<string, object?> { ["@n"] = name }, AppConstants.RepairCommandTimeoutSeconds, ct);
        return r.Success;
    }

    /// <summary>
    /// Tamanho ocupado (MB) das tabelas — pelos METADADOS (sys.dm_db_partition_stats), sem ler páginas
    /// de dados; funciona num banco corrompido/EMERGENCY.
    /// </summary>
    public async Task<double> GetTablesUsedMbAsync(string databaseName, IEnumerable<string> schemaTables, CancellationToken ct = default)
    {
        double total = 0;
        foreach (var t in schemaTables)
        {
            try
            {
                var (schema, table) = SplitSchemaTable(t);
                var sql =
                    $"SELECT SUM(ps.used_page_count) FROM {Quote(databaseName)}.sys.dm_db_partition_stats ps " +
                    $"WHERE ps.object_id = OBJECT_ID(@o);";
                await EnsureOpenAsync(ct);
                await using var cmd = new SqlCommand(sql, _connection) { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
                cmd.Parameters.AddWithValue("@o", $"{Quote(databaseName)}.{Quote(schema)}.{Quote(table)}");
                var v = await cmd.ExecuteScalarAsync(ct);
                if (v is not null && v != DBNull.Value) total += Convert.ToDouble(v) * 8 / 1024;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* best-effort */ }
        }
        return total;
    }

    /// <summary>
    /// Coluna para ler por faixas: 1ª coluna do índice CLUSTERIZADO (leitura por seek no próprio dado) ou,
    /// numa heap, a 1ª coluna da PK. Null se a tabela não tiver nenhum dos dois.
    /// </summary>
    public async Task<RangeKey?> GetRangeKeyAsync(string databaseName, string schemaTable, CancellationToken ct = default)
    {
        try
        {
            var (schema, table) = SplitSchemaTable(schemaTable);
            var db = Quote(databaseName);
            var sql =
                $"SELECT TOP (1) c.name, t.name, CAST(c.is_nullable AS bit) FROM {db}.sys.indexes i " +
                $"JOIN {db}.sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 1 " +
                $"JOIN {db}.sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id " +
                $"JOIN {db}.sys.types t ON t.user_type_id = c.system_type_id " +
                $"WHERE i.object_id = OBJECT_ID(@o) AND (i.index_id = 1 OR i.is_primary_key = 1) " +
                $"ORDER BY CASE WHEN i.index_id = 1 THEN 0 ELSE 1 END;";
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand(sql, _connection) { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@o", $"{db}.{Quote(schema)}.{Quote(table)}");
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            return new RangeKey(r.GetString(0), r.GetString(1).ToLowerInvariant(), r.GetBoolean(2));
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>
    /// Colunas copiáveis da tabela de origem (todas menos rowversion, na ordem). As computadas entram:
    /// no salvamento viram colunas comuns (a devolução usa só as colunas inseríveis do banco vivo).
    /// </summary>
    public async Task<List<string>> GetCopyableColumnsAsync(string databaseName, string schemaTable, CancellationToken ct = default)
    {
        var (schema, table) = SplitSchemaTable(schemaTable);
        var cols = new List<string>();
        var sql =
            $"SELECT c.name FROM {Quote(databaseName)}.sys.columns c " +
            $"WHERE c.object_id = OBJECT_ID(@o) AND c.system_type_id <> 189 ORDER BY c.column_id;";
        await EnsureOpenAsync(ct);
        await using var cmd = new SqlCommand(sql, _connection) { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
        cmd.Parameters.AddWithValue("@o", $"{Quote(databaseName)}.{Quote(schema)}.{Quote(table)}");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) cols.Add(r.GetString(0));
        return cols;
    }

    /// <summary>
    /// Cria no banco de salvamento uma tabela VAZIA com as colunas dadas (mesmo schema/nome, ou
    /// <paramref name="targetTable"/>). O UNION ALL descarta a propriedade IDENTITY, então as linhas são
    /// copiadas com os valores originais sem precisar de IDENTITY_INSERT. Não lê páginas de dados (TOP 0).
    /// </summary>
    public async Task<bool> CreateSalvageTableAsync(string fromDb, string toDb, string schemaTable, IReadOnlyList<string> cols,
        string? targetTable = null, CancellationToken ct = default)
    {
        var (schema, table) = SplitSchemaTable(schemaTable);
        var colList = string.Join(", ", cols.Select(Quote));
        var src = $"{Quote(fromDb)}.{Quote(schema)}.{Quote(table)}";
        var dst = $"{Quote(toDb)}.{Quote(schema)}.{Quote(targetTable ?? table)}";

        var schemaSql =
            "DECLARE @proc nvarchar(300) = QUOTENAME(@db) + N'.sys.sp_executesql';\n" +
            "DECLARE @stmt nvarchar(max) = N'IF SCHEMA_ID(@s) IS NULL BEGIN DECLARE @c nvarchar(max) = N''CREATE SCHEMA '' + QUOTENAME(@s); EXEC(@c); END';\n" +
            "EXEC @proc @stmt, N'@s sysname', @s = @schema;";
        var s1 = await ExecuteAsync("Preparando schema no banco de salvamento", schemaSql,
            new Dictionary<string, object?> { ["@db"] = toDb, ["@schema"] = schema }, AppConstants.QueryCommandTimeoutSeconds, ct);
        if (!s1.Success) return false;

        var create =
            $"SELECT TOP (0) {colList} INTO {dst} FROM {src} UNION ALL SELECT TOP (0) {colList} FROM {src};";
        var r = await ExecuteAsync($"Criando a tabela de salvamento {dst}", create, null, AppConstants.RepairCommandTimeoutSeconds, ct);
        return r.Success;
    }

    /// <summary>MIN/MAX da coluna-chave (lê só as pontas do índice). Null se a leitura falhar.</summary>
    public async Task<(object? Min, object? Max)?> GetKeyBoundsAsync(string databaseName, string schemaTable, RangeKey key, CancellationToken ct = default)
    {
        var (schema, table) = SplitSchemaTable(schemaTable);
        var sql = $"SELECT MIN({Quote(key.Column)}), MAX({Quote(key.Column)}) FROM {Quote(databaseName)}.{Quote(schema)}.{Quote(table)};";
        try
        {
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand(sql, _connection) { CommandTimeout = AppConstants.RepairCommandTimeoutSeconds };
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            return (r.IsDBNull(0) ? null : r.GetValue(0), r.IsDBNull(1) ? null : r.GetValue(1));
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>
    /// Copia as linhas da tabela de origem que satisfazem <paramref name="where"/> para a tabela de
    /// salvamento (INSERT ... SELECT, uma instrução — falhou, nada entra). Nunca lança erro do servidor:
    /// classifica-o como corrupção (dividir a faixa), falta de espaço/fatal (parar) ou outro erro.
    /// </summary>
    public async Task<CopyOutcome> CopyRowsAsync(string fromDb, string toDb, string schemaTable, IReadOnlyList<string> cols,
        string? where, IReadOnlyDictionary<string, object?>? parameters, string? targetTable = null, string? indexHint = null,
        CancellationToken ct = default)
    {
        var (schema, table) = SplitSchemaTable(schemaTable);
        var colList = string.Join(", ", cols.Select(Quote));
        var src = $"{Quote(fromDb)}.{Quote(schema)}.{Quote(table)}" + (indexHint is null ? string.Empty : $" WITH (INDEX({Quote(indexHint)}))");
        var dst = $"{Quote(toDb)}.{Quote(schema)}.{Quote(targetTable ?? table)}";
        var sql = $"INSERT INTO {dst} ({colList}) SELECT {colList} FROM {src}" + (where is null ? ";" : $" WHERE {where};");
        try
        {
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand(sql, _connection) { CommandTimeout = AppConstants.RepairCommandTimeoutSeconds };
            if (parameters is not null)
                foreach (var kv in parameters) cmd.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
            var rows = await cmd.ExecuteNonQueryAsync(ct);
            return new CopyOutcome(true, Math.Max(rows, 0), 0, string.Empty, false, false);
        }
        catch (OperationCanceledException) { throw; }
        catch (SqlException ex)
        {
            var errs = ex.Errors.Cast<SqlError>().ToList();
            var number = errs.FirstOrDefault(e => e.Class > 10)?.Number ?? ex.Number;
            var noSpace = errs.Any(e => NoSpaceErrors.Contains(e.Number));
            var corrupt = !noSpace && (errs.Any(e => CorruptionErrors.Contains(e.Number) || e.Class >= 20)
                                       || _connection.State != System.Data.ConnectionState.Open);
            return new CopyOutcome(false, 0, number, ex.Message, corrupt, noSpace);
        }
        catch (Exception ex)
        {
            // Conexão caída sem SqlException (transporte): trata como página ilegível — reconecta no próximo comando.
            var broken = _connection.State != System.Data.ConnectionState.Open;
            return new CopyOutcome(false, 0, 0, ex.Message, broken, false);
        }
    }

    /// <summary>
    /// Índice NÃO clusterizado utilizável para recompor linhas parciais: o de mais colunas (chave +
    /// incluídas), habilitado. Devolve o nome e as colunas que ele cobre (inclui a chave clusterizada,
    /// que todo índice não-clusterizado carrega).
    /// </summary>
    public async Task<(string Index, List<string> Columns)?> GetWidestNonClusteredIndexAsync(
        string databaseName, string schemaTable, IReadOnlyList<string> clusteredKey, CancellationToken ct = default)
    {
        try
        {
            var (schema, table) = SplitSchemaTable(schemaTable);
            var db = Quote(databaseName);
            var sql =
                $"SELECT i.name, c.name FROM {db}.sys.indexes i " +
                $"JOIN {db}.sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id " +
                $"JOIN {db}.sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id " +
                $"WHERE i.object_id = OBJECT_ID(@o) AND i.index_id > 1 AND i.is_disabled = 0 AND i.is_hypothetical = 0 " +
                $"AND c.system_type_id <> 189 ORDER BY i.index_id, ic.key_ordinal, ic.index_column_id;";
            var byIndex = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            await EnsureOpenAsync(ct);
            await using (var cmd = new SqlCommand(sql, _connection) { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds })
            {
                cmd.Parameters.AddWithValue("@o", $"{db}.{Quote(schema)}.{Quote(table)}");
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    var ix = r.GetString(0);
                    if (!byIndex.TryGetValue(ix, out var cols)) byIndex[ix] = cols = new List<string>();
                    var col = r.GetString(1);
                    if (!cols.Contains(col, StringComparer.OrdinalIgnoreCase)) cols.Add(col);
                }
            }
            if (byIndex.Count == 0) return null;
            var best = byIndex.OrderByDescending(kv => kv.Value.Count).First();
            var all = clusteredKey.Concat(best.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return (best.Key, all);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>Colunas da chave do índice clusterizado (na ordem). Vazio numa heap.</summary>
    public async Task<List<string>> GetClusteredKeyColumnsAsync(string databaseName, string schemaTable, CancellationToken ct = default)
    {
        var list = new List<string>();
        try
        {
            var (schema, table) = SplitSchemaTable(schemaTable);
            var db = Quote(databaseName);
            var sql =
                $"SELECT c.name FROM {db}.sys.index_columns ic " +
                $"JOIN {db}.sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id " +
                $"WHERE ic.object_id = OBJECT_ID(@o) AND ic.index_id = 1 AND ic.key_ordinal > 0 ORDER BY ic.key_ordinal;";
            await EnsureOpenAsync(ct);
            await using var cmd = new SqlCommand(sql, _connection) { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@o", $"{db}.{Quote(schema)}.{Quote(table)}");
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) list.Add(r.GetString(0));
        }
        catch (OperationCanceledException) { throw; }
        catch { /* heap ou metadados ilegíveis */ }
        return list;
    }
}
