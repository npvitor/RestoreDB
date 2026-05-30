namespace RestoreDB.Domain.Models;

/// <summary>
/// Resultado da execução de um comando T-SQL: indica sucesso, mensagens informativas
/// (PRINT / DBCC) e mensagens de erro capturadas. Independente de idioma do servidor:
/// "sucesso" significa que o comando não lançou erros de severidade >= 11.
/// </summary>
public sealed class CommandExecutionResult
{
    public string Description { get; init; } = string.Empty;
    public string Sql { get; init; } = string.Empty;
    public bool Success { get; set; }
    public List<string> InfoMessages { get; } = new();
    public List<string> ErrorMessages { get; } = new();
    public Exception? Exception { get; set; }
}
