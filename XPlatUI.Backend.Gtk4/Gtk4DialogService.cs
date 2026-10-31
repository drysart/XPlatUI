namespace XPlatUI.Backend.Gtk4;

/// <summary>
/// The GTK4 dialog service: message boxes come from <see cref="Gtk.AlertDialog"/> and file pickers
/// from <see cref="Gtk.FileDialog"/>, so dialogs look and behave like the rest of the desktop
/// instead of being drawn by SkiaSharp.
/// </summary>
/// <remarks>
/// Both GTK4 dialog APIs are asynchronous, while <see cref="IXPlatDialogService"/> is deliberately
/// synchronous: callers expect a result when the method returns. The gap is bridged by running the
/// default main context until the dialog's task completes, which is the same technique the
/// clipboard uses. That keeps the rest of the window painting and the application's tick running
/// while the dialog is up.
/// </remarks>
internal sealed class Gtk4DialogService : IXPlatDialogService
{
    private const string FallbackTitle = "XPlat UI";

    public XPlatDialogResult ShowMessage(XPlatMessageBoxOptions options, XPlatWindow? owner)
    {
        ArgumentNullException.ThrowIfNull(options);

        var (titles, results) = MapButtons(options.Buttons);

        var dialog = new Gtk.AlertDialog
        {
            Message = string.IsNullOrWhiteSpace(options.Title) ? FallbackTitle : options.Title,
            Detail = options.Message,
            Modal = true,
        };

        dialog.SetButtons(titles);

        var defaultButton = Array.IndexOf(results, options.DefaultResult);
        dialog.SetDefaultButton(defaultButton >= 0 ? defaultButton : 0);

        var cancelButton = Array.IndexOf(results, XPlatDialogResult.Cancel);
        if (cancelButton >= 0)
        {
            dialog.SetCancelButton(cancelButton);
        }

        try
        {
            var choose = dialog.ChooseAsync(ParentOf(owner));
            Pump(choose);

            var index = choose.GetAwaiter().GetResult();
            return index >= 0 && index < results.Length ? results[index] : XPlatDialogResult.Cancel;
        }
        catch (Exception)
        {
            // Dismissing the dialog without choosing (Escape, window close) surfaces as an error.
            return XPlatDialogResult.Cancel;
        }
    }

    public string? OpenFile(XPlatFileDialogOptions options, XPlatWindow? owner)
    {
        ArgumentNullException.ThrowIfNull(options);

        var dialog = Gtk.FileDialog.New();
        Configure(dialog, options);

        return Complete(dialog.OpenAsync(ParentOf(owner)));
    }

    public string? SaveFile(XPlatFileDialogOptions options, XPlatWindow? owner)
    {
        ArgumentNullException.ThrowIfNull(options);

        var dialog = Gtk.FileDialog.New();
        Configure(dialog, options);

        return Complete(dialog.SaveAsync(ParentOf(owner)));
    }

    public string? PickFolder(XPlatFileDialogOptions options, XPlatWindow? owner)
    {
        ArgumentNullException.ThrowIfNull(options);

        var dialog = Gtk.FileDialog.New();
        Configure(dialog, options);

        return Complete(dialog.SelectFolderAsync(ParentOf(owner)));
    }

    private static void Configure(Gtk.FileDialog dialog, XPlatFileDialogOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.Title))
        {
            dialog.Title = options.Title;
        }

        if (!string.IsNullOrWhiteSpace(options.FileName))
        {
            dialog.InitialName = options.FileName;
        }

        if (!string.IsNullOrWhiteSpace(options.InitialDirectory))
        {
            dialog.InitialFolder = Gio.Functions.FileNewForPath(options.InitialDirectory);
        }

        if (CreateFilters(options) is { } filters)
        {
            dialog.Filters = filters;
        }
    }

    private static Gio.ListStore? CreateFilters(XPlatFileDialogOptions options)
    {
        if (options.Filters.Count == 0)
        {
            return null;
        }

        var store = Gio.ListStore.New<Gtk.FileFilter>();

        foreach (var filter in options.Filters)
        {
            var native = Gtk.FileFilter.New();
            native.SetName(filter.Name);

            var patterns = 0;
            foreach (var pattern in filter.Patterns)
            {
                if (string.IsNullOrWhiteSpace(pattern))
                {
                    continue;
                }

                // GTK file filters are glob patterns, the same syntax the library already uses.
                native.AddPattern(pattern);
                patterns++;
            }

            if (patterns > 0)
            {
                store.Append(native);
            }
        }

        return store.GetNItems() > 0 ? store : null;
    }

    private static string? Complete(Task<Gio.File?> task)
    {
        try
        {
            Pump(task);
            return task.GetAwaiter().GetResult()?.GetPath();
        }
        catch (Exception)
        {
            // Cancelling a GTK4 file dialog is reported as an exception.
            return null;
        }
    }

    /// <summary>
    /// Runs the default main context until <paramref name="task"/> finishes. Iteration is blocking,
    /// so this does not spin, and the application backend's poll timer guarantees it wakes up
    /// regularly even when the dialog never answers.
    /// </summary>
    private static void Pump(Task task)
    {
        var context = GLib.MainContext.Default();

        while (!task.IsCompleted)
        {
            context.Iteration(true);
        }
    }

    private static Gtk.Window? ParentOf(XPlatWindow? owner) =>
        (owner?.Backend as Gtk4WindowBackend)?.Window;

    /// <summary>
    /// Maps the model's button set onto GTK's "an array of labels, answered by index" model.
    /// </summary>
    private static (string[] Titles, XPlatDialogResult[] Results) MapButtons(XPlatMessageBoxButtons buttons) =>
        buttons switch
        {
            XPlatMessageBoxButtons.OkCancel => (
                ["Cancel", "OK"],
                [XPlatDialogResult.Cancel, XPlatDialogResult.Ok]),

            XPlatMessageBoxButtons.YesNo => (
                ["No", "Yes"],
                [XPlatDialogResult.No, XPlatDialogResult.Yes]),

            XPlatMessageBoxButtons.YesNoCancel => (
                ["Cancel", "No", "Yes"],
                [XPlatDialogResult.Cancel, XPlatDialogResult.No, XPlatDialogResult.Yes]),

            XPlatMessageBoxButtons.RetryCancel => (
                ["Cancel", "Retry"],
                [XPlatDialogResult.Cancel, XPlatDialogResult.Retry]),

            XPlatMessageBoxButtons.AbortRetryIgnore => (
                ["Ignore", "Retry", "Abort"],
                [XPlatDialogResult.Ignore, XPlatDialogResult.Retry, XPlatDialogResult.Abort]),

            _ => (
                ["OK"],
                [XPlatDialogResult.Ok]),
        };
}
