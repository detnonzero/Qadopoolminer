using Qadopoolminer.Models;

namespace Qadopoolminer.Services;

public interface ILogSink
{
    event EventHandler<LogEntry>? EntryAdded;

    IReadOnlyList<LogEntry> GetSnapshot();

    void Info(string area, string message);

    void Warn(string area, string message);

    void Error(string area, string message);
}

public sealed class LogService : ILogSink
{
    private readonly object _sync = new();
    private readonly List<LogEntry> _entries = new();

    public event EventHandler<LogEntry>? EntryAdded;

    public IReadOnlyList<LogEntry> GetSnapshot()
    {
        lock (_sync)
        {
            return _entries.ToArray();
        }
    }

    public void Info(string area, string message)
        => Add(LogLevel.Info, area, message);

    public void Warn(string area, string message)
        => Add(LogLevel.Warn, area, message);

    public void Error(string area, string message)
        => Add(LogLevel.Error, area, message);

    private void Add(LogLevel level, string area, string message)
    {
        var entry = new LogEntry(DateTimeOffset.UtcNow, level, area, message);

        lock (_sync)
        {
            _entries.Add(entry);
            if (_entries.Count > 500)
            {
                _entries.RemoveAt(0);
            }
        }

        EntryAdded?.Invoke(this, entry);
    }
}
