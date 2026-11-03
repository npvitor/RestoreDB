using System.Globalization;
using RestoreDB.DataAccess;
using RestoreDB.Domain.Constants;
using RestoreDB.Domain.Models;

namespace RestoreDB.Services;

/// <summary>
/// SALVAMENTO SEM BACKUP: antes do REPAIR_ALLOW_DATA_LOSS, copia para um banco de salvamento todas as
/// linhas ainda LEGÍVEIS das tabelas danificadas — por faixas da chave, pulando só os trechos em
/// páginas destruídas (<see cref="RangeBisector"/>). Depois do reparo, devolve ao banco o que o reparo
/// descartou (por chave primária, sem duplicar, gatilhos desligados).
///
/// Por que é seguro devolver automaticamente: a cópia é feita com o banco em acesso EXCLUSIVO, no
/// instante anterior ao reparo — retrata exatamente aquele momento. Tudo o que está na cópia e falta
/// no banco depois do reparo foi descartado PELO reparo (nunca um registro excluído de propósito).
///
/// Ganho real: o reparo pode descartar mais do que as páginas ilegíveis (cadeias de páginas, LOB,
/// estruturas de alocação) — a cópia preserva o que dava para ler. As linhas que estavam nas páginas
/// destruídas só voltam com backup; quando um índice não-clusterizado ainda as tem, uma versão
/// PARCIAL (só as colunas do índice) fica guardada para revisão do DBA, sem voltar sozinha.
/// </summary>
internal sealed class PreRepairExtractor
{
    private readonly Action<RepairLogLevel, string> _report;

    /// <summary>Limite de trechos ilegíveis por tabela (cada tentativa pode derrubar e refazer a conexão).</summary>
    public int MaxFailuresPerTable { get; init; } = 2000;

    public PreRepairExtractor(Action<RepairLogLevel, string> report) => _report = report;

    public async Task<PreRepairExtraction> ExtractAsync(
        SqlSession s, string db, IReadOnlyCollection<string> tables, IReadOnlyDictionary<string, long> rowsBefore, CancellationToken ct)
    {
        var ex = new PreRepairExtraction();
        if (tables.Count == 0)
        {
            ex.SkippedReason = "nenhuma tabela de usuário identificada como danificada";
            return ex;
        }

        // Espaço: a cópia ocupa no máximo o tamanho atual das tabelas (+ folga).
        var needMb = await s.GetTablesUsedMbAsync(db, tables, ct);
        var freeMb = await FreeMbForDefaultDataDirAsync(s, ct);
        if (freeMb >= 0 && freeMb < needMb * 1.3 + 512)
        {
            ex.SkippedReason = $"espaço em disco insuficiente para a cópia de salvamento (precisa ~{needMb * 1.3 + 512:N0} MB, livre {freeMb:N0} MB)";
            _report(RepairLogLevel.Warning, "Cópia de salvamento NÃO feita: " + ex.SkippedReason + ".");
            return ex;
        }

        var name = $"{AppConstants.SanitizeToken(db, "banco")}_Salvamento_{DateTime.Now:yyyyMMddHHmmss}";
        _report(RepairLogLevel.Info,
            $"Salvando as linhas legíveis de {tables.Count} tabela(s) danificada(s) em [{name}] antes do reparo (~{needMb:N0} MB)...");
        if (!await s.CreateSalvageDatabaseAsync(name, ct))
        {
            ex.SkippedReason = "não foi possível criar o banco de salvamento";
            _report(RepairLogLevel.Warning, "Cópia de salvamento NÃO feita: " + ex.SkippedReason + ".");
            return ex;
        }
        ex.SalvageDatabase = name;

        foreach (var t in tables)
        {
            ct.ThrowIfCancellationRequested();
            var te = await ExtractTableAsync(s, db, name, t, rowsBefore.GetValueOrDefault(t), ct);
            ex.Tables.Add(te);
            if (te.Error.StartsWith("SEM ESPAÇO", StringComparison.Ordinal))
            {
                _report(RepairLogLevel.Warning, "Disco cheio durante a cópia de salvamento — interrompida; o reparo segue com o que foi salvo.");
                break;
            }
        }

        var total = ex.Tables.Sum(x => x.RowsExtracted);
        _report(RepairLogLevel.Success, $"Cópia de salvamento concluída: {total:N0} linha(s) legível(is) guardada(s) em [{name}].");
        return ex;
    }

