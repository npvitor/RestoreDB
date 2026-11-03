using System.Diagnostics;
using System.Text.Json;
using RestoreDB.DataAccess;
using RestoreDB.Domain.Constants;
using RestoreDB.Domain.Models;

namespace RestoreDB.Services;

/// <summary>Progresso persistido da verificação em partes de um banco.</summary>
public sealed class IncrementalCheckState
{
    public string DatabaseName { get; set; } = string.Empty;
    public DateTime CycleStartedUtc { get; set; }
    public DateTime? LastRunUtc { get; set; }
    public DateTime? LastCycleCompletedUtc { get; set; }

    /// <summary>Próxima tabela a verificar (índice na lista ordenada por nome).</summary>
    public int NextIndex { get; set; }

    /// <summary>Tabelas/estruturas com erro encontradas NESTE ciclo.</summary>
    public List<string> Errors { get; set; } = new();
}

/// <summary>Resultado de uma rodada da verificação em partes.</summary>
public sealed class IncrementalCheckOutcome
{
    public int CheckedNow { get; set; }
    public int TotalTables { get; set; }
    public int Position { get; set; }
    public bool CycleCompleted { get; set; }
    public List<string> Errors { get; } = new();
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// VERIFICAÇÃO EM PARTES para bancos grandes demais para um CHECKDB inteiro na janela disponível:
/// cada rodada verifica as tabelas por um tempo limitado (DBCC CHECKTABLE) e lembra onde parou; no
/// início de cada ciclo roda também CHECKALLOC (alocação) e CHECKCATALOG (metadados). Ao fim do ciclo,
/// todo o banco foi coberto — equivalente a um CHECKDB distribuído em várias noites. Somente leitura.
/// </summary>
public sealed class IncrementalCheckService
{
    private readonly SqlDataAccess _data = new();
    private readonly LogService _log;
    private readonly string _dir;

    public IncrementalCheckService(LogService log, string? baseDir = null)
    {
        _log = log;
        _dir = baseDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RestoreDB", "IncrementalCheck");
    }

    private string FileFor(string db) => Path.Combine(_dir, AppConstants.SanitizeToken(db) + ".json");

    public IncrementalCheckState Load(string db)
    {
        try
        {
            var path = FileFor(db);
            if (File.Exists(path))
                return JsonSerializer.Deserialize<IncrementalCheckState>(File.ReadAllText(path)) ?? new IncrementalCheckState { DatabaseName = db };
        }
        catch { /* estado corrompido: recomeça */ }
        return new IncrementalCheckState { DatabaseName = db };
    }

    public void Save(IncrementalCheckState state)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(FileFor(state.DatabaseName), JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Posição de partida da rodada: recomeça do zero se a lista encolheu (tabelas removidas) ou se o
    /// ciclo anterior terminou. Puro — testável.
    /// </summary>
    public static (int Start, bool NewCycle) StartPosition(IncrementalCheckState state, int totalTables)
    {
        if (state.NextIndex <= 0 || state.NextIndex >= totalTables) return (0, true);
        return (state.NextIndex, false);
    }

    public async Task<IncrementalCheckOutcome> RunAsync(
        DatabaseInfo db, string connectionString, TimeSpan budget, IProgress<RepairLogEntry> p, CancellationToken ct = default)
    {
        var outcome = new IncrementalCheckOutcome();
        var state = Load(db.Name);
        await using var s = await _data.OpenSessionAsync(connectionString, ct);
        var tables = (await s.ListUserTablesAsync(db.Name, ct)).OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
        outcome.TotalTables = tables.Count;

        var (start, newCycle) = StartPosition(state, tables.Count);
        var clock = Stopwatch.StartNew();
        if (newCycle)
        {
            state.CycleStartedUtc = DateTime.UtcNow;
            state.Errors.Clear();
            Report(p, db.Name, RepairLogLevel.Info, $"════════ Verificação em partes de [{db.Name}] — novo ciclo ({tables.Count} tabela(s)) ════════");
            foreach (var (label, cmd) in new[] { ("alocação (CHECKALLOC)", "CHECKALLOC"), ("metadados (CHECKCATALOG)", "CHECKCATALOG") })
            {
                var r = await s.ExecuteAsync($"DBCC {cmd}",
                    $"DECLARE @c nvarchar(max) = N'DBCC {cmd} (' + QUOTENAME(@db) + N') WITH NO_INFOMSGS;';\nEXEC sys.sp_executesql @c;",
                    new Dictionary<string, object?> { ["@db"] = db.Name }, AppConstants.RepairCommandTimeoutSeconds, ct);
                if (r.Success)
                    Report(p, db.Name, RepairLogLevel.Success, $"  ✔ Estrutura de {label}: sem erros.");
                else
                {
                    state.Errors.Add($"Estrutura de {label}");
                    Report(p, db.Name, RepairLogLevel.Warning, $"  ⚠ Estrutura de {label}: " + string.Join(" ", r.ErrorMessages.Take(3)));
                }
            }
        }
        else
        {
            Report(p, db.Name, RepairLogLevel.Info,
                $"════════ Verificação em partes de [{db.Name}] — continuando da tabela {start + 1} de {tables.Count} ════════");
        }

        var i = start;
        for (; i < tables.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (clock.Elapsed >= budget && outcome.CheckedNow > 0) break;
            var t = tables[i];
            var rep = await s.CheckTableAsync(db.Name, t, ct);
            outcome.CheckedNow++;
            if (rep.Executed && rep.HasErrors)
            {
                state.Errors.Add(t);
                Report(p, db.Name, RepairLogLevel.Warning, $"  ⚠ {t}: {rep.ErrorCount} erro(s) de consistência.");
            }
            else if (!rep.Executed)
            {
                state.Errors.Add(t + " (não foi possível ler)");
                Report(p, db.Name, RepairLogLevel.Warning, $"  ⚠ {t}: não foi possível verificar.");
            }
            state.NextIndex = i + 1;
            state.LastRunUtc = DateTime.UtcNow;
            Save(state);
        }

        outcome.Position = i;
        outcome.CycleCompleted = i >= tables.Count;
        outcome.Errors.AddRange(state.Errors);
        if (outcome.CycleCompleted)
        {
            state.NextIndex = 0;
            state.LastCycleCompletedUtc = DateTime.UtcNow;
        }
        state.LastRunUtc = DateTime.UtcNow;
        Save(state);

        outcome.Message = outcome.CycleCompleted
            ? (outcome.Errors.Count == 0
                ? $"Ciclo completo: todas as {tables.Count} tabela(s) verificadas, sem erros."
                : $"Ciclo completo com {outcome.Errors.Count} problema(s): {string.Join(", ", outcome.Errors.Take(5))}. Rode o Diagnóstico completo.")
            : $"{outcome.CheckedNow} tabela(s) verificada(s) nesta rodada ({i} de {tables.Count} no ciclo)" +
              (outcome.Errors.Count > 0 ? $" — {outcome.Errors.Count} problema(s) até agora: {string.Join(", ", outcome.Errors.Take(5))}." : ", sem erros até agora.") +
              " Rode de novo para continuar de onde parou.";
        Report(p, db.Name, outcome.Errors.Count == 0 ? RepairLogLevel.Success : RepairLogLevel.Warning, outcome.Message);
        return outcome;
    }

    private void Report(IProgress<RepairLogEntry> p, string db, RepairLogLevel level, string message)
    {
        var entry = new RepairLogEntry { Level = level, Message = message, Database = db };
        _log.Write(entry);
        p.Report(entry);
    }
}
