using Microsoft.Data.SqlClient;
using RestoreDB.Domain.Constants;
using RestoreDB.Domain.Models;

namespace RestoreDB.DataAccess;

/// <summary>
/// Acesso a dados em ADO.NET puro. Monta a connection string a partir das constantes
/// fixas + dados do usuário, testa conexão, lista bancos e abre sessões de reparo.
/// </summary>
public sealed class SqlDataAccess
{
    public string BuildConnectionString(ConnectionInfo info)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = info.Server.Trim(),
            InitialCatalog = AppConstants.DefaultCatalog,
            ConnectTimeout = AppConstants.ConnectTimeoutSeconds,
            // Optional => Encrypt=False: necessário para SQL Server 2008 R2 (TLS antigo).
            Encrypt = SqlConnectionEncryptOption.Optional,
            TrustServerCertificate = AppConstants.TrustServerCertificate,
            ApplicationName = AppConstants.ConnectionApplicationName,
            Pooling = AppConstants.Pooling,
            MaxPoolSize = AppConstants.MaxPoolSize,
            MultipleActiveResultSets = false
        };

        if (info.UseWindowsAuth)
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.UserID = info.User.Trim();
            builder.Password = info.Password;
        }

        return builder.ConnectionString;
    }

    /// <summary>Abre e fecha uma conexão de teste; lança SqlException em caso de falha.</summary>
    public async Task TestConnectionAsync(string connectionString, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT @@VERSION;", conn)
        {
            CommandTimeout = AppConstants.QueryCommandTimeoutSeconds
        };
        await cmd.ExecuteScalarAsync(ct);
    }

    /// <summary>Lista todos os bancos e seus estados (sys.databases). Compatível com 2008 R2.</summary>
    public async Task<List<DatabaseInfo>> GetDatabasesAsync(string connectionString, CancellationToken ct = default)
    {
        const string sql = @"
SELECT  d.name                                   AS [name],
        CAST(d.state AS int)                      AS [state],
        d.state_desc                              AS [state_desc],
        ISNULL(d.recovery_model_desc, N'')        AS [recovery_model_desc]
FROM    sys.databases AS d
ORDER BY CASE WHEN d.state <> 0 THEN 0 ELSE 1 END, d.name;";

        var list = new List<DatabaseInfo>();
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new DatabaseInfo
            {
                Name = reader.GetString(0),
                State = reader.GetInt32(1),
                StateDesc = reader.IsDBNull(2) ? "DESCONHECIDO" : reader.GetString(2),
                RecoveryModel = reader.IsDBNull(3) ? string.Empty : reader.GetString(3)
            });
        }
        return list;
    }

    /// <summary>
    /// Coleta informações da instância (versão, edição, se o login é sysadmin) logo após a
    /// conexão. Best-effort para campos que não existem em todas as edições/versões.
    /// </summary>
    public async Task<ServerInfo> GetServerInfoAsync(string connectionString, CancellationToken ct = default)
    {
        const string sql = @"
SELECT  CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128))      AS ProductVersion,
        CAST(SERVERPROPERTY('ProductLevel')   AS nvarchar(128))      AS ProductLevel,
        CAST(SERVERPROPERTY('Edition')        AS nvarchar(256))      AS Edition,
        CAST(ISNULL(SERVERPROPERTY('EngineEdition'), 0) AS int)      AS EngineEdition,
        CAST(SERVERPROPERTY('MachineName')    AS nvarchar(256))      AS MachineName,
        CAST(ISNULL(IS_SRVROLEMEMBER('sysadmin'), 0) AS int)         AS IsSysadmin,
        @@VERSION                                                    AS FullVersion;";

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = AppConstants.QueryCommandTimeoutSeconds };
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
            return new ServerInfo();

        var productVersion = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
        var major = 0;
        var dot = productVersion.IndexOf('.');
        if (dot > 0) int.TryParse(productVersion[..dot], out major);

        return new ServerInfo
        {
            ProductVersion = productVersion,
            ProductLevel = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
            Edition = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
            EngineEdition = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
            MachineName = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
            IsSysadmin = !reader.IsDBNull(5) && reader.GetInt32(5) == 1,
            FullVersionText = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
            MajorVersion = major
        };
    }

    /// <summary>Abre uma sessão (conexão única) para a sequência de reparo.</summary>
    public async Task<SqlSession> OpenSessionAsync(string connectionString, CancellationToken ct = default)
    {
        var conn = new SqlConnection(connectionString);
        try
        {
            await conn.OpenAsync(ct);
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
        return new SqlSession(conn);
    }
}
