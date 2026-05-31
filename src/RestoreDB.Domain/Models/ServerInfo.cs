namespace RestoreDB.Domain.Models;

/// <summary>
/// Informações da instância do SQL Server, coletadas logo após a conexão. Servem para
/// pré-checagens (o login é sysadmin? a edição suporta restore de página online?) e para
/// adaptar o fluxo de reparo às capacidades do servidor.
/// </summary>
public sealed class ServerInfo
{
    public string ProductVersion { get; init; } = string.Empty;
    public string ProductLevel { get; init; } = string.Empty;
    public string Edition { get; init; } = string.Empty;
    public string MachineName { get; init; } = string.Empty;
    public string FullVersionText { get; init; } = string.Empty;

    /// <summary>Versão major do engine (10 = 2008/2008 R2, 11 = 2012, 13 = 2016, 16 = 2022...).</summary>
    public int MajorVersion { get; init; }

    /// <summary>SERVERPROPERTY('EngineEdition'): 3 = Enterprise/Developer/Evaluation; 4 = Express; 5 = Azure SQL DB.</summary>
    public int EngineEdition { get; init; }

    /// <summary>O login conectado pertence à role sysadmin (necessário p/ DBCC CHECKDB de reparo, ALTER DATABASE).</summary>
    public bool IsSysadmin { get; init; }

    /// <summary>Azure SQL Database — muitos comandos (ALTER DATABASE SET EMERGENCY, attach, etc.) não se aplicam.</summary>
    public bool IsAzureSqlDatabase => EngineEdition == 5;

    /// <summary>Edições "Enterprise-class" (3) suportam restore de página ONLINE; demais só offline.</summary>
    public bool SupportsOnlinePageRestore => EngineEdition == 3;

    public string ShortDescription =>
        $"{Edition} — versão {ProductVersion} ({ProductLevel})";
}
