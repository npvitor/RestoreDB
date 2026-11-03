using Microsoft.Data.SqlClient;
using RestoreDB.Domain.Models;
using RestoreDB.Services;
using Xunit;

namespace RestoreDB.IntegrationTests;

/// <summary>
/// Testes que travam um banco em SINGLE_USER e DESCONECTAM (KILL) as sessões presas a ele. Rodam sozinhos,
/// depois das coleções paralelas: o KILL atinge sessões com bloqueio no banco, e no 2008 R2 isso derrubava
/// comandos de testes de outras classes rodando ao mesmo tempo (erro 924 em bancos que nem eram deles).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ExclusiveCollection
{
    public const string Name = "Exclusivos (sem paralelismo)";
}

[Collection(ExclusiveCollection.Name)]
public sealed class AccessLockIntegrationTests
{
    private static RepairService Repair()
    {
        var log = new LogService();
        return new(log, new RestoreService(log));
    }

    [IntegrationFact]
    public async Task AcessoTravado_DiagnosticaQuemSeguraELibera()
    {
        await using var db = await TestDatabase.CreateAsync("travado");
        // Uma "aplicação" pega a vaga única e fica conectada.
        // Conexão fora do pool: ela será derrubada (KILL) e não pode voltar ao pool dos outros testes.
        var holder = new SqlConnection(new SqlConnectionStringBuilder(It.Conn) { Pooling = false, ApplicationName = "RestoreDB_IT_Holder" }.ConnectionString);
        await holder.OpenAsync();
        try
        {
            await using (var cmd = new SqlCommand($"ALTER DATABASE {It.Q(db.Name)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; USE {It.Q(db.Name)};", holder))
                await cmd.ExecuteNonQueryAsync();
            var holderSpid = Convert.ToInt32(await new SqlCommand("SELECT @@SPID;", holder).ExecuteScalarAsync());

            var diag = await Repair().DiagnoseAsync(db.AsInfo("ONLINE"), It.Conn, It.NoProgress);
            Assert.Equal(RecommendedActionKind.FixAccess, diag.RecommendedAction);
            Assert.Contains(diag.AccessHolders, h => h.SessionId == holderSpid);

            var r = await new AdjustmentService(new LogService()).ReleaseAccessAsync(db.AsInfo("ONLINE"), disconnectHolders: true, It.Conn, It.NoProgress);
            Assert.Equal(RepairStatus.Success, r.Status);
            Assert.Equal("MULTI_USER", await It.ScalarAsync("SELECT user_access_desc FROM sys.databases WHERE name = @n;", ("@n", db.Name)));
            Assert.Equal(3, await It.RowCountAsync(db.Name));
        }
        finally { try { holder.Dispose(); } catch { } }
    }
}
