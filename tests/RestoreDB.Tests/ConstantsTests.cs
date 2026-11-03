using RestoreDB.Domain.Constants;
using RestoreDB.Domain.Models;
using Xunit;

namespace RestoreDB.Tests;

public class ConstantsTests
{
    [Theory]
    [InlineData("SUSPECT", true)]
    [InlineData("suspect", true)]
    [InlineData("RECOVERY_PENDING", true)]
    [InlineData("OFFLINE", true)]
    [InlineData("ONLINE", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsProblematic(string? state, bool expected)
        => Assert.Equal(expected, AppConstants.IsProblematic(state));

    [Theory]
    [InlineData("master", true)]
    [InlineData("MSDB", true)]
    [InlineData("tempdb", true)]
    [InlineData("Faturamento", false)]
    public void IsSystemDatabase(string name, bool expected)
        => Assert.Equal(expected, AppConstants.IsSystemDatabase(name));

    [Fact]
    public void StateInfo_HasFriendlyAndRecommendation()
    {
        Assert.False(string.IsNullOrEmpty(StateInfo.Friendly("OFFLINE")));
        Assert.False(string.IsNullOrEmpty(StateInfo.RecommendedAction("SUSPECT")));
        // ONLINE não prova integridade (corrupção física/FK órfã não mudam o estado): o texto não pode dizer "saudável".
        Assert.DoesNotContain("saudável", StateInfo.Friendly("ONLINE"));
        Assert.Contains("Diagnosticar", StateInfo.RecommendedAction("ONLINE"));
        // RECOVERING é a recuperação automática do SQL Server: a orientação é aguardar, não "finalizar".
        Assert.Contains("Aguardar", StateInfo.RecommendedAction("RECOVERING"));
    }

    [Fact]
    public void DatabaseInfo_DerivedFlags()
    {
        var db = new DatabaseInfo { Name = "master" };
        db.StateDesc = "SUSPECT";
        Assert.True(db.IsProblematic);
        Assert.False(db.IsHealthy);
        Assert.True(db.IsSystemDatabase);
    }
}