    private async Task<TableExtraction> ExtractTableAsync(SqlSession s, string db, string salvage, string table, long rowsBefore, CancellationToken ct)
    {
        var te = new TableExtraction { Table = table, RowsBefore = rowsBefore };
        var (schema, name) = Split(table);
        try { te.HasPrimaryKey = (await s.GetPrimaryKeyColumnsAsync(db, schema, name, ct)).Count > 0; }
        catch (OperationCanceledException) { throw; }
        catch { /* metadados ilegíveis */ }

        var cols = await TryAsync(() => s.GetCopyableColumnsAsync(db, table, ct)) ?? new List<string>();
        if (cols.Count == 0 || !await s.CreateSalvageTableAsync(db, salvage, table, cols, ct: ct))
        {
            te.Mode = ExtractionMode.Failed;
            te.Error = "estrutura da tabela ilegível";
            _report(RepairLogLevel.Warning, $"  ⚠ {table}: {te.Error} — nada pôde ser copiado.");
            return te;
        }

        // 1) Tenta a tabela inteira (o caso comum quando o dano está em outro objeto/índice).
        var whole = await s.CopyRowsAsync(db, salvage, table, cols, null, null, ct: ct);
        if (whole.Ok)
        {
            te.Mode = ExtractionMode.Whole;
            te.RowsExtracted = whole.Rows;
            _report(RepairLogLevel.Success, $"  ✔ {table}: {whole.Rows:N0} linha(s) copiada(s) (tabela inteira legível).");
            return te;
        }
        if (whole.Fatal) { te.Mode = ExtractionMode.Failed; te.Error = "SEM ESPAÇO: " + whole.Message; return te; }
        if (!whole.Corruption)
        {
            te.Mode = ExtractionMode.Failed;
            te.Error = $"Msg {whole.ErrorNumber}: {whole.Message}";
            _report(RepairLogLevel.Warning, $"  ⚠ {table}: não foi possível copiar — {te.Error}");
            return te;
        }

        // 2) Há página ilegível no caminho: lê por faixas da chave, isolando os trechos ruins.
        var key = await s.GetRangeKeyAsync(db, table, ct);
        if (key is null || !key.CanBisect)
        {
            te.Mode = ExtractionMode.Failed;
            te.Error = key is null ? "tabela sem índice clusterizado/chave primária" : $"chave do tipo {key.TypeName} não permite leitura por faixas";
            _report(RepairLogLevel.Warning, $"  ⚠ {table}: há páginas ilegíveis e a {te.Error} — nada pôde ser copiado.");
            return te;
        }

        var (lo, hi) = await BoundsAsync(s, db, table, key, ct);
        _report(RepairLogLevel.Info, $"  {table}: páginas ilegíveis no caminho — lendo por faixas de [{key.Column}] para pular só o trecho danificado...");
        var res = await RangeBisector.RunAsync(lo, hi, async (a, b) =>
        {
            var (where, prm) = RangePredicate(key, a, b);
            var o = await s.CopyRowsAsync(db, salvage, table, cols, where, prm, ct: ct);
            return o.Ok ? (RangeCopyStatus.Ok, o.Rows)
                 : o.Corruption ? (RangeCopyStatus.Unreadable, 0L)
                 : (RangeCopyStatus.Abort, 0L);
        }, MaxFailuresPerTable, ct);

        te.Mode = ExtractionMode.KeyRanges;
        te.RowsExtracted = res.RowsCopied;
        if (key.Nullable)
        {
            var nulls = await s.CopyRowsAsync(db, salvage, table, cols, $"{Q(key.Column)} IS NULL", null, ct: ct);
            if (nulls.Ok) te.RowsExtracted += nulls.Rows;
        }
        foreach (var (a, b) in res.Unreadable.Take(50))
            te.UnreadableRanges.Add(DescribeRange(key, a, b));
        if (res.Stopped) te.Error = "leitura interrompida: " + res.StopReason;

        _report(res.Unreadable.Count == 0 ? RepairLogLevel.Success : RepairLogLevel.Warning,
            $"  {(res.Unreadable.Count == 0 ? "✔" : "◐")} {table}: {te.RowsExtracted:N0} linha(s) copiada(s)" +
            (te.UnreadableRanges.Count > 0 ? $"; trecho(s) ilegível(is): {string.Join("; ", te.UnreadableRanges.Take(5))}" : string.Empty) +
            (res.Stopped ? $" ({res.StopReason})" : string.Empty) + ".");

        // 3) Linhas dos trechos ilegíveis que um índice não-clusterizado ainda tem: versão PARCIAL, só para revisão.
        if (res.Unreadable.Count > 0)
            te.PartialRows = await SalvagePartialRowsAsync(s, db, salvage, table, key, res.Unreadable, ct);
        return te;
    }

