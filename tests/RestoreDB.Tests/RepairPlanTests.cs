using RestoreDB.Domain.Models;
using RestoreDB.Services;
using Xunit;

namespace RestoreDB.Tests;

public class RepairPlanTests
{
    private static DatabaseInfo Db(string name, string state)
    {
        var db = new DatabaseInfo { Name = name };
        db.StateDesc = state;
        return db;
    }

    [Fact]
    public void Offline_PlansSetOnline()
    {
        var script = RepairService.BuildPlannedScript(Db("Faturamento", "OFFLINE"), allowDataLoss: false);
        Assert.Contains("SET ONLINE", script);
        Assert.Contains("SET MULTI_USER", script);
        Assert.DoesNotContain("REPAIR_ALLOW_DATA_LOSS", script);
    }

    [Fact]
    public void Restoring_PlansRestoreWithRecovery()
    {
        var script = RepairService.BuildPlannedScript(Db("Faturamento", "RESTORING"), allowDataLoss: false);
        Assert.Contains("RESTORE DATABASE", script);
        Assert.Contains("WITH RECOVERY", script);
    }

    [Fact]
    public void Suspect_Safe_DoesNotIncludeDataLoss()
    {
        var script = RepairService.BuildPlannedScript(Db("Faturamento", "SUSPECT"), allowDataLoss: false);
        Assert.Contains("REPAIR_REBUILD", script);
        Assert.DoesNotContain("REPAIR_ALLOW_DATA_LOSS", script);
    }

    [Fact]
    public void Suspect_WithDataLoss_IncludesDataLossRepair()
    {
        var script = RepairService.BuildPlannedScript(Db("Faturamento", "SUSPECT"), allowDataLoss: true);
        Assert.Contains("REPAIR_ALLOW_DATA_LOSS", script);
    }

    [Fact]
    public void Online_Safe_PlansCheckAndRebuild_NotNoAction()
    {
        // "Ver script planejado" aparece para bancos ONLINE corrompidos: não pode dizer "nenhuma ação planejada".
        var script = RepairService.BuildPlannedScript(Db("Faturamento", "ONLINE"), allowDataLoss: false);
        Assert.Contains("DBCC CHECKDB", script);
        Assert.Contains("REPAIR_REBUILD", script);
        Assert.Contains("SET MULTI_USER", script);
        Assert.DoesNotContain("REPAIR_ALLOW_DATA_LOSS", script);
        Assert.DoesNotContain("nenhuma ação planejada", script);
    }

    [Fact]
    public void Online_WithDataLoss_IncludesSafetyNetBeforeRepair_AndNoEmergency()
    {
        var script = RepairService.BuildPlannedScript(Db("Faturamento", "ONLINE"), allowDataLoss: true);
        var repair = script.IndexOf("REPAIR_ALLOW_DATA_LOSS", StringComparison.Ordinal);
        Assert.True(repair > 0);
        Assert.InRange(script.IndexOf("COPY_ONLY", StringComparison.Ordinal), 0, repair);
        Assert.DoesNotContain("SET EMERGENCY", script); // ONLINE repara sem EMERGENCY (OnlineRepairFlowAsync)
    }

    [Fact]
    public void Suspect_WithDataLoss_SafetyNetRunsAfterExclusiveAccess()
    {
        var script = RepairService.BuildPlannedScript(Db("Faturamento", "SUSPECT"), allowDataLoss: true);
        var destructive = script.IndexOf("DESTRUTIVO", StringComparison.Ordinal);
        var emergency = script.IndexOf("SET EMERGENCY", destructive, StringComparison.Ordinal);
        var tail = script.IndexOf("NO_TRUNCATE", StringComparison.Ordinal);
        Assert.True(destructive > 0 && emergency > destructive && tail > emergency);
    }

    [Fact]
    public void DatabaseNameWithBracket_IsEscaped()
    {
        var script = RepairService.BuildPlannedScript(Db("a]b", "OFFLINE"), allowDataLoss: false);
        Assert.Contains("[a]]b]", script);
    }

