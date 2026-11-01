# Feature cost policy

How XPlatUI adds features without taxing the applications that do not use them.

Status: policy proposed on `feature/opt-in-features`. This document is the first and, so far, only
change on that branch; the implementation plan in §9 has not been started.

---

## 1. Why this policy exists

XPlatUI's reason to exist is a small, trimmed, AOT-compiled deliverable: a UI library that ships as
a few megabytes of native code with no runtime, on Windows, macOS and Linux. Every feature we add
works against that, because the trimmer can only remove code it can prove is unreachable. A feature
that hangs off a type every application touches, or that registers itself when a window is created,
is paid for by every application forever, including the ones that never heard of it.

The policy is therefore about *shape*, not about build flags: features must be shaped so that
"available but unused" actually compiles away.

## 2. The rule

An application that does not use a feature should pay as close to nothing for it as we can make it:
no code in the published AOT binary, no startup work, no per-window native registration, no memory.

This is not a demand for a literal zero in every case. It is a demand for **bare minimal bloat**,
established by measurement rather than by assertion. A small, always-present seam that lets
everything else be removed is a good trade and is explicitly allowed. A feature that silently costs
every application code and startup time it never asked for is not.

## 3. Why this is a design constraint, not a build setting

Trimming is static reachability from the entry point. Turning on `PublishTrimmed` and `PublishAot`
does not make an unused feature disappear; it removes only what nothing reaches. So the question for
every new feature is "what reaches this code, and does that path exist in an app that does not use
it?". The patterns that answer "yes" are the ones to avoid:

| Pattern | Why the code survives trimming |
|---|---|
| A new virtual method or interface implementation on `XPlatControl` or on a backend view type | The type is always retained, and a retained type keeps its overrides and interface implementations to fill its vtable. The member is kept even if nothing calls it. |
| Work performed at window creation | Runs for every window in every app, and is reachable from `Show`, so the code behind it is kept. |
| Reflection: `Activator.CreateInstance`, `Type.GetType`, assembly scanning, attribute-driven registration | The trimmer cannot prove what is reachable, so it keeps the world (or warns and keeps it). |
| Module initializers, or static constructors and static state on a type every app touches | They run the first time the type is touched, which every app does. |
| Feature code inside an always-used control | The app names the control, so the whole type and its helpers are kept. |

The corollary worth stating plainly: a feature cannot be made zero-cost by hiding it behind a
property the app sets at runtime if the code behind that property is reachable from a retained
type. Reachability is decided statically, not by the value of a `bool` at run time. That is why §4
has a second and third rung rather than just "make it lazy".

## 4. The three rungs

In order of preference. Each feature must be placed on the lowest rung it can actually reach, and
the reason it could not reach a lower one must be written down.

### Rung 1: zero cost by construction

Design it so that the only path to the feature's code runs through application code that opted in.
The app referencing the type, or calling the method, is what keeps it alive. No hooks, no
registration, no changes to always-retained types.

This is the default and should be the answer for most new controls: a control the app must name
costs nothing when it is not named.

### Rung 2: opt-in registration or hook

If the feature genuinely must do work at application startup or window creation, the *registration*
becomes the opt-in point. Nothing registered means nothing runs, and the feature's implementation is
trimmed away along with the registration call. The framework supplies the seam; the feature supplies
the hook implementation; the application supplies the one line that turns it on.

### Rung 3: separate project

If neither of the above is practical, the implementation does not go into the base `XPlatUI`
projects at all. It goes into its own project that applications reference explicitly, so the base
deliverable never contains it and no amount of trimming cleverness is required.

This is a deliberate fallback, not a failure, but it has a cost of its own (more projects, more
versioning, more places to look), so it should be chosen for a stated reason rather than by default.

## 5. The opt-in mechanism

The shape below is a sketch of the intended design, not a settled API. Names, signatures and the
exact hook points will be decided when we implement Phase 1.

### 5.1 Two phases, because services and work happen at different times

`XPlatApplication` builds its container in one place and runs the loop in another, so a feature may
need two distinct things:

1. **Contribute services**, before `BuildServiceProvider`. This already exists as
   `XPlatApplicationOptions.ConfigureServices`, which receives MEDI's own `IServiceCollection` and is
   documented to stay inside the AOT-safe subset (generic `AddSingleton<TService, TImpl>()`, factory
   overloads, `AddWindowScoped<T>`; no assembly scanning).
2. **Run work**, after the container exists. This is the new hook.

A feature that needs both registers a service and a hook, in the same opt-in call.

### 5.2 The hook

```csharp
public interface IXPlatApplicationStartupHook
{
	void OnStartup(XPlatApplication application);
}
```

`XPlatApplication`, after `BuildServiceProvider` and before the loop runs, resolves them and invokes
each one:

```csharp
foreach (var hook in Services.GetServices<IXPlatApplicationStartupHook>())
{
	hook.OnStartup(this);
}
```

The application opts in through the registration it already has:

