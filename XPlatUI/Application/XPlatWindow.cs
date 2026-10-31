namespace XPlatUI;

/// <summary>
/// One OS window. Thin: it forwards to an <see cref="IXPlatWindowBackend"/> and raises the
/// cross-platform events. It does not duplicate <see cref="XPlatUiHost"/>; the window's host
/// <em>is</em> the host.
/// </summary>
/// <remarks>
/// Migration step 1 declares the type only, so the backend contract in <c>Platform/</c> can name
/// it in a method signature. The members - <c>Content</c>, <c>Host</c>, <c>Backend</c>, the
/// sizing and placement properties, the events and <see cref="IDisposable"/> - arrive in step 2.
/// </remarks>
public sealed partial class XPlatWindow
{
}
