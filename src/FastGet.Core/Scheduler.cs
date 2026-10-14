namespace FastGet.Core;

/// <summary>Starts and stops the main queue at the scheduled times.</summary>
public sealed class QueueScheduler : IDisposable
{
    /// <summary>A start that was missed (PC asleep, app not running) still fires within this window.</summary>
    static readonly TimeSpan GraceWindow = TimeSpan.FromMinutes(30);

    readonly DownloadManager _manager;
    readonly Timer? _timer;
    DateTime? _lastStart, _lastStop;

    public QueueScheduler(DownloadManager manager, bool autoTick = true)
    {
        _manager = manager;
        if (autoTick) _timer = new Timer(_ => Tick(DateTime.Now), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15));
    }

    public event Action? Started;

    ScheduleSettings S => _manager.Settings.Schedule;

    public void Tick(DateTime now)
    {
        if (S.StartEnabled && DueAt(now, S.StartTime, checkDay: true) is { } start && _lastStart != start)
        {
            _lastStart = start;
            if (!S.Daily) S.StartEnabled = false; // one-time schedule is used up
            if (!_manager.QueueRunning)
            {
                _manager.StartQueue();
                Started?.Invoke();
            }
        }
        if (S.StopEnabled && DueAt(now, S.StopTime, checkDay: false) is { } stop && _lastStop != stop)
        {
            _lastStop = stop;
            if (_manager.QueueRunning) _manager.StopQueue();
        }
    }

    /// <summary>The scheduled moment that is due now (within the grace window), or null.</summary>
    DateTime? DueAt(DateTime now, string time, bool checkDay)
    {
        if (!ScheduleSettings.TryParseTime(time, out var t)) return null;
        foreach (var day in new[] { now.Date, now.Date.AddDays(-1) })
        {
            var at = day + t;
            if (now < at || now - at > GraceWindow) continue;
            if (checkDay && !S.DayAllowed(day)) continue;
            return at;
        }
        return null;
    }

    public DateTime? NextStart(DateTime now) => S.NextStart(now);

    public void Dispose() => _timer?.Dispose();
}
