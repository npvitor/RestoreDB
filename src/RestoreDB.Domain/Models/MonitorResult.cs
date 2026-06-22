namespace RestoreDB.Domain.Models;

/// <summary>
/// Resultado de uma verificação AGENDADA (monitoramento): além de diagnosticar, decide se há algo
/// NOVO desde a última vez e, em caso afirmativo, registra um alerta. Pensado para rodar via Probe
/// no Agendador de Tarefas do Windows — o código de saída sinaliza a novidade para disparar ações.
/// </summary>
public sealed class MonitorResult
{
    public string DatabaseName { get; init; } = string.Empty;

    /// <summary>Há problema NOVO em relação à última verificação (gatilho de alerta).</summary>
    public bool HasNewIssue { get; set; }

    /// <summary>Há corrupção presente AGORA (mesmo que já conhecida).</summary>
    public bool CorruptionPresent { get; set; }

    /// <summary>Um arquivo de alerta foi gravado.</summary>
    public bool AlertWritten { get; set; }

    public string RecommendedAction { get; set; } = string.Empty;
    public string? AlertFilePath { get; set; }

    public List<string> Messages { get; } = new();
}
