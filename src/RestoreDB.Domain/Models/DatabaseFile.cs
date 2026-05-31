namespace RestoreDB.Domain.Models;

/// <summary>Um arquivo físico de um banco (linha de sys.master_files).</summary>
public sealed class DatabaseFile
{
    /// <summary>0 = ROWS (dados: .mdf/.ndf); 1 = LOG (.ldf).</summary>
    public int Type { get; init; }
    public string TypeDesc { get; init; } = string.Empty;
    public string PhysicalName { get; init; } = string.Empty;

    public bool IsData => Type == 0;
    public bool IsLog => Type == 1;
}
