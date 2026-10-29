using SkiaSharp;
using XPlatUI;

namespace WinFormsApp3;

/// <summary>
/// Shows XPlat context menus as native Win32 popup menus. This is the whole of the Windows side
/// of the feature: the UI library hands over an <see cref="XPlatContextMenu"/> and never learns
/// how it was drawn, so a macOS or Linux host replaces this class with an equivalent that calls
/// NSMenu or GTK and changes nothing else.
/// </summary>
internal sealed class Win32ContextMenuPresenter : IXPlatContextMenuPresenter
{
    private readonly IntPtr _hwnd;
    private readonly Func<SKPoint, NativeMethods.POINT> _toScreen;

    /// <param name="hwnd">Window that owns the menu, which also receives input while it is up.</param>
    /// <param name="toScreen">
    /// Maps a logical client point to screen pixels. Only the platform layer knows where the
    /// surface sits on screen and how it is scaled, so the presenter is handed the mapping.
    /// </param>
    public Win32ContextMenuPresenter(IntPtr hwnd, Func<SKPoint, NativeMethods.POINT> toScreen)
    {
        _hwnd = hwnd;
        _toScreen = toScreen;
    }

    public void Show(XPlatContextMenuRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var menu = BuildMenu(request.Menu);
        if (menu.Handle == IntPtr.Zero)
        {
            return;
        }

        var screen = _toScreen(request.Location);

        // Ownership matters for dismissal: a menu whose owner is not foreground keeps its modal
        // loop alive after the user clicks elsewhere, which looks like a hung application.
        NativeMethods.SetForegroundWindow(_hwnd);

        var command = NativeMethods.TrackPopupMenuEx(
            menu.Handle,
            NativeMethods.TpmLeftAlign | NativeMethods.TpmTopAlign | NativeMethods.TpmRightButton | NativeMethods.TpmReturnCmd,
            screen.X,
            screen.Y,
            _hwnd,
            IntPtr.Zero);

        // Documented workaround for a menu that stays on screen until the next input arrives.
        NativeMethods.PostMessageW(_hwnd, NativeMethods.WmNull, IntPtr.Zero, IntPtr.Zero);

        menu.Invoke(command);
    }

    /// <summary>
    /// Turns an <see cref="XPlatContextMenu"/> into an <c>HMENU</c>, giving every pickable item a
    /// command id so the one the user picks can be mapped back to its command. The caller owns
    /// the result and must dispose it.
    /// </summary>
    internal static BuiltMenu BuildMenu(XPlatContextMenu menu)
    {
        ArgumentNullException.ThrowIfNull(menu);

        var handle = NativeMethods.CreatePopupMenu();
        if (handle == IntPtr.Zero)
        {
            return new BuiltMenu(IntPtr.Zero);
        }

        var built = new BuiltMenu(handle);

        foreach (var item in TrimSeparators(menu.Items))
        {
            if (item.IsSeparator)
            {
                NativeMethods.AppendMenuW(handle, NativeMethods.MfSeparator, IntPtr.Zero, null);
                continue;
            }

            var id = built.Add(item);
            NativeMethods.AppendMenuW(handle, FlagsFor(item), new IntPtr(id), TextFor(item));
        }

        return built;
    }

    /// <summary>
    /// Drops leading, trailing and doubled separators. A native menu draws a stray line for each
    /// of them, and an application that appends groups conditionally produces them easily.
    /// </summary>
    private static List<XPlatMenuItem> TrimSeparators(IReadOnlyList<XPlatMenuItem> items)
    {
        var result = new List<XPlatMenuItem>(items.Count);

        foreach (var item in items)
        {
            if (!item.IsSeparator)
            {
                result.Add(item);
                continue;
            }

            if (result.Count > 0 && !result[^1].IsSeparator)
            {
                result.Add(item);
            }
        }

        while (result.Count > 0 && result[^1].IsSeparator)
        {
            result.RemoveAt(result.Count - 1);
        }

        return result;
    }

    private static uint FlagsFor(XPlatMenuItem item)
    {
        var flags = NativeMethods.MfString;

        if (!item.Enabled)
        {
            flags |= NativeMethods.MfGrayed;
        }

        if (item.Checked)
        {
            flags |= NativeMethods.MfChecked;
        }

        if (item.IsDefault)
        {
            flags |= NativeMethods.MfDefault;
        }

        return flags;
    }

    private static string TextFor(XPlatMenuItem item)
    {
        // '&' marks a mnemonic in a Win32 menu string, so a literal one has to be doubled. The
        // shortcut hint is right aligned because the label and hint are tab separated.
        var text = item.Text.Replace("&", "&&", StringComparison.Ordinal);
        return string.IsNullOrEmpty(item.ShortcutText) ? text : $"{text}\t{item.ShortcutText}";
    }

    /// <summary>A popup menu plus the mapping from Win32 command ids back to menu items.</summary>
    internal sealed class BuiltMenu : IDisposable
    {
        private readonly Dictionary<int, XPlatMenuItem> _items = new();

        internal BuiltMenu(IntPtr handle) => Handle = handle;

        public IntPtr Handle { get; private set; }

        /// <summary>How many pickable items the menu holds.</summary>
        public int Count => _items.Count;

        /// <summary>Registers <paramref name="item"/> and returns the command id to give it.</summary>
        public int Add(XPlatMenuItem item)
        {
            var id = _items.Count + 1;
            _items[id] = item;
            return id;
        }

        /// <summary>The command id of the item whose text is <paramref name="text"/>, or 0.</summary>
        public int IdOf(string text)
        {
            foreach (var pair in _items)
            {
                if (string.Equals(pair.Value.Text, text, StringComparison.Ordinal))
                {
                    return pair.Key;
                }
            }

            return 0;
        }

        /// <summary>Runs the command of the item with <paramref name="command"/>. 0 means cancelled.</summary>
        public bool Invoke(int command)
        {
            if (command <= 0 || !_items.TryGetValue(command, out var item))
            {
                return false;
            }

            return item.Invoke();
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                NativeMethods.DestroyMenu(Handle);
                Handle = IntPtr.Zero;
            }
        }
    }
}
