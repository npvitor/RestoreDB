namespace RestoreDB.Domain.Models;

/// <summary>
/// Topologia de alta disponibilidade/distribuição de um banco. Operações destrutivas
/// (ALTER DATABASE SET ..., detach, RESTORE WITH RECOVERY) podem QUEBRAR essas configurações,
/// portanto a ferramenta detecta e avisa antes de agir.
/// </summary>
public sealed class TopologyInfo
{
    public bool IsMirrored { get; set; }
    public string MirroringRole { get; set; } = string.Empty;
    public string MirroringState { get; set; } = string.Empty;

    public bool IsInAvailabilityGroup { get; set; }
    public string AvailabilityGroupName { get; set; } = string.Empty;

    public bool IsLogShippingPrimary { get; set; }
    public bool IsLogShippingSecondary { get; set; }

    public bool IsPublished { get; set; }
    public bool IsSubscribed { get; set; }
    public bool IsMergePublished { get; set; }

    /// <summary>Há alguma configuração que torna o reparo destrutivo arriscado/inadequado.</summary>
    public bool IsRisky =>
        IsMirrored || IsInAvailabilityGroup || IsLogShippingPrimary || IsLogShippingSecondary
        || IsPublished || IsSubscribed || IsMergePublished;

    public List<string> Warnings { get; } = new();

    public string Summary
    {
        get
        {
            if (!IsRisky) return "Sem topologia de alta disponibilidade/replicação detectada.";
            var parts = new List<string>();
            if (IsInAvailabilityGroup) parts.Add($"Always On AG '{AvailabilityGroupName}'");
            if (IsMirrored) parts.Add($"Espelhamento ({MirroringRole}/{MirroringState})");
            if (IsLogShippingPrimary) parts.Add("Log shipping (primário)");
            if (IsLogShippingSecondary) parts.Add("Log shipping (secundário)");
            if (IsPublished || IsMergePublished) parts.Add("Replicação (publicador)");
            if (IsSubscribed) parts.Add("Replicação (assinante)");
            return "Participa de: " + string.Join("; ", parts) + ".";
        }
    }
}
