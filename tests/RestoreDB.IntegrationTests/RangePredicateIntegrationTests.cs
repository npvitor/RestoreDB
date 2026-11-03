using Microsoft.Data.SqlClient;
using RestoreDB.DataAccess;
using RestoreDB.Services;
using Xunit;

namespace RestoreDB.IntegrationTests;

/// <summary>
/// Faixas da chave do salvamento sem backup, avaliadas pelo PRÓPRIO SQL Server (arredondamento de
/// datetime/smalldatetime, truncamento de date). Só usa uma tabela temporária — não cria banco.
/// </summary>
public sealed class RangePredicateIntegrationTests
{
    [IntegrationFact]
    public async Task ChaveDeData_VarreduraPorFaixas_CobreTodasAsLinhas_InclusiveAChaveMaxima()
    {
        foreach (var (type, values) in new (string, string[])[]
        {
            ("datetime", new[] { "2024-01-01T09:00:00.000", "2024-01-01T09:30:00.003", "2024-01-01T10:00:00.007", "2024-01-02T00:00:00.000" }),
            ("smalldatetime", new[] { "2024-01-01T09:00:00", "2024-01-01T09:31:00", "2024-01-01T10:00:00", "2024-01-02T00:00:00" }),
            ("date", new[] { "2024-01-01", "2024-01-03", "2024-01-04", "2024-01-05" }),
            ("datetime2", new[] { "2024-01-01T09:00:00.0000001", "2024-01-01T09:30:00", "2024-01-01T10:00:00", "2024-01-02T00:00:00.9999999" })
        })
        {
            await using var c = new SqlConnection(It.Conn);
            await c.OpenAsync();
            await ExecAsync(c, $"CREATE TABLE #k(k {type} NOT NULL PRIMARY KEY, ruim bit NOT NULL);");
            // A terceira linha é a "página destruída": força a divisão em faixas (fronteiras internas + a última faixa).
            for (var i = 0; i < values.Length; i++)
                await ExecAsync(c, "INSERT #k VALUES (@v, @r);", ("@v", values[i]), ("@r", i == 2));

            var key = new RangeKey("k", type, false);
            var lo = ((DateTime)(await ScalarAsync(c, "SELECT CAST(MIN(k) AS datetime2(7)) FROM #k;"))!).Ticks;
            var hi = ((DateTime)(await ScalarAsync(c, "SELECT CAST(MAX(k) AS datetime2(7)) FROM #k;"))!).Ticks;

            var res = await RangeBisector.RunAsync(lo, hi, async (a, b) =>
            {
                var (where, prm) = PreRepairExtractor.RangePredicate(key, a, b, openEnd: b >= hi);
                await using var cmd = new SqlCommand($"SELECT COUNT(*), ISNULL(SUM(CAST(ruim AS int)), 0) FROM #k WHERE {where};", c);
                foreach (var kv in prm) cmd.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
                await using var r = await cmd.ExecuteReaderAsync();
                await r.ReadAsync();
                return r.GetInt32(1) > 0 ? (RangeCopyStatus.Unreadable, 0L) : (RangeCopyStatus.Ok, (long)r.GetInt32(0));
            });

            // Todas as linhas boas copiadas exatamente uma vez — inclusive a da chave máxima.
            Assert.True(values.Length - 1 == res.RowsCopied, $"{type}: copiou {res.RowsCopied} de {values.Length - 1} linha(s) legível(is)");
            Assert.NotEmpty(res.Unreadable);
        }
    }

    private static async Task ExecAsync(SqlConnection c, string sql, params (string, object)[] ps)
    {
        await using var cmd = new SqlCommand(sql, c);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(SqlConnection c, string sql)
    {
        await using var cmd = new SqlCommand(sql, c);
        return await cmd.ExecuteScalarAsync();
    }
}
