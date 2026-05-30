namespace RestoreDB.Domain.Models;

public enum RepairLogLevel
{
    Info,
    Command,
    Success,
    Warning,
    Error
}

/// <summary>Uma linha de log de reparo, com timestamp, exibida na tela e gravada em arquivo.</summary>
public sealed class RepairLogEntry
{
    public DateTime Timestamp { get; } = DateTime.Now;
    public RepairLogLevel Level { get; init; } = RepairLogLevel.Info;
    public string Message { get; init; } = string.Empty;
    public string? Database { get; init; }

    public string TimestampText => Timestamp.ToString("HH:mm:ss");

    public override string ToString()
    {
        var db = string.IsNullOrEmpty(Database) ? string.Empty : $" ({Database})";
        return $"[{Timestamp:yyyy-MM-dd HH:mm:ss}] [{Level.ToString().ToUpperInvariant()}]{db} {Message}";
    }
}
