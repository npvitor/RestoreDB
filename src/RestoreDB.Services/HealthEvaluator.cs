using RestoreDB.Domain.Models;

namespace RestoreDB.Services;

/// <summary>
/// Transforma as configurações e achados do diagnóstico em AJUSTES RECOMENDADOS, em linguagem simples.
/// Os ajustes marcados como corrigíveis não mexem em dados (só configuração/metadados); os demais são
/// apenas avisos porque a correção depende do fornecedor do sistema ou do DBA. Puro — testável sem banco.
/// </summary>
public static class HealthEvaluator
{
    /// <summary>Acima disso o log fica lento para recuperar/fazer backup (referência usual de mercado).</summary>
    public const int VlfWarnThreshold = 1000;

    /// <summary>Crescimento fixo abaixo disso, num arquivo grande, gera crescimentos demais.</summary>
    public const double TinyGrowthMb = 8;

    public static List<HealthIssue> Evaluate(
        DatabaseSettings? s,
        IReadOnlyCollection<string> untrustedConstraints,
        IReadOnlyCollection<string> disabledIndexes,
        IReadOnlyCollection<string> brokenModules,
        bool isSystemDatabase = false)
    {
        var list = new List<HealthIssue>();

        if (s is not null)
        {
            if (s.PageVerify.Length > 0 && !s.PageVerify.Equals("CHECKSUM", StringComparison.OrdinalIgnoreCase))
                list.Add(new HealthIssue
                {
                    Kind = HealthIssueKind.PageVerify, Severity = FindingSeverity.Medium, CanFix = true,
                    Title = $"Proteção contra corrupção desligada (PAGE_VERIFY = {s.PageVerify})",
                    Detail = "Sem CHECKSUM, uma página danificada pelo disco pode passar despercebida e se espalhar. " +
                             "Ligar não altera nenhum dado; as páginas passam a ser protegidas conforme forem gravadas.",
                    FixLabel = "Ligar PAGE_VERIFY CHECKSUM"
                });

            if (s.AutoShrink)
                list.Add(new HealthIssue
                {
                    Kind = HealthIssueKind.AutoShrink, Severity = FindingSeverity.Medium, CanFix = true,
                    Title = "Encolhimento automático ligado (AUTO_SHRINK)",
                    Detail = "O banco encolhe e volta a crescer o tempo todo: fragmenta os índices e causa travamentos. Desligar não altera dados.",
                    FixLabel = "Desligar AUTO_SHRINK"
                });

            if (s.AutoClose && !isSystemDatabase)
                list.Add(new HealthIssue
                {
                    Kind = HealthIssueKind.AutoClose, Severity = FindingSeverity.Low, CanFix = true,
                    Title = "Fechamento automático ligado (AUTO_CLOSE)",
                    Detail = "O banco é fechado e reaberto a cada conexão, deixando o sistema lento. Desligar não altera dados.",
                    FixLabel = "Desligar AUTO_CLOSE"
                });

            var badGrowth = s.Files.Where(NeedsGrowthFix).ToList();
            if (badGrowth.Count > 0)
                list.Add(new HealthIssue
                {
                    Kind = HealthIssueKind.FileGrowth, Severity = FindingSeverity.Medium, CanFix = true,
                    Title = "Crescimento dos arquivos mal configurado",
                    Detail = "Crescer em porcentagem (ou de pouquinho em pouquinho) causa pausas cada vez maiores e fragmenta o disco/log. " +
                             "A correção troca por um crescimento fixo adequado ao tamanho do arquivo — não altera dados.",
                    FixLabel = "Usar crescimento fixo",
                    Items = badGrowth.Select(f => f.Display + " → " + RecommendedGrowthMb(f) + " MB").ToList()
                });

            if (s.VlfCount > VlfWarnThreshold)
                list.Add(new HealthIssue
                {
                    Kind = HealthIssueKind.TooManyVlfs, Severity = s.VlfCount > 10 * VlfWarnThreshold ? FindingSeverity.High : FindingSeverity.Medium,
                    CanFix = true,
                    Title = $"Log de transações muito fragmentado ({s.VlfCount:N0} VLFs)",
                    Detail = "Com o log picado em milhares de pedaços, o banco demora muito para voltar depois de uma queda (fica em RECOVERING) " +
                             "e backups/restaurações ficam lentos. A correção encolhe o log e o recria em blocos grandes, no mesmo tamanho. " +
                             (s.RecoveryModel.Equals("SIMPLE", StringComparison.OrdinalIgnoreCase)
                                 ? "Não altera dados."
                                 : "Não altera dados; no modelo FULL o ganho pode ser parcial até o próximo backup de log."),
                    FixLabel = "Reorganizar o log"
                });

            if (s.OrphanedUsers.Count > 0)
            {
                var fixable = s.OrphanedUsers.Where(u => u.LoginExists).Select(u => u.User).ToList();
                var noLogin = s.OrphanedUsers.Where(u => !u.LoginExists).Select(u => u.User).ToList();
                list.Add(new HealthIssue
                {
                    Kind = HealthIssueKind.OrphanedUsers, Severity = FindingSeverity.Medium, CanFix = fixable.Count > 0,
                    Title = $"{s.OrphanedUsers.Count} usuário(s) do banco sem login correspondente",
                    Detail = "Acontece ao mover, anexar ou restaurar o banco em outro servidor: o usuário existe no banco, mas não " +
                             "consegue entrar. " +
                             (fixable.Count > 0 ? $"{fixable.Count} pode(m) ser religado(s) ao login de mesmo nome. " : "") +
                             (noLogin.Count > 0 ? $"{noLogin.Count} não tem login com o mesmo nome no servidor — crie o login (com a senha certa) e rode de novo." : ""),
                    FixLabel = "Religar usuários aos logins",
                    Items = fixable.Select(u => u + " (religável)").Concat(noLogin.Select(u => u + " (sem login no servidor)")).ToList()
                });
            }

            if (s.OwnerName.Length == 0)
                list.Add(new HealthIssue
                {
                    Kind = HealthIssueKind.InvalidOwner, Severity = FindingSeverity.Medium, CanFix = !s.Trustworthy,
                    Title = "Dono do banco inválido (o login dono não existe mais)",
                    Detail = s.Trustworthy
                        ? "Com TRUSTWORTHY ligado, trocar o dono muda as permissões do sistema — acione o DBA/fornecedor."
                        : "Causa erros em rotinas agendadas, em permissões e ao restaurar. A correção torna o administrador (sa) o dono — não altera dados.",
                    FixLabel = "Corrigir o dono do banco"
                });

            if (s.IsReadOnly)
                list.Add(new HealthIssue
                {
                    Kind = HealthIssueKind.ReadOnly, Severity = FindingSeverity.Medium, CanFix = true,
                    Title = "Banco em modo SOMENTE LEITURA",
                    Detail = "O sistema não consegue gravar nada. Pode ser proposital (banco de arquivo/histórico) — confirme antes de liberar a gravação.",
                    FixLabel = "Liberar gravação (READ_WRITE)"
                });

            if (s.CompatibilityLevel > 0 && s.ServerCompatibilityLevel > 0 && s.CompatibilityLevel < s.ServerCompatibilityLevel - 20)
                list.Add(new HealthIssue
                {
                    Kind = HealthIssueKind.OldCompatibility, Severity = FindingSeverity.Info, CanFix = false,
                    Title = $"Nível de compatibilidade antigo ({s.CompatibilityLevel}; o servidor suporta {s.ServerCompatibilityLevel})",
                    Detail = "Normal em bancos migrados. Mudar pode alterar o comportamento do sistema — só com aval do fornecedor."
                });

            if (s.Trustworthy && !isSystemDatabase)
                list.Add(new HealthIssue
                {
                    Kind = HealthIssueKind.Trustworthy, Severity = FindingSeverity.Low, CanFix = false,
                    Title = "TRUSTWORTHY ligado",
                    Detail = "Amplia permissões de código do banco (risco de segurança). Desligar pode quebrar o sistema — avalie com o fornecedor/DBA."
                });
        }

        if (untrustedConstraints.Count > 0)
            list.Add(new HealthIssue
            {
                Kind = HealthIssueKind.UntrustedConstraints, Severity = FindingSeverity.Low, CanFix = true,
                Title = $"{untrustedConstraints.Count} regra(s) de integridade não verificada(s) (constraints não confiáveis)",
                Detail = "As regras existem, mas o SQL Server não garante que os dados atuais as respeitam (e não as usa para otimizar consultas). " +
                         "A correção revalida cada regra; as que tiverem dados violando continuam como estão e são listadas no registro.",
                FixLabel = "Revalidar as regras",
                Items = untrustedConstraints.Take(30).ToList()
            });

        if (disabledIndexes.Count > 0)
            list.Add(new HealthIssue
            {
                Kind = HealthIssueKind.DisabledIndexes, Severity = FindingSeverity.Medium, CanFix = true,
                Title = $"{disabledIndexes.Count} índice(s) desabilitado(s)",
                Detail = "Consultas ficam lentas — e, se for o índice principal (clusterizado), a tabela fica inacessível. " +
                         "A correção reconstrói os índices (não altera dados; pode demorar em tabelas grandes).",
                FixLabel = "Reconstruir os índices",
                Items = disabledIndexes.Take(30).ToList()
            });

        if (brokenModules.Count > 0)
            list.Add(new HealthIssue
            {
                Kind = HealthIssueKind.BrokenModules, Severity = FindingSeverity.Low, CanFix = true,
                Title = $"{brokenModules.Count} view(s)/procedure(s) com referência quebrada",
                Detail = "Elas citam objetos que não existem mais (ou mudaram). A correção revalida os metadados; as que continuarem " +
                         "quebradas dependem do fornecedor do sistema e ficam listadas no registro.",
                FixLabel = "Revalidar os objetos",
                Items = brokenModules.Take(30).ToList()
            });

        return list.OrderByDescending(i => i.Severity).ThenBy(i => i.Kind).ToList();
    }

    /// <summary>Arquivo com crescimento em % ou fixo minúsculo num arquivo grande (≥ 1 GB).</summary>
    public static bool NeedsGrowthFix(FileGrowthSetting f) =>
        f.IsPercentGrowth ? f.Growth > 0 : f.Growth > 0 && f.Growth < TinyGrowthMb && f.SizeMb >= 1024;

    /// <summary>
    /// Crescimento fixo recomendado (MB) conforme o tamanho do arquivo: pequeno 64; até 10 GB 256; acima 512 (dados)
    /// ou 256 (log — blocos de log muito grandes não ajudam e demoram para zerar).
    /// </summary>
    public static int RecommendedGrowthMb(FileGrowthSetting f) =>
        f.SizeMb < 1024 ? 64 : f.SizeMb < 10 * 1024 || f.IsLog ? 256 : 512;
}
