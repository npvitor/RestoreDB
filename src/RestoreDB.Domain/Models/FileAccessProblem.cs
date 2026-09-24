namespace RestoreDB.Domain.Models;

public enum FileProblemKind
{
    /// <summary>O arquivo não existe no caminho registrado (movido, disco trocado, letra de unidade mudou).</summary>
    Missing,

    /// <summary>O SQL Server não tem permissão sobre o arquivo (erro 5 do sistema operacional).</summary>
    AccessDenied,

    /// <summary>Outro processo mantém o arquivo aberto (erro 32 — antivírus, backup de arquivos, cópia...).</summary>
    InUseByAnotherProcess
}

/// <summary>
/// Um arquivo do banco que o SQL Server não consegue abrir — causa comum de RECOVERY_PENDING/SUSPECT que
/// NÃO é corrupção: resolve-se apontando o novo caminho, concedendo permissão à conta de serviço ou
/// liberando o arquivo, e religando o banco — sem perda de dados.
/// </summary>
public sealed class FileAccessProblem
{
    public string LogicalName { get; init; } = string.Empty;
    public string PhysicalName { get; init; } = string.Empty;
    public bool IsLog { get; init; }
    public FileProblemKind Kind { get; init; }

    public string Display => Kind switch
    {
        FileProblemKind.Missing => $"Arquivo AUSENTE: {PhysicalName} ({(IsLog ? "log" : "dados")})",
        FileProblemKind.AccessDenied => $"SEM PERMISSÃO (erro 5) para a conta do SQL Server: {PhysicalName}",
        _ => $"Arquivo EM USO por outro processo (erro 32): {PhysicalName}"
    };

    /// <summary>
    /// Classifica uma linha do log de erros do SQL Server (inglês ou português) que menciona falha ao
    /// abrir arquivo: "OS error: 5(Access is denied.)", "erro 32 do sistema operacional", etc.
    /// Devolve null quando a linha não descreve um problema de acesso a arquivo.
    /// </summary>
    public static FileProblemKind? Classify(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var l = line.ToLowerInvariant();
        bool Has(params string[] t) => t.Any(l.Contains);

        if (!Has("os error", "operating system error", "sistema operacional", "erro do so", "fcb::open", "could not open", "não foi possível abrir", "unable to open"))
            return null;
        if (Has("error: 5(", "error 5(", "error 5 ", "access is denied", "acesso negado", "erro 5(", "erro 5 "))
            return FileProblemKind.AccessDenied;
        if (Has("error: 32(", "error 32(", "error 32 ", "being used by another process", "sendo usado por outro processo", "erro 32"))
            return FileProblemKind.InUseByAnotherProcess;
        if (Has("error: 2(", "error: 3(", "error 2(", "error 3(", "cannot find the file", "cannot find the path",
                "não pode encontrar o arquivo", "não pode encontrar o caminho", "o sistema não pode encontrar"))
            return FileProblemKind.Missing;
        return null;
    }
}
