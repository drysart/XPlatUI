namespace XPlatUI;

/// <summary>
/// Owns the message loop, the window list and the platform services. One per process;
/// <c>XPlatApplication.Current</c> is the ambient handle controls and services use to reach it.
/// </summary>
/// <remarks>
/// Migration step 1 declares the type only, so the backend contract in <c>Platform/</c> can name
/// it in a method signature. The members - <c>Run</c>, <c>Current</c>, <c>Services</c>, the window
/// list, the events and <c>Dialogs</c> - arrive in step 2 together with the headless backend that
/// exercises them.
/// </remarks>
public sealed partial class XPlatApplication
{
}
