namespace RestoreDB.Domain.Models;

/// <summary>Dados informados pelo usuário na tela inicial de conexão.</summary>
public sealed class ConnectionInfo
{
    public string Server { get; set; } = string.Empty;
    public string User { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
