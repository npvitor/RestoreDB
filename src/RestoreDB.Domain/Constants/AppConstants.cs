namespace RestoreDB.Domain.Constants;

/// <summary>
/// Parâmetros fixos da aplicação (módulo de Faturamento).
/// Os valores sensíveis da connection string (timeout, encrypt, application name, etc.)
/// ficam centralizados aqui, conforme requisito — apenas Servidor/Usuário/Senha são
/// informados pelo usuário na tela inicial.
/// </summary>
public static class AppConstants
{
    // ----- Identidade / branding -----
    public const string ApplicationDisplayName = "RestoreDB — Diagnóstico e Reparo de Bancos (Módulo de Faturamento)";
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

    public static bool IsSystemDatabase(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        SystemDatabases.Contains(name, StringComparer.OrdinalIgnoreCase);
}
