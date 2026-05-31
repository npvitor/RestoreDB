namespace RestoreDB.Domain.Models;

/// <summary>Um arquivo físico de um banco (linha de sys.master_files).</summary>
public sealed class DatabaseFile
{
    /// <summary>0 = ROWS (dados: .mdf/.ndf); 1 = LOG (.ldf); 2 = FILESTREAM; 4 = FULLTEXT.</summary>
    public int Type { get; init; }
    public string TypeDesc { get; init; } = string.Empty;
    public string PhysicalName { get; init; } = string.Empty;
    public string LogicalName { get; init; } = string.Empty;

    public bool IsData => Type == 0;
    public bool IsLog => Type == 1;

    /// <summary>FILESTREAM (type 2).</summary>
    public bool IsFileStream => Type == 2;

    /// <summary>Full-text legado (type 4).</summary>
    public bool IsFullText => Type == 4;

    /// <summary>
    /// Arquivo que NÃO é dados (ROWS) nem log simples. A reconstrução de log por
    /// detach + ATTACH_REBUILD_LOG não cobre com segurança bancos com FILESTREAM/full-text.
    /// </summary>
    public bool IsSpecialContainer => IsFileStream || IsFullText;
}
