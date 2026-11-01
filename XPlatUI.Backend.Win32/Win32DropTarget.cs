using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace XPlatUI.Backend.Win32;

/// <summary>
/// The OLE drop target of one Win32 window: the COM object the shell calls into while something is
/// dragged over the window, translated into the control tree's drag and drop events.
/// </summary>
/// <remarks>
/// <para>
/// The shell owns the drag, not this process, so there is no message loop involvement: everything
/// arrives as COM calls on the UI thread, in the window's single-threaded apartment. That is why
/// every entry point has to swallow its own exceptions rather than let them unwind into native
/// frames.
/// </para>
/// <para>
/// The COM object is laid out by hand instead of being handed to <c>ComWrappers</c>: a small block
/// of unmanaged memory holding the vtable pointer, the reference count and a handle back to this
/// instance, fronted by a vtable of <see cref="UnmanagedCallersOnlyAttribute"/> entry points. Those
/// entry points are real native code addresses, which is what Control Flow Guard demands of an
/// indirect call made by the shell, and building the vtable directly keeps the whole thing working
/// under NativeAOT.
/// </para>
/// </remarks>
internal sealed unsafe class Win32DropTarget : IDropTarget
{
    private const int VtableOffset = 0;
    private const int RefCountOffset = 1;
    private const int HandleOffset = 2;
    private const int ObjectSize = 3;
    private const int VtableSlots = 7;

    private static readonly object VtableGate = new();

    private static nint* s_vtable;

    private readonly Win32WindowBackend _window;

    private nint* _object;
    private XPlatDataObject? _data;

    /// <summary>Creates the drop target of a window.</summary>
    /// <param name="window">The window the target belongs to.</param>
    public Win32DropTarget(Win32WindowBackend window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _window = window;
    }

    /// <summary>Registers the target with the window, so the shell starts sending it drags.</summary>
    /// <param name="hwnd">The native handle of the window.</param>
    /// <returns><see langword="true"/> when the window now accepts drops.</returns>
    public bool Register(IntPtr hwnd)
    {
        if (_object is not null)
        {
            return true;
        }

        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        // Registration fails with CO_E_NOTINITIALIZED unless OLE has put this thread into an
        // apartment. WinForms only does that for its own clipboard and drag support, so a window
        // created by this backend has to ask for it. The call is idempotent per thread and reports
        // S_FALSE once the apartment exists.
        NativeMethods.OleInitialize(IntPtr.Zero);

        var instance = GCHandle.Alloc(this, GCHandleType.Normal);
        var comObject = (nint*)NativeMemory.Alloc(ObjectSize, (nuint)sizeof(nint));
        comObject[VtableOffset] = (nint)Vtable();
        comObject[RefCountOffset] = 1;
        comObject[HandleOffset] = GCHandle.ToIntPtr(instance);
        _object = comObject;

        var registerHr = NativeMethods.RegisterDragDrop(hwnd, (IntPtr)comObject);
        if (registerHr == NativeMethods.SOk)
        {
            return true;
        }

        // Registration fails when the thread is not in a single-threaded apartment, or when the
        // window already has a target. Either way the window simply never sees a drop, which is a
        // degraded but working application.
        ReleaseObject();
        return false;
    }

    /// <summary>Unregisters the target, which must happen before the window is destroyed.</summary>
    /// <param name="hwnd">The native handle of the window.</param>
    public void Revoke(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero)
        {
            NativeMethods.RevokeDragDrop(hwnd);
        }

