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
points, paints into an `SKBitmap` and inspects pixels and `Selection.Ranges`. The harness
demo is re-rendered with `WinFormsApp3.exe --render <png> <w> <h>` and, from step 7 on, the
AOT single-file publish is repeated.

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
| 7-9 | Pending | See the step table. |

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
