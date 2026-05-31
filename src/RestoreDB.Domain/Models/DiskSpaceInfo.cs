namespace RestoreDB.Domain.Models;

/// <summary>
/// Espaço livre de um volume que hospeda arquivos do banco (sys.dm_os_volume_stats,
/// disponível a partir do SQL Server 2008 R2 SP1). Pouco espaço livre é uma causa comum de
/// RECOVERY_PENDING / log cheio e também impede o DBCC CHECKDB de criar seu snapshot.
/// </summary>
public sealed class DiskSpaceInfo
{
    public string VolumeMountPoint { get; init; } = string.Empty;
    public string LogicalVolumeName { get; init; } = string.Empty;
    public long TotalBytes { get; init; }
    public long AvailableBytes { get; init; }

    public double TotalGb => TotalBytes / 1024.0 / 1024.0 / 1024.0;
    public double AvailableGb => AvailableBytes / 1024.0 / 1024.0 / 1024.0;

    public double PercentFree => TotalBytes == 0 ? 0 : 100.0 * AvailableBytes / TotalBytes;

    /// <summary>Heurística: menos de 1 GB ou menos de 5% livre é considerado crítico.</summary>
    public bool IsLow => AvailableBytes < 1L * 1024 * 1024 * 1024 || PercentFree < 5.0;
}
