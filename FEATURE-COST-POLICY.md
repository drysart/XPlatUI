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

In order of preference. Each feature sits on the lowest rung it can actually reach, and the reason
it could not reach a lower one is written down. The one exception is a feature obscure enough that
keeping it out of the main package is worth more than the trimming it would have earned: that
belongs on rung 3 even if rung 1 or 2 would have worked, and the reason is written down the same
way.

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

The opt-in is coarse on purpose. It is a decision the application makes once, not a decision each
control or each window makes, so an application that calls it pays the feature's full runtime cost
even if it never exercises the feature, and that is accepted. Fine-grained gating would mean
conditioning the work on a value the application can change at any moment, which is the same
reachability problem §3 describes: the code behind the value is retained either way, so the gating
buys nothing at publish time and risks a feature that is broken half the time at run time.

### Rung 3: separate package

If neither of the above is practical, the implementation does not go into the base `XPlatUI`
projects at all. It goes into its own project that applications reference explicitly, so the base
deliverable never contains it and no amount of trimming cleverness is required.

The library ships as a small set of packages, and this rung is where the rest of them live:

| Package | Contents |
|---|---|
| `XPlatUI` | Everything platform-independent: the controls, the drawing model, the application and window types, and the hook seam. An application that uses only the common controls needs nothing else. |
| `XPlatUI.Backend.<platform>` | One per supported platform. Exactly one is referenced by an application, and it is the only place that platform's native code lives. |
| `XPlatUI.Feature.<feature>` | A feature that could not reach rung 1 or 2, or that is rare enough not to belong in the main package. One package covers every platform the feature supports. |

An application references `XPlatUI` and one backend. That is enough for a simple application using
the common controls, and it is what the project templates contain. Everything else is a reference
the application adds, which is the opt-in in its packaging form: a feature that is not referenced
cannot be used and cannot be in the deliverable.

A feature author writes one `<PackageReference>` with no `Condition`, on every platform. The package
carries one assembly per platform, and NuGet selects the right platform's assembly at restore, from
the consuming project's target framework. Nothing is dispatched at run time, and the platforms that
were not selected are not in the deliverable.

One consequence applies to every rung-3 feature: its surface is extension members, because a
separate assembly cannot add members to a type declared in `XPlatUI`. §5's shape is therefore
not a preference here but the only option, and a feature that genuinely needs a member on a core
type cannot take this rung at all.

#### The project layout

One project, four folders:

```
XPlatUI.Feature.<feature>/
    common/     portable code, compiled into every platform
    windows/    net10.0-windows
    macos/      net10.0-macos
    linux/      net10.0
```

The project multi-targets every platform the feature supports, and each inner build compiles
`common/` plus exactly one platform folder:

```xml
<PropertyGroup>
  <TargetFrameworks>net10.0-windows;net10.0-macos;net10.0</TargetFrameworks>
</PropertyGroup>

<ItemGroup>
  <Compile Remove="windows/**;macos/**;linux/**" />
  <Compile Include="windows/**" Condition="'$(TargetFramework)' == 'net10.0-windows'" />
  <Compile Include="macos/**" Condition="'$(TargetFramework)' == 'net10.0-macos'" />
  <Compile Include="linux/**" Condition="'$(TargetFramework)' == 'net10.0'" />
</ItemGroup>
```

Verified with a planted error in each platform folder in turn: a build narrowed to
`net10.0-windows` reported neither the `macos/` nor the `linux/` error, and a `net10.0` build
reported the `linux/` one and not the `windows/` one. Each folder is compiled on its own branch and
no other. The folders are named after the platform rather than the backend, so a feature author
chooses a folder by the operating system being supported, and which backend that folder targets
follows from the framework, since `net10.0-windows` can only mean Win32.

Sharing is a folder, not an assembly. `common/` compiles into each platform's output, so nothing has
to be `public` for the sake of another platform, there is no `InternalsVisibleTo`, and there is no
second assembly to place in each `lib/` folder. `#if` is still available inside `common/` for
differences too small to justify a file in each platform folder, and a feature with nothing portable
simply has an empty `common/`. The same `$(TargetFramework)` condition is how a platform's
`PackageReference` is applied, which is what produces the per-platform dependency groups below.

A consuming solution is simpler than either alternative. The project reference is one unconditional
line, and MSBuild builds only the inner build that matches the consuming project, so there is no
host detection and no condition choosing which platform to reference. Only the feature project's own
build enumerates all three, which matters in one case: a direct or solution build of the project
needs every platform's workload.

