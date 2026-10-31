using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace XPlatUI.Backend.Win32;

/// <summary>
/// The Windows <see cref="IXPlatDialogService"/>: <c>MessageBoxW</c> for messages and the
/// <c>IFileDialog</c> family for open, save and folder picking.
/// </summary>
/// <remarks>
/// <para>
/// Both are already modal loops owned by the OS: <c>MessageBoxW</c> and <c>IFileDialog::Show</c> run
/// their own message pump on the calling thread until the user answers. That satisfies the nested
/// loop requirement in <see cref="IXPlatDialogService"/> without this class pumping anything, because
/// those pumps dispatch every message for the thread - including the backend's wake-up message, so
/// <see cref="IXPlatApplicationBackend.Invoke"/> callbacks keep arriving while a dialog is open.
/// </para>
/// <para>
/// The file dialogs are the modern common item dialogs rather than the older
/// <c>GetOpenFileNameW</c>, because only those can pick a folder. They are driven through
/// source-generated COM so the whole thing survives NativeAOT, where built-in COM interop is
/// trimmed away.
/// </para>
/// </remarks>
internal sealed class Win32DialogService : IXPlatDialogService
{
    /// <summary>The application name a message box falls back to when no title is given.</summary>
    private static readonly string FallbackTitle = "XPlat UI";

    /// <inheritdoc />
    public XPlatDialogResult ShowMessage(XPlatMessageBoxOptions options, XPlatWindow? owner)
    {
        ArgumentNullException.ThrowIfNull(options);

        var (buttonFlags, positions) = MapButtons(options.Buttons);
        var type = buttonFlags | MapIcon(options.Icon) | MapDefaultButton(options.DefaultResult, positions);

        var result = NativeMethods.MessageBoxW(
            HandleOf(owner),
            options.Message,
            options.Title ?? FallbackTitle,
            type);

        return result switch
        {
            NativeMethods.IdOk => XPlatDialogResult.Ok,
            NativeMethods.IdCancel => XPlatDialogResult.Cancel,
            NativeMethods.IdYes => XPlatDialogResult.Yes,
            NativeMethods.IdNo => XPlatDialogResult.No,
            NativeMethods.IdRetry => XPlatDialogResult.Retry,
            NativeMethods.IdAbort => XPlatDialogResult.Abort,
            NativeMethods.IdIgnore => XPlatDialogResult.Ignore,
            // Zero means the box could not be shown at all. Reporting Cancel is the answer a
            // dismissed box gives, and the alternatives are throwing from a diagnostic helper.
            _ => XPlatDialogResult.Cancel,
        };
    }

    /// <inheritdoc />
    public string? OpenFile(XPlatFileDialogOptions options, XPlatWindow? owner)
    {
        ArgumentNullException.ThrowIfNull(options);

        return ShowFileDialog(
            Win32ComInterfaces.FileOpenDialogClass,
            FileOpenDialogOptions.FileMustExist | FileOpenDialogOptions.PathMustExist,
            options,
            owner);
    }

    /// <inheritdoc />
    public string? SaveFile(XPlatFileDialogOptions options, XPlatWindow? owner)
    {
        ArgumentNullException.ThrowIfNull(options);

        return ShowFileDialog(
            Win32ComInterfaces.FileSaveDialogClass,
            options.OverwritePrompt ? FileOpenDialogOptions.OverwritePrompt : 0,
            options,
            owner);
    }

    /// <inheritdoc />
    public string? PickFolder(XPlatFileDialogOptions options, XPlatWindow? owner)
    {
        ArgumentNullException.ThrowIfNull(options);

        // FOS_PICKFOLDERS turns the same dialog into a folder picker; the file-only flags would
        // then reject every folder the user picks, so they are left off.
        return ShowFileDialog(
            Win32ComInterfaces.FileOpenDialogClass,
            FileOpenDialogOptions.PickFolders | FileOpenDialogOptions.PathMustExist,
            options,
            owner);
    }

    private static unsafe string? ShowFileDialog(
        Guid classId,
        FileOpenDialogOptions modeOptions,
        XPlatFileDialogOptions options,
        XPlatWindow? owner)
    {
        var interfaceId = typeof(IFileDialog).GUID;
        var hr = NativeMethods.CoCreateInstance(classId, IntPtr.Zero, NativeMethods.ClsCtxInprocServer, interfaceId, out var unknown);
        Marshal.ThrowExceptionForHR(hr);

        var dialog = ComInterfaceMarshaller<IFileDialog>.ConvertToManaged((void*)unknown)!;
        try
        {
            dialog.GetOptions(out var current);
            dialog.SetOptions(current | modeOptions | FileOpenDialogOptions.ForceFileSystem | FileOpenDialogOptions.NoChangeDir);

            if (!string.IsNullOrWhiteSpace(options.Title))
            {
                dialog.SetTitle(options.Title);
            }

            if (!string.IsNullOrWhiteSpace(options.FileName))
            {
                dialog.SetFileName(options.FileName);
            }

            ApplyFilters(dialog, options);

            if (!string.IsNullOrWhiteSpace(options.InitialDirectory))
            {
                ApplyInitialDirectory(dialog, options.InitialDirectory);
            }

            var show = dialog.Show(HandleOf(owner));
            if (show == NativeMethods.ErrorCancelled)
            {
                return null;
            }

            Marshal.ThrowExceptionForHR(show);

            dialog.GetResult(out var item);
            try
            {
                item.GetDisplayName(ShellItemDisplayName.FileSysPath, out var name);
                try
                {
                    return Marshal.PtrToStringUni(name);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(name);
                }
            }
            finally
            {
                Release(item);
            }
        }
        finally
        {
            Release(dialog);
        }
    }

