using RestoreDB.DataAccess;
using RestoreDB.Domain.Models;
using RestoreDB.Services;
using Xunit;

namespace RestoreDB.Tests;

/// <summary>Leitura por faixas (salvamento sem backup): isola só os trechos ilegíveis.</summary>
public class RangeBisectorTests
{
    /// <summary>Simula uma tabela com chaves 1..n e algumas chaves em "páginas destruídas".</summary>
    private static Func<long, long, Task<(RangeCopyStatus, long)>> Table(long n, params long[] bad)
    {
        var badSet = bad.ToHashSet();
        return (a, b) =>
        {
            var lo = Math.Max(a, 1); var hi = Math.Min(b, n);
            if (lo > hi) return Task.FromResult((RangeCopyStatus.Ok, 0L));
            for (var k = lo; k <= hi; k++)
                if (badSet.Contains(k)) return Task.FromResult((RangeCopyStatus.Unreadable, 0L));
            return Task.FromResult((RangeCopyStatus.Ok, hi - lo + 1));
        };
    }

    [Fact]
    public async Task SemPaginaRuim_CopiaTudoNumaTentativa()
    {
        var r = await RangeBisector.RunAsync(1, 1000, Table(1000));
        Assert.Equal(1000, r.RowsCopied);
        Assert.Equal(1, r.Attempts);
        Assert.Empty(r.Unreadable);
    }

    [Fact]
    public async Task PaginaRuim_PulaSoOTrechoDanificado()
    {
        // "Página" com as chaves 401..450 destruída.
        var bad = Enumerable.Range(401, 50).Select(i => (long)i).ToArray();
        var r = await RangeBisector.RunAsync(1, 1000, Table(1000, bad));
        Assert.Equal(950, r.RowsCopied);
        Assert.Equal(new[] { (401L, 450L) }, r.Unreadable);
        Assert.False(r.Stopped);
    }

    [Fact]
    public async Task VariosTrechos_SaoAgrupadosSeparadamente()
    {
        var r = await RangeBisector.RunAsync(1, 100, Table(100, 10, 11, 12, 90));
        Assert.Equal(96, r.RowsCopied);
        Assert.Equal(new[] { (10L, 12L), (90L, 90L) }, r.Unreadable);
    }

    [Fact]
    public async Task LimiteDeFalhas_ParaEInformaOQueNaoFoiLido()
    {
        var bad = Enumerable.Range(1, 1000).Select(i => (long)i).ToArray();
        var r = await RangeBisector.RunAsync(1, 1000, Table(1000, bad), maxFailures: 20);
        Assert.True(r.Stopped);
        Assert.Equal(0, r.RowsCopied);
        Assert.Equal(new[] { (1L, 1000L) }, r.Unreadable); // tudo o que sobrou fica registrado
    }

    [Fact]
    public async Task ErroQueImpedeContinuar_Aborta()
    {
        var calls = 0;
        var r = await RangeBisector.RunAsync(1, 100, (a, b) =>
        {
            calls++;
            return Task.FromResult(calls == 1 ? (RangeCopyStatus.Unreadable, 0L) : (RangeCopyStatus.Abort, 0L));
        });
        Assert.True(r.Stopped);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(long.MinValue, long.MaxValue)]
    [InlineData(-3, -2)]
    [InlineData(0, 1)]
    [InlineData(int.MinValue, int.MaxValue)]
    public void Mid_SemEstouroEDentroDaFaixa(long a, long b)
    {
        var m = RangeBisector.Mid(a, b);
        Assert.InRange(m, a, b - 1); // garante progresso: [a, m] e [m+1, b] não vazios
    }

    [Fact]
    public async Task FaixaDoTipoInteiro_BigintCompleto_NaoEstoura()
    {
        var r = await RangeBisector.RunAsync(long.MinValue, long.MaxValue, Table(10, 5));
        Assert.Equal(9, r.RowsCopied);
        Assert.Equal(new[] { (5L, 5L) }, r.Unreadable);
    }
}

public class PreRepairExtractorTests
{
    [Fact]
    public void Predicado_Inteiro_UsaBetweenNoProprioTipo()
    {
        var (where, prm) = PreRepairExtractor.RangePredicate(new RangeKey("Id", "int", false), 10, 20);
        Assert.Equal("[Id] BETWEEN CAST(@lo AS int) AND CAST(@hi AS int)", where);
        Assert.Equal(10L, prm["@lo"]);
        Assert.Equal(20L, prm["@hi"]);
    }

    [Fact]
    public void Predicado_ValorUnico_UsaIgualdade_ParaNaoLerAPaginaSeguinte()
    {
        var (where, prm) = PreRepairExtractor.RangePredicate(new RangeKey("Id", "int", false), 999, 999);
        Assert.Equal("[Id] = CAST(@lo AS int)", where);
        Assert.False(prm.ContainsKey("@hi"));
    }

