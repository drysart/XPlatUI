using AppKit;
using Foundation;
using UniformTypeIdentifiers;

namespace XPlatUI.Backend.AppKit;

/// <summary>
/// The macOS <see cref="IXPlatDialogService"/>: <c>NSAlert</c> for messages and
/// <c>NSOpenPanel</c>/<c>NSSavePanel</c> for open, save and folder picking.
/// </summary>
/// <remarks>
/// <para>
/// Both run their own modal loop on the calling thread, which is what the nested loop requirement
/// in <see cref="IXPlatDialogService"/> asks for. They are shown application-modal rather than as
/// window sheets: a sheet is asynchronous, so it would return to the caller before the user had
/// answered, and <see cref="IXPlatDialogService"/> is a blocking API by design.
/// </para>
/// <para>
/// The application backend's common-mode poll timer keeps firing during those loops, so
/// <see cref="IXPlatApplicationBackend.Invoke"/> callbacks and ticks still arrive while a dialog is
/// open.
/// </para>
/// </remarks>
internal sealed class AppKitDialogService : IXPlatDialogService
{
    /// <summary>The application name a message box falls back to when no title is given.</summary>
    private static readonly string FallbackTitle = "XPlat UI";

    /// <summary>The response code AppKit returns for the first button of an alert.</summary>
    private static readonly nint FirstButtonResponse = 1000;

    /// <summary>The response code a panel returns when the user accepted.</summary>
    private static readonly nint PanelOkResponse = 1;

    /// <inheritdoc />
    public XPlatDialogResult ShowMessage(XPlatMessageBoxOptions options, XPlatWindow? owner)
    {
        ArgumentNullException.ThrowIfNull(options);

        var (titles, results) = MapButtons(options.Buttons);

        var alert = new NSAlert
        {
            // macOS puts the headline in bold and the detail under it, so the title is the headline
            // when there is one and the message stands on its own when there is not.
            MessageText = string.IsNullOrWhiteSpace(options.Title) ? FallbackTitle : options.Title,
            InformativeText = options.Message,
            AlertStyle = MapIcon(options.Icon),
        };

        foreach (var title in titles)
        {
            alert.AddButton(title);
        }

        ApplyDefaultButton(alert, results, options.DefaultResult);

        var response = alert.RunModal();
        var index = (int)(response - FirstButtonResponse);

        return index >= 0 && index < results.Length ? results[index] : XPlatDialogResult.Cancel;
    }

    /// <inheritdoc />
    public string? OpenFile(XPlatFileDialogOptions options, XPlatWindow? owner)
    {
        ArgumentNullException.ThrowIfNull(options);

        // AppKit wants panels created through its factory selectors: since 10.15 the whole panel
        // runs out of process, and the initializers are declared obsolete.
        var panel = NSOpenPanel.OpenPanel;
        panel.CanChooseFiles = true;
        panel.CanChooseDirectories = false;
        panel.AllowsMultipleSelection = false;
        panel.CanCreateDirectories = true;
        panel.ResolvesAliases = true;

        Configure(panel, options);

        return Run(panel);
    }

    /// <inheritdoc />
    public string? SaveFile(XPlatFileDialogOptions options, XPlatWindow? owner)
    {
        ArgumentNullException.ThrowIfNull(options);

        // NSSavePanel always confirms an overwrite; XPlatFileDialogOptions.OverwritePrompt has no
        // switch to turn that off, so it is honoured in the only direction macOS allows.
        var panel = NSSavePanel.SavePanel;
        panel.CanCreateDirectories = true;
        panel.ExtensionHidden = false;

        Configure(panel, options);

        return Run(panel);
    }

    /// <inheritdoc />
    public string? PickFolder(XPlatFileDialogOptions options, XPlatWindow? owner)
    {
        ArgumentNullException.ThrowIfNull(options);

        var panel = NSOpenPanel.OpenPanel;
        panel.CanChooseFiles = false;
        panel.CanChooseDirectories = true;
        panel.AllowsMultipleSelection = false;
        panel.CanCreateDirectories = true;
        panel.ResolvesAliases = true;

        Configure(panel, options);

        return Run(panel);
    }

