namespace XPlatUI;

/// <summary>
/// A repeating timer whose <see cref="Tick"/> always runs on the UI thread, so handlers may touch
/// controls without marshalling.
/// </summary>
/// <remarks>
/// <para>
/// The wait itself happens on a pool thread; only the <see cref="Tick"/> callback is moved to the UI
/// thread, through <see cref="XPlatApplication.BeginInvoke"/>. That keeps the message loop free of
/// any timer bookkeeping and gives every platform the same behaviour.
/// </para>
/// <para>
/// Ticks are not queued up: if the loop is busy for longer than <see cref="Interval"/>, missed ticks
/// are dropped rather than replayed. Before any <see cref="XPlatApplication"/> exists - or once it
/// has been disposed - ticks run inline on the pool thread.
/// </para>
/// </remarks>
public sealed class XPlatTimer : IDisposable
{
    private readonly System.Threading.Timer _timer;
    private TimeSpan _interval;
    private bool _enabled;
    private bool _disposed;
    private int _ticking;

    /// <summary>Creates a stopped timer with a 250 ms interval.</summary>
    public XPlatTimer()
        : this(TimeSpan.FromMilliseconds(250))
    {
    }

    /// <summary>Creates a stopped timer.</summary>
    /// <param name="interval">The interval between ticks; must be positive.</param>
    public XPlatTimer(TimeSpan interval)
    {
        _interval = ValidateInterval(interval);
        _timer = new System.Threading.Timer(static state => ((XPlatTimer)state!).OnTimer(), this, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>The interval between ticks. Changing it while the timer is running restarts it.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public TimeSpan Interval
    {
        get => _interval;
        set
        {
            var interval = ValidateInterval(value);
            if (_interval == interval)
            {
                return;
            }

            _interval = interval;

            if (_enabled)
            {
                Restart();
            }
        }
    }

    /// <summary>Whether the timer is running. Setting it is the same as calling <see cref="Start"/> or <see cref="Stop"/>.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (value)
            {
                Start();
            }
            else
            {
                Stop();
            }
        }
    }

    /// <summary>True once the timer has been disposed.</summary>
    public bool IsDisposed => _disposed;

    /// <summary>Raised on the UI thread every <see cref="Interval"/>.</summary>
    public event EventHandler? Tick;

    /// <summary>Starts the timer, or restarts it when it is already running.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _enabled = true;
        Restart();
    }

    /// <summary>Stops the timer. A tick already queued on the UI thread still arrives.</summary>
    public void Stop()
    {
        if (_disposed)
        {
            return;
        }

        _enabled = false;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _enabled = false;
        _timer.Dispose();
    }

    private static TimeSpan ValidateInterval(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "The interval must be positive.");
        }

        return interval;
    }

    private void Restart() => _timer.Change(_interval, _interval);

    private void OnTimer()
    {
        // A slow handler must not let ticks pile up behind it.
        if (_disposed || Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }

        var application = XPlatApplication.Current;
        if (application is null)
        {
            RaiseTick();
            return;
        }

        application.BeginInvoke(RaiseTick);
    }

    private void RaiseTick()
    {
        try
        {
            if (!_disposed && _enabled)
            {
                Tick?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }
}
