namespace Beacon.Tests;

/// <summary>
/// A time provider whose timers never run out by themselves. A test decides when a wait is
/// over by calling <see cref="ManualTimer.Fire"/>, so nothing depends on how fast the machine is.
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];

    /// <summary>Every timer created so far, oldest first.</summary>
    public IReadOnlyList<ManualTimer> Timers
    {
        get
        {
            lock (_timers)
            {
                return _timers.ToList();
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(callback, state, dueTime);
        lock (_timers)
        {
            _timers.Add(timer);

            // Whoever waits in WaitForTimer is told that there is one more.
            Monitor.PulseAll(_timers);
        }

        return timer;
    }

    /// <summary>
    /// Waits until as many timers as <paramref name="count"/> have been made with this wait, and
    /// gives the last of them. It waits for the signal that a timer was made and looks at
    /// nothing in between. Null when no timer at all was made for as long as
    /// <paramref name="limit"/>, which is only there so that a test that has gone wrong ends.
    /// </summary>
    /// <param name="due">The wait the timers were made with (<see cref="ManualTimer.DueWhenMade"/>).</param>
    public ManualTimer? WaitForTimer(TimeSpan due, int count, TimeSpan limit)
    {
        lock (_timers)
        {
            while (true)
            {
                var made = _timers.Where(timer => timer.DueWhenMade == due).ToList();
                if (made.Count >= count)
                {
                    return made[count - 1];
                }

                if (!Monitor.Wait(_timers, limit))
                {
                    return null;
                }
            }
        }
    }
}

public sealed class ManualTimer : ITimer
{
    private readonly TimerCallback _callback;
    private readonly object? _state;
    private readonly Lock _gate = new();
    private TimeSpan? _due;
    private int _starts;
    private bool _disposed;

    public ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime)
    {
        _callback = callback;
        _state = state;
        _due = dueTime == Timeout.InfiniteTimeSpan ? null : dueTime;
        DueWhenMade = _due;
    }

    /// <summary>The wait the timer was made with, or null when it was made without one. It never changes.</summary>
    public TimeSpan? DueWhenMade { get; }

    /// <summary>How long the wait was last set to, or null when the timer is not waiting.</summary>
    public TimeSpan? Due
    {
        get
        {
            lock (_gate)
            {
                return _due;
            }
        }
    }

    /// <summary>How many times the wait has been started or started again.</summary>
    public int Starts
    {
        get
        {
            lock (_gate)
            {
                return _starts;
            }
        }
    }

    public bool Disposed
    {
        get
        {
            lock (_gate)
            {
                return _disposed;
            }
        }
    }

    public bool Change(TimeSpan dueTime, TimeSpan period)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            _due = dueTime == Timeout.InfiniteTimeSpan ? null : dueTime;
            if (_due is not null)
            {
                _starts++;
            }

            return true;
        }
    }

    /// <summary>
    /// Says the wait has run out. It calls back even when the timer has been disposed, the way
    /// a real timer's callback can already be on its way when the timer is disposed.
    /// </summary>
    public void Fire()
    {
        lock (_gate)
        {
            _due = null;
        }

        _callback(_state);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _due = null;
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
