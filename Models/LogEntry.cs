namespace Qadopoolminer.Models;

public enum LogLevel
{
    Info,
    Warn,
    Error
}

public sealed record LogEntry(DateTimeOffset TimestampUtc, LogLevel Level, string Area, string Message)
{
    public string Text => $"{TimestampUtc.ToLocalTime():HH:mm:ss} [{Level}] {Area}: {Message}";
}
