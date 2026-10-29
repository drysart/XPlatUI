# XPlatUI

A tiny, AOT-friendly, cross-platform UI layer that borrows the WinForms/WPF programming
model (controls, docking, table layout, events) but draws everything with
[SkiaSharp](https://github.com/mono/SkiaSharp). The `WinFormsApp3` project is only a
**Windows test harness**; the library itself has no Windows dependency and can be hosted
by any windowing toolkit on macOS/Linux.

```
XPlatUI/                  net10.0 class library (no OS-specific code, IsAotCompatible)
  Core/                   primitives: control base class, geometry, painting, fonts, theme, clipboard
  Controls/               XPlatPanel, XPlatTableLayoutPanel, XPlatSplitContainer, XPlatLabel, XPlatRichLabel, XPlatButton, XPlatTextBox, XPlatImage
  Layout/                 row/column sizing styles
  XPlatUiHost.cs          platform-neutral host: layout, painting, input routing, focus
WinFormsApp3/             Windows test harness: raw Win32 + SkiaSharp (no WinForms, no System.Drawing)
  Native/                 NativeMethods.cs (P/Invoke), HarnessWindow.cs (host), Win32Clipboard.cs
  DemoUi.cs               the sample control tree
  app.manifest            per-monitor v2 DPI awareness
```

## Design

| Concept | WinForms equivalent | Notes |
| --- | --- | --- |
| `XPlatControl` | `Control` | Bounds, `Margin`/`Padding`, `Dock`, `Visible`/`Enabled`, colours, focus, mouse/keyboard events; any control can host children |
| `XPlatUiHost` | the window/`Form` | Owns the root control, lays out, paints into an `SKCanvas`, routes input, tracks hover/capture/focus |
| `XPlatPanel` | `Panel` | Docking layout, optional frame/card chrome |
| `XPlatTableLayoutPanel` | `TableLayoutPanel` | Absolute / Percent / AutoSize rows and columns, `CellPadding`, per-cell positions |
| `XPlatSplitContainer` | `SplitContainer` | Two panels with a draggable splitter, fixed/min/max sizes |
| `XPlatLabel` | `Label` | Nine-way alignment, optional word wrap |
| `XPlatRichLabel` | `RichTextBox`/`FlowDocument` | Wrapping runs of styled text with interactive spans and inline child controls |
| `XPlatButton` | `Button` | Primary/Secondary/Ghost styles, hover/press/disabled states, Enter/Space activation |
| `XPlatTextBox` | `TextBox` | Single-line editor: caret, selection, clipboard, `MaxLength`, `ReadOnly`, password masking, placeholder |
| `XPlatImage` | `PictureBox` | `SizeMode` (Normal/StretchImage/CenterImage/Zoom), `Load` helpers |

Everything is drawn in **logical (96 DPI) coordinates**; the host adapter is responsible
for scaling the canvas and the incoming pointer coordinates (the Win32 harness uses
`GetDpiForWindow() / 96f`).

## Using it

```csharp
var host = new XPlatUiHost { ClearColor = SKColors.White };

var root = new XPlatPanel { BackColor = XPlatTheme.Background, Padding = new XPlatThickness(16) };
var table = new XPlatTableLayoutPanel { Dock = DockStyle.Fill };
table.ColumnStyles.Add(XPlatColumnStyle.Percent(50));
table.ColumnStyles.Add(XPlatColumnStyle.Percent(50));
table.RowStyles.Add(XPlatRowStyle.AutoSize());
table.RowStyles.Add(XPlatRowStyle.Percent(100));

var button = new XPlatButton("Say hello") { Dock = DockStyle.Fill };
button.Click += (_, _) => Console.WriteLine("clicked");

table.Add(new XPlatLabel("Hello") { AutoSize = true }, 0, 0);
table.Add(button, 1, 1);
root.Controls.Add(table);

host.Root = root;
host.SetClientSize(800, 600);   // lays out the tree
host.Paint(canvas);             // any SKCanvas: window surface, bitmap, PDF, …
```

Input is plain method calls, so any backend can drive it:

```csharp
var pt = new SKPoint(x, y);                    // logical coordinates
host.ProcessMouseMove(pt);
host.ProcessMouseDown(pt, XPlatMouseButtons.Left);
host.ProcessMouseUp(pt, XPlatMouseButtons.Left);
host.ProcessKeyDown(XPlatKey.Tab);             // cycles focus
host.ProcessTextInput("a");
```

`host.Invalidated` tells the adapter to repaint; `host.CursorChanged` lets it update the
native cursor.

### Split container

`XPlatSplitContainer` hosts exactly two panels with a draggable splitter between them.
Orientation follows the WinForms convention:

| `Orientation` | Layout | `SplitterDistance` is | Cursor |
| --- | --- | --- | --- |
| `Vertical` | `Panel1` left, `Panel2` right | `Panel1` width | `SizeWE` |
| `Horizontal` | `Panel1` top, `Panel2` bottom | `Panel1` height | `SizeNS` |

`Panel1` is the sized panel; `Panel2` always fills the remaining space. The distance is
clamped to `Panel1MinSize` … `Panel1MaxSize`, further limited by `Panel2MinSize` so the
second panel never collapses.

```csharp
var split = new XPlatSplitContainer
{
    Dock = DockStyle.Fill,
    Orientation = XPlatOrientation.Vertical,   // side by side
    SplitterDistance = 240f,                   // width of Panel1
    SplitterWidth = 6f,
    Panel1MinSize = 120f,
    Panel1MaxSize = 420f,
    Panel2MinSize = 180f,
};

split.Panel1.Controls.Add(new XPlatLabel("Navigation"));
split.Panel2.Controls.Add(content);
split.SplitterMoved += (_, _) => Console.WriteLine($"panel 1 is now {split.SplitterDistance:0} px");
root.Controls.Add(split);
```

Dragging is handled internally with mouse capture, so the pointer keeps resizing the panel
even when it strays over the panels or outside the control. The splitter highlights on
hover, drag and focus, and can be moved with the arrow keys (`Left`/`Right` for a vertical
split, `Up`/`Down` for a horizontal one) when the container has keyboard focus. Set
`IsSplitterFixed` to lock it, and `SplitterColor`/`ShowSplitterGrip` to restyle it.
`SplitterMoving` fires continuously during a drag; `SplitterDistanceChanged` fires whenever
the distance changes for any reason.

### Rich label

`XPlatRichLabel` flows styled runs of text into a wrapping paragraph. Formatting lives on
`XPlatTextSpan` objects held in the `Spans` collection; a span can also carry a child control
that is embedded **inline** in the text flow (CSS `inline-block` style) and a set of mouse
events that make it behave like a hyperlink.

| Member | Behaviour |
| --- | --- |
| `Spans` | Ordered list of `XPlatTextSpan`; add/remove directly or use the helpers below |
| `AddText(text)`, `AddSpan(span)`, `AddLink(text, onClick)` | Convenience appenders; `AddLink` returns the span so you can set `Tag`, colours or extra handlers |
| `AddInlineControl(control, alignment)` | Places `control` in the flow, aligned `Top`/`Center`/`Bottom` to its text line |
| `WrapText` | Word wrap (default `true`); when `false` the label is as wide as its longest line |
| `LineSpacing`, `TextAlign` | Extra leading between lines; nine-way block + line alignment |
| `Text` | The flow as plain text; assigning replaces every span with one unstyled span |
| `SpanClick`, `SpanAt(point)` | Click notification and hit testing; span-level `Click`/`MouseDown`/`MouseUp`/`MouseMove`/`MouseEnter`/`MouseLeave` fire too |

Span formatting: `Bold`, `Italic`, `Underline`, `Strikethrough`, `ForeColor`, `HoverBackColor`,
`FontSize`, `FontFamily`, or a whole `Font` override. Decorations and the hover/press background
are drawn once per contiguous run of a span, so an underline or highlight carries across the
spaces between its words instead of restarting at each one. Spans are hit-tested per **line**, so a
link that wraps across two lines is clickable on both, and a press on one span followed by a
release over another is not treated as a click. Any span with a mouse handler becomes
interactive: it shows a hand cursor by default (override with `Cursor`) and reports
`IsMouseOver`/`IsPressed` for custom painting. A hard line break is a `\n` inside a span's
text.

```csharp
var rich = new XPlatRichLabel { Dock = DockStyle.Fill, WrapText = true };

rich.AddText("Read the ");
rich.AddSpan(new XPlatTextSpan("terms of service") { Underline = true, ForeColor = XPlatTheme.Accent });
rich.AddText(" and the ");
rich.AddSpan(new XPlatTextSpan("privacy policy") { Bold = true });
rich.AddText(".\nInline controls:");

var link = rich.AddLink("documentation", span => Open(span.Tag as string));
link.Tag = "https://example.com/docs";
link.HoverBackColor = XPlatTheme.Secondary;

rich.AddText(" ");
rich.AddInlineControl(new XPlatButton("Retry") { AutoSize = true }, XPlatInlineAlignment.Center);
rich.AddText(" ");
rich.AddInlineControl(badge, XPlatInlineAlignment.Bottom);

rich.SpanClick += (_, e) => Console.WriteLine($"clicked {e.Span.Text}");
```

Inline controls are measured like any other control (`AutoSize`, `Margin`, explicit sizes) and
are laid out by the label, so they receive input normally. A control taller than the text grows
its line and pushes the whole line down, so `Top`-aligned controls never paint outside the
label.

### Text box

`XPlatTextBox` is a single-line editor: caret, selection, clipboard, password masking,
placeholder text and horizontal scrolling are all implemented by the control itself.

| Member | Behaviour |
| --- | --- |
| `Text`, `TextChanged` | Current value. Control characters (including line breaks) are stripped so the field stays single line |
| `CaretIndex`, `SelectionStart`, `SelectionLength`, `SelectedText`, `HasSelection` | Selection state; the caret is `0 … Text.Length` |
| `Select(start, length)`, `SelectAll()`, `ClearSelection()` | Programmatic selection |
| `MaxLength` | Characters the user may type (`0` = unlimited); enforced for insertions and replacements |
| `ReadOnly` | Selection and copy still work; typing, paste, cut and delete are blocked |
| `IsPassword`, `PasswordChar` | Draws one mask glyph per character (`'\u2022'` by default) |
| `PlaceholderText` | Muted text shown while the field is empty |
| `Font` | Optional override of `XPlatTheme.DefaultFont` |
| `Copy()`, `Cut()`, `Paste()`, `InsertText(text)` | Programmatic edit operations |

Keyboard: `Left`/`Right` move by character (`Ctrl` moves by word), `Home`/`End` jump to the
edges, `Shift` extends the selection, `Backspace`/`Delete` delete the selection or one
character, and `Ctrl+A`/`C`/`X`/`V` select all, copy, cut and paste. `Enter` is *not*
consumed, so a form can still treat it as an accept gesture. The mouse sets the caret,
drags a selection and supports double click (word) and triple click (everything).

```csharp
var name = new XPlatTextBox
{
    Dock = DockStyle.Top,
    PlaceholderText = "Type your name",
    MaxLength = 40,
};
name.TextChanged += (_, _) => Console.WriteLine(name.Text);

var password = new XPlatTextBox { Dock = DockStyle.Top, IsPassword = true };
var readOnly = new XPlatTextBox { Dock = DockStyle.Top, ReadOnly = true, Text = "fixed" };
```

The library itself stays platform free, so two things remain the adapter's job:

* **Clipboard** - implement `IXPlatClipboard` (`GetText`/`SetText`, plus the optional
  `GetHtml`/`SetHtml` pair) and assign an instance to `XPlatClipboard.Current` once at startup;
  without one an in-process fallback is used. The Win32 harness installs `Win32Clipboard`
  (`Native/Win32Clipboard.cs`), which publishes a real `HTML Format` payload.
* **Caret blink** - call `host.ProcessTick(elapsed)` from a timer (the Win32 harness uses a
  250 ms `WM_TIMER`) so the caret blinks. Tests can call `host.ProcessTick(...)` directly.

### Selection and the clipboard

`Selectable` opts a control into pointer selection; `SelectionStyle` chooses between selecting
the control as a whole (`Whole`) and selecting the text inside it (`Partial`). A drag can start
in one control and finish in another, so the selection lives on the host
(`host.Selection`) rather than in a control, and `host.ClearSelection()` plus
`control.ClaimSelection()` keep exactly one selection - of any kind, including a text box's own
caret and selection - alive at a time.

When a selection is copied, every control it touches contributes its own piece in visual order:

| Member | Behaviour |
| --- | --- |
| `GetSelectionText(start, length)` | The plain-text piece for a slice; override to control what a copy pastes into a text target |
| `GetSelectionHtml(start, length)` | The HTML piece for the same slice, or `null` (default) to fall back to the escaped plain text |
| `host.CopySelection()` | Writes both flavours to `XPlatClipboard`; `host.SelectionTextSeparator` joins the pieces |
| `host.ProcessContextMenu(point)` | Builds the menu: the control's own `ContextMenu`, then `ContextMenuOpening`, then a default **Copy** item |

`XPlatRichLabel` overrides `GetSelectionHtml` so a copy keeps its formatting: `<b>`, `<i>`,
`<u>`, `<s>` and a `<span style="...">` for colour and size overrides, wrapped around the
*selected slice* of each span - a selection that starts mid-span gets markup for just those
characters. A selection spanning several controls joins their pieces with a line break.

```csharp
var html = rich.GetSelectionHtml(12, 4);   // "<b>bold</b>"
XPlatClipboard.SetHtml(rich.GetSelectionText(12, 4), html);   // both flavours at once
```

#### Built-in controls

| Control | Defaults | Notes |
| --- | --- | --- |
| `XPlatLabel`, `XPlatRichLabel` | `Selectable = false`, `Partial` | Set `Selectable`; a drag selects character by character |
| `XPlatTextBox` | `Selectable = true`, `Partial` | Its own caret and anchor answer for a drag that *starts* inside it; a drag that merely passes through it selects its text and drops the caret |
| `XPlatButton`, `XPlatImage` | `Selectable = false`, `Whole` | Set `Selectable`; a drag over the control selects it as one unit |
| `XPlatPanel` and other containers | `Selectable = false`, `Whole` | A selectable container hides its children from the selection, so leave containers off unless the container itself is what should be selected |

A text box is selectable by default and does not paint a second overlay: `GetSelectionOffset` and
`GetSelectionRects` bridge the host's selection into the same `IndexFromX`/`MeasureX` axis the
caret already uses, so the single-selection rule above still holds.

A text box also builds its own menu - **Cut**, **Copy**, **Paste** and **Select All**, with their
shortcuts and per-item `Enabled` states - so a right-click inside one always opens a menu. Its
**Copy** falls back to `host.CopySelection()` when the box has no selection of its own, which is
what makes copying a multi-control selection work no matter which control was right-clicked.

## Writing a backend

Implement the small surface that `Native/HarnessWindow.cs` implements for Win32:

1. Create a native window and an `SKSurface` (or a bitmap-backed surface) of its client size.
2. On resize: `host.SetClientSize(width / dpiScale, height / dpiScale)` and repaint.
3. On paint: `canvas.Scale(dpiScale)` then `host.Paint(canvas)`.
4. Forward pointer/key events to the `Process*` methods (divide coordinates by `dpiScale`).
5. Repaint when `Invalidated` fires.
6. Install a clipboard into `XPlatClipboard.Current` and call `host.ProcessTick(elapsed)` from
   a timer (see below).

## NativeAOT

`XPlatUI` sets `IsAotCompatible` and uses no reflection, so it publishes with NativeAOT.
Verified locally with the Win32 harness itself (no Windows Forms, no System.Drawing). The
`FolderProfile` publish profile (`WinFormsApp3/Properties/PublishProfiles/FolderProfile.pubxml`)
is preconfigured for AOT + trimming, so publishing is just:

```
dotnet publish WinFormsApp3\WinFormsApp3.csproj -p:PublishProfile=FolderProfile
```

or right-click **Publish** in Visual Studio. The result is a **single** self-contained
`WinFormsApp3.exe` (~7.8 MB, of which ~5.2 MB is the compressed Skia native binary) and an optional
`WinFormsApp3.pdb` for debugging. Nothing else is needed to run it - the AOT build opens the real
window and renders the demo.

SkiaSharp only ships a dynamic `libSkiaSharp.dll`, so a native executable cannot simply link it. When
`PublishAot` is on, `WinFormsApp3.csproj` gzips the RID-specific `libSkiaSharp.dll` and embeds it in
the assembly as the manifest resource `XPlatUI.Native.libSkiaSharp.dll.gz`; `Native/SkiaNativeLoader.cs`
extracts it on first use to `%LOCALAPPDATA%\XPlatUI\native\<version>-<size>\libSkiaSharp.dll` (cached,
so later runs just load it) and hands it to the runtime before SkiaSharp's first P/Invoke. A regular
(non-AOT) build is untouched: the native library is resolved the usual way (from the package cache, or
copied next to the executable for a RID-specific build) and the loader does nothing. If you would
rather ship a loose `libSkiaSharp.dll`, set `EmbedSkiaSharpNative` to `false` - the publish then emits
the executable and the DLL side by side again.

NativeAOT needs the MSVC linker (`link.exe`). The profile switches to
`IlcUseEnvironmentalTools` automatically once a VC environment is initialized, so the most
reliable way to publish is from a *Developer PowerShell/Command Prompt for VS* (or a CI step that
calls `vcvars64.bat` first):

```
call "C:\Program Files\Microsoft Visual Studio\18\Enterprise\VC\Auxiliary\Build\vcvars64.bat"
dotnet publish WinFormsApp3\WinFormsApp3.csproj -p:PublishProfile=FolderProfile
```

From a plain shell the SDK locates the toolset itself with `vswhere.exe`; if that fails (for
example a `vswhere.exe` error leaking into the link step), publish from a Developer prompt instead.

## Running the harness

```
dotnet run --project WinFormsApp3
```

`HarnessWindow` (`Native/HarnessWindow.cs`) creates the window with raw `user32` P/Invoke, owns a
32-bit DIB section that Skia draws into, blits it on `WM_PAINT`, and forwards mouse/keyboard
messages to the `XPlatUiHost`. `Program.cs` wires up `DemoUi` and runs the message pump.

To render the same sample UI headlessly (useful for visual checks in CI):

```
WinFormsApp3.exe --render out.png 1280 760
```

The sample UI is laid out for a 1280 x 760 client area. The host lays out in *logical* (96 DPI)
points and the harness scales the Skia canvas by `GetDpiForWindow(hwnd) / 96`, sizing its client
area to `1280 x 760` multiplied by that scale (clamped to the monitor work area) so the demo is
never cropped on a high-DPI monitor. The **Selection and clipboard** card is the one to try:
drag down from its label, over the whole-control button and into the text box, then right-click
the selection.