The `macos` workload is therefore a build requirement for this project. It is installed here and the
`macos` inner build succeeds, but a Windows machine without it fails with `NETSDK1147`, which is why
`XPlatUI.Backend.AppKit` is absent from `WinFormsApp3.slnx`. A feature project follows the same
rule: it stays out of the development solution, and the pack pipeline builds it on a machine with
all three workloads. For local work, `dotnet build -p:TargetFrameworks=net10.0-windows` narrows the
build to one platform without editing the file, and `-p:TargetFramework=net10.0` narrows it to one
framework. Both verified.

Pin the macOS target framework version rather than writing the floating `net10.0-macos`. The packed
`lib/` folder name carries whatever platform version the build machine's workload supplies, and an
unpinned build produced `lib/net10.0-macos26.0/`. A consumer on an older workload then resolves to a
lower platform version and does not match the folder.

#### Shipping: one pack

`dotnet pack` on the project produces the whole package, because each inner build contributes its
own `lib/` folder and its own dependency group:

1. One `lib/<tfm>/` folder per platform, each holding that platform's assembly. Nothing else needs
   to go in it, because `common/` was compiled into it.
2. One dependency group per platform, naming `XPlatUI` plus whatever feed packages that platform
   needs, generated from the same `$(TargetFramework)` conditions that selected the source folder.

Verified with the four-folder project above, carrying one `PackageReference` conditioned on the
Windows framework: one `dotnet pack` produced `lib/net10.0/`, `lib/net10.0-windows7.0/` and
`lib/net10.0-macos26.0/`, with an empty group for `net10.0`, an empty group for the macOS framework,
and a Windows group. No merge step, no hand-written groups, no `SuppressDependenciesWhenPacking`,
and no `NU5128`.

A dependency group per platform is what makes a platform-only package tolerable. Dependencies are
selected by the same nearest-framework rule as assemblies, so a macOS-only package listed in the
macOS group is never restored by a Windows application. Verified with a package carrying three
groups: a `net10.0-windows` consumer restored only the Windows dependency, a `net10.0` consumer only
the Linux one, and a `net10.0-macos` consumer only the macOS one.

What makes that safe is that every platform gets a group, even an empty one. When the matching group
is absent NuGet falls back to a compatible one, and `net10.0` is always compatible with
`net10.0-windows`, so a missing Windows group silently hands a Windows application the Linux
dependencies. Verified: a package with no Windows group restored the Linux dependency into a
`net10.0-windows` consumer, while an empty `<group targetFramework="net10.0-windows7.0" />`
restored nothing. Each group must also be the platform's whole closure rather than its extras,
because nothing is inherited from the `net10.0` group once a platform group is chosen.

The same rule governs the `lib/` folders, where the failure is worse because it is silent. NuGet
chooses one `lib/` folder per package per target framework, so a missing Windows folder is not an
error: the `net10.0-windows` consumer compiles against the `net10.0` folder instead, and a Windows
application ships the Linux assembly. Verified: a `net10.0-windows` consumer of a package with no
Windows folder compiled and printed the marker string from the `net10.0` assembly. A project that
multi-targets cannot produce that shape by accident, because each folder is written by the same
inner build that the `TargetFrameworks` list requires.

#### The `net10.0` fallback guard

A missing `lib/` folder cannot be caught at build time, so a feature's Linux build carries a runtime
guard: a module initializer that fails the process if the `net10.0` assembly is loaded anywhere but
Linux. The `net10.0` folder is the only one that needs it, because it is the only one that is
compatible with another platform's target framework.

```csharp
// linux/PlatformGuard.cs
internal static class PlatformGuard
{
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Verify()
    {
        if (!OperatingSystem.IsLinux())
        {
            Environment.FailFast(
                "XPlatUI.Feature.<feature>: the net10.0 (Linux) assembly was loaded on "
                + RuntimeInformation.OSDescription
                + ". The package is missing this platform's lib folder.");
        }
    }
}
```

`FailFast` rather than an exception, because this is a packaging bug rather than a condition an
application can handle. The alternative is a feature that misbehaves somewhere unrelated, and an
exception can be caught and masked. The process dies with a distinctive exit code and the message on
standard error. `OperatingSystem.IsLinux()` is a constant check with no reflection, so it is safe
under trimming and AOT.

Verified, with the guard deliberately set to fire:

| Build | Behaviour |
|---|---|
| framework-dependent | runs before `Main`, stack shows `<Module>..cctor()`, exit `0x80131623` |
| `PublishTrimmed` | runs before `Main`, exit `0x80131623` |
| `PublishAot` | runs during startup, stack shows `RunModuleInitializers`, exit `0xC0000409` |

