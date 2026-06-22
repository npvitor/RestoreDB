namespace RestoreDB.Domain.Models;

/// <summary>Uma peça (conjunto) de backup a ser restaurada, na ordem da cadeia de restauração.</summary>
public sealed class RestorePiece
{
    public string DiskPath { get; init; } = string.Empty;

    /// <summary>Arquivos ADICIONAIS quando o backup está dividido em vários (striped). Vazio = único.</summary>
    public List<string> AdditionalDevices { get; init; } = new();

    /// <summary>Todos os dispositivos do conjunto (o principal + os adicionais), para o FROM DISK=...</summary>
    public IReadOnlyList<string> AllDevices =>
        new[] { DiskPath }.Concat(AdditionalDevices).Where(d => !string.IsNullOrEmpty(d)).ToList();

    /// <summary>Posição (WITH FILE = n) dentro do arquivo físico. 1 quando há um único conjunto.</summary>
    public int Position { get; init; } = 1;

    /// <summary>L = log; D/I = dados/diferencial. Usado apenas para montar o comando correto.</summary>
    public bool IsLog { get; init; }
}

/// <summary>
/// Pedido de restauração de um banco a partir de backup. Suporta cadeia (full → diferencial →
/// logs), realocação de arquivos (WITH MOVE), substituição (REPLACE) e ponto-no-tempo (STOPAT).
/// </summary>
public sealed class RestoreRequest
{
    public string DatabaseName { get; init; } = string.Empty;

    /// <summary>Peças na ordem de aplicação. A última define o estado final (RECOVERY).</summary>
    public List<RestorePiece> Pieces { get; init; } = new();

    /// <summary>Sobrescreve um banco existente (WITH REPLACE).</summary>
    public bool Replace { get; init; }

    /// <summary>Mapeia nome lógico → caminho físico de destino (WITH MOVE). Vazio = manter original.</summary>
    public Dictionary<string, string> MoveMap { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Quando true e <see cref="MoveMap"/> está vazio, a restauração resolve os MOVE
    /// AUTOMATICAMENTE: lê os arquivos do backup (FILELISTONLY) e realoca para a pasta padrão da
    /// instância os que não couberem no caminho original (pasta inexistente no servidor).
    /// </summary>
    public bool AutoRelocate { get; init; }

    /// <summary>Restauração ponto-no-tempo (apenas em peças de log; WITH STOPAT).</summary>
    public DateTime? StopAt { get; init; }

    /// <summary>Preserva configurações de replicação no RESTORE final (WITH KEEP_REPLICATION).</summary>
    public bool KeepReplication { get; init; }

    /// <summary>
    /// Caminho do arquivo de UNDO para restauração em STANDBY (somente leitura entre logs). Quando
    /// informado, a ÚLTIMA peça usa WITH STANDBY = '...' em vez de RECOVERY, deixando o banco
    /// consultável; finalize depois com RESTORE ... WITH RECOVERY.
    /// </summary>
    public string? StandbyUndoFile { get; init; }
}