    private static void Configure(NSSavePanel panel, XPlatFileDialogOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.Title))
        {
            panel.Title = options.Title;
        }

        if (!string.IsNullOrWhiteSpace(options.FileName))
        {
            panel.NameFieldStringValue = options.FileName;
        }

        if (!string.IsNullOrWhiteSpace(options.InitialDirectory))
        {
            panel.DirectoryUrl = NSUrl.FromFilename(options.InitialDirectory);
        }

        var types = TypesFrom(options);
        if (types.Length > 0)
        {
            panel.AllowedContentTypes = types;
        }
    }

    private static string? Run(NSSavePanel panel)
    {
        var response = panel.RunModal();

        return response == PanelOkResponse ? panel.Url?.Path : null;
    }

    /// <summary>
    /// Turns the filter patterns into the uniform types macOS filters by. A pattern whose extension
    /// has no registered type is dropped rather than widening the dialog to everything.
    /// </summary>
    private static UTType[] TypesFrom(XPlatFileDialogOptions options)
    {
        if (options.Filters.Count == 0)
        {
            return [];
        }

        var types = new List<UTType>();

        foreach (var filter in options.Filters)
        {
            foreach (var pattern in filter.Patterns)
            {
                if (ExtensionOf(pattern) is not { } extension)
                {
                    continue;
                }

                if (UTType.CreateFromExtension(extension) is { } type && !types.Contains(type))
                {
                    types.Add(type);
                }
            }
        }

        return [.. types];
    }

    /// <summary>Extracts the extension a glob pattern matches, or null when it matches everything.</summary>
    private static string? ExtensionOf(string pattern)
    {
        var extension = pattern.Trim();

        var star = extension.LastIndexOf('*');
        if (star >= 0)
        {
            extension = extension[(star + 1)..];
        }

        extension = extension.TrimStart('.');

        return extension.Length == 0 || extension.Contains('*') ? null : extension;
    }

    /// <summary>
    /// Moves the Enter key to the requested button and the Escape key to Cancel, which is the only
    /// way AppKit lets a caller choose the default without reordering the buttons on screen.
    /// </summary>
    private static void ApplyDefaultButton(NSAlert alert, XPlatDialogResult[] results, XPlatDialogResult preferred)
    {
        var buttons = alert.Buttons;
        if (buttons.Length == 0)
        {
            return;
        }

        foreach (var button in buttons)
        {
            button.KeyEquivalent = string.Empty;
        }

        var index = Array.IndexOf(results, preferred);
        buttons[index >= 0 ? index : 0].KeyEquivalent = "\r";

        var cancel = Array.IndexOf(results, XPlatDialogResult.Cancel);
        if (cancel >= 0)
        {
            buttons[cancel].KeyEquivalent = "\u001b";
        }
    }

    private static NSAlertStyle MapIcon(XPlatMessageBoxIcon icon) => icon switch
    {
        XPlatMessageBoxIcon.Error => NSAlertStyle.Critical,
        XPlatMessageBoxIcon.Warning => NSAlertStyle.Warning,
        _ => NSAlertStyle.Informational,
    };

    /// <summary>
    /// The buttons and the result each one reports, in the order they appear. macOS puts the
    /// affirmative button leftmost, which is the same order Windows uses.
    /// </summary>
    private static (string[] Titles, XPlatDialogResult[] Results) MapButtons(XPlatMessageBoxButtons buttons) =>
        buttons switch
        {
            XPlatMessageBoxButtons.OkCancel => (["OK", "Cancel"], [XPlatDialogResult.Ok, XPlatDialogResult.Cancel]),
            XPlatMessageBoxButtons.YesNo => (["Yes", "No"], [XPlatDialogResult.Yes, XPlatDialogResult.No]),
            XPlatMessageBoxButtons.YesNoCancel => (["Yes", "No", "Cancel"], [XPlatDialogResult.Yes, XPlatDialogResult.No, XPlatDialogResult.Cancel]),
            XPlatMessageBoxButtons.RetryCancel => (["Retry", "Cancel"], [XPlatDialogResult.Retry, XPlatDialogResult.Cancel]),
            XPlatMessageBoxButtons.AbortRetryIgnore => (["Abort", "Retry", "Ignore"], [XPlatDialogResult.Abort, XPlatDialogResult.Retry, XPlatDialogResult.Ignore]),
            _ => (["OK"], [XPlatDialogResult.Ok]),
        };
}