    /// <summary>
    /// Hands the filter drop-down to the dialog. The strings live in unmanaged memory only for the
    /// duration of the call, because the dialog copies them.
    /// </summary>
    private static void ApplyFilters(IFileDialog dialog, XPlatFileDialogOptions options)
    {
        if (options.Filters.Count == 0)
        {
            return;
        }

        var specs = new ComDlgFilterSpec[options.Filters.Count];
        var pinned = new List<IntPtr>(options.Filters.Count * 2);
        try
        {
            for (var i = 0; i < options.Filters.Count; i++)
            {
                var filter = options.Filters[i];
                specs[i] = new ComDlgFilterSpec
                {
                    Name = Marshal.StringToHGlobalUni(filter.Name),
                    // One entry can name several patterns; the common dialog wants them joined by
                    // semicolons in a single string.
                    Spec = Marshal.StringToHGlobalUni(string.Join(';', filter.Patterns)),
                };
                pinned.Add(specs[i].Name);
                pinned.Add(specs[i].Spec);
            }

            dialog.SetFileTypes((uint)specs.Length, specs);
            dialog.SetFileTypeIndex(1);
        }
        finally
        {
            foreach (var pointer in pinned)
            {
                Marshal.FreeHGlobal(pointer);
            }
        }
    }

    private static unsafe void ApplyInitialDirectory(IFileDialog dialog, string directory)
    {
        var interfaceId = Win32ComInterfaces.ShellItemInterface;
        if (NativeMethods.SHCreateItemFromParsingName(directory, IntPtr.Zero, interfaceId, out var unknown) != 0)
        {
            // A directory that no longer exists is not worth failing the whole dialog over; the
            // dialog simply opens wherever it last remembered.
            return;
        }

        var folder = ComInterfaceMarshaller<IShellItem>.ConvertToManaged((void*)unknown)!;
        try
        {
            dialog.SetFolder(folder);
        }
        finally
        {
            Release(folder);
        }
    }

    /// <summary>
    /// Drops this class's reference to a generated-COM wrapper. The wrapper is a
    /// <c>ComWrappers</c>-based object rather than a classic RCW, so <c>ReleaseComObject</c> is not
    /// the right tool; releasing the underlying pointer is.
    /// </summary>
    private static void Release(object wrapper)
    {
        try
        {
            Marshal.FinalReleaseComObject(wrapper);
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or ArgumentException)
        {
            // Source-generated COM wrappers do not participate in classic RCW bookkeeping, so
            // there is no deterministic release for them; their own finalizer drops the reference.
        }
    }

    private static IntPtr HandleOf(XPlatWindow? owner) =>
        owner?.Backend is Win32WindowBackend window ? window.Handle : IntPtr.Zero;

    private static (uint Flags, XPlatDialogResult[] Positions) MapButtons(XPlatMessageBoxButtons buttons) =>
        buttons switch
        {
            XPlatMessageBoxButtons.Ok => (NativeMethods.MbOk, [XPlatDialogResult.Ok]),
            XPlatMessageBoxButtons.OkCancel => (NativeMethods.MbOkCancel, [XPlatDialogResult.Ok, XPlatDialogResult.Cancel]),
            XPlatMessageBoxButtons.YesNo => (NativeMethods.MbYesNo, [XPlatDialogResult.Yes, XPlatDialogResult.No]),
            XPlatMessageBoxButtons.YesNoCancel => (NativeMethods.MbYesNoCancel, [XPlatDialogResult.Yes, XPlatDialogResult.No, XPlatDialogResult.Cancel]),
            XPlatMessageBoxButtons.RetryCancel => (NativeMethods.MbRetryCancel, [XPlatDialogResult.Retry, XPlatDialogResult.Cancel]),
            XPlatMessageBoxButtons.AbortRetryIgnore => (NativeMethods.MbAbortRetryIgnore, [XPlatDialogResult.Abort, XPlatDialogResult.Retry, XPlatDialogResult.Ignore]),
            _ => throw new ArgumentOutOfRangeException(nameof(buttons), buttons, "Unknown button set."),
        };

    private static uint MapIcon(XPlatMessageBoxIcon icon) =>
        icon switch
        {
            XPlatMessageBoxIcon.None => 0,
            XPlatMessageBoxIcon.Information => NativeMethods.MbIconInformation,
            XPlatMessageBoxIcon.Warning => NativeMethods.MbIconWarning,
            XPlatMessageBoxIcon.Error => NativeMethods.MbIconError,
            XPlatMessageBoxIcon.Question => NativeMethods.MbIconQuestion,
            _ => throw new ArgumentOutOfRangeException(nameof(icon), icon, "Unknown icon."),
        };

    /// <summary>
    /// Picks the <c>MB_DEFBUTTON</c> flag for the requested default. A default the button set does
    /// not contain falls back to the first button, which is what WinForms does.
    /// </summary>
    private static uint MapDefaultButton(XPlatDialogResult preferred, XPlatDialogResult[] positions)
    {
        var index = Array.IndexOf(positions, preferred);
        return index switch
        {
            1 => NativeMethods.MbDefButton2,
            2 => NativeMethods.MbDefButton3,
            _ => NativeMethods.MbDefButton1,
        };
    }
}
