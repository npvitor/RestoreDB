namespace RestoreDB.Domain.Constants;

/// <summary>
/// Parâmetros fixos da aplicação (módulo de Faturamento).
/// Os valores sensíveis da connection string (timeout, encrypt, application name, etc.)
/// ficam centralizados aqui, conforme requisito — apenas Servidor/Usuário/Senha são
/// informados pelo usuário na tela inicial.
/// </summary>
public static class AppConstants
{
    // ----- Identidade -----
    public const string ConnectionApplicationName = "RestoreDB - Faturamento";

    // ----- Connection string (valores pré-definidos) -----
    /// <summary>Operamos sempre a partir de master (nunca conectamos diretamente ao banco quebrado).</summary>
    public const string DefaultCatalog = "master";
    public const int ConnectTimeoutSeconds = 15;
    public const bool TrustServerCertificate = true; // compatibilidade com SQL Server 2008 R2
    public const bool Pooling = true;
    public const int MaxPoolSize = 50;

    // ----- Timeouts de comando (segundos). 0 = ilimitado (reparos podem demorar muito) -----
    public const int QueryCommandTimeoutSeconds = 30;
    public const int RepairCommandTimeoutSeconds = 0;

    // ----- Estados problemáticos (sys.databases.state_desc) -----
    public static readonly IReadOnlyList<string> ProblematicStates = new[]
    {
        "SUSPECT", "RECOVERY_PENDING", "EMERGENCY", "RECOVERING", "RESTORING", "OFFLINE"
    };

    public static bool IsProblematic(string? stateDesc) =>
        !string.IsNullOrWhiteSpace(stateDesc) &&
        ProblematicStates.Contains(stateDesc, StringComparer.OrdinalIgnoreCase);

    // ----- Bancos de sistema: nunca aplicar reparo destrutivo automaticamente -----
    public static readonly IReadOnlyList<string> SystemDatabases = new[]
    {
        "master", "model", "msdb", "tempdb"
    };

    /// <summary>Prefixo do NAME dos backups gerados pela própria ferramenta (rede de segurança, simulação...).</summary>
    public const string ToolBackupNamePrefix = "RestoreDB ";

    /// <summary>Banco (na mesma instância) onde as linhas órfãs removidas ficam guardadas — a remoção vira reversível.</summary>
    public const string QuarantineDatabaseName = "RestoreDB_Quarentena";

    public static bool IsSystemDatabase(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        SystemDatabases.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Converte um nome (de banco, etc.) em um token seguro para nome de arquivo/banco temporário:
    /// mantém letras e dígitos, troca o resto por '_' e remove '_' das pontas. Helper único usado por
    /// toda a aplicação (backups de segurança, cópias de simulação, histórico, alertas).
    /// </summary>
    public static string SanitizeToken(string? name, string fallback = "x")
    {
        var token = new string((name ?? string.Empty).Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        return token.Length == 0 ? fallback : token;
    }
}
