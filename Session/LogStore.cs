using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace UniversalModConverter.Session;

public enum LogLevel
{
    Info,
    Success,
    Warning,
    Error,
}

public sealed record LogEntry(DateTime Time, LogLevel Level, string Text, int Operation);

/// <summary>
/// Structured operation log. Only touched from the framework thread; background work posts
/// its messages through <see cref="BackgroundRunner.Post"/>.
/// </summary>
public sealed class LogStore
{
    private const int MaxEntries = 5000;

    private readonly List<LogEntry> _entries = new();
    private int _operation;

    public IReadOnlyList<LogEntry> Entries => _entries;

    /// <summary>Incremented whenever entries are added or cleared; views use it to invalidate caches.</summary>
    public int Revision { get; private set; }

    /// <summary>The operation id of the most recent Apply, or 0.</summary>
    public int LastConversionOperation { get; private set; }

    /// <summary>Starts a new operation; subsequent entries are grouped under it.</summary>
    public void BeginOperation(bool isConversion)
    {
        _operation++;
        if (isConversion) LastConversionOperation = _operation;
    }

    public void Add(string message) => Add(Classify(message), message);

    public void Add(LogLevel level, string message)
    {
        _entries.Add(new LogEntry(DateTime.Now, level, message, _operation));
        if (_entries.Count > MaxEntries) _entries.RemoveRange(0, _entries.Count - MaxEntries);
        Revision++;
        Plugin.Log.Information("[UMC] {0}", message);
    }

    public void Clear()
    {
        _entries.Clear();
        LastConversionOperation = 0;
        Revision++;
    }

    public string Format(IEnumerable<LogEntry> entries)
    {
        var sb = new StringBuilder();
        foreach (var entry in entries)
            sb.Append('[').Append(entry.Time.ToString("HH:mm:ss")).Append("] ").AppendLine(entry.Text);
        return sb.ToString();
    }

    public IEnumerable<LogEntry> LastConversion()
        => LastConversionOperation == 0
            ? Enumerable.Empty<LogEntry>()
            : _entries.Where(e => e.Operation == LastConversionOperation);

    /// <summary>The converters report through plain strings; recover the severity from their prefixes.</summary>
    private static LogLevel Classify(string message)
    {
        var text = message.TrimStart();
        if (text.StartsWith("[ERROR]", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Error", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("[MISSING]", StringComparison.OrdinalIgnoreCase))
            return LogLevel.Error;
        if (text.StartsWith("[WARNING]", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Warning", StringComparison.OrdinalIgnoreCase))
            return LogLevel.Warning;
        return LogLevel.Info;
    }
}
