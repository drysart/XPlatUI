using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace XPlatUI.Backend.Win32;

/// <summary>
/// The COM interfaces behind the modern Windows file dialogs.
/// </summary>
/// <remarks>
/// These are declared with <see cref="GeneratedComInterfaceAttribute"/> rather than
/// <see cref="ComImportAttribute"/> because the library publishes with NativeAOT, and built-in COM
/// interop is trimmed away by default there. The generated marshallers are source-generated vtables
/// with no runtime reflection, which is what lets the dialogs work in an AOT build at all.
/// Method order is the vtable order from the SDK headers and must not be reordered.
/// </remarks>
internal static class Win32ComInterfaces
{
    /// <summary><c>CLSID_FileOpenDialog</c>.</summary>
    internal static readonly Guid FileOpenDialogClass = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");

    /// <summary><c>CLSID_FileSaveDialog</c>.</summary>
    internal static readonly Guid FileSaveDialogClass = new("C0B4E2F3-BA21-4773-8DBA-335EC946EB8B");

    /// <summary><c>IID_IShellItem</c>.</summary>
    internal static readonly Guid ShellItemInterface = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
}

/// <summary><c>FILEOPENDIALOGOPTIONS</c>: the flags that shape a file dialog.</summary>
[Flags]
internal enum FileOpenDialogOptions : uint
{
    OverwritePrompt = 0x00000002,
    StrictFileTypes = 0x00000004,
    NoChangeDir = 0x00000008,
    PickFolders = 0x00000020,
    ForceFileSystem = 0x00000040,
    AllNonStorageItems = 0x00000080,
    NoValidate = 0x00000100,
    AllowMultiSelect = 0x00000200,
    PathMustExist = 0x00000800,
    FileMustExist = 0x00001000,
    CreatePrompt = 0x00002000,
    ShareAware = 0x00004000,
    NoReadOnlyReturn = 0x00008000,
    NoTestFileCreate = 0x00010000,
    NoNetworkButton = 0x00020000,
    NoLongNames = 0x00040000,
    HideMrUPlaces = 0x00100000,
    HidePinnedPlaces = 0x00200000,
    NoDereferenceLinks = 0x00400000,
    DontAddToRecent = 0x02000000,
    ForceShowHidden = 0x10000000,
    DefaultNoMiniMode = 0x20000000,
}

/// <summary><c>SIGDN</c>: the form a shell item's display name is returned in.</summary>
internal enum ShellItemDisplayName : uint
{
    /// <summary>A full file system path, which is the only form this backend uses.</summary>
    FileSysPath = 0x80058000,
}

/// <summary>One row of a file dialog's filter drop-down.</summary>
/// <remarks>
/// The string fields are unmanaged pointers rather than <see cref="string"/> because the source
/// generated COM marshaller has no way to express "an array of structs each holding two strings".
/// The caller pins the strings and owns the buffers for the duration of the call.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct ComDlgFilterSpec
{
    /// <summary>Pointer to the label shown in the drop-down.</summary>
    public IntPtr Name;

    /// <summary>Pointer to a semicolon-separated list of glob patterns.</summary>
    public IntPtr Spec;
}

/// <summary><c>IModalWindow</c>: the one method a file dialog needs to become visible.</summary>
[GeneratedComInterface]
[Guid("B4DB1657-70D7-485E-8E3E-6FCB5A5C1802")]
internal partial interface IModalWindow
{
    /// <summary>Shows the window modally and returns once it closes.</summary>
    /// <param name="owner">The window to be modal to, or zero to let the dialog choose.</param>
    /// <returns>An <c>HRESULT</c>; <c>ERROR_CANCELLED</c> means the user dismissed the dialog.</returns>
    [PreserveSig]
    int Show(IntPtr owner);
}

/// <summary>
/// <c>IFileDialog</c>: everything <c>IFileOpenDialog</c> and <c>IFileSaveDialog</c> have in common.
/// </summary>
/// <remarks>
/// Only the single-selection surface is declared. Multi-select would need <c>IShellItemArray</c>,
/// and nothing in <see cref="IXPlatDialogService"/> can return more than one path.
/// </remarks>
[GeneratedComInterface]
[Guid("42F85136-DB7E-439C-85F1-E4075D135FC8")]
internal partial interface IFileDialog : IModalWindow
{
    /// <summary>Replaces the filter drop-down.</summary>
    /// <param name="count">How many entries <paramref name="specs"/> holds.</param>
    /// <param name="specs">The entries, in drop-down order.</param>
    void SetFileTypes(uint count, [MarshalUsing(CountElementName = nameof(count))] ComDlgFilterSpec[] specs);

    /// <summary>Selects a filter entry by its 1-based position.</summary>
    /// <param name="index">The entry to select.</param>
    void SetFileTypeIndex(uint index);

