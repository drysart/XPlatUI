# Selection and context menus - implementation plan

Cross-cutting "selectability" for XPlatUI: controls opt in to pointer-driven selection, a
selection can span several sibling controls, the selected content can be copied to the
clipboard through a context menu, and context menus are presented by the platform layer
(so they can be native) through a small adapter interface.

Status of this document: living plan. Each step is independently buildable and verifiable,
so work can stop after any step with the tree in a working state.

## Design decisions

| Topic | Decision | Rationale |
| --- | --- | --- |
| Where selection lives | One `XPlatSelection` owned by `XPlatUiHost` | A selection can span controls, so it cannot live in a control. The host already owns capture, focus and input routing. |
| Anchor model | `XPlatSelectionPosition` = `(XPlatControl Control, float Offset)` pair; an anchor and a focus | Offsets are per-control "selection units", so one type covers whole controls (`0..1`) and text (`0..Length`). Fractional offsets keep the whole model on one axis. |
| Unit inclusion | A partial unit counts when the selection covers its middle; a whole control counts as soon as the selection reaches into it from either side, but not when the selection merely touches its edge | Pressing a whole control maps to `0.5`, so it selects immediately and drags in either direction; a press that does not move selects nothing on text. Overlap (rather than covering the middle) is what keeps a whole control selected while the pointer is still inside it, even when the drag runs backwards. |
| Off-content drag | A pointer outside every selectable control clamps to the nearest selectable control, ties keeping the control that already holds the moving end | Otherwise a drag that leaves the content freezes wherever it last was over a control, so running off the end of the last label never selects up to that end. |
| Ordering | Depth-first walk of the control tree, z-order aware | Gives a total order for normalising anchor/focus into ranges without any geometry maths. |
| Control contract | Virtual members on `XPlatControl` (no interface) | Every control derives from `XPlatControl`; avoids casts and interface dispatch, and is AOT friendly. |
| Selection painting | Uniform: `XPlatControl.Render` fills the rects returned by `GetSelectionRects` between the background and the content | One look everywhere; controls only supply geometry. |
| Drag threshold | A press becomes a selection after ~3 logical pixels of movement | Plain clicks still reach links, buttons and text boxes inside selectable controls. |
| Text join | Per-control contributions joined with `XPlatUiHost.SelectionTextSeparator`, a newline by default | "end of label 1\nwhole label 2\nstart of label 3". |
| Keyboard | None. Selection is pointer-driven only | Explicit scope decision: there is no caret to move. |
| Single selection | Only one selection may exist at a time. `XPlatControl.ClaimSelection()` is the declaration point: the host clears its control-level selection and tells every other control to drop its internal one before the caller proceeds | A caret and a control-level selection must never be visible together, and no control should have to know about any other control's selection state. |
| Context menus | Native only; a no-op fallback when the platform installs no presenter | Keeps the library free of platform code and adds no menu renderer to the binary. |
| Menu trigger | The adapter calls `XPlatUiHost.ProcessContextMenu` from the platform's context menu message, not from a right button release | One code path covers the mouse and the keyboard menu key, and a right click never opens two menus. |
| Menu lookup | Hit control's `ContextMenu`, inherited from the nearest ancestor, then the `ContextMenuOpening` event, then a Copy menu built from the selection | A container can give every child the same menu, an application can build menus from state, and a selection is always copyable. |

## Steps

