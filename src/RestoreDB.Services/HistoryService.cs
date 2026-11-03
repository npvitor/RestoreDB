using System.Text.Json;
using RestoreDB.Domain.Constants;
using RestoreDB.Domain.Models;

namespace RestoreDB.Services;

/// <summary>
/// Histórico de diagnósticos: persiste um snapshot por verificação em JSON local e compara o
/// atual com o anterior para detecção PRECOCE — aponta corrupção/violações NOVAS desde a última vez.
/// Best-effort: nunca lança para o chamador (falha de E/S só significa "sem histórico").
/// </summary>
public sealed class HistoryService
{
    private const int MaxKept = 50;
    private readonly string _dir;

    public HistoryService(string? baseDir = null)
    {
        _dir = baseDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RestoreDB", "History");
    }

    private string FileFor(string db) => Path.Combine(_dir, AppConstants.SanitizeToken(db) + ".json");

    public List<DiagnosisSnapshot> Load(string db)
    {
        try
        {
            var path = FileFor(db);
            if (!File.Exists(path)) return new List<DiagnosisSnapshot>();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<DiagnosisSnapshot>>(json) ?? new List<DiagnosisSnapshot>();
        }
        catch { return new List<DiagnosisSnapshot>(); }
    }

    public DiagnosisSnapshot? LoadLatest(string db) => Load(db).LastOrDefault();

    public void Save(string db, DiagnosisSnapshot snapshot)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var all = Load(db);
            all.Add(snapshot);
            if (all.Count > MaxKept) all = all.Skip(all.Count - MaxKept).ToList();
            File.WriteAllText(FileFor(db), JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Diferenças relevantes do snapshot atual em relação ao anterior (o que PIOROU) — para alertar
    /// "novo desde a última verificação". Lista vazia = nada novo (ou primeira verificação).
    /// </summary>
    public static List<string> Compare(DiagnosisSnapshot? previous, DiagnosisSnapshot current)
    {
        var news = new List<string>();
        if (previous is null) return news;

        if (current.PhysicalCorruption && !previous.PhysicalCorruption)
            news.Add("Corrupção FÍSICA nova (DBCC CHECKDB) desde a última verificação.");
        else if (current.CheckDbErrors > previous.CheckDbErrors)
            news.Add($"Mais erros de CHECKDB: {current.CheckDbErrors} (antes {previous.CheckDbErrors}).");

        if (current.LogicalCorruption && !previous.LogicalCorruption)
            news.Add("Corrupção LÓGICA nova (constraints) desde a última verificação.");

        if (current.ForeignKeyViolations > previous.ForeignKeyViolations)
            news.Add($"Mais violações de chave estrangeira: {current.ForeignKeyViolations} (antes {previous.ForeignKeyViolations}).");

        if (current.SuspectPages > previous.SuspectPages)
            news.Add($"Mais páginas suspeitas: {current.SuspectPages} (antes {previous.SuspectPages}).");

        // Banco que SAIU DO AR (SUSPECT/RECOVERY_PENDING/OFFLINE...) sem contador de corrupção novo — log cheio,
        // arquivo ausente, disco cheio — é justamente o que o monitoramento agendado precisa avisar.
        if (AppConstants.IsProblematic(current.StateDesc)
            && !string.Equals(current.StateDesc, previous.StateDesc, StringComparison.OrdinalIgnoreCase))
            news.Add($"O banco está {current.StateDesc} (antes {(previous.StateDesc.Length > 0 ? previous.StateDesc : "desconhecido")}) — indisponível para o sistema.");

        return news;
    }
}
