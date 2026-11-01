using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace XPlatUI.Backend.Win32;

/// <summary>
/// The OLE drag and drop interfaces, declared so that they work under NativeAOT.
/// </summary>
/// <remarks>
/// <para>
/// Both directions of COM are involved. <see cref="IDropTarget"/> is implemented by this backend
/// and handed to the shell as a COM object, which then calls into us for the duration of a drag.
/// <see cref="IComDataObject"/> is called into, to read whatever is being dragged.
/// </para>
/// <para>
/// The method order in each interface is the vtable order from the Windows SDK headers and must not
/// be changed: COM dispatches by slot, not by name.
/// </para>
/// </remarks>
internal static class Win32DropInterfaces
{
    /// <summary>The <c>IID_IDropTarget</c> identifier.</summary>
    internal static readonly Guid DropTargetId = new("00000122-0000-0000-C000-000000000046");

    /// <summary>The <c>IID_IUnknown</c> identifier.</summary>
    internal static readonly Guid UnknownId = new("00000000-0000-0000-C000-000000000046");

    /// <summary>The <c>IID_IDataObject</c> identifier.</summary>
    internal static readonly Guid DataObjectId = new("0000010E-0000-0000-C000-000000000046");

    /// <summary><c>DVASPECT_CONTENT</c>: the fully rendered form of the data.</summary>
    internal const uint DvAspectContent = 1;

    /// <summary><c>TYMED_HGLOBAL</c>: the transfer medium is a block of global memory.</summary>
    internal const uint TymedHGlobal = 1;

    /// <summary>Asks <c>DragQueryFile</c> for the number of files instead of a file name.</summary>
    internal const uint DragQueryFileCount = 0xFFFFFFFF;
}

/// <summary>
/// <c>FORMATETC</c>: identifies one representation of a data object.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FORMATETC
{
    /// <summary>The clipboard format, for example <c>CF_HDROP</c>.</summary>
    public ushort cfFormat;

    /// <summary>A pointer to an optional <c>DVTARGETDEVICE</c>; unused here.</summary>
    public IntPtr ptd;

    /// <summary>The requested rendering, for example <see cref="Win32DropInterfaces.DvAspectContent"/>.</summary>
    public uint dwAspect;

    /// <summary>The part of the data to retrieve; -1 means the whole thing.</summary>
    public int lindex;

    /// <summary>The transfer media the caller can accept.</summary>
    public uint tymed;
}

/// <summary>
/// <c>IDropTarget</c>: the interface the shell calls while something is dragged over a window that
/// has registered a drop target.
/// </summary>
/// <remarks>
/// The object that implements this is built by hand in <see cref="Win32DropTarget"/> rather than
/// through <c>ComWrappers</c>, so the interface is a plain declaration whose only purpose is to keep
/// the four methods and their vtable order in one place.
/// </remarks>
internal interface IDropTarget
{
    /// <summary>Called once when the dragged data first enters the window.</summary>
    /// <param name="dataObject">The dragged data.</param>
    /// <param name="keyState">The mouse button and modifier keys held down.</param>
    /// <param name="point">The pointer position, in screen pixels.</param>
    /// <param name="effect">The effects the source allows; set to the effect to display.</param>
    /// <returns><c>S_OK</c> to accept the drag, a failure code to refuse it outright.</returns>
    int DragEnter(IntPtr dataObject, uint keyState, NativeMethods.POINT point, ref uint effect);

    /// <summary>Called as the pointer moves while the data is over the window.</summary>
    /// <param name="keyState">The mouse button and modifier keys held down.</param>
    /// <param name="point">The pointer position, in screen pixels.</param>
    /// <param name="effect">The effects the source allows; set to the effect to display.</param>
    /// <returns><c>S_OK</c> to keep accepting the drag.</returns>
    int DragOver(uint keyState, NativeMethods.POINT point, ref uint effect);

    /// <summary>Called when the data leaves the window without being dropped.</summary>
    /// <returns><c>S_OK</c>.</returns>
    int DragLeave();

    /// <summary>Called when the data is dropped on the window.</summary>
    /// <param name="dataObject">The dropped data.</param>
    /// <param name="keyState">The mouse button and modifier keys held down.</param>
    /// <param name="point">The pointer position, in screen pixels.</param>
    /// <param name="effect">The effects the source allows; set to the effect that was performed.</param>
    /// <returns><c>S_OK</c>.</returns>
    int Drop(IntPtr dataObject, uint keyState, NativeMethods.POINT point, ref uint effect);
}

/// <summary>
/// <c>IDataObject</c>: the interface the shell's dragged data is read through.
/// </summary>
[GeneratedComInterface]
[Guid("0000010E-0000-0000-C000-000000000046")]
internal partial interface IComDataObject
{
    /// <summary>Asks the data object for one rendering of the data.</summary>
    /// <param name="format">The representation to retrieve.</param>
    /// <param name="medium">Receives the data.</param>
    /// <returns><c>S_OK</c> on success, <c>DV_E_FORMATETC</c> if the format is not available.</returns>
    [PreserveSig]
    int GetData(in FORMATETC format, out NativeMethods.STGMEDIUM medium);

    /// <summary>Retrieves the data in place; not used by this backend.</summary>
    /// <param name="format">The representation to retrieve.</param>
    /// <param name="medium">Receives the data.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int GetDataHere(in FORMATETC format, ref NativeMethods.STGMEDIUM medium);

    /// <summary>Reports whether the data object can supply a representation.</summary>
    /// <param name="format">The representation to test for.</param>
    /// <returns><c>S_OK</c> if it is available.</returns>
    [PreserveSig]
    int QueryGetData(in FORMATETC format);

    /// <summary>Asks for the canonical format of a rendering; not used by this backend.</summary>
    /// <param name="format">The representation to translate.</param>
    /// <param name="direction">Whether to read from or write to the format; unused here.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int GetCanonicalFormatEtc(in FORMATETC format, out FORMATETC canonicalFormat);

    /// <summary>Supplies one rendering of the data; not used by this backend.</summary>
    /// <param name="format">The representation being supplied.</param>
    /// <param name="medium">The data.</param>
    /// <param name="release">Whether the data object owns the medium.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int SetData(in FORMATETC format, in NativeMethods.STGMEDIUM medium, int release);

    /// <summary>Asks the data object for the formats it can supply.</summary>
    /// <param name="direction">Whether to read from or write to the enumerator; unused here.</param>
    /// <param name="formatEnumerator">Receives the enumerator, or <c>null</c> if there is none.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int EnumFormatEtc(uint direction, out IntPtr formatEnumerator);

    /// <summary>Asks to be notified when the data changes; not used by this backend.</summary>
    /// <param name="format">The representation of interest.</param>
    /// <param name="advise">Whether to start or stop listening.</param>
    /// <param name="connection">Receives the connection cookie when listening starts.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int DAdvise(in FORMATETC format, uint advise, IntPtr sink, out uint connection);

    /// <summary>Stops a notification registered with <c>DAdvise</c>.</summary>
    /// <param name="connection">The connection cookie to release.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int DUnadvise(uint connection);

    /// <summary>Asks for the enumerator of the existing notifications.</summary>
    /// <param name="notifications">Receives the enumerator, or <c>null</c> if there is none.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int EnumDAdvise(out IntPtr notifications);
}
