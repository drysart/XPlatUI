namespace XPlatUI;

/// <summary>
/// Adapts a single callback to <see cref="IXPlatApplicationHook"/>, so a feature whose setup is one
/// line does not need a class of its own.
/// </summary>
internal sealed class ActionBasedAppHook : IXPlatApplicationHook
{
    private readonly XPlatApplicationPhase _phase;
    private readonly Action<XPlatApplication> _hookFunc;

    public ActionBasedAppHook(XPlatApplicationPhase phase, Action<XPlatApplication> hookFunc)
    {
        _phase = phase;
        _hookFunc = hookFunc;
    }

    public void OnPhase(XPlatApplicationPhase phase, XPlatApplication application)
    {
        if (phase == _phase)
        {
            _hookFunc(application);
        }
    }
}

/// <summary>
/// Adapts one callback per window phase to <see cref="IXPlatWindowHook"/>, so a feature that only
/// cares about one or two points writes a lambda per point instead of a class.
/// </summary>
internal sealed class ActionBasedWindowHook : IXPlatWindowHook
{
    private readonly Action<XPlatWindow>? _created;
    private readonly Action<XPlatWindow>? _showing;
    private readonly Action<XPlatWindow>? _shown;
    private readonly Action<XPlatWindow>? _hiding;
    private readonly Action<XPlatWindow>? _hidden;
    private readonly Action<XPlatWindow>? _closing;
    private readonly Action<XPlatWindow>? _closed;
    private readonly Action<XPlatWindow>? _disposed;

    public ActionBasedWindowHook(
        Action<XPlatWindow>? created,
        Action<XPlatWindow>? showing,
        Action<XPlatWindow>? shown,
        Action<XPlatWindow>? hiding,
        Action<XPlatWindow>? hidden,
        Action<XPlatWindow>? closing,
        Action<XPlatWindow>? closed,
        Action<XPlatWindow>? disposed)
    {
        _created = created;
        _showing = showing;
        _shown = shown;
        _hiding = hiding;
        _hidden = hidden;
        _closing = closing;
        _closed = closed;
        _disposed = disposed;
    }

    public void OnPhase(XPlatWindowPhase phase, XPlatWindow window)
    {
        var callback = phase switch
        {
            XPlatWindowPhase.Created => _created,
            XPlatWindowPhase.Showing => _showing,
            XPlatWindowPhase.Shown => _shown,
            XPlatWindowPhase.Hiding => _hiding,
            XPlatWindowPhase.Hidden => _hidden,
            XPlatWindowPhase.Closing => _closing,
            XPlatWindowPhase.Closed => _closed,
            XPlatWindowPhase.Disposed => _disposed,
            _ => null,
        };

        callback?.Invoke(window);
    }
}
