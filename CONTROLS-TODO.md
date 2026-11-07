# Controls roadmap - what is left

Status: living document. It tracks the controls and control-adjacent features XPlatUI has not built
yet, in the order they are worth building. What has shipped, and how it behaves, is in
[README.md](README.md#design); this file is only the remaining work, so a control is *deleted* from
here when it lands rather than ticked off in place.

Each item is independently shippable: work can stop after any of them with the tree in a working
state, which is the rule [SELECTION-PLAN.md](SELECTION-PLAN.md) already follows.

---

## 1. Where the tiers stand

The tiers were set when the library had twelve controls and they still describe the right order.

| Tier | Contents | State |
| --- | --- | --- |
| 1 - common input | `XPlatCheckBox`, `XPlatRadioButton`, `XPlatProgressBar`, `XPlatGroupBox` | shipped |
| 2 - popup-based | the popup layer (`IXPlatPopupService`, `XPlatPopup`, one popup window per backend), `XPlatComboBox`, `XPlatTabControl`/`XPlatTabPage`, `XPlatToolTip` | shipped |
| 2b - unlocked by the popup layer | in-library menus, `XPlatDateTimePicker`, text-box autocomplete, drag previews | in-library menus, `XPlatDateTimePicker` and text-box autocomplete shipped; drag previews in Ã‚Â§3 |
| 3 - large list controls | `XPlatVirtualList` (the shared row base), `XPlatListView`, `XPlatTreeView` | shipped |
| 4 - small widgets | `XPlatNumericUpDown`, `XPlatTrackBar`, `XPlatLinkLabel`, `XPlatStatusStrip`/`XPlatToolStrip` | shipped |

Tier 2 is what unblocked the rest: until there was a popup that could leave the window, every
remaining control that wants one (a calendar, a suggestion list, a menu, a drag ghost) was either
clipped by its ancestors or impossible.

## 2. What "done" means

A control is finished when all of the following are true. The last three batches were held to it, and
a reviewer should ask for each one rather than infer it.

- It lives in `XPlatUI/Controls` and contains no platform code. Anything that needs a platform goes
  behind a seam in `XPlatUI/Platform` that a backend implements and a null implementation satisfies,
  so the portable library still builds and runs with nothing installed.
- It derives from `XPlatControl`, or from the closest existing control the way `XPlatCheckBox`
  derives from `XPlatToggleControl`, and follows the conventions the others follow:
  - the constructor sets `TabStop`, `Cursor`, `Padding` and `MinimumSize`; anything that measures its
	own text also sets `AutoSize`;
  - it never captures a font or a colour when it is constructed: a control that draws with another
	role overrides `DefaultFont` / `DefaultBackground` (`EmphasizedFont` for a button, the input fill
	for a field), and the effective appearance is read when text is measured and painted - see
	[Inherited appearance](README.md#inherited-appearance);
  - there is a `XPlatXxx(string text)` convenience constructor where text is meaningful;
  - `MeasureContent` answers `AutoSize`, and the paint path uses `XPlatPainter.Fill`,
	`XPlatPainter.DrawBorder` and `XPlatPainter.DrawFocusRing` rather than raw canvas state;
  - colours are read from `XPlatTheme` *at paint time*, so a palette swap recolours a live control
	with no rebuild;
  - events come in `AddXxxHandler` / `RemoveXxxHandler` pairs returning `XPlatHandlerToken<T>`.
- Input is complete: pointer press inside/release inside is a click, hover and press are distinct
  states, `TabStop`/`TabIndex` order it - per container, with tree order breaking ties - the keys a
  native control of that kind would take are handled and marked `e.Handled`, and focus draws a ring.
- Disabled is complete: no input, `XPlatTheme.DisabledBackground` / `DisabledForeground`, any press
  cancelled and capture released in `OnEnabledChanged`. The cascade from a disabled ancestor is
  inherited from `XPlatControl` and needs no code.
- Anything time-based uses `CreateTimer`/`XPlatTimer` and stops when there is nothing to do. An idle
  application must run no timers; only the marquee progress bar, tool-tip delays and the caret blink
  are allowed to have one, and each of them stops.
- It has a README section (in the `Design` table as well as its own `###` section) and a demo page
  appended to `Demos` in `XPlatUIDemo.App/DemoUi.cs` - appended, not inserted, because `--page N`
  and the render harness address pages by index.
- Its behaviour is covered by tests in `XPlatUI.Tests` - or in the feature's own
  `XPlatUI.Feature.*.Tests` assembly when the control is part of a feature package - written in the
  same change rather than a follow-up. The suite runs headless with no display, window server or
  network, so the same tests run on Windows, macOS and GTK4, and what they cover is the public
  behaviour an application depends on (through the host's own input entry points where input is the
  subject) rather than incidental internals. A bug fix carries a test that fails before it.
- It is verified on Windows, macOS and GTK4: a headless `--render` of the new page on all three, plus
  a real-window check wherever the control's value is interaction (a popup, a drag, a keyboard path).
- The pull request names the rung of [FEATURE-COST-POLICY.md](FEATURE-COST-POLICY.md) Ã‚Â§4 it sits on
  and says why it could not sit lower.

## 3. Unlocked by the popup layer

This one is pure control work now; the hard foundation is in place.

### 3.1 Drag previews

Two separable halves, and only the first belongs to the library proper:

1. A drag ghost - a popup following the pointer showing a translucent snapshot of the dragged
   content. Non-interactive, so it is close to `XPlatToolTip` with a different placement rule.
2. Drag *out*: starting an OS drag from an `XPlatDataObject`. The drop half exists in
   `XPlatUI.Feature.DragDrop` (`AllowDrop`, `XPlatDragDropRouter`); the outbound half is
   per-platform and is the larger job.

Do (1) first. (2) is worth its own plan document, the way selection got one.

## 4. Controls not yet built

Everything here is absent from the library today, ordered by how often an ordinary desktop
application reaches for it. The costs are honest estimates of the work, not commitments: the point of
listing them is that a reviewer can say "this next" without re-deriving the gap.

### 4.1 Reached for in most applications

| Control | What is missing | Cost |
| --- | --- | --- |
| `XPlatSplitButton` / `XPlatDropDownButton` | `XPlatToolStrip` has buttons, labels, progress, separators and springs, but no button that is both a command and a menu. The popup layer and the menu system already do the hard part, so this is mostly composition. | Low |
| `XPlatExpander` | `XPlatGroupBox` is static. Collapsible sections are the normal shape of a settings page, and there is nothing to collapse with. | Low |
| A validation error provider | The error *icon*, the message, and the plumbing that maps a validation result onto a control. The span squiggles that shipped draw the underline; what is missing is the provider an application hands its validation rules to, in the shape WinForms' `ErrorProvider` has. | Low to medium |
| `XPlatMaskedTextBox` | Input masks for dates, phone numbers, currency and the like. `XPlatNumericUpDown` covers numbers and `XPlatDateTimePicker` covers dates; free-form masked text has no answer. | Medium |
| `XPlatWrapPanel` | Items flowing onto the next line at a width, which is what tag rows, button groups and thumbnail grids want. `XPlatStackPanel` never wraps and `XPlatTableLayoutPanel` needs its columns known up front. | Low |
| A busy indicator and an overlay | A long operation needs something that is not a determinate `XPlatProgressBar`. A spinner plus a modal overlay that blocks input while work runs. | Low |
| `XPlatToast` / notification banner | Transient success and error messages that do not need a dialog. Cheap now that `XPlatUI.Feature.Animations` supplies the fade and slide. | Low |

### 4.2 Worth having, but not in the way of building an application

`XPlatBadge` (a count or dot on a tab or button), a breadcrumb bar, wizard/stepper chrome,
`XPlatRating`, an avatar or chip control, a zoomable and pannable canvas for diagram and image
viewers, and animated expand/collapse, which the animation feature now makes a small job.

### 4.3 Expensive, and deliberately not decided

Not excluded the way section 5's list is - these are wanted, but each is large enough that it needs a
plan document and a decision rather than a line in this table.

| Not yet decided | What it would take |
| --- | --- |
| Editable rich text | `XPlatRichLabel` is display-only and the text-box spans are annotation, not formatting. In-place bold, italic and colour editing needs a document model, a formatting toolbar and a caret that understands runs - a project of its own, not a control. |
| Charts | Arguably belongs to an application rather than to a control toolkit, the same argument that keeps printing out. If it does belong here, it needs a plan for axes, scales, series types and hit-testing. |

## 5. Deliberately not planned

Recorded so the decision is not re-litigated every time someone asks:

| Not building | Why |
| --- | --- |
| `XPlatDataGridView`, `XPlatPropertyGrid` | A grid is a project of its own: cell editors, span, freeze, per-cell formatting. `XPlatTableLayoutPanel` plus a virtualized list covers the ordinary cases. |
| `XPlatWebView` | A browser engine per platform, and it would dominate the binary - the opposite of the point. |
| MDI | No platform does it well; use separate windows, which the model already supports with `Owner`. |
| VS-style dockable/floatable panels | Large, and the split container plus layout panels cover the common arrangement. |
| Ribbon | Nobody wants it back. |
| Printing / print preview | Belongs to an application, not to a Skia-drawn control toolkit. |

## 6. Suggested order

1. **Drag previews** (Ã‚Â§3.1) - small, and independent of everything above.
2. **The small batch in Ã‚Â§4.1** - `XPlatSplitButton`, `XPlatExpander`, `XPlatWrapPanel`, the busy
   indicator and the toast. Five small controls, each independently shippable, none of them waiting
   on anything.
3. Then the rest of Ã‚Â§4.1 in the order listed, and Ã‚Â§4.3 only once one of them has a plan document.
