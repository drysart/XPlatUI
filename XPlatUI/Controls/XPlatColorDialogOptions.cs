using SkiaSharp;

namespace XPlatUI;

/// <summary>
/// The starting point of a colour dialog, passed to <see cref="XPlatColorDialog"/>.
/// </summary>
/// <remarks>
/// <para>
/// The dialog is drawn by the library from an ordinary <see cref="XPlatWindow"/> and the
/// <see cref="XPlatColorPicker"/> control, not by a platform chooser. Everything the picker can
/// edit, the dialog can edit, and nothing else: three colour channels and a hex value.
/// </para>
/// <para>
/// Alpha is not offered. The picker edits RGB only and has no alpha control, so neither does the
/// dialog, and there is no option to ask for one. The alpha the colour arrives with is preserved
/// by the picker through every edit, so <see cref="InitialColor"/> comes back with the same alpha it
/// went in with unless the caller supplied a different colour; an opaque colour stays opaque.
/// </para>
/// </remarks>
public sealed class XPlatColorDialogOptions
{
    /// <summary>
    /// Title bar text. Null uses "Choose a colour".
    /// </summary>
    public string? Title { get; set; }

    /// <summary>The colour selected when the dialog opens. Defaults to opaque black.</summary>
    public SKColor InitialColor { get; set; } = new(0, 0, 0);

    /// <summary>
    /// Which colour space the picker's component fields show. Defaults to
    /// <see cref="XPlatColorMode.Rgb"/>. This changes only how the colour is presented, never the
    /// colour itself, and the dialog does not add or remove any of the picker's modes.
    /// </summary>
    public XPlatColorMode ColorMode { get; set; } = XPlatColorMode.Rgb;
}