    /// <summary>Gets the selected filter entry's 1-based position.</summary>
    /// <param name="index">Receives the position.</param>
    void GetFileTypeIndex(out uint index);

    /// <summary>Not used; declared to keep the vtable aligned.</summary>
    /// <param name="events">Unused.</param>
    /// <param name="cookie">Unused.</param>
    void Advise(IntPtr events, out uint cookie);

    /// <summary>Not used; declared to keep the vtable aligned.</summary>
    /// <param name="cookie">Unused.</param>
    void Unadvise(uint cookie);

    /// <summary>Replaces the dialog's behaviour flags.</summary>
    /// <param name="options">The new flags.</param>
    void SetOptions(FileOpenDialogOptions options);

    /// <summary>Gets the dialog's behaviour flags.</summary>
    /// <param name="options">Receives the flags.</param>
    void GetOptions(out FileOpenDialogOptions options);

    /// <summary>Sets the folder the dialog opens in when it has no remembered one.</summary>
    /// <param name="folder">The folder.</param>
    void SetDefaultFolder(IShellItem folder);

    /// <summary>Sets the folder the dialog opens in.</summary>
    /// <param name="folder">The folder.</param>
    void SetFolder(IShellItem folder);

    /// <summary>Not used; declared to keep the vtable aligned.</summary>
    /// <param name="folder">Unused.</param>
    void GetFolder(out IShellItem folder);

    /// <summary>Not used; declared to keep the vtable aligned.</summary>
    /// <param name="item">Unused.</param>
    void GetCurrentSelection(out IShellItem item);

    /// <summary>Sets the text shown in the file name box.</summary>
    /// <param name="name">The file name, without a path.</param>
    void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);

    /// <summary>Not used; declared to keep the vtable aligned.</summary>
    /// <param name="name">Unused.</param>
    void GetFileName(out IntPtr name);

    /// <summary>Sets the title bar text.</summary>
    /// <param name="title">The title.</param>
    void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);

    /// <summary>Not used; declared to keep the vtable aligned.</summary>
    /// <param name="text">Unused.</param>
    void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);

    /// <summary>Not used; declared to keep the vtable aligned.</summary>
    /// <param name="label">Unused.</param>
    void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);

    /// <summary>Gets what the user chose. Only valid after <see cref="IModalWindow.Show"/> succeeded.</summary>
    /// <param name="item">Receives the chosen item.</param>
    void GetResult(out IShellItem item);

    /// <summary>Not used; declared to keep the vtable aligned.</summary>
    /// <param name="item">Unused.</param>
    /// <param name="place">Unused.</param>
    void AddPlace(IShellItem item, int place);

    /// <summary>Not used; declared to keep the vtable aligned.</summary>
    /// <param name="extension">Unused.</param>
    void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);

    /// <summary>Not used; declared to keep the vtable aligned.</summary>
    /// <param name="result">Unused.</param>
    void Close(int result);

    /// <summary>Not used; declared to keep the vtable aligned.</summary>
    /// <param name="guid">Unused.</param>
    void SetClientGuid(in Guid guid);

    /// <summary>Not used; declared to keep the vtable aligned.</summary>
    void ClearClientData();

    /// <summary>Not used; declared to keep the vtable aligned.</summary>
    /// <param name="filter">Unused.</param>
    void SetFilter(IntPtr filter);
}

/// <summary><c>IShellItem</c>: one entry in the shell namespace, such as a file or a folder.</summary>
[GeneratedComInterface]
[Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
internal partial interface IShellItem
{
    /// <summary>Not used; declared to keep the vtable aligned.</summary>
    /// <param name="context">Unused.</param>
    /// <param name="handler">Unused.</param>
    /// <param name="interfaceId">Unused.</param>
    /// <param name="result">Unused.</param>
    void BindToHandler(IntPtr context, in Guid handler, in Guid interfaceId, out IntPtr result);

    /// <summary>Not used; declared to keep the vtable aligned.</summary>
    /// <param name="parent">Unused.</param>
    void GetParent(out IShellItem parent);

    /// <summary>Gets the item's name in the requested form.</summary>
    /// <param name="form">The form to return.</param>
    /// <param name="name">
    /// Receives a string the caller must free with <c>CoTaskMemFree</c>. It is a raw pointer rather
    /// than a <see cref="string"/> so that ownership stays explicit.
    /// </param>
    void GetDisplayName(ShellItemDisplayName form, out IntPtr name);

    /// <summary>Not used; declared to keep the vtable aligned.</summary>
    /// <param name="mask">Unused.</param>
    /// <param name="attributes">Unused.</param>
    void GetAttributes(uint mask, out uint attributes);

    /// <summary>Not used; declared to keep the vtable aligned.</summary>
    /// <param name="other">Unused.</param>
    /// <param name="hint">Unused.</param>
    /// <param name="order">Unused.</param>
    void Compare(IShellItem other, uint hint, out int order);
}