    /// <summary>
    /// Para os trechos ilegíveis, lê o índice NÃO clusterizado mais largo (sem tocar nas páginas de dados
    /// danificadas) e guarda as linhas PARCIAIS em "tabela__parcial" no banco de salvamento.
    /// </summary>
    private async Task<long> SalvagePartialRowsAsync(SqlSession s, string db, string salvage, string table, RangeKey key,
        IReadOnlyList<(long Lo, long Hi)> unreadable, CancellationToken ct)
    {
        try
        {
            var ckey = await s.GetClusteredKeyColumnsAsync(db, table, ct);
            if (ckey.Count == 0 || !ckey[0].Equals(key.Column, StringComparison.OrdinalIgnoreCase)) return 0;
            var ix = await s.GetWidestNonClusteredIndexAsync(db, table, ckey, ct);
            if (ix is null) return 0;
            var (_, name) = Split(table);
            var partial = name + "__parcial";
            if (!await s.CreateSalvageTableAsync(db, salvage, table, ix.Value.Columns, partial, ct)) return 0;

            long rows = 0;
            foreach (var (a, b) in unreadable.Take(200))
            {
                var (where, prm) = RangePredicate(key, a, b);
                var o = await s.CopyRowsAsync(db, salvage, table, ix.Value.Columns, where, prm, partial, ix.Value.Index, ct);
                if (o.Ok) rows += o.Rows;
            }
            if (rows > 0)
                _report(RepairLogLevel.Info,
                    $"    + {rows:N0} linha(s) PARCIAL(is) recuperada(s) do índice [{ix.Value.Index}] (só as colunas {string.Join(", ", ix.Value.Columns.Take(8))}) " +
                    $"— guardadas em [{salvage}].[{Split(table).Schema}].[{partial}] para revisão; não voltam sozinhas.");
            return rows;
        }
        catch (OperationCanceledException) { throw; }
        catch { return 0; }
    }

    // ----------------------------------------------------------------------------------
    // Devolução depois do reparo
    // ----------------------------------------------------------------------------------