| # | Step | Scope | Done when |
| --- | --- | --- | --- |
| 1 | Selection primitives and control contract | `XPlatSelectionStyle`, `XPlatSelectionPosition`, `XPlatSelectionRange`; `Selectable`, `SelectionStyle`, `SelectionUnitCount`, `GetSelectionOffset`, `GetSelectionRects`, `GetSelectionText`, `SelectionRange` on `XPlatControl` | A selectable button reports one unit, its text, and paints a highlight |
| 2 | Host selection state and drag selection | `XPlatSelection` (anchor/focus, ranges, `Changed`), press/drag/release handling, drag threshold, mouse capture reuse | Dragging inside a selectable control highlights it and `Selection.Ranges` is correct |
| 3 | Rich label offsets and partial selection | `Fragment.Start` (each fragment's offset in `Text`, so `Text.Length` is the unit count), nearest-offset hit testing, per-line selection rects, text slices | Positions land on character boundaries, rects are per line, slices match the source |
| 3b | Label offsets and partial selection | `XPlatTextRenderer.BuildTextLines`/`XPlatTextLine` (line text is always a slice of the source), `GetBlockTop`/`GetLineLeft` shared with `Draw`, `XPlatLabel` overrides | Labels select character by character, including wrapped and multi-line text |
| 3c | One selection at a time | `XPlatUiHost.ClaimSelection`/`ClearSelection`/`SelectionOwner`, `XPlatControl.ClaimSelection`, `OnSelectionCleared` hook, `XPlatTextBox` claims on caret activity and collapses on release, focus loss ends an internal selection | A caret, a text selection and a control-level selection can never coexist |
| 4 | Cross-control selection | Visual-order clamping while dragging outside a control; boundary rules for whole controls | Dragging from the middle of label 1 to the middle of label 3 selects partial/whole/partial |
| 5 | Clipboard assembly | `XPlatSelection.GetText()`, `XPlatUiHost.CopySelection()` through `XPlatClipboard` | The clipboard receives the expected multi-control string |
| 6 | Context menu model and presenter API | `XPlatContextMenu`, `XPlatMenuItem`, `IXPlatContextMenuPresenter`, `XPlatContextMenuPresenter.Current`, `XPlatControl.ContextMenu`, default Copy menu on right-click | A fake presenter captures the menu and Copy fills the clipboard |
| 7 | Win32 native menu in the harness | `TrackPopupMenuEx` presenter, `WM_CONTEXTMENU` routing, install in `Program.cs` | Right-clicking a selection shows a native menu that copies |
| 8 | Built-in controls adopt selection | `XPlatTextBox` (partial, bridged to its caret/anchor), `XPlatButton`/`XPlatImage` (whole), containers opt-in. `XPlatLabel` is done (step 3b) | Mixed partial/whole selections paint and copy correctly |
| 9 | Demo, docs and AOT verification | Selection demo card, README sections, headless assertions, single-file AOT publish | Published exe renders and copies as expected |

## Verification approach

Every step is checked with a throwaway console app that references `XPlatUI`, builds an
`XPlatUiHost`, drives `ProcessMouseDown`/`ProcessMouseMove`/`ProcessMouseUp` with logical
points, paints into an `SKBitmap` and inspects pixels and `Selection.Ranges`. The demo is
re-rendered with `XPlatUIDemoApp.exe --render <png> <w> <h>` and, from step 7 on, the AOT
single-file publish is repeated.

## Progress

| Step | State | Notes |
| --- | --- | --- |
| 1 | Done | Control contract lives on `XPlatControl`; `Render` fills the selection between background and content, so every control gets the same look for free. |
| 2 | Done | `XPlatSelection` normalises anchor/focus into per-control ranges over one global offset axis; `XPlatUiHost` starts a selection on left press and extends it once the pointer passes `SelectionDragThreshold` (3 px). Verified with 28 headless checks (whole, partial, multi-control both directions, threshold, ancestor mapping, painted highlight) plus an unchanged harness render. |
| 3 | Done | `Fragment.Start` gives every word, space, break and wrapped-word piece its offset in `Text`; points snap to the nearest line and then the nearest character boundary; rects merge per line and per inline control. Verified with 45 headless checks (offsets, slices, merged rects, per-line rects, inline-control coverage, wrapped words, painted highlight, cross-label drag). |
| 3b | Done | `XPlatTextRenderer.BuildTextLines` returns `XPlatTextLine` (start, length, text) whose `Text` is always a slice of the source, so character offsets stay meaningful across wrapping, hard breaks and line breaks; `GetBlockTop`/`GetLineLeft` are shared with `Draw`. `XPlatLabel` overrides `GetSelectionOffset`/`GetSelectionRects` and defaults to `Partial`. Verified with 42 headless checks (character boundaries, slices, per-line rects, wrapped and hard-broken lines, painted tint, cross-label drag in both directions) plus an unchanged harness render. |
| 3c | Done | `XPlatUiHost.SelectionOwner`/`ClaimSelection`/`ClearSelection` arbitrate a single selection; `XPlatControl.ClaimSelection()` is the declaration point and `OnSelectionCleared` the hook. `XPlatTextBox` claims on caret activity and collapses its anchor/caret when another control claims; losing focus also ends its internal selection. Verified with 41 headless checks (click and typing claims, collapse on external claim, caret pixels appearing and disappearing, text box to text box, focus loss, `ClearSelection`, whole controls) plus an unchanged harness render. |
| 4 | Done | `XPlatUiHost.ResolveSelectionPosition` maps a pointer location into the selection: over a selectable control it maps straight in, and outside every selectable control it clamps to the nearest one (`FindNearestSelectable`, ties keep the current end) so dragging off the content still selects up to that end. Whole controls now join a selection by overlap instead of by covering their midpoint, so dragging backwards out of a button keeps it selected. Verified with 20 headless checks (past-the-end, single jump, above-the-start, beside-the-text, outside-the-surface, whole-control entry/exit/boundary) plus an unchanged harness render. |
| 5 | Done | `XPlatSelection.GetText` assembles the per-control contributions in visual order and `XPlatUiHost.CopySelection` writes them through `XPlatClipboard`; `SelectionTextSeparator` (newline by default) is the join. Verified with 14 headless checks (whole and partial pieces, custom separator, no selection, installed clipboard receives the write). |
| 6 | Done | `XPlatContextMenu`/`XPlatMenuItem` hold the items, `IXPlatContextMenuPresenter` is the platform hook and `XPlatContextMenuPresenter.Current` the process-wide slot; `XPlatControl.ContextMenu` (inherited from the nearest ancestor) and the overridable `BuildContextMenu()` supply the menu, `XPlatUiHost.ContextMenuOpening` lets an application build one on the fly, and a selection with nothing else to offer gets a Copy menu. Verified with 58 headless checks (item invoke/enabled/separator, no presenter, default Copy menu, control and inherited menus, event precedence, off-content right-click, cleared selection, fluent building) plus an unchanged harness render. |
| 7 | Done | The harness turns an `XPlatContextMenu` into a real Win32 popup menu: `Win32ContextMenuPresenter` maps each item to a 1-based command id (`AppendMenuW` with `MF_STRING`, plus `MF_GRAYED`/`MF_CHECKED`/`MF_DEFAULT`), trims leading, trailing and doubled separators, escapes `&`, appends `ShortcutText` after a tab, converts the logical host location to screen pixels, and runs `TrackPopupMenuEx` with `TPM_RETURNCMD` so the picked command is known immediately. `HarnessWindow` routes `WM_CONTEXTMENU` into `XPlatUiHost.ProcessContextMenu`, falling back to `DefWindowProc` when no menu opens, and `Program.cs` installs the presenter once the window exists. Verified with 41 checks in a throwaway app (native item text and state read back with `GetMenuStringW`/`GetMenuState`, separator trimming, id mapping, invoke/cancel/disabled, empty and separator-only menus, ampersand escaping, flag assembly, logical/screen round trip on a real window) plus the whole path end to end in the demo tree: a real drag selects text, the host hands a presenter a Copy menu, invoking it puts exactly the selection text on the clipboard, and a live menu keeps the calling thread inside TrackPopupMenuEx. The harness render is unchanged. |
| 8 | Done | `XPlatTextBox` is the first built-in control that is selectable by default: `Selectable = true` and `SelectionStyle = Partial` in its constructor. Its control-level selection is not a second overlay — `GetSelectionOffset`/`GetSelectionRects` bridge into the same `IndexFromX`/`MeasureX` axis the caret and anchor already use, so the step-3c invariant holds: a drag that starts inside the box uses the box's own caret, and a drag that passes through it makes `OnSelectionCleared` drop the caret. The box also builds its own editing menu (Cut, Copy, Paste, Select All, with `ShortcutText` and per-item `Enabled` states), which is what makes a right-click in a text box open a native menu. `XPlatButton`/`XPlatImage` needed no code: `SelectionStyle` already defaults to `Whole`, so they join a selection as a unit as soon as an application sets `Selectable`, and containers stay opt-in because a selectable container hides its children from the selection. Verified with 89 headless checks (contract, geometry, cross-control drags, caret arbitration, menus, whole controls, containers) plus the real harness: a right-click over the name and read-only boxes opens a native menu, a drag over the rich label followed by a right-click opens the copy menu, and a right-click on empty chrome opens nothing. The harness render is unchanged. |
| 9 | Done | The harness grows a **Selection and clipboard** card as a fifth column of the card table (the design width goes from 1200 to 1280 so the cards keep their width): a selectable wrapping label, a whole-control selectable button and a selectable text box stacked along one drag path, with a hint underneath, so a single gesture produces a partial, a whole and a partial range. Verified with 41 headless checks over the real demo tree - the four controls exist, stack in order and nothing overflows its parent; a drag from the label across the button and into the box produces exactly three ranges whose assembled text is the label, the button and the box slice in order; the label's right-click menu is a one-item Copy that writes exactly that text to the clipboard; the box's menu is Cut/Copy/Paste/Select All and its Copy falls back to the host selection; a button right-click with nothing selected opens no menu; and the painted tint appears on the three participating controls only and disappears on `ClearSelection`. `README.md` documents the per-control defaults (`XPlatTextBox` on and partial, labels off and partial, buttons and images off and whole, containers opt-in) and the editing menu. The NativeAOT single-file publish still produces one 7,907,328 byte exe whose headless render is byte-for-byte identical to the JIT build's. |

Step 3c keeps the selection exclusive. `XPlatUiHost` tracks which control, if any, owns a
selection of its own (`SelectionOwner`); `XPlatControl.ClaimSelection()` is the generic way to
declare "I am starting a selection", which clears the host's selection and notifies the previous
owner through the `OnSelectionCleared` hook so it can drop its own state. `XPlatTextBox` claims
whenever its caret becomes active (focus, mouse press, `Select`, caret moves, edits) and
collapses its `anchor`/`caret` when someone else claims, so a caret never lingers next to a
label selection. Losing focus also ends an internal selection, which is what stops two text
boxes from showing highlights at once. Verified with 41 headless checks (claim on click, claim
on typing, collapse on external claim, caret pixels appearing and disappearing, text box to text
box, focus loss, `ClearSelection`, whole controls) plus an unchanged harness render.

Step 3b was pulled forward from step 8 because the label is the simplest partial control and
because the renderer change it needed (line layout that remembers source offsets) is shared:
`XPlatTextRenderer.BuildTextLines` now returns `XPlatTextLine` values whose `Text` is always a
slice of the source string, and `GetBlockTop`/`GetLineLeft` hold the alignment maths that both
`Draw` and the selection code use, so painted text and highlight can never drift apart. As with
the rich label, `SelectionStyle` is `Partial` but `Selectable` stays opt-in per instance.

Step 4 makes the drag behave the same whether or not the pointer stays on content. A pointer over a
selectable control maps straight into it; a pointer over a gap, a panel's padding or beyond the
surface clamps to the nearest selectable control, so a drag that runs off the end of the last
label selects up to that end instead of freezing. Ties keep the control that already holds the
moving end, so a drag along the edge between two neighbours does not flicker. The same step
changed the whole-control rule from "the selection must cover its midpoint" to "the selection
must reach into it": pressing a whole control maps to `0.5`, so any drag direction now keeps it
selected while the pointer is still inside it, while a drag that stops exactly on its edge leaves
it out.

Step 5 turns a selection into text. Every control contributes through its own
`GetSelectionText(start, length)`, the pieces are joined in visual order with
`XPlatUiHost.SelectionTextSeparator`, and `CopySelection` writes the result through
`XPlatClipboard`, which the platform layer installs. The step-6 context menu only has to call it.

Step 6 adds the menu model and the platform hook. A menu is plain data - `XPlatContextMenu`
holds `XPlatMenuItem` values with a text, a command, an enabled flag and a separator singleton -
so the library never renders one. `XPlatUiHost.ProcessContextMenu` resolves the menu in three
steps: the hit control's `ContextMenu`, inherited from the nearest ancestor that has one, then
the `ContextMenuOpening` event, then a Copy menu built from the current selection. The result
goes to `IXPlatContextMenuPresenter.Current`, and with no presenter installed menus are simply
not shown, which is what keeps the library platform-free. Adapters call `ProcessContextMenu`
from the platform's own context menu message rather than from a right button release, because
that message also covers the keyboard menu key and avoids two menus for one click.

Step 7 puts a real menu on screen. `Win32ContextMenuPresenter` is the entire Windows half of the
feature and lives in the harness, not the library: it builds an `HMENU` from the menu data, one
`AppendMenuW` per item with a 1-based command id, and keeps a dictionary from id back to the
`XPlatMenuItem` so `TPM_RETURNCMD` gives it the picked item directly instead of forcing a
`WM_COMMAND` round trip. Item state maps onto the native flags, `&` is doubled because Win32
reads it as a mnemonic, and `ShortcutText` follows a tab so it is right aligned. Separators are
trimmed first, because a native menu draws a visible line for a leading, trailing or doubled one,
and a host that appends groups conditionally produces those easily. `SetForegroundWindow` before
`TrackPopupMenuEx` and a `WM_NULL` afterwards are the two documented fixes for a menu that either
refuses to close or stays on screen until the next input arrives. The presenter is handed a
`Func<SKPoint, POINT>` for the logical-to-screen conversion, because only the platform layer knows
where its surface sits and how it is scaled, and it is installed from `Program.cs` after the window
exists rather than being constructed lazily, which keeps the `--render` path free of any window.

Two things about showing a menu are worth knowing before touching this code. A posted mouse or key
message cannot be used to pick an item or dismiss a menu: `TrackPopupMenuEx` runs its own modal
loop and does not route posted input through it, so a verification has to observe the loop from
outside - which is what the throwaway app does, by asserting the calling thread is still blocked
inside the call. And a menu that closes the instant it opens usually means the owner window was not
foreground, which is why `SetForegroundWindow` is not optional here.

Three rules worth remembering when implementing the later steps:

- The visual-order walk does not descend into a selectable control, so a selectable container
  hides its children from selection and ranges can never overlap.
- A press that lands on a non-selectable control clears the selection; a press on a child of a
  selectable control resolves to the nearest selectable ancestor.
- Inline controls inside a rich label carry no text, so they are only highlighted when the
  selection reaches past them on both sides; a selection that stops at the control's position
  does not include it.
- A control that keeps a selection of its own calls `ClaimSelection()` when that selection
  becomes live and overrides `OnSelectionCleared` to drop it, so the UI never shows two
  selections at once.
- Whole controls join a selection by overlap, so a drag that stops exactly on the boundary of one
  leaves it out, but a drag that enters it and comes back out keeps it selected.

Step 7 has one platform trap worth writing down. Windows generates `WM_CONTEXTMENU` inside the
default window procedure for `WM_RBUTTONUP`, so an adapter that handles the right button release
itself has to pass it on to `DefWindowProcW`; returning zero swallows the request, and because the
library only ever learns about a menu through `ProcessContextMenu` there is nothing to debug - the
right click simply does nothing. Only the right button needs the fall-through.
Copying is where a control's influence over the clipboard became visible. The plain text piece a
control contributes (`GetSelectionText`) is not always the whole story: a rich label knows its text
is bold or coloured, and a text target cannot carry that. So a control may also answer
`GetSelectionHtml`, and the selection joins those pieces the same way it joins the text ones, with
a line break between controls. The base implementation returns null, which means "no markup of my
own" and the plain text is used instead, escaped - so a control that does not care is unaffected.

Two details make the markup come out right. Because a selection can start or end part way through a
span, the label wraps only the selected slice of each span rather than the whole span, which is
also why the tags are derived from the span list (whose texts concatenate into `Text`, exactly the
axis the selection offsets are counted on) and not from the laid-out fragments. And because the
markup has to be valid HTML even when the selection cuts a span in half, tags open and close around
each slice, so adjacent slices of the same span repeat them - which is correct, just not minimal.

On Windows the markup travels in the `HTML Format` clipboard format, which has to be registered at
runtime and needs a small header of byte offsets before the fragment. The offsets are written as
fixed-width numbers so filling them in does not move the content they point at.