    [Fact]
    public void Predicado_Data_EhMeioAberto_SemSobreposicao()
    {
        var key = new RangeKey("Emissao", "datetime", false);
        var a = new DateTime(2024, 1, 1).Ticks;
        var b = new DateTime(2024, 1, 31).Ticks;
        var (where, prm) = PreRepairExtractor.RangePredicate(key, a, b);
        Assert.Contains(">= CAST(CAST(@lo AS datetime2(7)) AS datetime)", where);
        Assert.Contains("< CAST(CAST(@hi AS datetime2(7)) AS datetime)", where);
        // O fim (exclusivo) desta faixa é b+1 tick — exatamente o início da faixa seguinte.
        Assert.Equal(new DateTime(b + 1).ToString("yyyy-MM-ddTHH:mm:ss.fffffff"), prm["@hi"]);
    }

    [Fact]
    public void Predicado_Data_NoLimiteSuperior_NaoTemFim()
    {
        var key = new RangeKey("D", "date", false);
        var (_, hi) = PreRepairExtractor.TypeBounds(key);
        var (where, prm) = PreRepairExtractor.RangePredicate(key, 0, hi);
        Assert.DoesNotContain("@hi", where);
        Assert.False(prm.ContainsKey("@hi"));
    }

    [Theory]
    [InlineData("tinyint", 0, 255)]
    [InlineData("smallint", short.MinValue, short.MaxValue)]
    [InlineData("int", int.MinValue, int.MaxValue)]
    public void LimitesDoTipo(string type, long lo, long hi)
        => Assert.Equal((lo, hi), PreRepairExtractor.TypeBounds(new RangeKey("k", type, false)));

    [Fact]
    public void ChaveNaoNumerica_NaoPermiteFaixas()
    {
        Assert.False(new RangeKey("Codigo", "varchar", false).CanBisect);
        Assert.True(new RangeKey("Id", "bigint", false).CanBisect);
        Assert.True(new RangeKey("Data", "datetime2", false).CanBisect);
    }

    [Fact]
    public void DescricaoDaFaixa_EmLinguagemSimples()
    {
        var key = new RangeKey("Id", "int", false);
        Assert.Equal("Id = 7", PreRepairExtractor.DescribeRange(key, 7, 7));
        Assert.Equal("Id 1201–1400", PreRepairExtractor.DescribeRange(key, 1201, 1400));
    }
}

public class HealthEvaluatorTests
{
    private static DatabaseSettings Ok() => new()
    {
        PageVerify = "CHECKSUM", UserAccess = "MULTI_USER", CompatibilityLevel = 100, ServerCompatibilityLevel = 100,
        OwnerName = "sa", VlfCount = 50, RecoveryModel = "SIMPLE"
    };

