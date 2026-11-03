using RestoreDB.Domain.Constants;
using RestoreDB.Domain.Models;
using RestoreDB.Services;
using Xunit;

namespace RestoreDB.Tests;

/// <summary>
/// Os textos do diagnóstico viram rótulos na tela: não podem prometer o que a ação não faz
/// (ex.: "remove" quando a correção move para a quarentena; "modo seguro reconstrói o log").
/// </summary>
public class ConclusionHonestyTests
{
    [Fact]
    public void Recovering_SemCorrupcao_OrientaAguardar()
    {
        var d = new DiagnosisResult
        {
            DatabaseName = "X", StateDesc = "RECOVERING",
            Recovery = new RecoveryProgress { Command = "DB STARTUP", PercentComplete = 40, EstimatedRemainingMs = 120000 }
        };

        RepairService.BuildConclusion(d);

        Assert.Equal(RecommendedActionKind.SafeRepair, d.RecommendedAction);
        Assert.Contains("RECOVERING", d.Summary);
        Assert.Contains("Aguarde", d.Recommendation);
        Assert.Contains("40", d.Recommendation); // mostra o andamento lido do motor
    }

    [Fact]
    public void FkOrfa_RecomendacaoFalaDaQuarentena_ENaoPrometePreservar100()
    {
        var d = new DiagnosisResult { DatabaseName = "X", StateDesc = "ONLINE", CheckDb = new CheckDbReport { Executed = true }, LogicalCorruptionSuspected = true };
        d.ConstraintViolations.Add(new ConstraintViolation { Constraint = "[FK_Item_Pedido]", Table = "[dbo].[Item]", Where = "[PedidoId] = 7", IsForeignKey = true });

        RepairService.BuildConclusion(d);

        Assert.Equal(RecommendedActionKind.FixConstraints, d.RecommendedAction);
        Assert.Contains("QUARENTENA", d.Recommendation);
        Assert.Contains(AppConstants.QuarantineDatabaseName, d.Recommendation);
        Assert.DoesNotContain("100%", d.Recommendation);
    }

    [Fact]
    public void SoViolacaoDeCheck_NaoOfereceRemocaoAutomatica()
    {
        var d = new DiagnosisResult { DatabaseName = "X", StateDesc = "ONLINE", CheckDb = new CheckDbReport { Executed = true }, LogicalCorruptionSuspected = true };
        d.ConstraintViolations.Add(new ConstraintViolation { Constraint = "[CK_Valor]", Table = "[dbo].[Item]", Where = "[Valor] = -1", IsForeignKey = false });

        RepairService.BuildConclusion(d);

        Assert.Equal(RecommendedActionKind.FixConstraints, d.RecommendedAction);
        Assert.Contains("não há correção automática", d.Recommendation);
    }

    [Fact]
    public void LogCorrompido_ComecaPeloModoSeguro_ECitaAdministradorNoUltimoRecurso()
    {
        var d = new DiagnosisResult { DatabaseName = "X", StateDesc = "SUSPECT", LogCorruptionSuspected = true };

        RepairService.BuildConclusion(d);

        Assert.Equal(RecommendedActionKind.RebuildLog, d.RecommendedAction);
        Assert.StartsWith("Comece por 'Reparar (modo seguro)'", d.Recommendation);
        Assert.Contains("Administrador", d.Recommendation);
        Assert.DoesNotContain("Os dados devem ser preservados", d.Recommendation);
    }
}
