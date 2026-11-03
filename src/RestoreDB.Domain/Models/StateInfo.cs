namespace RestoreDB.Domain.Models;

/// <summary>
/// Textos amigáveis (para usuários leigos) sobre cada estado de banco e a ação recomendada.
/// </summary>
public static class StateInfo
{
    public static string Friendly(string? state) => Normalize(state) switch
    {
        // ONLINE só diz que o banco abriu: corrupção física ou registro órfão não mudam o estado.
        "ONLINE"           => "Disponível (no ar). Só o diagnóstico confirma se não há corrupção.",
        "OFFLINE"          => "Desligado manualmente. Pode ser religado sem perda de dados.",
        "RESTORING"        => "Em restauração / aguardando finalização da recuperação.",
        "RECOVERING"       => "O SQL Server está recuperando o banco sozinho (pode demorar).",
        "RECOVERY_PENDING" => "Recuperação pendente — geralmente falta de arquivo ou recurso.",
        "SUSPECT"          => "Suspeito — possível corrupção. Requer diagnóstico e reparo.",
        "EMERGENCY"        => "Em modo de emergência (acesso somente leitura, usado durante o reparo).",
        _                  => string.IsNullOrWhiteSpace(state) ? "Estado desconhecido." : state!
    };

    public static string RecommendedAction(string? state) => Normalize(state) switch
    {
        "ONLINE"           => "Diagnosticar para confirmar que está íntegro.",
        "OFFLINE"          => "Religar o banco (sem perda de dados).",
        "RESTORING"        => "Finalizar a recuperação do banco (sem perda de dados).",
        "RECOVERING"       => "Aguardar a recuperação terminar (o diagnóstico mostra o andamento).",
        "RECOVERY_PENDING" => "Tentar religar; se não bastar, diagnóstico e reparo automático.",
        "SUSPECT"          => "Diagnóstico (DBCC CHECKDB) e reparo — primeiro sem perda de dados.",
        "EMERGENCY"        => "Diagnóstico (DBCC CHECKDB) e reparo — primeiro sem perda de dados.",
        _                  => "Avaliar manualmente."
    };

    private static string Normalize(string? state) => (state ?? string.Empty).Trim().ToUpperInvariant();
}