The analyzer warns `CA2255`, that module initializers are intended for application code. That is the
intended use here, so it is suppressed at the attribute with a comment. The guard's own cost is one
method and one string in the Linux assembly, inside a package the application has already chosen to
reference.

The guard is a detector, not the guarantee. The guarantee is target framework selection, which is a
build-time decision; the guard exists because the failure it detects is otherwise silent. Two limits
are worth knowing:

- A module initializer does not keep its own assembly alive. If trimming inlines away everything
  reachable in the assembly, the assembly and its initializer are deleted together and the guard
  never runs. Verified: a library whose only used method returned a constant was eliminated and the
  guard did not fire, while the same library with one non-inlinable member survived and fired. A
  real feature assembly is not in danger, but the guard should not be the only code in its folder,
  and a test that exercises it is worth having.
- The guard runs when the assembly is loaded, which in a framework-dependent build is the first use
  of the feature. An application that never touches the feature never loads the assembly, and has
  nothing to guard.

Packaging does not change the namespace (§5.6). A feature in `XPlatUI.Feature.Clocks` is still
`namespace XPlatUI.Clocks`, so moving a feature between rung 2 and rung 3 does not touch application
code.

This rung has a cost of its own (a project to version and publish, a workload requirement to build
it, and a package to keep correct), so it is chosen for a stated reason rather than by default.

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
pair of methods taking an `Action`:

```csharp
public IDisposable AddDroppedHandler(Action<object, XPlatDragEventArgs> handler);
public void RemoveDroppedHandler(Action<object, XPlatDragEventArgs> handler);
```

The callback is the sender followed by the payload, and an event that carries no payload has no
second parameter. That is most of them:

```csharp
public IDisposable AddClosedHandler(Action<object> handler);
public IDisposable AddKeyDownHandler(Action<object, XPlatKeyEventArgs> handler);
```

No wrapper type is involved. The payload is whatever the event already carries, so a handler
signature is the delegate the event would have had, written out, and nothing exists only to be
unpacked again at the top of the lambda.

Subscribing then reads like a member of the control, and because the pair is made of methods it can
be written as extension methods for a feature:

```csharp
using var subscription = control.AddDroppedHandler((sender, e) => { ... });
control.RemoveDroppedHandler(handler);
```

Verified in a throwaway project: the pair works as extension members, both arities resolve, and
disposing the token unsubscribes.

This is a codebase-wide convention rather than a rule for opt-in features only. Core events on
`XPlatControl`, `XPlatWindow`, `XPlatApplication` and the backend interfaces use the same shape, so
there is one way to subscribe to anything.

Rules:

- The name is `Add` or `Remove`, then the event name, then `Handler`: `AddDroppedHandler`. The event
  name is the part that appears in documentation, so it keeps a normal event name.
- `AddXHandler` returns an `IDisposable` that removes the handler when disposed, so a scope can own a
  subscription without holding the handler itself. Disposing twice is a no-op, and so is disposing a
  subscription whose owner has already been torn down.
- `RemoveXHandler` stays alongside the token, because the two suit different call sites. A consumer
  that already holds the handler callback can pass it again rather than storing a token next to it,
  and a handler registered from a place with no natural scope has no obvious owner for a token
  anyway. The pair is also what makes the shape look like the event it replaces; a lone `Add` would
  read as a subscription that can only be ended by holding a token. Both are one line per event.
- Handlers run on the UI thread in subscription order. A handler that throws stops the ones after it,
  as a C# event would, and the exception reaches `XPlatApplication.UnhandledException`.
- The backing storage stays private and unchanged: one field per event, exactly as a field-like event
  has today, so a core event costs no more than it does now. The new cost is one small allocation per
  subscription for the returned token. A feature keeps its own subscriptions in its per-control state
  (§5.3), which is trimmed along with the feature.
- No `event` keyword anywhere, including interfaces and backend types. A review that finds one has
  found a bug in the convention.
- The payload is whatever the event already carries. `XPlatDragEventArgs`, `XPlatMouseEventArgs` and
  friends stay as they are, and a feature that needs a payload defines its own type rather than
  wrapping a value in a generic one.
- The `System.EventArgs` base on those classes is vestigial once nothing takes an `EventHandler`.
  Dropping it is cosmetic, and belongs with the Phase 2 change that touches them rather than with a
  feature.

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
2. **Run work**, after the container exists, at one of the phases in §6.3. This is the new hook.

A feature that needs both registers a service and a hook, in the same opt-in call.

### 6.2 The application hook

```csharp
public enum XPlatApplicationPhase
{
	Creating,
	Created,
	Stopping,
	Stopped,
}

public interface IXPlatApplicationHook
{
	void OnPhase(XPlatApplicationPhase phase, XPlatApplication application);
}
```

