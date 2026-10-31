namespace XPlatUI.Backend.Gtk4;

/// <summary>
/// The GTK clipboard. Writing is synchronous, but reading is not: GDK can only answer a paste by
/// running the main loop while the selection owner (or the Wayland data source) replies. The
/// library's clipboard contract is synchronous, so the read is driven by pumping the default main
/// context until the task completes, with a short deadline so a stuck owner cannot hang the UI.
/// </summary>
internal sealed class Gtk4Clipboard : IXPlatClipboard
{
    private const int ReadTimeoutMs = 500;

    public string? GetText()
    {
        var clipboard = Gdk.Display.GetDefault()?.GetClipboard();
        if (clipboard is null)
        {
            return null;
        }

        try
        {
            var read = clipboard.ReadTextAsync();
            if (!PumpUntilComplete(read))
            {
                return null;
            }

            return read.GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // No selection owner, no text flavour, or the owner went away mid-read.
            return null;
        }
    }

    public void SetText(string? text)
    {
        var clipboard = Gdk.Display.GetDefault()?.GetClipboard();
        if (clipboard is null)
        {
            return;
        }

        try
        {
            clipboard.SetText(text ?? string.Empty);
        }
        catch (Exception)
        {
            // Losing the clipboard is not worth taking the application down for.
        }
    }

    /// <summary>
    /// Runs the default main context until <paramref name="task"/> finishes. Iteration is blocking,
    /// so this does not spin, and the application backend's poll timer guarantees it wakes up
    /// regularly even when the clipboard never answers.
    /// </summary>
    private static bool PumpUntilComplete(Task task)
    {
        var context = GLib.MainContext.Default();
        var deadline = Environment.TickCount64 + ReadTimeoutMs;

        while (!task.IsCompleted)
        {
            if (Environment.TickCount64 > deadline)
            {
                return false;
            }

            context.Iteration(true);
        }

        return true;
    }
}
