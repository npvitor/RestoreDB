using Microsoft.Data.SqlClient;
using RestoreDB.DataAccess;
using RestoreDB.Domain.Models;

namespace RestoreDB.Services;

/// <summary>
/// Validação dos dados de conexão e teste de conectividade. Traduz erros de SQL Server
/// em mensagens amigáveis (sem stack trace cru). Em caso de falha lança
/// <see cref="InvalidOperationException"/> com texto pronto para exibição.
/// </summary>
public sealed class ConnectionService
{
    private readonly SqlDataAccess _data = new();

    public string BuildConnectionString(ConnectionInfo info) => _data.BuildConnectionString(info);

    /// <summary>Valida campos, testa a conexão e devolve a connection string pronta para uso.</summary>
    public async Task<string> ConnectAsync(ConnectionInfo info, CancellationToken ct = default)
    {
        Validate(info);

        var connectionString = _data.BuildConnectionString(info);
        try
        {
            await _data.TestConnectionAsync(connectionString, ct);
            return connectionString;
        }
        catch (SqlException ex)
        {
            throw new InvalidOperationException(FriendlyError(ex), ex);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Não foi possível conectar ao servidor. Verifique os dados informados e a rede.\n\nDetalhe: " + ex.Message, ex);
        }
    }

    private static void Validate(ConnectionInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.Server))
            throw new InvalidOperationException("Informe o nome ou endereço do servidor.");
        if (string.IsNullOrWhiteSpace(info.User))
            throw new InvalidOperationException("Informe o usuário.");
        if (string.IsNullOrEmpty(info.Password))
            throw new InvalidOperationException("Informe a senha.");
    }

    private static string FriendlyError(SqlException ex) => ex.Number switch
    {
        18456 => "Falha de autenticação: usuário ou senha inválidos.",
        4060  => "Não foi possível acessar o banco padrão (master). Verifique as permissões do usuário.",
        53 or 17 or -1 => "Servidor não encontrado ou inacessível. Verifique o nome/endereço e se o SQL Server está em execução.",
        40    => "Não foi possível abrir a conexão com o SQL Server. Verifique rede e firewall.",
        10060 or 10061 => "Tempo de conexão esgotado ou conexão recusada. Verifique rede, porta e firewall.",
        233   => "A conexão foi recusada pelo servidor. Verifique se conexões remotas estão habilitadas.",
        _     => $"Erro de conexão (código {ex.Number}): {ex.Message}"
    };
}
