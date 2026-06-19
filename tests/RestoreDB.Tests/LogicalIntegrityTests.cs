using RestoreDB.Domain.Models;
using Xunit;

namespace RestoreDB.Tests;

public class LogicalIntegrityTests
{
    [Fact]
    public void ConstraintViolation_Display_IncludesKindTableAndPredicate()
    {
        var fk = new ConstraintViolation
        {
            Table = "dbo.Pedido", Constraint = "FK_Pedido_Cliente", Where = "[ClienteId] = 42", IsForeignKey = true
        };
        Assert.Contains("FK", fk.Display);
        Assert.Contains("dbo.Pedido", fk.Display);
        Assert.Contains("[ClienteId] = 42", fk.Display);

        var chk = new ConstraintViolation { Table = "dbo.Item", Constraint = "CK_Item_Qtd", Where = "[Qtd] < 0" };
        Assert.Contains("CHECK", chk.Display);
    }

    [Fact]
    public void DiagnosisResult_ForeignKeyViolations_FiltersToFkOnly()
    {
        var d = new DiagnosisResult { DatabaseName = "X" };
        d.ConstraintViolations.Add(new ConstraintViolation { Constraint = "FK_A", IsForeignKey = true });
        d.ConstraintViolations.Add(new ConstraintViolation { Constraint = "CK_B", IsForeignKey = false });
        d.ConstraintViolations.Add(new ConstraintViolation { Constraint = "FK_C", IsForeignKey = true });

        Assert.Equal(2, d.ForeignKeyViolations.Count);
        Assert.All(d.ForeignKeyViolations, v => Assert.True(v.IsForeignKey));
    }

    [Fact]
    public void DiagnosisResult_LogicalCorruptionFlag_DefaultsFalse()
    {
        var d = new DiagnosisResult();
        Assert.False(d.LogicalCorruptionSuspected);
        Assert.Empty(d.ConstraintViolations);
        Assert.Empty(d.UntrustedConstraints);
        Assert.Empty(d.CatalogErrors);
    }

    [Fact]
    public void FixConstraints_IsADistinctRecommendedAction()
    {
        // Garante que o enum tem o valor usado pelo diagnóstico de integridade lógica.
        Assert.True(System.Enum.IsDefined(typeof(RecommendedActionKind), RecommendedActionKind.FixConstraints));
        Assert.NotEqual(RecommendedActionKind.NoAction, RecommendedActionKind.FixConstraints);
    }
}