The application-level hook is a singleton, because the work it does is process-wide. Per-window work
has its own hook with its own lifetime; see §6.3, which also says when each phase fires and why they
are split where they are.

`XPlatApplication` resolves them once, immediately after `BuildServiceProvider`, and keeps the
instances for the rest of its life:

```csharp
_applicationHooks = Services.GetServices<IXPlatApplicationHook>().ToArray();

foreach (var hook in _applicationHooks)
{
	hook.OnPhase(XPlatApplicationPhase.Creating, this);
}
```

One lookup for the whole run, the same instances and the same order at every phase, and nothing
resolved while the application is tearing itself down.

The application opts in through the registration it already has:

```csharp
new XPlatApplicationOptions
{
	ConfigureServices = services => services.AddSingleton<IXPlatApplicationHook, DragDropHook>(),
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
public static IServiceCollection AddXPlatApplicationHook(
	this IServiceCollection services,
	XPlatApplicationPhase phase,
	Action<XPlatApplication> hookFunc,
	Action? onDispose = null)
{
	ArgumentNullException.ThrowIfNull(services);
	ArgumentNullException.ThrowIfNull(hookFunc);

	return services.AddSingleton<IXPlatApplicationHook>(
		_ => new ActionBasedAppHook(phase, hookFunc, onDispose));
}

/// <summary>Shorthand for a hook at <see cref="XPlatApplicationPhase.Creating"/>.</summary>
public static IServiceCollection AddXPlatApplicationStartupHook(
	this IServiceCollection services,
	Action<XPlatApplication> startupHookFunc,
	Action? onDispose = null) =>
	services.AddXPlatApplicationHook(XPlatApplicationPhase.Creating, startupHookFunc, onDispose);

internal sealed class ActionBasedAppHook : IXPlatApplicationHook, IDisposable
{
	private readonly XPlatApplicationPhase _phase;
	private readonly Action<XPlatApplication> _hookFunc;
	private readonly Action? _onDispose;

	public ActionBasedAppHook(
		XPlatApplicationPhase phase, Action<XPlatApplication> hookFunc, Action? onDispose)
	{
		_phase = phase;
		_hookFunc = hookFunc;
		_onDispose = onDispose;
	}

	public void OnPhase(XPlatApplicationPhase phase, XPlatApplication application)
	{
		if (phase == _phase)
		{
			_hookFunc(application);
		}
	}

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
says so in its own `Stopping` or `Stopped` callback rather than relying on `Dispose` ordering.

The trim property that makes this work: `GetServices<T>()` on a service nobody registered returns an
empty sequence, and the only thing retained is the interface itself. The concrete hook type is
reachable solely from the registration call, so an app without that call publishes without it.

### 6.3 Hook points and lifetimes

A single startup hook is not enough, because most expensive work is per window rather than per
process. There are two kinds of hook, with two lifetimes:

| Kind | Lifetime | Resolved from | What it is for |
|---|---|---|---|
| Application hook | Singleton | The root provider | Process-wide platform initialisation that must precede any window (OLE initialisation, application-level pasteboard types), and process-wide teardown after the loop returns. |
| Window hook | Window-scoped | `window.Services` | Per-window work: native registration once the handle exists (drop targets, extra pasteboard types, GTK controllers), and its teardown when the window closes. |

Each kind is one interface taking a phase value rather than one interface per moment, because one
instance serves every phase and a feature normally cares about more than one of them.

#### Application phases

| Phase | When it fires | Direction | Where |
|---|---|---|---|
| `Creating` | The container exists, the process services are installed, and no window exists yet. | Ascending | End of the `XPlatApplication` constructor, after `XPlatApplication.cs:100` |
| `Created` | Every window shown before `Run()` is visible, and the loop is about to start. | Ascending | `XPlatApplication.cs:283-287` |
| `Stopping` | The loop has returned. Every window, the backend and every service are still alive. | Descending | Start of `TearDown`, `XPlatApplication.cs:719` |
| `Stopped` | Every window is gone, and the services are about to be. | Descending | `XPlatApplication.cs:739-740` |

The splits are placed where they carry information:

- **`Creating` before `Created`** is everything the application does between building its container
  and entering its loop: creating windows, showing them, sizing the main one. Work that has to
  precede the first window needs `Creating`. Work that wants the finished window tree needs
  `Created`.
- **`Stopping` before `Stopped`** is ordering rather than capability. `Stopping` is the first thing
  that happens after the loop returns, so the whole application is still intact. `Stopped` is the
  last moment at which a singleton service can be resolved, because the container disposes the hooks
  next.

There is no phase after `Stopped`. Teardown is `IDisposable`: the container disposes every hook that
implements it (`XPlatApplication.cs:740`), after every window is gone (`:732-735`) and before the
backend shuts down (`:747`). A hook with something to release implements `IDisposable` or passes
`onDispose` (§6.2) rather than waiting for a callback. A window hook keeps a `Disposed` phase
because that is its last chance to reach other scoped services; an application hook has `Stopping`
and `Stopped` for the same purpose.

`Stopping` and `Stopped` are raised only when a loop ran, matching the guard `Exiting` already has
(`XPlatApplication.cs:723`). A forwarded second instance returns from `Run()` without entering one
(`:257-262`), so it sees `Creating` and nothing else. **`Creating` is therefore paired with
`Dispose`, not with `Stopping`:** the two are not guaranteed to both happen, and the teardown action
is the only one that is.

#### Window phases

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

**Ascending phases run hooks in registration order; descending phases run them in reverse.** In both
lists the split is the turn of a lifecycle: the phases that bring something up ascend, and the
phases that take it down descend. Reversing the teardown half is what makes a pair of hooks nest: if
`A` registers before `B`, `A` sets up before `B` and tears down after `B`, whatever order the phases
arrive in. The rule holds at application level as well, where the two hooks may not even live in the
same assembly.

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
- **Registration order is invocation order**, ascending in the phases that bring something to life
  and reversed in the phases that take it away (§6.3). Features may rely on the nesting this gives
  them, which is why it is written down rather than left to chance.
- **Runs on the UI thread.** Application hooks run on the thread that constructs and runs the
  application, before the loop starts and after it returns; window hooks run on the UI thread.
  Documented rather than assumed.
- **Per-window state lives on the hook instance.** A window hook is scoped, so each window gets its
  own instance and the created, shown, closing and closed callbacks share state without a table
  keyed by window. A hook that finds itself needing such a table is registered at the wrong
  lifetime.
- **Failures surface.** A hook that throws goes through the existing `UnhandledException` path
  rather than silently aborting startup.
- **Resolve from the right provider.** Application hooks come from the root provider; window hooks
  come from `window.Services`. Resolving a scoped hook from the root is the trap: MEDI either throws
  under scope validation or hands back a single instance that outlives every window.
- **Dependencies point one way.**
  singleton must never hold a window hook, because that would keep one window's instance alive for
  the life of the process.
- **Portable hooks are the outer layer.** They run before a backend's typed hooks (§6.5) in an
  ascending phase and after them in a descending one, so a typed hook always tears down before the
  portable hook that set it up.
- **Typed hooks never derive from portable ones.** An `IXPlatWin32WindowHook` that also implemented
  `IXPlatWindowHook` would be found by both loops and invoked twice.
- **A backend's window phase entry point survives its own `Dispose`.** The window's `Disposed` phase
  fires after `Backend.Dispose()` (`XPlatWindow.cs:386`) and before the scope goes (`:390`), so
  `RunWindowHooks` must touch nothing native by then and must not assume the backend is alive.
- **Application teardown is `IDisposable`, not a phase.** There is no application `Disposed` phase,
  because the container already disposes every hook that implements `IDisposable` at a well-defined
  point: after every window is gone (`XPlatApplication.cs:732-735`), before the backend shuts down
  (`:747`). By then the native window and the process-wide state may already be gone, so anything
  that has to run while it still exists belongs in `Stopping` or `Stopped`.

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
public interface IXPlatWin32ApplicationHook
{
	void OnPhase(
		XPlatApplicationPhase phase, XPlatApplication application, Win32ApplicationBackend backend);
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
// IXPlatApplicationBackend, after the portable hooks for the phase
void RunApplicationHooks(XPlatApplicationPhase phase, XPlatApplication application);

// IXPlatWindowBackend, after the portable hooks for the phase
void RunWindowHooks(XPlatWindowPhase phase, XPlatWindow window);
```

