# Feature cost policy

How XPlatUI adds features without taxing the applications that do not use them.

Status: policy proposed on `feature/opt-in-features`. This document is the first and, so far, only
change on that branch; the implementation plan in §10 has not been started.

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
| A new member on `XPlatControl` or on a backend view type: a virtual method, an interface implementation, or an event | The type is always retained, and a retained type keeps its overrides and interface implementations to fill its vtable. Field-like events add a delegate field to every instance and keep their accessors. All of it is kept even if nothing calls it. Section 5 is the shape that avoids this. |
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

When a feature wants to present itself as new members on a control that already exists, §5 is the
shape that keeps it on this rung.

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

## 5. Expressing a feature as an extension

The most natural way for a feature to present itself is as new members on `XPlatControl` or on one
of its subclasses: a property here, a method there, an event when something happens. Those members
must not be added to the type. The feature's implementation goes in its own namespace and is exposed
through extension members, so the surface reads as part of the control while the code stays
removable.

This is a shape rule rather than a size rule, and it is the main way a feature stays on rung 1.

### 5.1 Why this is the trim-safe shape

An extension member compiles to a static method on a static class. It adds nothing to the extended
type: no new member, no vtable slot, no interface implementation, no instance field. An extension
member that nothing calls is trimmed, one member at a time, which is finer granularity than a member
of a retained type ever gets.

For contrast, `AllowDrop` plus the four drag events on `XPlatControl` are real members of a retained
type. The events are field-like, so every control instance in every application carries four
delegate fields (32 bytes on 64-bit) whether or not anything ever drags, and the accessors stay with
the type.

Converting an event to the `Add`/`Remove` pair in §5.4 does not change that cost for a core event,
which still needs its backing field. What the pair buys is that the same shape works for a feature,
which has no field on the control to hang a C# event on.

### 5.2 What the language allows

