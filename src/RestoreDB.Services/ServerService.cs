using Microsoft.Data.SqlClient;
using RestoreDB.DataAccess;
using RestoreDB.Domain.Models;

namespace RestoreDB.Services;

/// <summary>
/// Coleta e interpreta as capacidades da instância (versão, edição, se o login é sysadmin).
/// Usado para pré-checagens logo após a conexão e para adaptar o fluxo às limitações do servidor.
/// </summary>
public sealed class ServerService
{
    private readonly SqlDataAccess _data = new();

    public async Task<ServerInfo> GetServerInfoAsync(string connectionString, CancellationToken ct = default)
    {
        try
        {
            return await _data.GetServerInfoAsync(connectionString, ct);
        }
        catch (SqlException)
        {
            // Sem permissão para ler SERVERPROPERTY/@@VERSION: devolve um objeto vazio (não bloqueia o uso).
            return new ServerInfo();
        }
    }

    /// <summary>Mensagem de alerta caso a instância não atenda os pré-requisitos do reparo (ou null).</summary>
    public static string? PreflightWarning(ServerInfo info)
    {
        if (info.IsAzureSqlDatabase)
            return "Esta é uma instância do Azure SQL Database. Comandos de reparo locais (EMERGENCY, " +
                   "detach, attach, reconstrução de log) não se aplicam — use a restauração nativa do Azure.";

        if (!info.IsSysadmin)
            return "O login conectado NÃO é sysadmin. DBCC CHECKDB (reparo), ALTER DATABASE e RESTORE " +
                   "exigem sysadmin — as operações de reparo provavelmente falharão. Conecte-se com um login sysadmin.";

        return null;
    }
}
