using RestoreDB.Domain.Models;
using RestoreDB.Services;
using Xunit;

namespace RestoreDB.Tests;

public class BackupPolicyEvaluatorTests
{
    private static readonly DateTime Now = new(2026, 1, 31, 12, 0, 0);

    private static BackupSetInfo Set(string type, int day) => new()
    {
        BackupTypeCode = type,
        BackupFinishDate = new DateTime(2026, 1, day, 0, 0, 0)
    };

    [Fact]
    public void NoFull_IsCritical()
    {
        var a = BackupPolicyEvaluator.Evaluate("DB", "FULL", new List<BackupSetInfo>(), Now);
        Assert.Equal(FindingSeverity.Critical, a.MaxSeverity);
        Assert.True(a.HasRisk);
    }

    [Fact]
    public void FullModel_WithoutLogBackups_IsHigh()
    {
        var history = new List<BackupSetInfo> { Set("D", 30) }; // full recente, mas sem log
        var a = BackupPolicyEvaluator.Evaluate("DB", "FULL", history, Now);

        Assert.Contains(a.Findings, f => f.Severity == FindingSeverity.High && f.Message.Contains("LOG"));
    }

    [Fact]
    public void FullModel_WithLogs_AndRecentFull_HasNoRisk()
    {
        var history = new List<BackupSetInfo> { Set("D", 30), Set("L", 31) };
        var a = BackupPolicyEvaluator.Evaluate("DB", "FULL", history, Now);

        Assert.False(a.HasRisk);
        Assert.Equal("FULL", a.RecoveryModel);
        Assert.NotNull(a.LastLog);
    }

    [Fact]
    public void StaleFull_OverThirtyDays_IsHigh()
    {
        var history = new List<BackupSetInfo> { Set("D", 1), Set("L", 1) }; // full de 30 dias atrás
        var a = BackupPolicyEvaluator.Evaluate("DB", "FULL", history, Now);
        Assert.Contains(a.Findings, f => f.Severity == FindingSeverity.High && f.Message.Contains("FULL"));
    }

    [Fact]
    public void SimpleModel_IsInfoOnly_WhenFullPresent()
    {
        var history = new List<BackupSetInfo> { Set("D", 30) };
        var a = BackupPolicyEvaluator.Evaluate("DB", "SIMPLE", history, Now);

        Assert.DoesNotContain(a.Findings, f => f.Severity >= FindingSeverity.Medium);
        Assert.Contains(a.Findings, f => f.Message.Contains("SIMPLE"));
    }
}