```csharp
new XPlatApplicationOptions
{
	ConfigureServices = services => services.AddSingleton<IXPlatApplicationStartupHook, DragDropStartupHook>(),
}
```

or, preferably, through a feature extension that does the same thing with a name that says what it
turns on:

```csharp
ConfigureServices = services => services.AddDragDrop(),
```

The trim property that makes this work: `GetServices<T>()` on a service nobody registered returns an
empty sequence, and the only thing retained is the interface itself. The concrete hook type is
reachable solely from the registration call, so an app without that call publishes without it.

### 5.3 Hook points

A single startup hook is not enough, because most expensive work is per window rather than per
process. The set to provide, and to keep stable:

| Point | What it is for |
|---|---|
| Application startup | Process-wide platform initialisation that must precede any window (for example OLE initialisation, or registering application-level pasteboard types). |
| Window created | Per-window native registration once the handle exists (drop targets, extra pasteboard types, controllers). |
| Window shown | Work that requires a visible window. |
| Window closed | Teardown symmetry, so nothing leaks on close and reopen. |
| Application shutdown | Process-wide teardown that must happen before the loop returns. |

The window points take the window (or its `IXPlatWindowBackend`) so the hook can reach the native
handle without the model having to expose anything new. A control-level "attached to tree" point is
deliberately out of scope for now; the window points are enough for every feature currently on the
roadmap, and each added point is permanent cost in the seam.

### 5.4 Rules for hook implementations

- **Standalone classes, never backend views.** A hook implemented on `Win32SkiaView`,
  `AppKitSkiaView` or `Gtk4SkiaView` would be retained with those types and defeat the purpose. The
  hook lives in a small class whose only reference is the registration call.
- **Explicit registration, never scanning.** Assembly scanning is the one common pattern that is not
  trimming-safe, and the codebase already forbids it for `ConfigureServices`. Hook discovery is
  registration order, full stop.
- **Registration order is invocation order**, and it is documented so features can depend on it.
- **Runs on the UI thread.** Startup hooks run before the loop starts, on the thread that will own
  it; window hooks run on the UI thread. Documented rather than assumed.
- **Idempotent per window.** A window point must be safe to reach once per window, not once per
  resize or repaint.
- **Failures surface.** A hook that throws goes through the existing `UnhandledException` path
  rather than silently aborting startup.
- **Root provider only.** Application hooks resolve from the root provider, not from a window scope.

## 6. The seam's own budget

The interface, the enumeration and the dispatch loop are paid by every application, forever. They
therefore get a size budget and a stability promise:

- Keep the seam to a handful of tiny types with no dependencies of their own.
- No feature-specific types may appear in the seam; a hook signature that names a drag payload or a
  menu item has already lost.
- Adding a hook point is a deliberate act with a measured cost, not a convenience.
- Target: the whole seam costs low single-digit kilobytes in the AOT binary. If a proposed hook
  point cannot meet that, it belongs on rung 3.

## 7. How we verify

Claims about cost are worth nothing without a number. Every feature lands with:

1. **A size measurement.** Publish a minimal application that does not use the feature with
   `PublishTrimmed` and `PublishAot`, on `win-x64`, `linux-x64` and `osx-arm64`, and compare against
   the previous commit. The delta is quoted in the pull request.
2. **A warning review.** Build with `-p:TrimmerSingleWarn=false` and read the IL2xxx/IL3xxx output
   for the new code. That is where accidental rooting shows up first.
3. **A startup check.** Confirm that an application which does not use the feature performs no new
   work at startup or window creation: no new native calls, no new registrations, no new
   allocations.
4. **A use check.** Confirm that an application which does use the feature still gets it, still
   publishes cleanly, and still trims.

This needs a stable measurement fixture: a minimal app (one window, one label, nothing else) kept in
the repository and published by the same commands every time. Without a fixed fixture the numbers
are not comparable and the policy becomes folklore.

## 8. Case study: drag and drop as merged

Drag and drop is the first feature that fails this policy, and it is worth writing down exactly how,
because it is the template for the fix.

What every application pays today, whether or not it sets `AllowDrop`:

| Platform | Cost | Where |
|---|---|---|
| Windows | `OleInitialize`, a COM drop-target object, and `RegisterDragDrop`, per window | `XPlatUI.Backend.Win32/Win32WindowBackend.cs:575-576`, `Win32DropTarget.cs:71,80` |
| macOS | `RegisterForDraggedTypes` on every view | `XPlatUI.Backend.AppKit/AppKitSkiaView.cs:101` |
| GTK4 | Two `Gtk.DropTarget` controllers per view | `XPlatUI.Backend.Gtk4/Gtk4SkiaView.cs:76-77` |
| Shared | `XPlatDataObject`, `XPlatDataFormat`, `XPlatDragEventArgs`, the host routing in `XPlatUiHost`, and `AllowDrop` plus four events on `XPlatControl` | `XPlatUI/DragDrop/`, `XPlatUI/XPlatUiHost.cs`, `XPlatUI/Core/XPlatControl.cs` |

