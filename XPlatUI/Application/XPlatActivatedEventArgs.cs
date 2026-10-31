namespace XPlatUI;

/// <summary>
/// Describes a request from the platform for the application to come to the foreground, such as a
/// click on the macOS Dock tile or a second launch that could not start a competing process.
/// </summary>
public sealed class XPlatActivatedEventArgs : EventArgs
{
    /// <summary>
    /// Set to true in a <see cref="XPlatApplication.Activated"/> handler to stop the default action,
    /// which is <see cref="XPlatApplication.Activate"/>.
    /// </summary>
    public bool Handled { get; set; }
}
