namespace RestoreDB.Domain.Models;

/// <summary>Dados informados pelo usuário na tela inicial de conexão.</summary>
public sealed class ConnectionInfo
{
    public string Server { get; set; } = string.Empty;
    public string User { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Quando true, usa autenticação integrada do Windows (Integrated Security) e os campos
    /// Usuário/Senha são ignorados. Útil em ambientes que não permitem login SQL.
    /// </summary>
    public bool UseWindowsAuth { get; set; }
}
