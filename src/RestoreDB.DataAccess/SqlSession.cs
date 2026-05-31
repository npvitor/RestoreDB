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

    /// <summary>Lê os arquivos físicos de um banco (sys.master_files). Compatível com 2008 R2.</summary>
    public async Task<List<DatabaseFile>> GetDatabaseFilesAsync(string databaseName, CancellationToken ct = default)
    {
        const string sql =
            "SELECT CAST(mf.type AS int) AS [type], mf.type_desc, mf.physical_name " +
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
                PhysicalName = reader.IsDBNull(2) ? string.Empty : reader.GetString(2)
            });
        }
        return list;
    }

    /// <summary>
    /// Lê o log de erros atual do SQL Server filtrando pelas linhas que mencionam o banco
    /// (sp_readerrorlog). Somente leitura. Best-effort: se não houver permissão/sp, devolve vazio.
    /// Compatível com SQL Server 2008 R2.
    /// </summary>
    public async Task<List<string>> ReadErrorLogAsync(string databaseName, CancellationToken ct = default)
    {
        var lines = new List<string>();
        try
        {
            // @p1 = 0 (log atual), @p2 = 1 (SQL Server, não o Agent), @p3 = texto a filtrar.
            await using var cmd = new SqlCommand("EXEC sys.sp_readerrorlog @arch, @logtype, @search;", _connection)
            {
                CommandTimeout = AppConstants.QueryCommandTimeoutSeconds
            };
            cmd.Parameters.AddWithValue("@arch", 0);
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
            // best-effort: sem permissão (não-sysadmin), sp indisponível, etc.
        }
        return lines;
    }

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
