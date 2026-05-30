using Microsoft.Data.SqlClient;
using RestoreDB.DataAccess;
using RestoreDB.Domain.Models;

namespace RestoreDB.Services;

/// <summary>Listagem de bancos e seus estados.</summary>
public sealed class DatabaseService
{
    private readonly SqlDataAccess _data = new();

    public async Task<List<DatabaseInfo>> ListDatabasesAsync(string connectionString, CancellationToken ct = default)
    {
        try
        {
            return await _data.GetDatabasesAsync(connectionString, ct);
        }
        catch (SqlException ex)
        {
            throw new InvalidOperationException(
                "Não foi possível listar os bancos de dados.\n\nDetalhe: " + ex.Message, ex);
        }
    }
}