    [Theory]
    [InlineData("1:24;drop table x", "1:24")]
    [InlineData("1:24,1:56", "1:24,1:56")]
    [InlineData("abc", "")]
    public void SanitizePages_KeepsOnlyDigitsColonComma(string input, string expected)
    {
        Assert.Equal(expected, RestoreService.SanitizePages(input));
    }

    // ---- ParseEqualityPredicate (predicado gerado pelo DBCC CHECKCONSTRAINTS) ----

    [Fact]
    public void ParseEqualityPredicate_SingleIntegerColumn()
    {
        var pairs = RepairService.ParseEqualityPredicate("[ClienteId] = 42");
        Assert.Single(pairs);
        Assert.Equal("[ClienteId]", pairs[0].Column);
        Assert.Equal("42", pairs[0].Literal);
    }

    [Fact]
    public void ParseEqualityPredicate_CompositeAnd_SplitsAtTopLevel()
    {
        var pairs = RepairService.ParseEqualityPredicate("[A] = 1 AND [B] = 2");
        Assert.Equal(2, pairs.Count);
        Assert.Equal("[A]", pairs[0].Column);
        Assert.Equal("1", pairs[0].Literal);
        Assert.Equal("[B]", pairs[1].Column);
        Assert.Equal("2", pairs[1].Literal);
    }

    [Fact]
    public void ParseEqualityPredicate_StringLiteralContainingAnd_IsNotSplit()
    {
        var pairs = RepairService.ParseEqualityPredicate("[Nome] = 'FOO AND BAR'");
        Assert.Single(pairs);
        Assert.Equal("[Nome]", pairs[0].Column);
        Assert.Equal("'FOO AND BAR'", pairs[0].Literal);
    }

    [Fact]
    public void ParseEqualityPredicate_EscapedQuotes_KeptIntact()
    {
        var pairs = RepairService.ParseEqualityPredicate("[Nome] = 'O''Brien AND Sons'");
        Assert.Single(pairs);
        Assert.Equal("[Nome]", pairs[0].Column);
        Assert.Equal("'O''Brien AND Sons'", pairs[0].Literal);
    }

    [Fact]
    public void ParseEqualityPredicate_DateLiteralAndComposite()
    {
        var pairs = RepairService.ParseEqualityPredicate("[Criado] = '2024-01-01' AND [ClienteId] = 7");
        Assert.Equal(2, pairs.Count);
        Assert.Equal("[Criado]", pairs[0].Column);
        Assert.Equal("'2024-01-01'", pairs[0].Literal);
        Assert.Equal("[ClienteId]", pairs[1].Column);
        Assert.Equal("7", pairs[1].Literal);
    }

    [Fact]
    public void ParseEqualityPredicate_NationalStringLiteral_IsNotSplit()
    {
        var pairs = RepairService.ParseEqualityPredicate("[Nome] = N'x AND y'");
        Assert.Single(pairs);
        Assert.Equal("N'x AND y'", pairs[0].Literal);
    }

    [Fact]
    public void ParseEqualityPredicate_BracketedColumnContainingAnd_IsNotSplit()
    {
        var pairs = RepairService.ParseEqualityPredicate("[My AND Col] = 5");
        Assert.Single(pairs);
        Assert.Equal("[My AND Col]", pairs[0].Column);
        Assert.Equal("5", pairs[0].Literal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseEqualityPredicate_EmptyOrBlank_ReturnsEmpty(string input)
    {
        Assert.Empty(RepairService.ParseEqualityPredicate(input));
    }

    [Fact]
    public void ParseEqualityPredicate_SkipsNonEqualityConjunct()
    {
        // Um conjunto sem '=' (ex.: "[X] IS NULL") é ignorado — só o par de igualdade retorna.
        // Isso justifica a exigência de correspondência 1-para-1 com as colunas da FK antes de recuperar.
        var pairs = RepairService.ParseEqualityPredicate("[X] IS NULL AND [Y] = 5");
        Assert.Single(pairs);
        Assert.Equal("[Y]", pairs[0].Column);
        Assert.Equal("5", pairs[0].Literal);
    }
}
