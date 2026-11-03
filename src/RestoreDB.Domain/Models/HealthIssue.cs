namespace RestoreDB.Domain.Models;

/// <summary>Tipo de ajuste recomendado (configuração/saúde) — cada um tem uma correção própria, sem perda de dados.</summary>
public enum HealthIssueKind
{
    /// <summary>PAGE_VERIFY diferente de CHECKSUM: corrupção futura passa despercebida.</summary>
    PageVerify,
    /// <summary>AUTO_SHRINK ligado: encolhe e recresce os arquivos o tempo todo (fragmenta e trava).</summary>
    AutoShrink,
    /// <summary>AUTO_CLOSE ligado: o banco fecha/reabre a cada conexão (lentidão).</summary>
    AutoClose,
    /// <summary>Crescimento de arquivo em % ou muito pequeno: pausas longas e fragmentação.</summary>
    FileGrowth,
    /// <summary>Log de transações com VLFs demais: recuperação (RECOVERING) e backups lentos.</summary>
    TooManyVlfs,
    /// <summary>Constraints não confiáveis (is_not_trusted): integridade nunca validada.</summary>
    UntrustedConstraints,
    /// <summary>Índices desabilitados: consultas lentas ou tabela inacessível (clusterizado).</summary>
    DisabledIndexes,
    /// <summary>Views/procedures com referências quebradas: revalidar os metadados.</summary>
    BrokenModules,
    /// <summary>Usuários do banco sem login correspondente (após mover/anexar/restaurar o banco).</summary>
    OrphanedUsers,
    /// <summary>Dono do banco inválido (login inexistente): erros em jobs, CLR e permissões.</summary>
    InvalidOwner,
    /// <summary>Banco marcado como somente leitura.</summary>
    ReadOnly,
    /// <summary>Nível de compatibilidade antigo (apenas informativo — mudar afeta o sistema).</summary>
    OldCompatibility,
    /// <summary>TRUSTWORTHY ligado (apenas informativo — desligar pode quebrar o sistema).</summary>
    Trustworthy
}

/// <summary>
/// Um ajuste recomendado pelo diagnóstico, em linguagem simples. Quando <see cref="CanFix"/> é true, a
/// ferramenta sabe corrigir sozinha, sem perda de dados; senão é só um aviso (com o motivo em
/// <see cref="Detail"/>), porque a correção depende de uma decisão do fornecedor/DBA.
/// </summary>
public sealed class HealthIssue
{
    public HealthIssueKind Kind { get; init; }
    public FindingSeverity Severity { get; init; } = FindingSeverity.Low;

    /// <summary>Frase curta e leiga: o que está errado.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Por que importa e o que a correção faz (ou por que não é automática).</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>A ferramenta corrige sozinha, sem perda de dados.</summary>
    public bool CanFix { get; init; }

    /// <summary>Rótulo da ação de correção (ex.: "Ligar CHECKSUM").</summary>
    public string FixLabel { get; init; } = string.Empty;

    /// <summary>Objetos envolvidos (constraints, índices, arquivos, usuários...).</summary>
    public List<string> Items { get; init; } = new();

    public string Display => $"[{Severity.ToString().ToUpperInvariant()}] {Title}";
}

/// <summary>Configurações do banco relevantes para os ajustes recomendados (lidas de sys.databases/sys.master_files).</summary>
public sealed class DatabaseSettings
{
    public string PageVerify { get; set; } = string.Empty;
    public bool AutoShrink { get; set; }
    public bool AutoClose { get; set; }
    public bool IsReadOnly { get; set; }
    public string UserAccess { get; set; } = string.Empty;
    public bool Trustworthy { get; set; }
    public int CompatibilityLevel { get; set; }

    /// <summary>Nível de compatibilidade nativo da instância (100 = 2008/2008 R2 ... 170 = 2025).</summary>
    public int ServerCompatibilityLevel { get; set; }

    /// <summary>Dono do banco (nome do login), ou vazio quando o SID não corresponde a nenhum login.</summary>
    public string OwnerName { get; set; } = string.Empty;

    /// <summary>Quantidade de VLFs no log (-1 = desconhecido).</summary>
    public int VlfCount { get; set; } = -1;

    public string RecoveryModel { get; set; } = string.Empty;

    public List<FileGrowthSetting> Files { get; } = new();

    /// <summary>Usuários órfãos: (usuário, existe login de mesmo nome → reassociável).</summary>
    public List<(string User, bool LoginExists)> OrphanedUsers { get; } = new();
}

/// <summary>Configuração de crescimento de um arquivo do banco.</summary>
public sealed class FileGrowthSetting
{
    public string LogicalName { get; init; } = string.Empty;
    public bool IsLog { get; init; }
    public bool IsPercentGrowth { get; init; }

    /// <summary>Crescimento: % quando <see cref="IsPercentGrowth"/>, senão em MB.</summary>
    public double Growth { get; init; }
    public double SizeMb { get; init; }

    public string Display => IsPercentGrowth
        ? $"{LogicalName}: cresce {Growth:0}% (tamanho atual {SizeMb:N0} MB)"
        : $"{LogicalName}: cresce {Growth:0.#} MB (tamanho atual {SizeMb:N0} MB)";
}

/// <summary>Sessão conectada a um banco (quem está "segurando" o acesso).</summary>
public sealed class DbSessionInfo
{
    public int SessionId { get; init; }
    public string Login { get; init; } = string.Empty;
    public string Host { get; init; } = string.Empty;
    public string Program { get; init; } = string.Empty;
    public DateTime? LastBatch { get; init; }

    public string Display =>
        $"sessão {SessionId} — {(Program.Length > 0 ? Program : "programa desconhecido")} em {(Host.Length > 0 ? Host : "?")} ({Login})" +
        (LastBatch is { } t ? $", última atividade {t:dd/MM HH:mm}" : string.Empty);
}
