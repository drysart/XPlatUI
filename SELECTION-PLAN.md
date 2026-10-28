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
| Unit inclusion | A whole control counts when the selection covers the middle of its unit; a partial unit counts when the selection covers its middle | Pressing a whole control maps to `0.5`, so it selects immediately and drags in either direction; a press that does not move selects nothing. |
| Ordering | Depth-first walk of the control tree, z-order aware | Gives a total order for normalising anchor/focus into ranges without any geometry maths. |
| Control contract | Virtual members on `XPlatControl` (no interface) | Every control derives from `XPlatControl`; avoids casts and interface dispatch, and is AOT friendly. |
| Selection painting | Uniform: `XPlatControl.Render` fills the rects returned by `GetSelectionRects` between the background and the content | One look everywhere; controls only supply geometry. |
| Drag threshold | A press becomes a selection after ~3 logical pixels of movement | Plain clicks still reach links, buttons and text boxes inside selectable controls. |
| Text join | Per-control contributions joined with `\n` (configurable) | "end of label 1\nwhole label 2\nstart of label 3". |
| Keyboard | None. Selection is pointer-driven only | Explicit scope decision: there is no caret to move. |
| Context menus | Native only; a no-op fallback when the platform installs no presenter | Keeps the library free of platform code and adds no menu renderer to the binary. |

## Steps

| # | Step | Scope | Done when |
| --- | --- | --- | --- |
| 1 | Selection primitives and control contract | `XPlatSelectionStyle`, `XPlatSelectionPosition`, `XPlatSelectionRange`; `Selectable`, `SelectionStyle`, `SelectionUnitCount`, `GetSelectionOffset`, `GetSelectionRects`, `GetSelectionText`, `SelectionRange` on `XPlatControl` | A selectable button reports one unit, its text, and paints a highlight |
| 2 | Host selection state and drag selection | `XPlatSelection` (anchor/focus, ranges, `Changed`), press/drag/release handling, drag threshold, mouse capture reuse | Dragging inside a selectable control highlights it and `Selection.Ranges` is correct |
| 3 | Rich label offsets and partial selection | `Fragment.Start`/`Length` (offsets into the span text), nearest-offset hit testing, per-line selection rects, text slices | Positions land on word boundaries, rects are per line, slices match the source |
| 4 | Cross-control selection | Visual-order clamping while dragging outside a control; boundary rules for whole controls | Dragging from the middle of label 1 to the middle of label 3 selects partial/whole/partial |
| 5 | Clipboard assembly | `XPlatSelection.GetText()`, `XPlatUiHost.CopySelection()` through `XPlatClipboard` | The clipboard receives the expected multi-control string |
| 6 | Context menu model and presenter API | `XPlatContextMenu`, `XPlatMenuItem`, `IXPlatContextMenuPresenter`, `XPlatContextMenuPresenter.Current`, `XPlatControl.ContextMenu`, default Copy menu on right-click | A fake presenter captures the menu and Copy fills the clipboard |
| 7 | Win32 native menu in the harness | `TrackPopupMenuEx` presenter, `WM_CONTEXTMENU` routing, install in `Program.cs` | Right-clicking a selection shows a native menu that copies |
| 8 | Built-in controls adopt selection | `XPlatLabel` (partial), `XPlatTextBox` (partial, bridged to its caret/anchor), `XPlatButton`/`XPlatImage` (whole), containers opt-in | Mixed partial/whole selections paint and copy correctly |
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
| 3-9 | Pending | See the step table. |

Two rules worth remembering when implementing the later steps:

- The visual-order walk does not descend into a selectable control, so a selectable container
  hides its children from selection and ranges can never overlap.
- A press that lands on a non-selectable control clears the selection; a press on a child of a
  selectable control resolves to the nearest selectable ancestor.
