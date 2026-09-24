using RestoreDB.Domain.Models;
using RestoreDB.Services;
using Xunit;

namespace RestoreDB.Tests;

/// <summary>
/// Lógica pura dos novos caminhos de recuperação: forense (hora do fn_dblog / tabela), classificação de
/// falhas de arquivo, object_ids do reparo, log cheio, cópias de segurança da ferramenta no plano de
/// restauração e as novas conclusões do diagnóstico.
/// </summary>
public class NewRecoveryLogicTests
{
    // ---------------- Forense ----------------

    [Theory]
    [InlineData("2026/09/23 14:05:07:123", 2026, 9, 23, 14, 5, 7, 123)]   // formato real do fn_dblog (ms com ':')
    [InlineData("2026/09/23 14:05:07.123", 2026, 9, 23, 14, 5, 7, 123)]
    [InlineData("2026/09/23 14:05:07", 2026, 9, 23, 14, 5, 7, 0)]
    public void ParseLogTime_AceitaOFormatoDoFnDblog(string raw, int y, int mo, int d, int h, int mi, int s, int ms)
        => Assert.Equal(new DateTime(y, mo, d, h, mi, s, ms), ForensicTransaction.ParseLogTime(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("não é data")]
    public void ParseLogTime_InvalidoDevolveNull(string? raw) => Assert.Null(ForensicTransaction.ParseLogTime(raw));

    [Theory]
    [InlineData("dbo.NotaFiscal.PK_NotaFiscal", "dbo.NotaFiscal")]
    [InlineData("vendas.Pedido", "vendas.Pedido")]
    [InlineData("SemSchema", "")]
    [InlineData("", "")]
    public void TableFromAllocUnit_ExtraiSchemaETabela(string au, string expected)
        => Assert.Equal(expected, ForensicTransaction.TableFromAllocUnit(au));

    [Fact]
    public void Forense_SoDeleteComTabelaPodeSerDesfeitoSemSobrescrever()
    {
        Assert.True(new ForensicTransaction { Kind = "DELETE", Table = "dbo.X" }.CanRecoverRowsInPlace);
        Assert.False(new ForensicTransaction { Kind = "DELETE", Table = "" }.CanRecoverRowsInPlace);
        Assert.False(new ForensicTransaction { Kind = "DROP", Table = "dbo.X" }.CanRecoverRowsInPlace);
    }

    // ---------------- Falhas de acesso a arquivo ----------------

    [Theory]
    [InlineData("FCB::Open failed: Could not open file D:\\dados\\X.mdf for file number 1.  OS error: 5(Access is denied.).", FileProblemKind.AccessDenied)]
    [InlineData("FCB::Open failed: Could not open file D:\\dados\\X_log.ldf for file number 2.  OS error: 32(The process cannot access the file because it is being used by another process.).", FileProblemKind.InUseByAnotherProcess)]
    [InlineData("FCB::Open failed: Could not open file E:\\X.mdf for file number 1.  OS error: 3(The system cannot find the path specified.).", FileProblemKind.Missing)]
    [InlineData("Falha em FCB::Open: não foi possível abrir o arquivo D:\\X.mdf para o arquivo número 1. Erro do SO: 5(Acesso negado.).", FileProblemKind.AccessDenied)]
    public void FileAccessProblem_ClassificaLinhasDoLogDeErros(string line, FileProblemKind expected)
        => Assert.Equal(expected, FileAccessProblem.Classify(line));

    [Theory]
    [InlineData("Starting up database 'X'.")]
    [InlineData("Error: 824, Severity: 24 — logical consistency-based I/O error: incorrect checksum")]
    [InlineData("")]
    public void FileAccessProblem_IgnoraLinhasQueNaoSaoDeAbertura(string line)
        => Assert.Null(FileAccessProblem.Classify(line));

    // ---------------- object_ids que o REPAIR desalocou ----------------

    [Fact]
    public void ParseRepairObjectIds_ExtraiIdsSemRepetir()
    {
        var msgs = new[]
        {
            "Repair: The page (1:153) has been deallocated from object ID 245575913, index ID 1, partition ID 72057594040549376.",
            "The error has been repaired. Object ID 245575913, index ID 1 ...",
            "Reparo: a página (1:200) foi desalocada do ID de objeto 309576141, ID de índice 1.",
            "sem id aqui"
        };
        Assert.Equal(new[] { 245575913, 309576141 }, RepairService.ParseRepairObjectIds(msgs));
    }

    // ---------------- Log cheio ----------------

    [Fact]
    public void LogSpace_CriticoQuandoQuaseCheioEAlgoSeguraOLog()
    {
        Assert.True(new LogSpaceInfo { UsedPercent = 97, ReuseWait = "LOG_BACKUP" }.IsCritical);
        Assert.False(new LogSpaceInfo { UsedPercent = 97, ReuseWait = "NOTHING" }.IsCritical);
        Assert.False(new LogSpaceInfo { UsedPercent = 40, ReuseWait = "LOG_BACKUP" }.IsCritical);
    }

    [Fact]
    public void LogSpace_NaoCresceQuandoLimiteOuDiscoCheio()
    {
        var info = new LogSpaceInfo();
        info.Files.Add(new LogFileInfo { SizeMb = 1024, MaxSizeMb = 1024, Growth = 64, VolumeFreeMb = 50_000 });
        info.Files.Add(new LogFileInfo { SizeMb = 10, Growth = 0, VolumeFreeMb = 50_000 });
        Assert.True(info.CannotGrow);

        var ok = new LogSpaceInfo();
        ok.Files.Add(new LogFileInfo { SizeMb = 10, Growth = 64, VolumeFreeMb = 50_000 });
        Assert.False(ok.CannotGrow);
    }

    // ---------------- Cópias de segurança da ferramenta no plano ----------------

    private static BackupSetInfo Full(int day, string name, bool copyOnly, string device) => new()
    {
        BackupTypeCode = "D", Name = name, IsCopyOnly = copyOnly, PhysicalDevice = device, Position = 1,
        BackupFinishDate = new DateTime(2026, 1, day), FirstLsn = day * 100, LastLsn = day * 100 + 50, CheckpointLsn = day * 100 + 10
    };

    [Fact]
    public void Plano_IgnoraCopiaDeSegurancaDaFerramentaComoBase()
    {
        var history = new List<BackupSetInfo>
        {
            Full(1, "Agendado", copyOnly: false, "agendado.bak"),
            Full(2, "RestoreDB COPY_ONLY backup de X", copyOnly: true, "seguranca.bak"),  // estado já danificado
            Full(3, "RestoreDB simulação de X", copyOnly: true, "sim.bak"),               // apagado após a simulação
        };
        var plan = BackupChainPlanner.Plan("X", history, target: null);
        Assert.Equal("agendado.bak", plan.BaseFull!.PhysicalDevice);
    }

    [Fact]
    public void Plano_UsaCopiaDaFerramentaSoSeForOUnicoFull_ComAviso()
    {
        var history = new List<BackupSetInfo> { Full(2, "RestoreDB COPY_ONLY backup de X", copyOnly: true, "seguranca.bak") };
        var plan = BackupChainPlanner.Plan("X", history, target: null);
        Assert.Equal("seguranca.bak", plan.BaseFull!.PhysicalDevice);
        Assert.Contains(plan.Warnings, w => w.Contains("cópia de segurança", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Plano_CopyOnlyDoUsuarioContinuaValida()
    {
        var history = new List<BackupSetInfo>
        {
            Full(1, "Agendado", copyOnly: false, "agendado.bak"),
            Full(2, "Cópia manual do DBA", copyOnly: true, "manual.bak"),
        };
        Assert.Equal("manual.bak", BackupChainPlanner.Plan("X", history, null).BaseFull!.PhysicalDevice);
    }

    [Fact]
    public void BackupSet_IdentificaCopiaDaFerramenta()
    {
        Assert.True(Full(1, "RestoreDB COPY_ONLY backup de X", true, "a").IsToolSafetyCopy);
        Assert.False(Full(1, "RestoreDB pós-reparo de X", false, "a").IsToolSafetyCopy); // FULL agendado novo: base legítima
        Assert.False(Full(1, "Backup do DBA", true, "a").IsToolSafetyCopy);
    }

    // ---------------- Conclusões novas do diagnóstico ----------------

    [Fact]
    public void Conclusao_ArquivoInacessivel_TemPrioridadeENaoECorrupcao()
    {
        var d = new DiagnosisResult { DatabaseName = "X", StateDesc = "RECOVERY_PENDING" };
        d.DataCorruptionSuspected = true; // a linha "could not open" também parece corrupção — não deve vencer
        d.FileProblems.Add(new FileAccessProblem { LogicalName = "X", PhysicalName = @"D:\X.mdf", Kind = FileProblemKind.AccessDenied });

        RepairService.BuildConclusion(d);

        Assert.Equal(RecommendedActionKind.FixFileAccess, d.RecommendedAction);
        Assert.Contains("NÃO é corrupção", d.Summary);
    }

    [Fact]
    public void Conclusao_ArquivoInacessivel_IgnoradoSeBancoOnline()
    {
        var d = new DiagnosisResult { DatabaseName = "X", StateDesc = "ONLINE", CheckDb = new CheckDbReport { Executed = true } };
        d.FileProblems.Add(new FileAccessProblem { PhysicalName = @"D:\X.mdf", Kind = FileProblemKind.AccessDenied });
        RepairService.BuildConclusion(d);
        Assert.Equal(RecommendedActionKind.NoAction, d.RecommendedAction);
    }

    [Fact]
    public void Conclusao_LogCheio_RecomendaResolverLogSemPerda()
    {
        var d = new DiagnosisResult { DatabaseName = "X", StateDesc = "ONLINE", CheckDb = new CheckDbReport { Executed = true }, LogFull = true };
        d.LogSpace = new LogSpaceInfo { UsedPercent = 99, ReuseWait = "LOG_BACKUP" };
        RepairService.BuildConclusion(d);
        Assert.Equal(RecommendedActionKind.ResolveLogFull, d.RecommendedAction);
        Assert.Contains("BACKUP DE LOG", d.Recommendation);
    }

    [Fact]
    public void ConstraintDesabilitada_AparecenoDisplay()
        => Assert.Contains("desabilitada", new ConstraintViolation { Constraint = "[FK]", Table = "[dbo].[T]", Where = "[A] = 1", IsForeignKey = true, IsDisabled = true }.Display);
}