    private static List<HealthIssue> Eval(DatabaseSettings s) =>
        HealthEvaluator.Evaluate(s, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

    [Fact]
    public void BancoBemConfigurado_SemAjustes() => Assert.Empty(Eval(Ok()));

    [Fact]
    public void PageVerifyTornPage_AjusteCorrigivel()
    {
        var s = Ok(); s.PageVerify = "TORN_PAGE_DETECTION";
        var i = Assert.Single(Eval(s));
        Assert.Equal(HealthIssueKind.PageVerify, i.Kind);
        Assert.True(i.CanFix);
    }

    [Fact]
    public void AutoShrinkEAutoClose_SaoDetectados()
    {
        var s = Ok(); s.AutoShrink = true; s.AutoClose = true;
        var kinds = Eval(s).Select(x => x.Kind).ToList();
        Assert.Contains(HealthIssueKind.AutoShrink, kinds);
        Assert.Contains(HealthIssueKind.AutoClose, kinds);
    }

    [Theory]
    [InlineData(true, 10, 500, true)]    // 10% → sempre ruim
    [InlineData(false, 1, 2048, true)]   // 1 MB num arquivo de 2 GB → ruim
    [InlineData(false, 1, 100, false)]   // 1 MB num arquivo pequeno → tolerável
    [InlineData(false, 256, 4096, false)]
    [InlineData(true, 0, 500, false)]    // crescimento desligado não é tratado aqui
    public void Crescimento(bool pct, double growth, double size, bool needs)
        => Assert.Equal(needs, HealthEvaluator.NeedsGrowthFix(new FileGrowthSetting { LogicalName = "f", IsPercentGrowth = pct, Growth = growth, SizeMb = size }));

    [Theory]
    [InlineData(500, false, 64)]
    [InlineData(5000, false, 256)]
    [InlineData(20000, false, 512)]
    [InlineData(20000, true, 256)]
    public void CrescimentoRecomendado(double size, bool isLog, int mb)
        => Assert.Equal(mb, HealthEvaluator.RecommendedGrowthMb(new FileGrowthSetting { SizeMb = size, IsLog = isLog }));

    [Fact]
    public void MuitosVlfs_Detectado()
    {
        var s = Ok(); s.VlfCount = 4000;
        var i = Assert.Single(Eval(s));
        Assert.Equal(HealthIssueKind.TooManyVlfs, i.Kind);
        Assert.Equal(FindingSeverity.Medium, i.Severity);
    }

    [Fact]
    public void UsuariosOrfaos_SemLoginNaoSaoCorrigiveis()
    {
        var s = Ok(); s.OrphanedUsers.Add(("app", false));
        var i = Assert.Single(Eval(s));
        Assert.False(i.CanFix);

        s.OrphanedUsers.Add(("faturamento", true));
        Assert.True(Assert.Single(Eval(s)).CanFix);
    }

    [Fact]
    public void DonoInvalido_ComTrustworthy_SoAviso()
    {
        var s = Ok(); s.OwnerName = ""; s.Trustworthy = true;
        var owner = Eval(s).Single(x => x.Kind == HealthIssueKind.InvalidOwner);
        Assert.False(owner.CanFix);
    }

    [Fact]
    public void CompatibilidadeAntiga_ApenasInformativo()
    {
        var s = Ok(); s.CompatibilityLevel = 100; s.ServerCompatibilityLevel = 170;
        var i = Assert.Single(Eval(s));
        Assert.Equal(HealthIssueKind.OldCompatibility, i.Kind);
        Assert.False(i.CanFix);
    }

    [Fact]
    public void AchadosDeSaudeLogica_ViramAjustesCorrigiveis()
    {
        var list = HealthEvaluator.Evaluate(Ok(), new[] { "FK_A" }, new[] { "T.IX" }, new[] { "dbo.vw" });
        Assert.Equal(3, list.Count);
        Assert.All(list, i => Assert.True(i.CanFix));
    }
}

public class FixAccessConclusionTests
{
    [Fact]
    public void BancoTravado_CheckDbNaoRodou_RecomendaLiberarAcesso()
    {
        var d = new DiagnosisResult { StateDesc = "ONLINE", AccessRestricted = true, Settings = new DatabaseSettings { UserAccess = "SINGLE_USER" } };
        d.AccessHolders.Add(new DbSessionInfo { SessionId = 55, Program = "Faturamento.exe", Host = "CAIXA01", Login = "sa" });
        RepairService.BuildConclusion(d);
        Assert.Equal(RecommendedActionKind.FixAccess, d.RecommendedAction);
        Assert.Contains("TRAVADO", d.Summary);
        Assert.Contains("Faturamento.exe", d.Recommendation);
    }

    [Fact]
    public void BancoTravado_ComCorrupcaoConfirmada_ReparoTemPrioridade()
    {
        var d = new DiagnosisResult { StateDesc = "ONLINE", AccessRestricted = true, DataCorruptionSuspected = true };
        d.CheckDb = new CheckDbReport { Executed = true };
        d.CheckDb.Details.Add(new CorruptionDetail { Error = 8928, Level = 16, ObjectId = 100, IndexId = 1, RepairLevel = "repair_allow_data_loss" });
        RepairService.BuildConclusion(d);
        Assert.NotEqual(RecommendedActionKind.FixAccess, d.RecommendedAction);
    }

    [Fact]
    public void BancoIntegro_ComAjustes_MencionaAjustesNaRecomendacao()
    {
        var d = new DiagnosisResult { StateDesc = "ONLINE", CheckDb = new CheckDbReport { Executed = true } };
        d.Adjustments.Add(new HealthIssue { Kind = HealthIssueKind.AutoShrink, CanFix = true, Title = "x" });
        RepairService.BuildConclusion(d);
        Assert.Equal(RecommendedActionKind.NoAction, d.RecommendedAction);
        Assert.Contains("ajuste", d.Recommendation);
    }
}

public class IncrementalCheckStateTests
{
    [Theory]
    [InlineData(0, 10, 0, true)]
    [InlineData(4, 10, 4, false)]
    [InlineData(10, 10, 0, true)]   // ciclo anterior terminou
    [InlineData(12, 10, 0, true)]   // tabelas removidas desde a última rodada
    public void PosicaoDePartida(int next, int total, int start, bool newCycle)
        => Assert.Equal((start, newCycle), IncrementalCheckService.StartPosition(new IncrementalCheckState { NextIndex = next }, total));

    [Fact]
    public void Estado_PersisteEntreRodadas()
    {
        var dir = Path.Combine(Path.GetTempPath(), "RestoreDB_Inc_" + Guid.NewGuid().ToString("N"));
        try
        {
            var svc = new IncrementalCheckService(new LogService(), dir);
            svc.Save(new IncrementalCheckState { DatabaseName = "Faturamento", NextIndex = 7, Errors = { "dbo.Nota" } });
            var st = svc.Load("Faturamento");
            Assert.Equal(7, st.NextIndex);
            Assert.Equal(new[] { "dbo.Nota" }, st.Errors);
            Assert.Equal(0, svc.Load("OutroBanco").NextIndex);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