Two seam members with empty bodies in `Headless`, and nothing else in the always-on path: a hook
that nothing registers is trimmed away along with the registration. In exchange no feature type is
reachable from the backend, which is the property a conditional lookup in the window creation path
would have destroyed.

At application level the container already gets the layering right for free: it disposes singletons
in reverse creation order, and the `Creating` phase resolves every portable hook before the backend
resolves any typed one, so typed application hooks are disposed first.

**A rung-3 feature reuses all of this.** The typed interfaces live in the backend assembly, so a
feature package cannot add to them, and does not need to: the feature's platform folder references
the backend package, implements `IXPlatWin32WindowHook` there, and registers itself from its own
`Add<Feature>()`. The backend's loop finds it in the container whether the implementation came from
the backend or from a package that referenced the backend.

What changes is only where the registration lives. A backend must not reference a feature package,
so the same-named `Add<Feature>()` extension belongs to the feature instead, declared in each
platform folder (§4). The application still references one package per feature, unconditionally,
and forgetting a platform is still a compile error on that platform.

**A platform that cannot support a feature still declares it.** The extension method exists
everywhere so application code compiles, and the platform-neutral service reports `IsSupported` or
throws `PlatformNotSupportedException` from the operation. A silent no-op is how the macOS image
drag ended up arriving as its own URL string and reading as success.

