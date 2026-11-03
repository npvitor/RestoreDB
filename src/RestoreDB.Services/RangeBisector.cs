namespace RestoreDB.Services;

/// <summary>Resultado da cópia de uma faixa da chave.</summary>
public enum RangeCopyStatus
{
    /// <summary>Faixa copiada.</summary>
    Ok,
    /// <summary>Página ilegível no caminho — dividir a faixa ao meio e tentar as metades.</summary>
    Unreadable,
    /// <summary>Erro que dividir não resolve (falta de espaço, permissão...) — parar.</summary>
    Abort
}

/// <summary>Resultado da varredura por faixas de uma tabela.</summary>
public sealed class BisectResult
{
    /// <summary>Faixas (inclusivas) que não puderam ser lidas, já agrupadas quando contíguas.</summary>
    public List<(long Lo, long Hi)> Unreadable { get; } = new();
    public long RowsCopied { get; set; }
    public int Attempts { get; set; }
    public int Failures { get; set; }

    /// <summary>A varredura parou antes do fim (limite de falhas ou erro que impede continuar).</summary>
    public bool Stopped { get; set; }
    public string StopReason { get; set; } = string.Empty;
}

/// <summary>
/// Lê uma tabela por FAIXAS da chave, isolando as páginas ilegíveis por divisão ao meio: copia a faixa
/// inteira; se uma página ruim estiver no caminho, divide em duas e tenta cada metade — até chegar a
/// faixas de um único valor. Assim só as linhas que estão de fato em páginas destruídas ficam de fora.
/// Puro (a cópia real é o delegate) — testável sem banco.
/// </summary>
public static class RangeBisector
{
    /// <param name="lo">Início da faixa (inclusivo).</param>
    /// <param name="hi">Fim da faixa (inclusivo).</param>
    /// <param name="copy">Copia a faixa [a, b] e devolve o status e quantas linhas entraram.</param>
    /// <param name="maxFailures">Limite de tentativas falhas (cada uma pode derrubar a conexão) — protege o tempo total.</param>
    public static async Task<BisectResult> RunAsync(
        long lo, long hi, Func<long, long, Task<(RangeCopyStatus Status, long Rows)>> copy,
        int maxFailures = 2000, CancellationToken ct = default)
    {
        var result = new BisectResult();
        if (hi < lo) return result;

        // Pilha com a metade de baixo por cima: processa as faixas em ordem crescente.
        var stack = new Stack<(long Lo, long Hi)>();
        stack.Push((lo, hi));
        var unreadable = new List<(long Lo, long Hi)>();

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (a, b) = stack.Pop();
            result.Attempts++;
            var (status, rows) = await copy(a, b);

            if (status == RangeCopyStatus.Ok)
            {
                result.RowsCopied += rows;
                continue;
            }
            if (status == RangeCopyStatus.Abort)
            {
                unreadable.Add((a, b));
                while (stack.Count > 0) unreadable.Add(stack.Pop());
                result.Stopped = true;
                result.StopReason = "erro que impede continuar a leitura";
                break;
            }

            result.Failures++;
            if (a == b)
            {
                unreadable.Add((a, b));
            }
            else if (result.Failures >= maxFailures)
            {
                unreadable.Add((a, b));
                while (stack.Count > 0) unreadable.Add(stack.Pop());
                result.Stopped = true;
                result.StopReason = $"limite de {maxFailures} trechos ilegíveis atingido";
                break;
            }
            else
            {
                var mid = Mid(a, b);
                stack.Push((mid + 1, b));
                stack.Push((a, mid));
            }
        }

        result.Unreadable.AddRange(Merge(unreadable));
        return result;
    }

    /// <summary>Ponto médio de [a, b] sem estouro, mesmo nos extremos de bigint.</summary>
    public static long Mid(long a, long b) => (a >> 1) + (b >> 1) + (a & b & 1);

    /// <summary>Agrupa faixas contíguas/sobrepostas ([1,1],[2,5] → [1,5]).</summary>
    public static List<(long Lo, long Hi)> Merge(IEnumerable<(long Lo, long Hi)> ranges)
    {
        var ordered = ranges.OrderBy(r => r.Lo).ToList();
        var merged = new List<(long Lo, long Hi)>();
        foreach (var r in ordered)
        {
            if (merged.Count > 0 && (merged[^1].Hi == long.MaxValue || r.Lo <= merged[^1].Hi + 1))
            {
                var last = merged[^1];
                merged[^1] = (last.Lo, Math.Max(last.Hi, r.Hi));
            }
            else merged.Add(r);
        }
        return merged;
    }
}