Verified against the toolchain this repository builds with (`net10.0`, C# 14):

| Member kind | Available | Notes |
|---|---|---|
| Methods | Yes | The classic extension method. |
| Properties, including setters | Yes | Extension blocks (`extension(T value) { ... }`), not only methods. |
| Properties in object initializers | Yes | `new XPlatLabel { AllowDrop = true }` still compiles, so the object-initializer style the demo uses survives. |
| Indexers, operators, static members | Yes | Available if a feature needs them. |
| Events | No | `CS9282`, "This member is not allowed in an extension block". Use the handler methods in §5.4 instead. |

Extension blocks are a C# 14 feature, so the policy assumes the library and its consumers are on the
same compiler. They are, because the library targets `net10.0`.

### 5.3 Access and state

- **Access.** An extension block in the same assembly can read and write `internal` members of the
  extended type; it cannot reach `private` ones. That is enough for a rung 1 feature, which lives in
  `XPlatUI` and can use internal seams instead of widening the public API. A feature that has to
  live in its own assembly (rung 3) needs public accessors or `InternalsVisibleTo`.
- **State.** Extension members cannot add fields to the control, so per-control state belongs to the
  feature: a lazily created state object, or a `ConditionalWeakTable<XPlatControl, TState>` keyed
  on the control. Both are trimmed with the feature, but both cost a lookup per access, so a hot
  path caches the state rather than looking it up again.
- **No reflection discovery.** Extension members are invisible to `GetMethods()` on the type. That
  is good for trimming, and it means nothing may register a feature by scanning for its members.

### 5.4 Events: `Add` and `Remove` handler methods

Extension members cannot be events: `CS9282`, "This member is not allowed in an extension block", for
`event`, `add` and `remove` alike. Rather than work around that with a helper object, which makes the
feature read as bolted on, the library does not use the C# `event` keyword at all. Every event is a
pair of methods:

```csharp
public IDisposable AddDroppedHandler(EventHandler<XPlatDragEventArgs> handler);
public void RemoveDroppedHandler(EventHandler<XPlatDragEventArgs> handler);
```

Subscribing then reads like a member of the control, and because the pair is made of methods it can
be written as extension methods for a feature:

```csharp
using var subscription = control.AddDroppedHandler((sender, e) => { ... });
control.RemoveDroppedHandler(handler);
```

Verified in a throwaway project: the pair works as extension members, disposing the token
unsubscribes, and `EventArgs<T>` sits beside `System.EventArgs` without ambiguity.

This is a codebase-wide convention rather than a rule for opt-in features only. Core events on
`XPlatControl`, `XPlatWindow`, `XPlatApplication` and the backend interfaces use the same shape, so
there is one way to subscribe to anything.

Rules:

- The name is `Add` or `Remove`, then the event name, then `Handler`: `AddDroppedHandler`. The event
  name is the part that appears in documentation, so it keeps a normal event name.
- `AddXHandler` returns an `IDisposable` that removes the handler when disposed, so a scope can own a
  subscription without holding the handler itself. Disposing twice is a no-op, and so is disposing a
  subscription whose owner has already been torn down.
- `RemoveXHandler` covers removal by identity, which is what a long-lived handler needs.
- Handlers run on the UI thread in subscription order. A handler that throws stops the ones after it,
  as a C# event would, and the exception reaches `XPlatApplication.UnhandledException`.
- The backing storage stays private and unchanged: one field per event, exactly as a field-like event
  has today, so a core event costs no more than it does now. The new cost is one small allocation per
  subscription for the returned token. A feature keeps its own subscriptions in its per-control state
  (§5.3), which is trimmed along with the feature.
- No `event` keyword anywhere, including interfaces and backend types. A review that finds one has
  found a bug in the convention.
- The payload is an `EventArgs`-derived type: the existing `XPlatDragEventArgs`, `XPlatMouseEventArgs`
  and friends stay as they are, and a feature with one payload value uses the generic `EventArgs<T>`
  (the generic event args question in §12).

### 5.5 When a member does belong on the control

Three exceptions, each of which has to be stated in the pull request:

1. **The control cannot work without it.** Core behaviour is not optional and does not belong behind
   an opt-in. A text box that cannot accept typing is not a feature.
2. **Subclasses must be able to override it.** Use an interface the control implements, never a
   virtual on the base class: a virtual or override on a retained type is kept for every
   application, while an interface is only retained by the controls that implement it.
3. **The extension shape would break a common idiom.** If the alternative makes ordinary application
   code awkward, say so, take the member, and measure what it costs.

### 5.6 Naming

- One namespace per feature: `XPlatUI.DragDrop`, rather than members sprinkled across `XPlatUI`.
- The `using` is the visible opt-in. An application that never writes `using XPlatUI.DragDrop;`
  never sees the surface, which also reminds the reader that it is not paying for it.
- Extension classes are named for the type they extend plus the feature:
  `XPlatControlDragDropExtensions`.
- Handler methods follow §5.4: `AddDragDropHandler`, `RemoveDragDropHandler`.
- A feature that needs per-control state keeps it behind an extension property or an extension method
  on the control, never behind an object the application has to hold on to.

## 6. The opt-in mechanism

The shape below is a sketch of the intended design, not a settled API. Names, signatures and the
exact hook points will be decided when we implement Phase 1.

### 6.1 Two phases, because services and work happen at different times

`XPlatApplication` builds its container in one place and runs the loop in another, so a feature may
need two distinct things:

1. **Contribute services**, before `BuildServiceProvider`. This already exists as
   `XPlatApplicationOptions.ConfigureServices`, which receives MEDI's own `IServiceCollection` and is
   documented to stay inside the AOT-safe subset (generic `AddSingleton<TService, TImpl>()`, factory
   overloads, `AddWindowScoped<T>`; no assembly scanning).
2. **Run work**, after the container exists. This is the new hook.

A feature that needs both registers a service and a hook, in the same opt-in call.

### 6.2 The application hook

```csharp
public interface IXPlatApplicationStartupHook
{
	void OnStartup(XPlatApplication application);
}
```

The application-level hook is a singleton, because the work it does is process-wide. Per-window work
has its own hook with its own lifetime; see §6.3.

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

A hook that is a single call does not need a class of its own. One overload in
`XPlatServiceCollectionExtensions` wraps the action in a hook that already exists, and takes an
optional teardown action so a lambda hook can own a disposable:

```csharp
public static IServiceCollection AddXPlatApplicationStartupHook(
	this IServiceCollection services,
	Action<XPlatApplication> startupHookFunc,
	Action? onDispose = null)
{
	ArgumentNullException.ThrowIfNull(services);
	ArgumentNullException.ThrowIfNull(startupHookFunc);

	return services.AddSingleton<IXPlatApplicationStartupHook>(
		_ => new ActionBasedAppStartupHook(startupHookFunc, onDispose));
}

internal sealed class ActionBasedAppStartupHook : IXPlatApplicationStartupHook, IDisposable
{
	private readonly Action<XPlatApplication> _startupHookFunc;
	private readonly Action? _onDispose;

	public ActionBasedAppStartupHook(Action<XPlatApplication> startupHookFunc, Action? onDispose)
	{
		_startupHookFunc = startupHookFunc;
		_onDispose = onDispose;
	}

	public void OnStartup(XPlatApplication application) => _startupHookFunc(application);

	public void Dispose() => _onDispose?.Invoke();
}
```

Window hooks get the same treatment: `AddXPlatWindowHook` takes one optional callback per point plus
the same `onDispose`, so a feature that only cares about teardown still writes a single lambda. The
wrappers are seam types, so the seam pays for them once whether or not anything calls them; see §7.

The container runs the teardown, and its order is the nesting order. A window hook is disposed when
its window's scope is disposed (`XPlatWindow.cs:390`). Application hooks are disposed when the root
provider is disposed (`XPlatApplication.cs:740`), and every window is disposed first (`:732-735`),
so window teardown always precedes application teardown. A hook that needs it the other way round
says so in its own shutdown point rather than relying on `Dispose` ordering.

The trim property that makes this work: `GetServices<T>()` on a service nobody registered returns an
empty sequence, and the only thing retained is the interface itself. The concrete hook type is
reachable solely from the registration call, so an app without that call publishes without it.

### 6.3 Hook points and lifetimes

A single startup hook is not enough, because most expensive work is per window rather than per
process. There are two kinds of hook, with two lifetimes:

| Kind | Lifetime | Resolved from | What it is for |
|---|---|---|---|
| Application hook | Singleton | The root provider | Process-wide platform initialisation that must precede any window (OLE initialisation, application-level pasteboard types), and process-wide teardown before the loop returns. |
| Window hook | Window-scoped | `window.Services` | Per-window work: native registration once the handle exists (drop targets, extra pasteboard types, GTK controllers), and its teardown when the window closes. |

Application points are startup and shutdown. Window points are phases of one lifecycle, and they are
one interface rather than eight, because one scoped instance serves every phase for its window:

```csharp
public enum XPlatWindowPhase
{
	Created,
	Showing,
	Shown,
	Hiding,
	Hidden,
	Closing,
	Closed,
	Disposed,
}

public interface IXPlatWindowHook
{
	void OnPhase(XPlatWindowPhase phase, XPlatWindow window);
}
```

| Phase | When it fires | Direction | Raised today |
|---|---|---|---|
| `Created` | The native window exists and is not visible yet. | Ascending | Yes |
| `Showing` | `Show` has been called; the native window is not ordered front yet. | Ascending | Yes |
| `Shown` | The window is visible. | Ascending | Yes |
| `Hiding` | The window is about to become invisible. | Descending | Reserved |
| `Hidden` | The window is invisible and still alive. | Descending | Reserved |
| `Closing` | The window is about to close; the native handle is still alive. | Descending | Yes |
| `Closed` | The native window is gone and the backend is disposed. | Descending | Yes |
| `Disposed` | The window object is being disposed; its scope is still alive. | Descending | Yes |

`Hiding` and `Hidden` are declared and raised by nothing, because `XPlatWindow` has no `Visible`
property and no `Hide` method yet. Declaring them now costs one enum member and stops the hide and
show feature from renumbering a public enum later.

**Ascending phases run hooks in registration order; descending phases run them in reverse.** The two
halves of the list are the two directions of a window's life, and reversing the teardown half is
what makes a pair of hooks nest: if `A` registers before `B`, `A` sets up before `B` and tears down
after `B`, whatever order the phases arrive in.

The window lifetime already exists, so this costs nothing new. `XPlatWindow` creates an
`IServiceScope` in its constructor (`XPlatWindow.cs:60`), exposes it as `Services` (`:115`), and
disposes it when the window closes (`:390`). `AddWindowScoped<T>` (`XPlatWindowScope.cs:32`) is
`AddScoped` with the window handed to the factory, and `XPlatWindowAccessor` is how a scoped service
reaches its window. A window hook is therefore registered like this, and one window's instance is
nobody else's:

```csharp
services.AddWindowScoped<IXPlatWindowHook>((window, provider) => new DragDropWindowHook(window));
```

The ordering matters and is easy to get wrong. `XPlatWindow` disposes the backend at `:386`, raises
`Closed` at `:388` and disposes the scope at `:390`. `Closing` therefore fires while the native
handle is alive and `Closed` after it is gone, which is the split a feature needs: revoking a drop
target before the window is destroyed belongs in `Closing`, and releasing managed bookkeeping
belongs in `Closed` or `Disposed`. A scoped hook's own `Dispose` runs last of all, after the native
window is gone.

`Disposed` is the last point at which a hook can still reach other scoped services, and it fires
after the backend is disposed. Two consequences, both rules in §6.4: a typed `Disposed` callback is
managed-only, and the backend's phase entry point has to stay callable once it has been disposed.

The window phases take the window so a portable hook can reach the native handle through
`window.Backend` without the model having to expose anything new. A control-level "attached to tree"
point is deliberately out of scope for now; the window phases are enough for every feature currently
on the roadmap, and each added point is permanent cost in the seam.

### 6.4 Rules for hook implementations

- **Standalone classes, never backend views.** A hook implemented on `Win32SkiaView`,
  `AppKitSkiaView` or `Gtk4SkiaView` would be retained with those types and defeat the purpose. The
  hook lives in a small class whose only reference is the registration call.
- **Explicit registration, never scanning.** Assembly scanning is the one common pattern that is not
  trimming-safe, and the codebase already forbids it for `ConfigureServices`. Hook discovery is
  registration order, full stop.
- **Registration order is invocation order**, ascending in the phases that bring a window to life
  and reversed in the phases that take it away (§6.3). Features may rely on the nesting this gives
  them, which is why it is written down rather than left to chance.
- **Runs on the UI thread.** Startup hooks run before the loop starts, on the thread that will own
  it; window hooks run on the UI thread. Documented rather than assumed.
- **Per-window state lives on the hook instance.** A window hook is scoped, so each window gets its
  own instance and the created, shown, closing and closed callbacks share state without a table
  keyed by window. A hook that finds itself needing such a table is registered at the wrong
  lifetime.
- **Failures surface.** A hook that throws goes through the existing `UnhandledException` path
  rather than silently aborting startup.
- **Resolve from the right provider.** Application hooks come from the root provider; window hooks
  come from `window.Services`. Resolving a scoped hook from the root is the trap: MEDI either throws
  under scope validation or hands back a single instance that outlives every window.
- **Dependencies point one way.** A window hook may depend on application singletons normally. A
  singleton must never hold a window hook, because that would keep one window's instance alive for
  the life of the process.
- **Portable hooks are the outer layer.** They run before a backend's typed hooks (§6.5) in an
  ascending phase and after them in a descending one, so a typed hook always tears down before the
  portable hook that set it up.
- **Typed hooks never derive from portable ones.** An `IXPlatWin32WindowHook` that also implemented
  `IXPlatWindowHook` would be found by both loops and invoked twice.
- **A backend's phase entry point survives its own `Dispose`.** `Disposed` fires after
  `Backend.Dispose()` (`XPlatWindow.cs:386`) and before the scope goes (`:390`), so
  `RunWindowHooks` must touch nothing native by then and must not assume the backend is alive.
- **`Dispose` is the last teardown point, not the first.** The container disposes a hook that is
  `IDisposable`: a window hook with its scope, an application hook with the root provider. By then
  the native window or the process-wide state may already be gone, so anything that has to run while
  it still exists belongs in the closing or shutdown point.

### 6.5 Platform-specific hooks

A feature that is native on every platform is three layers, and only the middle one differs.

| Layer | Lives in | Cost when the feature is unused |
|---|---|---|
| Contract: service interface, event args, control extension members | `XPlatUI`, in the feature's namespace | Trimmed per member |
| Implementation: `Win32DragDrop`, `AppKitDragDrop`, `Gtk4DragDrop` | The matching backend assembly | Trimmed, unless something retained names it |
| Selection: which implementation exists | The backend project reference in the app's project file | None |

Core never references a backend, and an application references exactly one backend per target
framework (`XPlatUIDemo.App.csproj`, "Exactly one backend per TFM"). The choice is therefore made
when the application is compiled: no runtime dispatch, no reflection, no operating system check.

**The registration call site has one name.** Each backend declares the same extension method, in the
feature's namespace, so the application calls it without knowing which platform it is on:

```csharp
using XPlatUI.DragDrop;

options.ConfigureServices = services => services.AddDragDrop();
```

That method is the opt-in, so it must be the only thing in the backend that names the
implementation. If a backend's window creation path mentioned `Win32DropTarget`, the whole COM
implementation would be retained in every Win32 application that never drags anything. It does not:
the hook does the per-window wiring, so the always-on path contains no feature code and there is
nothing to gate, look up or branch on.

The contract across backends is a naming convention, and a backend that forgets it fails at compile
time on that platform, which is the failure we want. `Headless` declares no feature extensions: it
is referenced alongside a real backend, so a duplicate name would be an ambiguity error, and it has
no windows to attach anything to.

**Typed hooks remove the downcast.** A portable hook reaches its backend through
`application.Backend` or `window.Backend`, both typed as the interface, so native work would start
with a cast. Instead each backend declares its own hook interfaces, which name the backend by its
concrete type:

```csharp
public interface IXPlatWin32ApplicationStartupHook
{
	void OnStartup(XPlatApplication application, Win32ApplicationBackend backend);
}

public interface IXPlatWin32WindowHook
{
	void OnPhase(XPlatWindowPhase phase, XPlatWindow window, Win32WindowBackend backend);
}
```

They live in the backend assembly, so core pays nothing, and they are unrelated to the portable
interfaces (§6.4). `XPlatApplication` runs the portable hooks and then hands the phase to the
backend, which runs its own loop over its typed interface:

```csharp
// IXPlatApplicationBackend, after the portable startup loop
void RunStartupHooks(XPlatApplication application);

// IXPlatWindowBackend, after the portable hooks for the phase
void RunWindowHooks(XPlatWindowPhase phase, XPlatWindow window);
```

Two seam members with empty bodies in a backend that has no typed hooks, and an indirect call that
disappears when it does nothing. In exchange no feature type is reachable from the backend, which is
the property a conditional lookup in the window creation path would have destroyed.

At application level the container already gets the layering right for free: it disposes singletons
in reverse creation order, and the startup loop creates every portable hook before the backend
creates any typed one, so typed application hooks are disposed first.

**A platform that cannot support a feature still declares it.** The extension method exists
everywhere so application code compiles, and the platform-neutral service reports `IsSupported` or
throws `PlatformNotSupportedException` from the operation. A silent no-op is how the macOS image
drag ended up arriving as its own URL string and reading as success.

## 7. The seam's own budget

The interface, the enumeration and the dispatch loop are paid by every application, forever. They
therefore get a size budget and a stability promise:

- Keep the seam to a handful of tiny types with no dependencies of their own.
- No feature-specific types may appear in the seam; a hook signature that names a drag payload or a
  menu item has already lost.
- Adding a hook point is a deliberate act with a measured cost, not a convenience.
- Target: the whole seam costs low single-digit kilobytes in the AOT binary. If a proposed hook
  point cannot meet that, it belongs on rung 3.
- Named exception: the action wrappers from §6.2 are seam types, so they are retained whether or
  not anything calls the overload. One class and one method each is the price of not forcing a class
  on a one-line hook, and it is small enough to pay once.
- Named exception: `RunStartupHooks` and `RunWindowHooks` (§6.5) exist only to keep features out of
  the backends. Both are empty where nothing is registered, and both are what lets a platform
  feature be reachable from the opt-in alone. Two members is the cheapest form of that, so they
  stay.

## 8. How we verify

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

## 9. Case study: drag and drop as merged

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
calling the opt-in. The shared payload types can stay where they are: they are small and inert, and
they cost nothing until something calls them.

`AllowDrop` and the four events are a different matter. Under §5 the surface for this feature should
be `using XPlatUI.DragDrop;`, `AllowDrop` as an extension property, and `AddDragEnterHandler`,
`AddDragOverHandler`, `AddDragLeaveHandler` and `AddDragDropHandler` as extension methods, which
takes those members off `XPlatControl` altogether. Whether to make that move on an API that has
already shipped is the drag and drop surface question in §12.

Note the trap: making `AllowDrop` the opt-in would *not* achieve this. `AllowDrop` lives on
`XPlatControl`, which is always retained, so the code behind it is reachable and kept regardless of
whether any application ever sets it. Per-control opt-in is a nice ergonomic layer on top; it is not
a substitute for an application-level opt-in.

## 10. Implementation plan

### Phase 0: the measurement fixture

Add a minimal application (one window, one label, no features) and the publish commands for the
three platforms, and record its AOT sizes in this document. Everything after this phase is judged
against those numbers. Nothing else is meaningful until this exists.

### Phase 1: the seam

- Add `IXPlatApplicationStartupHook` and invoke it in `XPlatApplication` after
  `BuildServiceProvider`.
- Add `IXPlatWindowHook` and `XPlatWindowPhase` around the window lifecycle, raising the six phases
  the model can raise today, with the shutdown point alongside
  `IXPlatApplicationBackend.Shutdown`.
- Add the two typed-phase seam members, `IXPlatApplicationBackend.RunStartupHooks` and
  `IXPlatWindowBackend.RunWindowHooks`, with empty bodies in every backend, so the platform half of
  §6.5 exists before any feature needs it.
- Keep every hook implementation-free: the seam names no feature.

Measure: the seam's own delta on the minimal app, which is the number the budget in §7 is checked
against.

### Phase 2: the event convention

- Replace every `event` declaration with an `Add`/`Remove` handler pair (§5.4), across `XPlatUI`, the
  four backends and the demo.
- There are 95 declarations and roughly a hundred subscription sites today, so this is one change
  rather than a rolling one: half converted leaves two shapes in the API.
- No behaviour change: same order, same thread, same exception path.

Measure: nothing. This phase is about shape, and the publish size should move only by the tokens it
removes.

### Phase 3: drag and drop as the first consumer

- Add the opt-in as a same-named extension in each backend (§6.5), registering whatever services and
  typed hooks the feature needs.
- Move `Win32DropTarget` construction and registration (`Win32WindowBackend.cs:575-576`) behind the
  Win32 window hook's `Created` phase, and its revoke (`:457`) behind `Closing`.
- Move `RegisterForDraggedTypes` (`AppKitSkiaView.cs:101`) behind the AppKit window hook's `Created`
  phase.
- Move `Gtk.DropTarget` construction (`Gtk4SkiaView.cs:409-410`) and removal (`:201-210`) behind the
  Gtk4 window hook's `Created` and `Closing` phases.
- Leave the payload types and the host routing where they are.
- Move `AllowDrop` and the four handler pairs to `XPlatUI.DragDrop` extensions, unless the drag and
  drop surface question in §12 says otherwise.

Measure: an application that does not call the opt-in must lose OLE, the COM target, the pasteboard
types and the GTK controllers, and the size must return to the Phase 0 baseline.

This phase is also the proof that the pattern works, so it should be done before any new feature is
held to the policy.

### Phase 4: documentation and review

- Point `README.md` at this policy from the drag and drop section and from wherever features are
  listed.
- Add the checklist in §11 to the review expectations for feature work.

### Phase 5: hold new work to it

Every subsequent feature states, in its pull request, which rung it sits on, why it could not sit
lower, and the measured cost for an application that does not use it.

## 11. Checklist for a new feature

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
- [ ] If the feature wants members on `XPlatControl` or a subclass, is it exposed as extension
	  members in its own namespace instead, with the exception stated if not?
- [ ] If it needs events, does it expose them as `AddXHandler`/`RemoveXHandler` extension methods?
- [ ] If it is native, is the implementation in each backend, is the registration a same-named
	  extension in the feature's namespace, and does each backend's always-on path name none of it?
	  Does `Headless` stay free of the extension?
- [ ] If a hook needs the concrete backend, is it a typed hook (§6.5) rather than a downcast, and is
	  it unrelated to the portable interface?
- [ ] Is there any C# `event` keyword in the change, including on interfaces and backend types? It
	  has to be an `AddXHandler`/`RemoveXHandler` pair instead (§5.4).
- [ ] If it needs per-control state, where does that state live, and what does each access cost?

## 12. Decisions and open questions

### Decided

- **Hook scoping.** Application hooks are singletons resolved from the root provider. Window hooks
  are window-scoped services resolved from `window.Services`, which gives each window its own
  instance and disposes it with the window. See §6.3 and §6.4.
- **Hook shape.** Marker interfaces resolved with `GetServices<T>()`, not a builder method on the
  application. `application.Enable<T>()` cannot be the whole opt-in, because the container is built
  in the `XPlatApplication` constructor (`XPlatApplication.cs:89-97`) and there is no moment when an
  application exists and services can still be registered. The call site is a named
  `services.AddX()` extension, and the `AddXPlatApplicationStartupHook(Action<XPlatApplication>)`
  overload covers hooks that are one line. See §6.2.
- **Hook disposal.** Both registration helpers take an optional `onDispose`, and the wrapper
  forwards it, so a lambda hook can own a disposable without a class. Hooks that are `IDisposable`
  are disposed by the container, windows before the application. See §6.2 and §6.4.
- **Platform-specific hooks.** Contract in core, implementation in each backend, selection by the
  project reference. The registration is a same-named extension in the feature's namespace, which
  makes a backend that forgets it a compile error rather than a missing feature. `Headless`
  declares none. See §6.5.
- **Typed hooks.** Each backend declares its own application and window hook interfaces naming its
  backend by its concrete type, and runs them from its own loop after the portable hooks. The
  portable and typed interfaces are unrelated types. See §6.5.
- **Window phases.** Created, Showing, Shown, Hiding, Hidden, Closing, Closed and Disposed, with
  Hiding and Hidden reserved until `Visible` and `Hide` exist. Ascending phases run hooks in
  registration order and descending phases in reverse. See §6.3.
- **Hook callback shape.** One method taking the phase, not one method per phase. A no-op base class
  can be added later for ergonomics without changing the interface.

### Open

These need answers before Phase 1, and the answers belong in this document:

1. **`AllowDrop` ergonomics.** Should setting `AllowDrop` implicitly opt the window in, on top of
   the application-level opt-in, or should drag and drop always be an explicit application-level
   choice?
2. **What "separate project" means.** A separate assembly in this repository, or a separate package
   with its own version and release? Rung 3 is only useful if it is cheap to take.
3. **The seam's size budget.** Low single-digit kilobytes is a guess. Phase 1 produces the real
   number, and this document should be corrected to it.
4. **Retrofitting.** Which existing features get moved onto the policy, and in what order? Menus,
   dialogs, timers and single instance all predate the policy and none has been measured.
5. **Moving the drag and drop surface.** `AllowDrop` and the four events shipped in the release
   before this policy existed. Do we move them to `XPlatUI.DragDrop` extensions, which is breaking
   but is what §5 asks for, or keep them and record the cost?
6. **The generic event args type.** The convention is written as `EventHandler<EventArgs<Y>>`, which
   means adding a generic args type. Is it `EventArgs<T>` (matches the sketch, sits next to
   `System.EventArgs` and will be confused with it) or `XPlatEventArgs<T>` (unambiguous, longer)?
7. **`RemoveXHandler` next to the token.** The `IDisposable` from `AddXHandler` already covers scope
   based removal. Is the explicit `RemoveXHandler` worth keeping for symmetry with the pattern, or
   does it just add surface?

## 13. Non-goals

- Retrofitting everything at once. The policy applies to new work, with drag and drop as the
  deliberate exception because it is the case study.
- Making an API awkward to hit a byte target. If the zero-cost shape would hurt the API, say so, take
  rung 2 or 3, and record the reason.
- A promise of literally zero cost. The standard is bare minimal, measured, and justified.
- A trimming/AOT test suite. The checks in §8 are a publish comparison and a warning review, run when
  a feature lands, not a new CI project.