## 7. The seam is an accepted cost

The interface, the enumeration and the dispatch loop are paid by every application, forever. That
cost is accepted rather than budgeted. The seam is what makes the extensibility points possible, so
there is no byte target for it and no hook is rejected for costing more than another. What the seam
gets instead is a stability promise and a shape rule:

- Keep the seam to a handful of tiny types with no dependencies of their own.
- No feature-specific types may appear in the seam; a hook signature that names a drag payload or a
  menu item has already lost.
- Adding a hook point is a deliberate act, not a convenience, and the reason it cannot be expressed
  as an extension member (§5) is recorded in the pull request.

The seam's cost is still reported (§8), because a number that only ever grows unnoticed is how a
small seam becomes a large one. It is a trend to watch, not a threshold to pass.

Two of the seam's types are retained whether or not anything uses them, deliberately:

- The action wrappers from §6.2 are seam types, so they are retained even when no application calls
  the overload. One class and one method each is the price of not forcing a class on a one-line
  hook.
- `RunApplicationHooks` and `RunWindowHooks` (§6.5) exist only to keep features out of the backends.
  Both are empty where nothing is registered, and both are what lets a platform feature be reachable
  from the opt-in alone. They stay.

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

Target state: an application that never calls the opt-in publishes without OLE initialisation,
without a COM drop target, without registered pasteboard types and without GTK controllers. The
shared payload types can stay where they are: they are small and inert, and they cost nothing until
something calls them.

`AllowDrop` and the four events are a different matter, and moving them is not a judgement call.
Their surface becomes `using XPlatUI.DragDrop;`, `AllowDrop` as an extension property, and
`AddDragEnterHandler`, `AddDragOverHandler`, `AddDragLeaveHandler` and `AddDragDropHandler` as
extension methods, which takes those members off `XPlatControl` altogether.

Rung 3 forces it. A feature package is a different assembly, and an extension member is the only way
one assembly can add a member to a type declared in another, so the members cannot stay on
`XPlatControl` and still ship separately. The breaking change is a precondition of the rung rather
than a cost weighed against the benefit of §5's shape. See the drag and drop surface entry in §12.

Note the trap: making `AllowDrop` the opt-in would *not* achieve this. `AllowDrop` lives on
`XPlatControl`, which is always retained, so the code behind it is reachable and kept regardless of
whether any application ever sets it. Per-control opt-in is a nice ergonomic layer on top; it is not
a substitute for an application-level opt-in.

The opt-in is coarse in the other direction too, and deliberately so. Once an application calls
`AddDragDrop()`, every window gets its drop target in the `Created` phase whether or not any control
ever sets `AllowDrop`, and nothing in the backend reads `AllowDrop` to decide whether to register.
`AllowDrop` can be set, cleared and set again on a control that is already inside a shown window, so
there is no correct moment at which the native registration could be made conditional on it. Gating
on it would buy a per-window saving in exchange for a feature that stops working the first time
someone toggles the property.

## 10. Implementation plan

### Phase 0: the measurement fixture

Add a minimal application (one window, one label, no features) and the publish commands for the
three platforms, and record its AOT sizes in this document. Everything after this phase is judged
against those numbers. Nothing else is meaningful until this exists.

The SkiaSharp and NativeAOT scaffolding every AOT application needs lives in XPlatUI's own
`buildTransitive/` assets, so the fixture is an ordinary small project rather than a copy of the
demo's project file. Its only Skia-specific line names its own assembly for `SkiaNativeLoader`.

### Phase 1: the seam

- Add `IXPlatApplicationHook` and `XPlatApplicationPhase`, and raise the four phases from
  `XPlatApplication`: `Creating` at the end of the constructor, `Created` immediately before
  `Backend.Run()`, `Stopping` at the start of `TearDown`, and `Stopped` immediately before
  `_provider.Dispose()`.
- Add `IXPlatWindowHook` and `XPlatWindowPhase` around the window lifecycle, raising the six phases
  the model can raise today.