        ReleaseObject();
        _data = null;
    }

    /// <summary>Drops the reference this object holds on its own COM object.</summary>
    private void ReleaseObject()
    {
        if (_object is null)
        {
            return;
        }

        var comObject = _object;
        _object = null;
        ReleaseCore(comObject);
    }

    /// <inheritdoc />
    public int DragEnter(IntPtr dataObject, uint keyState, NativeMethods.POINT point, ref uint effect)
    {
        try
        {
            _data = Read(dataObject);
            effect = _data is null
                ? NativeMethods.DropEffectNone
                : (uint)_window.OnDragEnter(point, _data, (XPlatDragDropEffects)effect, ModifiersOf(keyState));
        }
        catch (Exception ex)
        {
            XPlatApplication.HandleNativeBoundary(ex);
            _data = null;
            effect = NativeMethods.DropEffectNone;
        }

        return NativeMethods.SOk;
    }

    /// <inheritdoc />
    public int DragOver(uint keyState, NativeMethods.POINT point, ref uint effect)
    {
        try
        {
            if (_data is null)
            {
                effect = NativeMethods.DropEffectNone;
            }
            else
            {
                effect = (uint)_window.OnDragOver(point, _data, (XPlatDragDropEffects)effect, ModifiersOf(keyState));
            }
        }
        catch (Exception ex)
        {
            XPlatApplication.HandleNativeBoundary(ex);
            effect = NativeMethods.DropEffectNone;
        }

        return NativeMethods.SOk;
    }

    /// <inheritdoc />
    public int DragLeave()
    {
        try
        {
            _window.OnDragLeave();
        }
        catch (Exception ex)
        {
            XPlatApplication.HandleNativeBoundary(ex);
        }

        _data = null;
        return NativeMethods.SOk;
    }

    /// <inheritdoc />
    public int Drop(IntPtr dataObject, uint keyState, NativeMethods.POINT point, ref uint effect)
    {
        try
        {
            // The data object handed to Drop is the authority: read it again rather than trusting
            // whatever was cached at DragEnter, which a source is allowed to have replaced.
            var data = Read(dataObject) ?? _data;
            effect = data is null
                ? NativeMethods.DropEffectNone
                : (uint)_window.OnDrop(point, data, (XPlatDragDropEffects)effect, ModifiersOf(keyState));
        }
        catch (Exception ex)
        {
            XPlatApplication.HandleNativeBoundary(ex);
            effect = NativeMethods.DropEffectNone;
        }

        _data = null;
        return NativeMethods.SOk;
    }

    /// <summary>Returns the vtable shared by every drop target in the process, building it once.</summary>
    /// <remarks>
    /// The slot order is <c>IUnknown</c> followed by the four <c>IDropTarget</c> methods, which is
    /// the order the shell dispatches by. The table lives in unmanaged memory for the lifetime of
    /// the process, so the entries never move.
    /// </remarks>
    private static nint* Vtable()
    {
        if (s_vtable is not null)
        {
            return s_vtable;
        }

        lock (VtableGate)
        {
            if (s_vtable is null)
            {
                var table = (nint*)NativeMemory.Alloc(VtableSlots, (nuint)sizeof(nint));
                table[0] = (nint)(delegate* unmanaged[MemberFunction]<void*, Guid*, void**, int>)&QueryInterface;
                table[1] = (nint)(delegate* unmanaged[MemberFunction]<void*, uint>)&AddRef;
                table[2] = (nint)(delegate* unmanaged[MemberFunction]<void*, uint>)&Release;
                table[3] = (nint)(delegate* unmanaged[MemberFunction]<void*, nint, uint, NativeMethods.POINT, uint*, int>)&DragEnterCallback;
                table[4] = (nint)(delegate* unmanaged[MemberFunction]<void*, uint, NativeMethods.POINT, uint*, int>)&DragOverCallback;
                table[5] = (nint)(delegate* unmanaged[MemberFunction]<void*, int>)&DragLeaveCallback;
                table[6] = (nint)(delegate* unmanaged[MemberFunction]<void*, nint, uint, NativeMethods.POINT, uint*, int>)&DropCallback;
                s_vtable = table;
            }
        }

        return s_vtable;
    }

    /// <summary>Recovers the managed target a COM call was made on.</summary>
    private static Win32DropTarget? FromSelf(void* self) =>
        GCHandle.FromIntPtr(((nint*)self)[HandleOffset]).Target as Win32DropTarget;

    private static uint AddRefCore(void* self)
    {
        var comObject = (nint*)self;
        comObject[RefCountOffset]++;
        return (uint)comObject[RefCountOffset];
    }

    private static uint ReleaseCore(void* self)
    {
        var comObject = (nint*)self;
        comObject[RefCountOffset]--;
        var count = (uint)comObject[RefCountOffset];

        if (count == 0)
        {
            GCHandle.FromIntPtr(comObject[HandleOffset]).Free();
            NativeMemory.Free(comObject);
        }

        return count;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvMemberFunction)])]
    private static int QueryInterface(void* self, Guid* interfaceId, void** result)
    {
        if (result is null)
        {
            return NativeMethods.EPointer;
        }

        *result = null;

        if (interfaceId is null)
        {
            return NativeMethods.EInvalidArgument;
        }

        if (*interfaceId != Win32DropInterfaces.DropTargetId && *interfaceId != Win32DropInterfaces.UnknownId)
        {
            return NativeMethods.ENoInterface;
        }

        *result = self;
        AddRefCore(self);
        return NativeMethods.SOk;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvMemberFunction)])]
    private static uint AddRef(void* self) => AddRefCore(self);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvMemberFunction)])]
    private static uint Release(void* self) => ReleaseCore(self);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvMemberFunction)])]
    private static int DragEnterCallback(
        void* self,
        nint dataObject,
        uint keyState,
        NativeMethods.POINT point,
        uint* effect)
    {
        var target = FromSelf(self);
        if (target is null)
        {
            return NativeMethods.EPointer;
        }

        var requested = effect is null ? 0u : *effect;
        var result = target.DragEnter(dataObject, keyState, point, ref requested);

        if (effect is not null)
        {
            *effect = requested;
        }

        return result;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvMemberFunction)])]
    private static int DragOverCallback(
        void* self,
        uint keyState,
        NativeMethods.POINT point,
        uint* effect)
    {
        var target = FromSelf(self);
        if (target is null)
        {
            return NativeMethods.EPointer;
        }

        var requested = effect is null ? 0u : *effect;
        var result = target.DragOver(keyState, point, ref requested);

        if (effect is not null)
        {
            *effect = requested;
        }

        return result;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvMemberFunction)])]
    private static int DragLeaveCallback(void* self)
    {
        var target = FromSelf(self);
        return target is null ? NativeMethods.EPointer : target.DragLeave();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvMemberFunction)])]
    private static int DropCallback(
        void* self,
        nint dataObject,
        uint keyState,
        NativeMethods.POINT point,
        uint* effect)
    {
        var target = FromSelf(self);
        if (target is null)
        {
            return NativeMethods.EPointer;
        }

        var requested = effect is null ? 0u : *effect;
        var result = target.Drop(dataObject, keyState, point, ref requested);

        if (effect is not null)
        {
            *effect = requested;
        }

        return result;
    }

    /// <summary>Maps the <c>MK_*</c> bits of a drag notification onto the library's modifiers.</summary>
    private static XPlatModifiers ModifiersOf(uint keyState)
    {
        var modifiers = XPlatModifiers.None;

        if ((keyState & NativeMethods.MkShift) != 0)
        {
            modifiers |= XPlatModifiers.Shift;
        }

        if ((keyState & NativeMethods.MkControl) != 0)
        {
            modifiers |= XPlatModifiers.Control;
        }

        if ((keyState & NativeMethods.MkAlt) != 0)
        {
            modifiers |= XPlatModifiers.Alt;
        }

        return modifiers;
    }

    /// <summary>Reads everything the library understands out of a dragged data object.</summary>
    private static unsafe XPlatDataObject? Read(IntPtr dataObject)
    {
        if (dataObject == IntPtr.Zero)
        {
            return null;
        }

        var com = ComInterfaceMarshaller<IComDataObject>.ConvertToManaged((void*)dataObject);
        if (com is null)
        {
            return null;
        }

        XPlatDataObject data = new();

        try
        {
            var files = ReadFiles(com);
            if (files.Length > 0)
            {
                data.SetFiles(files);
            }

            if (ReadText(com) is { Length: > 0 } text)
            {
                data.SetText(text);
            }
        }
        finally
        {
            // The wrapper is the only owner of the reference taken by ConvertToManaged. The
            // generated wrappers do not support FinalReleaseComObject, so it is disposed instead.
            (com as IDisposable)?.Dispose();
        }

        // An empty object is still a drag: the source may be offering a format the library has no
        // model for, and the handlers are the ones that decide whether that is useful.
        return data;
    }

    /// <summary>Reads the dropped files, if the source offers any.</summary>
    private static string[] ReadFiles(IComDataObject com)
    {
        var format = FormatOf(NativeMethods.CfHDrop);

        if (com.GetData(format, out var medium) != NativeMethods.SOk)
        {
            return [];
        }

        try
        {
            if (medium.Tymed != Win32DropInterfaces.TymedHGlobal || medium.Handle == IntPtr.Zero)
            {
                return [];
            }

            var count = NativeMethods.DragQueryFileW(medium.Handle, Win32DropInterfaces.DragQueryFileCount, null, 0);
            if (count == 0)
            {
                return [];
            }

            var files = new string[count];
            var found = 0;

            for (uint index = 0; index < count; index++)
            {
                var length = NativeMethods.DragQueryFileW(medium.Handle, index, null, 0);
                if (length == 0)
                {
                    continue;
                }

                var buffer = new char[length + 1];
                if (NativeMethods.DragQueryFileW(medium.Handle, index, buffer, (uint)buffer.Length) == 0)
                {
                    continue;
                }

                files[found++] = new string(buffer, 0, (int)length);
            }

            return found == count ? files : files[..found];
        }
        finally
        {
            NativeMethods.ReleaseStgMedium(ref medium);
        }
    }

    /// <summary>Reads the dragged text, if the source offers any.</summary>
    private static string? ReadText(IComDataObject com)
    {
        var format = FormatOf(NativeMethods.CfUnicodeText);

        if (com.GetData(format, out var medium) != NativeMethods.SOk)
        {
            return null;
        }

        try
        {
            if (medium.Tymed != Win32DropInterfaces.TymedHGlobal || medium.Handle == IntPtr.Zero)
            {
                return null;
            }

            var pointer = NativeMethods.GlobalLock(medium.Handle);
            if (pointer == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                NativeMethods.GlobalUnlock(medium.Handle);
            }
        }
        finally
        {
            NativeMethods.ReleaseStgMedium(ref medium);
        }
    }

    /// <summary>Builds the request for one clipboard format, rendered as global memory.</summary>
    private static FORMATETC FormatOf(uint clipboardFormat) =>
        new()
        {
            cfFormat = (ushort)clipboardFormat,
            dwAspect = Win32DropInterfaces.DvAspectContent,
            lindex = -1,
            tymed = Win32DropInterfaces.TymedHGlobal,
        };
}
