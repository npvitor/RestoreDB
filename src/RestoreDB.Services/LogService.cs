using System.IO;
using RestoreDB.Domain.Models;

namespace RestoreDB.Services;

/// <summary>
/// Grava o log de todas as ações de reparo em arquivo (um por dia), com timestamp.
/// Thread-safe. Nunca lança: falha de log não pode interromper o fluxo principal.
/// </summary>
public sealed class LogService
{
    private readonly object _gate = new();

    public string LogDirectory { get; }
    public string LogFilePath { get; }

    public LogService()
    {
        LogDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RestoreDB", "Logs");

        try
        {
            Directory.CreateDirectory(LogDirectory);
        }
        catch
        {
            // Se não for possível criar a pasta, cai para a pasta temporária.
            LogDirectory = Path.GetTempPath();
        }

        LogFilePath = Path.Combine(LogDirectory, $"repair-{DateTime.Now:yyyyMMdd}.log");
    }

    public void Write(RepairLogEntry entry)
    {
        try
        {
            lock (_gate)
                File.AppendAllText(LogFilePath, entry + Environment.NewLine);
        }
        catch
        {
            // Logging é best-effort.
        }
    }
}
