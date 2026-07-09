namespace RestoreDB.Services;

/// <summary>
/// Estado passado à UI quando a recuperação de registros-pai (a partir de um backup) NÃO resolveu
/// todos os órfãos — seja porque o backup era ilegível/incompleto, seja porque só recuperou parte.
/// A UI decide se oferece outro <c>.bak</c> ou se prossegue com a remoção dos órfãos remanescentes.
/// </summary>
public sealed class ParentRecoveryPrompt
{
    public string DatabaseName { get; init; } = string.Empty;

    /// <summary>Total de registros-pai recuperados até agora (somando todas as tentativas).</summary>
    public int Recovered { get; init; }

    /// <summary>Quantidade de violações de FK (órfãos) que AINDA não têm pai.</summary>
    public int RemainingOrphans { get; init; }

    /// <summary>Motivo legível da recuperação incompleta (ex.: "arquivo ilegível", "recuperação parcial").</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>Último caminho de backup tentado (null = tentativa automática via histórico do msdb).</summary>
    public string? LastTriedPath { get; init; }
}

/// <summary>
/// Callback opcional acionado quando a recuperação de registros-pai falha ou fica parcial, ANTES de
/// remover os órfãos. Deve devolver o caminho de OUTRO arquivo <c>.bak</c> para uma nova tentativa,
/// ou <c>null</c> para prosseguir com a remoção dos órfãos remanescentes. Numa app WPF, marshale a
/// interação (MessageBox/OpenFileDialog) para a thread de UI.
/// </summary>
public delegate Task<string?> ChooseAnotherBackupAsync(ParentRecoveryPrompt prompt, CancellationToken ct);

/// <summary>
/// Opções da recuperação de registros-pai órfãos a partir de backup. Tudo opcional: sem nada, a
/// recuperação é automática (FULL mais recente do histórico) e, se falhar/for parcial, cai direto na
/// remoção dos órfãos — comportamento retrocompatível.
/// </summary>
public sealed class ParentRecoveryOptions
{
    /// <summary>.bak inicial escolhido pelo usuário (null = automático via histórico do msdb).</summary>
    public string? InitialBackupPath { get; init; }

    /// <summary>Callback para escolher outro .bak quando a recuperação falha/é parcial (null = não interativo).</summary>
    public ChooseAnotherBackupAsync? ChooseAnotherBackup { get; init; }
}