None of it is conditional on anything. The backend view types are always retained, so their drag
members are retained with them, and the per-window native registration happens for every window.

Target state: an application that never drops anything publishes without OLE initialisation, without
a COM drop target, without registered pasteboard types and without GTK controllers, by virtue of not
calling the opt-in. The shared payload types and the `AllowDrop` property can stay where they are:
they are small and inert, and they cost nothing until something calls them.

Note the trap: making `AllowDrop` the opt-in would *not* achieve this. `AllowDrop` lives on
`XPlatControl`, which is always retained, so the code behind it is reachable and kept regardless of
whether any application ever sets it. Per-control opt-in is a nice ergonomic layer on top; it is not
a substitute for an application-level opt-in.

## 9. Implementation plan

### Phase 0: the measurement fixture

Add a minimal application (one window, one label, no features) and the publish commands for the
three platforms, and record its AOT sizes in this document. Everything after this phase is judged
against those numbers. Nothing else is meaningful until this exists.

### Phase 1: the seam

- Add `IXPlatApplicationStartupHook` and invoke it in `XPlatApplication` after
  `BuildServiceProvider`.
- Add the window-level points (created, shown, closed) around the window lifecycle, with the
  shutdown point alongside `IXPlatApplicationBackend.Shutdown`.
- Keep every hook implementation-free: the seam names no feature.

Measure: the seam's own delta on the minimal app, which is the number the budget in §6 is checked
against.

### Phase 2: drag and drop as the first consumer

- Add the opt-in (`services.AddDragDrop()` or equivalent), registering whatever services and hooks
  the feature needs.
- Move `Win32DropTarget` construction and registration behind the window-created hook.
- Move `RegisterForDraggedTypes` behind the window-created hook.
- Move `Gtk.DropTarget` construction behind the window-created hook.
- Leave `AllowDrop`, the four events, the payload types and the host routing where they are.

Measure: an application that does not call the opt-in must lose OLE, the COM target, the pasteboard
types and the GTK controllers, and the size must return to the Phase 0 baseline.

This phase is also the proof that the pattern works, so it should be done before any new feature is
held to the policy.

### Phase 3: documentation and review

- Point `README.md` at this policy from the drag and drop section and from wherever features are
  listed.
- Add the checklist in §10 to the review expectations for feature work.

### Phase 4: hold new work to it

Every subsequent feature states, in its pull request, which rung it sits on, why it could not sit
lower, and the measured cost for an application that does not use it.

## 10. Checklist for a new feature

- [ ] Does any always-retained type (a control, a backend view, `XPlatUiHost`, `XPlatApplication`)
	  gain a member or an interface implementation? If so, why is that unavoidable?
- [ ] Does anything run at application startup or window creation? If so, is it behind an opt-in
	  hook?
- [ ] Is there any reflection, assembly scanning, attribute-driven registration or
	  `Activator.CreateInstance`?
- [ ] Is there any module initializer, or static constructor or static state on a shared type?
- [ ] Is the feature's code reachable only from the opt-in call, and therefore trimmed when the call
	  is absent?
- [ ] Was the publish size measured for an app that does not use it, on all three platforms, and
	  quoted in the pull request?
- [ ] Were the trim/AOT warnings for the new code reviewed?
- [ ] If it could not be made opt-in, is it in a separate project instead, with the reason recorded?

## 11. Open questions

These need answers before Phase 1, and the answers belong in this document:

1. **Hook shape.** A marker interface resolved as `IEnumerable<T>` (as sketched in §5), or a
   builder-style `application.Enable<T>()`? The builder reads better but needs somewhere to live
   before the container exists, which is exactly the awkward moment.
2. **Scoping.** Are hooks always root-provider singletons, or is a per-window scope useful enough to
   justify the extra seam?
3. **`AllowDrop` ergonomics.** Should setting `AllowDrop` implicitly opt the window in, on top of the
   application-level opt-in, or should drag and drop always be an explicit application-level choice?
4. **What "separate project" means.** A separate assembly in this repository, or a separate package
   with its own version and release? Rung 3 is only useful if it is cheap to take.
5. **The seam's size budget.** Low single-digit kilobytes is a guess. Phase 1 produces the real
   number, and this document should be corrected to it.
6. **Retrofitting.** Which existing features get moved onto the policy, and in what order? Menus,
   dialogs, timers and single instance all predate the policy and none has been measured.

## 12. Non-goals

- Retrofitting everything at once. The policy applies to new work, with drag and drop as the
  deliberate exception because it is the case study.
- Making an API awkward to hit a byte target. If the zero-cost shape would hurt the API, say so, take
  rung 2 or 3, and record the reason.
- A promise of literally zero cost. The standard is bare minimal, measured, and justified.
- A trimming/AOT test suite. The checks in §7 are a publish comparison and a warning review, run when
  a feature lands, not a new CI project.
