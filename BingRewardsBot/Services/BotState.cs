using BingRewardsBot.Models;

namespace BingRewardsBot.Services;

public record LogEntry(string Level, string Message, string Timestamp);

public class BotState
{
    private readonly object _lock = new();
    private readonly Queue<LogEntry> _logBuffer = new();
    private const int MaxLogs = 200;

    public bool IsRunning { get; private set; }
    public string? LastError { get; private set; }
    public ProgressData Progress { get; private set; } = new();

    public void SetRunning(bool running) { lock (_lock) IsRunning = running; }
    public void SetLastError(string? error) { lock (_lock) LastError = error; }

    public void UpdateProgress(ProgressData data) { lock (_lock) Progress = data; }

    public void AddLog(LogEntry entry)
    {
        lock (_lock)
        {
            _logBuffer.Enqueue(entry);
            if (_logBuffer.Count > MaxLogs)
                _logBuffer.Dequeue();
        }
    }

    public IEnumerable<LogEntry> GetLogs()
    {
        lock (_lock) return _logBuffer.ToArray();
    }

    public StatusResponse GetStatus()
    {
        lock (_lock)
        {
            return new StatusResponse
            {
                Running = IsRunning,
                Progress = Progress,
                LastError = LastError,
            };
        }
    }

    public void Reset(int desktopTotal, int mobileTotal)
    {
        lock (_lock)
        {
            IsRunning = true;
            LastError = null;
            Progress = new ProgressData
            {
                DesktopTotal = desktopTotal,
                MobileTotal = mobileTotal,
                Phase = "starting",
                Status = "running",
            };
        }
    }
}
