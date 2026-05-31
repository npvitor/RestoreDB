namespace RestoreDB.Domain.Models;

/// <summary>
/// Resultado de um diagnóstico SOMENTE LEITURA: tenta distinguir se a causa do estado
/// problemático está nos arquivos de DADOS (.mdf/.ndf) ou no LOG de transações (.ldf),
/// com base no log de erros do SQL Server (sp_readerrorlog) e em sys.master_files.
/// </summary>
public sealed class DiagnosisResult
{
    public string DatabaseName { get; init; } = string.Empty;
    public string StateDesc { get; set; } = string.Empty;

    public bool LogCorruptionSuspected { get; set; }
    public bool DataCorruptionSuspected { get; set; }

    public List<string> Evidence { get; } = new();
    public bool HasEvidence => Evidence.Count > 0;

    public string Summary { get; set; } = string.Empty;
    public string Recommendation { get; set; } = string.Empty;
}
