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
    public void CorrupcaoQueORepairRebuildConserta_RecomendaReparoSeguro_ENaoRestaurarBackup()
    {
        // Chaves fora de ordem no índice clusterizado (Msg 2511/8933): nível mínimo REPAIR_REBUILD, sem perda.
        var check = new CheckDbReport { Executed = true };
        check.Details.Add(new CorruptionDetail { Error = 2511, Level = 16, ObjectId = 245575913, IndexId = 1, RepairLevel = "repair_rebuild" });
        check.Details.Add(new CorruptionDetail { Error = 8933, Level = 16, ObjectId = 245575913, IndexId = 1, RepairLevel = "repair_rebuild" });
        var d = new DiagnosisResult { DatabaseName = "X", StateDesc = "ONLINE", CheckDb = check, DataCorruptionSuspected = true };

        RepairService.BuildConclusion(d);

        Assert.Equal(RecommendedActionKind.SafeRepair, d.RecommendedAction);
        Assert.Contains("Nenhum dado é perdido", d.Recommendation);
    }

    [Theory]
    [InlineData("FULL", "ONLINE", RecommendedActionKind.PageRestore)]
    [InlineData("SIMPLE", "ONLINE", RecommendedActionKind.RestoreFromBackup)]
    [InlineData("FULL", "SUSPECT", RecommendedActionKind.RestoreFromBackup)]
    public void PoucasPaginasCorrompidas_SoRecomendaRestaurarPaginasQuandoEhPossivel(string model, string state, RecommendedActionKind expected)
    {
        // Em SIMPLE a restauração de página não existe e num banco SUSPECT o SQL Server a recusa (Msg 4309):
        // nesses casos ela não pode ser a ação recomendada.
        var d = new DiagnosisResult { DatabaseName = "X", StateDesc = state, DataCorruptionSuspected = true,
                                      BackupPolicy = new BackupPolicyAssessment { DatabaseName = "X", RecoveryModel = model } };
        d.SuspectPages.Add(new SuspectPageInfo { FileId = 1, PageId = 254, EventType = 2, ErrorCount = 1 });

        RepairService.BuildConclusion(d);

        Assert.Equal(expected, d.RecommendedAction);
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