- Add the two typed-phase seam members, `IXPlatApplicationBackend.RunApplicationHooks` and
  `IXPlatWindowBackend.RunWindowHooks`, with empty bodies in every backend, so the platform half of
  §6.5 exists before any feature needs it.
- Keep every hook implementation-free: the seam names no feature.

Measure: the seam's own delta on the minimal app, and record it as the baseline for later phases.
There is no target to compare it against (§7); it is the reference point that shows whether the seam
is growing.

### Phase 2: the event convention

- Replace every `event` declaration with an `Add`/`Remove` handler pair (§5.4), across `XPlatUI`, the
  four backends and the demo.
- Payload-less events, which are most of them, become `Action<object>`. The rest become
  `Action<object, TPayload>` with the args class they already have. No new payload types.
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
- Move `AllowDrop` and the four handler pairs off `XPlatControl` into `XPlatUI.DragDrop` extensions.
  This is the one breaking change in the plan, and rung 3 requires it: an assembly cannot add
  members to a type declared in another assembly. See §12.
- Do not gate any of the above on `AllowDrop`. The window hook registers unconditionally, and
  `AllowDrop` only decides whether the control accepts what arrives.

Measure: an application that does not call the opt-in must lose OLE, the COM target, the pasteboard
types and the GTK controllers, and the size must return to the Phase 0 baseline.

This phase is also the proof that the pattern works, so it should be done before any new feature is
held to the policy.

### Phase 4: documentation and review

- Point `README.md` at this policy from the drag and drop section and from wherever features are
  listed.
- Carry the migration for the surface move in `README.md`, as a before-and-after of the two things
  an application does with the feature: setting `AllowDrop` and subscribing to the four events.
- Add the checklist in §11 to the review expectations for feature work.
- Keep the package layout in §4 current, so a feature's pull request can name the folder it belongs
  in rather than inventing one.
- Add the feature pack job to the pipeline, on a machine with the Windows, macOS and Linux
  workloads, so no feature author has to arrange a multi-platform build.

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
- [ ] If the feature gates per-window or per-control work on a runtime value, can the application
	  change that value after the window is shown? If so, the work has to happen unconditionally.
- [ ] Is the feature's code reachable only from the opt-in call, and therefore trimmed when the call
	  is absent?
- [ ] Was the publish size measured for an app that does not use it, on all three platforms, and
	  quoted in the pull request?
- [ ] Were the trim/AOT warnings for the new code reviewed?
- [ ] If it could not be made opt-in, is it in a separate package instead, with the reason recorded?
- [ ] If the feature wants members on `XPlatControl` or a subclass, is it exposed as extension
	  members in its own namespace instead, with the exception stated if not?
- [ ] If it needs events, does it expose them as `AddXHandler`/`RemoveXHandler` extension methods?
- [ ] If it is native, is the implementation in each backend or in the feature's platform folder,
	  is the registration a same-named extension in the feature's namespace, and does each backend's
	  always-on path name none of it? Does `Headless` stay free of the extension?
- [ ] If a hook needs the concrete backend, is it a typed hook (§6.5) rather than a downcast, and is
	  it unrelated to the portable interface?
- [ ] Is there any C# `event` keyword in the change, including on interfaces and backend types? It
	  has to be an `AddXHandler`/`RemoveXHandler` pair instead (§5.4).
- [ ] If it ships as its own package, does every platform get a `lib/` folder and a dependency
	  group, is the group the platform's whole closure, and is an empty group present where a
	  platform needs no extra packages?
- [ ] If it ships as its own package, does the `net10.0` assembly carry the fallback guard (§4), and
	  is it in a platform folder with other code rather than alone?
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
  `services.AddX()` extension, and the `AddXPlatApplicationHook` overload covers hooks that are one
  line, with `AddXPlatApplicationStartupHook` as its `Creating` shorthand. See §6.2.
- **Hook disposal.** Both registration helpers take an optional `onDispose`, and the wrapper
  forwards it, so a lambda hook can own a disposable without a class. Hooks that are `IDisposable`
  are disposed by the container, windows before the application, and that is the only application
  teardown point: there is no `Disposed` application phase. See §6.2, §6.3 and §6.4.
- **Platform-specific hooks.** Contract in core, implementation in each backend, selection by the
  project reference. The registration is a same-named extension in the feature's namespace, which
  makes a backend that forgets it a compile error rather than a missing feature. `Headless`
  declares none. See §6.5.
- **Typed hooks.** Each backend declares its own application and window hook interfaces naming its
  backend by its concrete type, and runs them from its own loop after the portable hooks. The
  portable and typed interfaces are unrelated types. See §6.5.