    /// <summary>
    /// Devolve ao banco reparado as linhas da cópia que faltam (por PK; várias passadas para respeitar
    /// FKs entre as tabelas). Devolve o total inserido e as tabelas que não puderam ser tratadas.
    /// </summary>
    public async Task<(long Returned, List<string> NotReturned)> ReturnRowsAsync(
        SqlSession s, string db, PreRepairExtraction ex, IReadOnlyCollection<string> tables, CancellationToken ct)
    {
        var notReturned = new List<string>();
        var candidates = ex.Tables
            .Where(t => tables.Contains(t.Table, StringComparer.OrdinalIgnoreCase) && t.Mode != ExtractionMode.Failed && t.RowsExtracted > 0)
            .ToList();
        foreach (var t in candidates.Where(t => !t.HasPrimaryKey))
            notReturned.Add($"{t.Table} (sem chave primária)");

        var pending = candidates.Where(t => t.HasPrimaryKey).Select(t => t.Table).ToList();
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        for (var pass = 0; pass < 3 && pending.Count > 0; pass++)
        {
            var failed = new List<string>();
            foreach (var t in pending)
            {
                try
                {
                    var n = await s.SalvageTableAsync(ex.SalvageDatabase, db, t, ct);
                    total += n;
                    errors.Remove(t);
                    if (n > 0) _report(RepairLogLevel.Success, $"  ✔ {n:N0} linha(s) de {t} devolvida(s) a partir da cópia de salvamento.");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { failed.Add(t); errors[t] = e.Message; }
            }
            if (failed.Count == pending.Count) break;
            pending = failed;
        }
        foreach (var (t, msg) in errors)
        {
            notReturned.Add($"{t} ({msg})");
            _report(RepairLogLevel.Warning, $"  ⚠ {t}: não foi possível devolver as linhas automaticamente — {msg}");
        }
        return (total, notReturned);
    }

    // ----------------------------------------------------------------------------------
    // Faixas da chave
    // ----------------------------------------------------------------------------------

    private static async Task<(long Lo, long Hi)> BoundsAsync(SqlSession s, string db, string table, RangeKey key, CancellationToken ct)
    {
        var (typeLo, typeHi) = TypeBounds(key);
        var b = await s.GetKeyBoundsAsync(db, table, key, ct);
        if (b is not { Min: not null, Max: not null } bounds) return (typeLo, typeHi);
        try
        {
            return key.IsInteger
                ? (Convert.ToInt64(bounds.Min, CultureInfo.InvariantCulture), Convert.ToInt64(bounds.Max, CultureInfo.InvariantCulture))
                : (Convert.ToDateTime(bounds.Min, CultureInfo.InvariantCulture).Ticks, Convert.ToDateTime(bounds.Max, CultureInfo.InvariantCulture).Ticks);
        }
        catch { return (typeLo, typeHi); }
    }

    /// <summary>Limites do tipo da chave (inteiros: valores; datas: ticks).</summary>
    public static (long Lo, long Hi) TypeBounds(RangeKey key) => key.TypeName switch
    {
        "tinyint" => (0, 255),
        "smallint" => (short.MinValue, short.MaxValue),
        "int" => (int.MinValue, int.MaxValue),
        "bigint" => (long.MinValue, long.MaxValue),
        "datetime" => (new DateTime(1753, 1, 1).Ticks, new DateTime(9999, 12, 31, 23, 59, 59, 997).Ticks),
        "smalldatetime" => (new DateTime(1900, 1, 1).Ticks, new DateTime(2079, 6, 6, 23, 59, 0).Ticks),
        _ => (DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks) // date / datetime2
    };

    /// <summary>
    /// Predicado da faixa [a, b]. Inteiros: BETWEEN no próprio tipo (seek no índice). Datas: meio-aberto
    /// [a, b+1 tick) convertido via datetime2(7) — a mesma fronteira vira o fim de uma faixa e o início da
    /// seguinte, então nenhuma linha cai em duas faixas, mesmo com o arredondamento do datetime.
    /// </summary>
    public static (string Where, Dictionary<string, object?> Params) RangePredicate(RangeKey key, long a, long b)
    {
        var col = Q(key.Column);
        // Valor único: igualdade (busca pontual). Com BETWEEN x AND x o SQL Server lê a página SEGUINTE para
        // confirmar o fim da faixa — a última linha boa antes de uma página destruída seria dada como ilegível.
        if (key.IsInteger && a == b)
            return ($"{col} = CAST(@lo AS {key.TypeName})", new Dictionary<string, object?> { ["@lo"] = a });
        if (key.IsInteger)
            return ($"{col} BETWEEN CAST(@lo AS {key.TypeName}) AND CAST(@hi AS {key.TypeName})",
                new Dictionary<string, object?> { ["@lo"] = a, ["@hi"] = b });

        var (_, typeHi) = TypeBounds(key);
        var lo = Iso(a);
        if (b >= typeHi)
            return ($"{col} >= CAST(CAST(@lo AS datetime2(7)) AS {key.TypeName})", new Dictionary<string, object?> { ["@lo"] = lo });
        return ($"{col} >= CAST(CAST(@lo AS datetime2(7)) AS {key.TypeName}) AND {col} < CAST(CAST(@hi AS datetime2(7)) AS {key.TypeName})",
            new Dictionary<string, object?> { ["@lo"] = lo, ["@hi"] = Iso(b + 1) });
    }

    public static string DescribeRange(RangeKey key, long a, long b)
    {
        if (key.IsInteger) return a == b ? $"{key.Column} = {a}" : $"{key.Column} {a}–{b}";
        var da = new DateTime(a); var dbt = new DateTime(b);
        return a == b ? $"{key.Column} = {da:dd/MM/yyyy HH:mm:ss}" : $"{key.Column} {da:dd/MM/yyyy HH:mm:ss}–{dbt:dd/MM/yyyy HH:mm:ss}";
    }

    private static string Iso(long ticks) => new DateTime(Math.Clamp(ticks, DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks))
        .ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture);

    private static async Task<double> FreeMbForDefaultDataDirAsync(SqlSession s, CancellationToken ct)
    {
        try
        {
            var dir = await s.GetDefaultDataDirAsync(ct);
            var folders = await s.GetInstanceFoldersByFreeSpaceAsync(ct);
            if (folders.Count == 0) return -1;
            var root = dir.Length >= 2 ? dir[..2] : string.Empty;
            var match = folders.FirstOrDefault(f => root.Length > 0 && f.Directory.StartsWith(root, StringComparison.OrdinalIgnoreCase));
            return match.Directory is null ? -1 : match.FreeMb;
        }
        catch (OperationCanceledException) { throw; }
        catch { return -1; }
    }

    private static async Task<T?> TryAsync<T>(Func<Task<T>> f) where T : class
    {
        try { return await f(); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private static (string Schema, string Name) Split(string schemaTable)
    {
        var i = schemaTable.IndexOf('.');
        return i <= 0 ? ("dbo", schemaTable) : (schemaTable[..i], schemaTable[(i + 1)..]);
    }

    private static string Q(string ident) => "[" + ident.Replace("]", "]]") + "]";
}