- **Application phases.** Creating, Created, Stopping and Stopped, raised from the constructor, the
  entry to the loop, and the two points inside `TearDown`. `Stopping` is split from `Stopped` for
  ordering, with `Stopped` the last point at which a singleton service can be resolved, and
  `Creating` from `Created` so a feature can run before the first window exists. There is no
  `Disposed` phase: a hook with teardown work implements `IDisposable` and the container calls it.
  See §6.3.
- **Window phases.** Created, Showing, Shown, Hiding, Hidden, Closing, Closed and Disposed, with
  Hiding and Hidden reserved until `Visible` and `Hide` exist. Ascending phases run hooks in
  registration order and descending phases in reverse. See §6.3.
- **Hook callback shape.** One method taking the phase, not one method per phase. A no-op base class
  can be added later for ergonomics without changing the interface.
- **Event shape.** `AddXHandler`/`RemoveXHandler` pairs taking `Action<object, TPayload>`, or
  `Action<object>` when the event carries no payload. No `event` keyword, and no generic args
  wrapper: the payload is the type the event already has, or there is none. Both halves are kept,
  because a consumer holding the callback can remove by identity instead of storing the token. See
  §5.4.
- **Packaging.** `XPlatUI` plus `XPlatUI.Backend.<platform>` is the whole requirement for an
  application using the common controls. Rung 3 is one `XPlatUI.Feature.<feature>` package per
  feature, referenced deliberately and unconditionally, and it is also where a feature goes when it
  is simply too rare to belong in the main package. The feature is one multi-targeted project with
  four folders, `common/`, `windows/`, `macos/` and `linux/`, of which each inner build compiles
  `common/` plus one platform folder; sharing is a folder rather than an assembly, so there is no
  `InternalsVisibleTo` and nothing to copy between `lib/` folders. One `dotnet pack` produces the
  package, because the SDK writes one `lib/` folder and one dependency group per target framework.
  Building it needs all three workloads, so the project stays out of the development solution. A
  platform-only NuGet dependency is tolerable because dependencies are grouped per platform and
  selected by target framework, so a macOS-only package is never restored by a Windows application;
  every platform therefore gets a group, empty if it needs nothing, since a missing group falls back
  to the `net10.0` one, and a missing `lib/` folder silently ships the wrong assembly. The Linux
  assembly carries a module-initializer guard that fails the process if it loads on another
  platform. Namespaces do not change with the packaging. See §4.
- **Seam cost.** The hook seam is accepted rather than budgeted, because it is what makes the
  extensibility points possible. There is no byte target, and no hook point is rejected on size
  alone; the seam keeps a stability promise and a shape rule instead, and the types it retains
  unconditionally are named. Its cost is still measured so that growth is visible. See §7.
- **Opt-in granularity.** Opting in is a per-application decision, and it buys the whole feature:
  once it is called, every window gets the feature's per-window setup regardless of any runtime
  value such as `AllowDrop`, because such a value can change after the window is shown and there is
  no correct moment to gate on it. An application that opts in and then never uses the feature pays
  for it, and that is accepted. See §4 and §9.
- **Retrofitting.** Drag and drop is the only feature being moved onto the policy. It is the case
  study, so it carries the work of proving the mechanism. Menus, dialogs, timers and single instance
  all predate the policy and stay as they are; they may be moved later, but nothing is planned for
  them and no part of Phases 0 to 5 is sized for them. See §10 and §13.
- **Drag and drop surface.** `AllowDrop` and the four events leave `XPlatControl` and become
  extension members in `XPlatUI.DragDrop`, and that breaking change is accepted. It is not a trade
  against §5's ergonomics: a rung-3 feature ships in its own assembly, and an assembly cannot add
  members to a type declared in another assembly, so the extension surface is what makes the rung
  possible in the first place. See §9 and §10.

### Open

None. Every question raised while writing this document has been answered, and the answers are
above. A new one goes here when it arises, rather than being settled silently in a review thread.

## 13. Non-goals

- Retrofitting everything at once. The policy applies to new work, and drag and drop is the only
  existing feature being moved onto it, because it is the case study. Menus, dialogs, timers and
  single instance keep their current shape until there is a reason to revisit them.
- Source compatibility for the drag and drop surface. It moves to extensions because rung 3 requires
  it, and the migration is documented rather than avoided.
- Making an API awkward to hit a byte target. If the zero-cost shape would hurt the API, say so, take
  rung 2 or 3, and record the reason.
- A promise of literally zero cost. The standard is bare minimal, measured, and justified.
- A trimming/AOT test suite. The checks in §8 are a publish comparison and a warning review, run when
  a feature lands, not a new CI project.
