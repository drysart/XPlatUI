# XPlatUI application model — working design

An application model for XPlatUI, so that an **application** is one project that builds and runs on
Windows, macOS and Linux — instead of one project per platform.

The library today stops at `XPlatUiHost`: a platform-independent object that owns a control tree, a
client size, input routing, focus, selection and painting. Everything *around* it — creating an OS
window, running a message loop, installing the clipboard/theme/context-menu services, loading the
native Skia library, reacting to DPI and appearance changes — is written three times, once per
harness, in three different styles. This document designs the layer that absorbs all of it.

Status of this document: **draft for review. Design only — no code has been written.** Decisions are
numbered **D1**–**D11**; the ones already settled are marked **decided**, and the ones that still
need a call are collected in §12.

---

## 1. Where we are today

`XPlatUiHost` is already the right seam. The three harnesses differ only in the plumbing around it:

| Responsibility | Win32 harness | GTK harness | AppKit harness |
|---|---|---|---|
| Native Skia load | `SkiaNativeLoader.EnsureLoaded()` + embedded native blob | package-provided | package-provided |
| Window creation | `HarnessWindow` (759 lines, P/Invoke) | `Gtk.ApplicationWindow` | `NSWindow` subclass |
| Message loop | `GetMessage`/`DispatchMessage` | `GLib.MainLoop` via `Gtk.Application.Run` | `NSApplication.Run` |
| Surface | `HarnessSurface` (private) | `GtkUiSurface` (369 lines) | `MacUiView` (691 lines) |
| Clipboard | `Win32Clipboard` | `GtkClipboard` | `NSPasteboard` wrapper |
| System appearance | registry read + `WM_SETTINGCHANGE` | `Gtk.Settings` | `NSApp.EffectiveAppearance` |
| Context menu | `TrackPopupMenu` | `Gtk.Popover` | `NSMenu` |
| Default font | `Segoe UI` | GTK system font | `NSFont.SystemFontOfSize(0)` |
| DPI | `SetProcessDpiAwarenessContext` + `WM_DPICHANGED` | scale factor from monitor | `BackingScaleFactor` |
| Idle/tick | `SetTimer` | `GLib.Timeout` | `NSTimer` |
| Menu bar | none | none | `MacMenuBar` (67 lines) |
| Headless render | `--render` in `Program.cs` | `--render` | `--render` |

The duplication is real but shallow: `WinFormsApp3/Program.cs` (81), `GtkHarness/Program.cs` (169),
`MacHarness/Program.cs` (184). Each is 80% identical in *intent* and 0% identical in *code*.

**The goal:** an application author writes `XPlatUI/…` code plus one `Program.cs`, and the platform
choice is a project-file concern, not a source concern.

---

## 2. North star

```csharp
// Program.cs — the whole thing. Identical on Windows, macOS and Linux.
using SkiaSharp;
using XPlatUI;

var app = new XPlatApplication(Backend.Create())
{
	MainWindow = new XPlatWindow("XPlatUI demo", new MainView())
	{
		MinimumClientSize = new SKSize(820, 660),
	},
};

return app.Run();
```

```csharp
// Backend.cs — the only file in the app that knows the platform exists.
#if WINDOWS
using XPlatUI.Backend.Win32;
#elif MACOS
using XPlatUI.Backend.AppKit;
#else
using XPlatUI.Backend.Gtk4;
#endif
using XPlatUI.Backend.Headless;

internal static class Backend
{
	public static IXPlatApplicationBackend Create(bool headless = false)
	{
		if (headless)
		{
			return new HeadlessApplicationBackend();
		}
#if WINDOWS
		return new Win32ApplicationBackend();
#elif MACOS
		return new AppKitApplicationBackend();
#else
		return new Gtk4ApplicationBackend();
#endif
	}
}
```

Two source files. No `#if` in application logic. `--render` passes `headless: true` and paints the same
control tree offscreen; `--dark`, `--light`, DPI, appearance tracking, clipboard, context menus and the
caret timer all come from the model.

The `#if` symbols above are measured, not assumed: `WINDOWS` is defined for `net10.0-windows`, `MACOS` for `net10.0-macos`, and neither for a portable `net10.0` build on either host (§6.1).

---

## 3. Design decisions

| # | Topic | Decision | Rationale |
|---|---|---|---|
| **D1** | Type names — **decided** | `XPlatApplication`, `XPlatWindow`, and the `XPlat`-prefixed supporting types in §4: `XPlatApplicationOptions`, `XPlatScreen`, `XPlatTimer`, `XPlatMenuBar`, `XPlatShutdownMode`, `XPlatDpiAwareness`, `XPlatWindowState`, `XPlatDialogService`, `XPlatDialogResult`. `XPlatUiHost` keeps its name. | The namespace is already `XPlatUI`, so a `UI` segment inside a type name is redundant, and it would have put both spellings (`XPlatUiHost` vs `XPlatUIWindow`) in one namespace. `XPlat` + noun matches every existing type (`XPlatControl`, `XPlatPanel`, `XPlatTheme`, `XPlatClipboard`). |
| **D2** | Where backends live — **decided** | One project per platform: `XPlatUI.Backend.Win32`, `XPlatUI.Backend.AppKit`, `XPlatUI.Backend.Gtk4`, `XPlatUI.Backend.Headless`. | Matches the existing harness boundaries 1:1 (lift `Native/`, `MacUiView`, `GtkUiSurface` wholesale). Keeps `XPlatUI` itself `net10.0` + OS-free, which is what makes it AOT-friendly and trimmable. A single multi-TFM backend project would force `net10.0-macos` onto Linux machines. |
| **D3** | Backend selection — **decided** | Compile-time `#if` in the app's own `Backend.cs`, as in §2. No reflection, no assembly scanning, no module initializer. | Trimming and AOT delete code nothing references; reflection-based discovery needs `DynamicDependency` attributes and silently breaks under `PublishTrimmed`. An explicit factory is four lines and always visible in the call stack. |
| **D4** | Service ownership — **decided** | The **application owns a service container** and is the composition root (§5). The static ambient slots (`XPlatClipboard.Current`, `XPlatSystemTheme.Current`, `XPlatContextMenuPresenter.Current`, `XPlatTheme.FontFamily`, `XPlatTheme.Palette`) stay, but move to the **bottom** of the resolution chain as fallbacks rather than being the only mechanism. | The earlier answer — "have the app install the statics at startup" — made the app the single writer, which was the real fix for three harnesses disagreeing by hand. It did not let a backend contribute its services without the app naming each one, and it could not give two windows their own presenter. Keeping the statics as fallbacks is what makes the change cost six call sites instead of every control (§5.9). |
| **D5** | Host per window | Exactly one `XPlatUiHost` per `XPlatWindow`, owned by the window, exposed as `window.Host`. | Already true in all three harnesses. Multi-window therefore needs no change to the host, the control tree, focus, selection or input routing. |
| **D6** | Message loop ownership | The **backend** runs the loop; the application owns the lifecycle around it. `Run()` blocks on the UI thread and returns an exit code. | Each platform has a different loop (Win32 `GetMessage`, GLib `MainLoop`, `NSApplication.Run`). Pretending otherwise (a hand-rolled pump) would break native modal dialogs, menu tracking and the macOS app menu. |
| **D7** | Shutdown semantics — **decided** | Three modes — `OnLastWindowClose` (default), `OnMainWindowClose`, `OnExplicitShutdown` — see §4.7. macOS keeps the process alive with zero windows only when the app opts into `OnExplicitShutdown`. | The platform convention genuinely differs (a Mac app with no windows still owns the menu bar), but a cross-platform default must be predictable. The three names match WinForms, so nobody has to learn a new vocabulary. |
| **D8** | Headless backend — **decided** | `XPlatUI.Backend.Headless` is a **first-class backend**, not a harness special case, and is delivered in migration step 2. The app reaches it through the same explicit factory as the others, so `--render` needs no `#if` of its own. | The render path already exists three times over, one copy per harness, and it is the only display-free check CI has. One implementation makes it testable on any machine and forces the model to work with no window server, which is exactly what migration step 2 depends on. |
| **D9** | Application project shape — **decided** | One `csproj`, one TFM per OS chosen by an MSBuild condition on `TargetFramework`, backend pulled in by a conditional `ProjectReference`. | This is the only arrangement that gives "one project builds everywhere" without forcing the macOS workload onto non-Mac machines. See §6. |
| **D10** | Multi-window — **decided** | Multi-window is supported. `MainWindow` is identity and convenience, never a requirement: `Run()` is legal with zero windows. See §4.6. | No platform requires a window to run its loop, but each needs a keep-alive when there is none. Making `MainWindow` mandatory would be a fiction the backends have to work around. |
| **D11** | Service container — **decided** | `Microsoft.Extensions.DependencyInjection` is the container. The public surface is `IServiceCollection` / `IServiceProvider`; the model adds exactly one extension, `AddWindowScoped`. See §5. | Reverses an earlier hand-written-container proposal, which was based on two claims that measurement disproved: MEDI 10.0.14 is AOT-annotated and warning-free, and it costs +666 KB on a SkiaSharp AOT binary rather than a megabyte (§5.2). The decisive argument is interop: every ecosystem package registers through `IServiceCollection`, so anything else forces an adapter and a second service graph. Window scope is the one gap MEDI leaves, and it closes in ~40 lines (§5.4). |
| **D12** | Dialogs in v1 — **decided** | Message box, open/save/folder dialogs and modal `XPlatWindow.ShowDialog` ship in v1, through `IXPlatDialogService` (§9). Every backend must therefore support a nested loop from migration step 1. | "Are you sure", "open", "save" are in every real app; deferring them leaves apps calling P/Invoke or pulling a third-party package against a window they do not own. The cost is paid once, in the contract, and the headless backend makes the nested path testable. |
| **D13** | Menu bar in v1 — **decided** | `XPlatMenuBar` ships in v1, per window, mirrored to the global bar on macOS (§10). | A macOS app with no menu bar is not merely unpolished, it is broken: Cmd+Q, Cmd+W and the Edit-menu clipboard shortcuts come from the menu bar, so text boxes cannot copy or paste without one. Windows and Linux want the same model drawn as a per-window bar. |

---

## 4. The model

### 4.1 `XPlatApplication`

Owns the message loop, the window list and the platform services. One per process;
`XPlatApplication.Current` is the ambient handle controls and services use to reach it.

```csharp
namespace XPlatUI;

public sealed class XPlatApplication
{
	public XPlatApplication(IXPlatApplicationBackend backend);
	public XPlatApplication(IXPlatApplicationBackend backend, XPlatApplicationOptions options);

	public static XPlatApplication? Current { get; }
	public static int Run(XPlatApplication application);   // convenience for `return ...;`

	public IXPlatApplicationBackend Backend { get; }
	public XPlatApplicationOptions Options { get; }
	public IServiceProvider Services { get; }          // application root provider; see §5
	public bool IsRunning { get; }
	public int ExitCode { get; }

	public XPlatWindow? MainWindow { get; set; }
	public IReadOnlyList<XPlatWindow> Windows { get; }
	public IReadOnlyList<XPlatScreen> Screens { get; }
	public IXPlatDialogService Dialogs { get; }        // never null: the backend must register one (§9)

	public event EventHandler? Startup;
	public event EventHandler<XPlatExitEventArgs>? Exit;                        // cancelable
	public event EventHandler<XPlatWindowEventArgs>? WindowOpened;
	public event EventHandler<XPlatWindowEventArgs>? WindowClosed;
	public event EventHandler? Activated;                                         // dock / taskbar click
	public event EventHandler? ThemeChanged;                                      // light/dark flipped
	public event EventHandler<XPlatUnhandledExceptionEventArgs>? UnhandledException;  // cancelable

	public int Run();
	public void Exit();                       // == Exit(0)
	public void Exit(int exitCode);
	public void Activate();                   // bring MainWindow forward, or the app if there is none

	public bool CheckAccess();                // true when on the UI thread
	public void Invoke(Action action);        // synchronous marshal to the UI thread
	public void BeginInvoke(Action action);   // asynchronous marshal to the UI thread
}
```

`Run()` returns the exit code so `Main` can `return app.Run();`. Calling it twice throws; calling
`Exit()` before `Run()` makes the loop return immediately (the standard "cancel startup" escape).

### 4.2 `XPlatApplicationOptions`

Configuration that every platform needs but that is not window-specific.

```csharp
public sealed class XPlatApplicationOptions
{
	public string? ApplicationName { get; set; }                 // menu bar, taskbar, about box
	public XPlatShutdownMode ShutdownMode { get; set; } = XPlatShutdownMode.OnLastWindowClose;
	public bool FollowSystemAppearance { get; set; } = true;     // light/dark tracking
	public bool? ForcedDarkMode { get; set; }                    // --dark / --light
	public string? FontFamily { get; set; }                      // null = platform default
	public XPlatDpiAwareness DpiAwareness { get; set; } = XPlatDpiAwareness.PerMonitor;
	public TimeSpan TickInterval { get; set; } = TimeSpan.FromMilliseconds(250);  // caret blink
	public bool AttachConsole { get; set; }                      // Windows: diagnostics to parent console
	public XPlatMenuBar? MenuBar { get; set; }                 // application-wide default; see §10
	public Action<IServiceCollection>? ConfigureServices { get; set; }   // see §5.3
}
```

`--render` / `--dark` / `--light` command-line parsing is deliberately **not** in the model: it is a
`XPlatApplicationOptions` factory that the headless backend and the sample apps share.

### 4.3 `XPlatWindow`

One OS window. Thin: it forwards to an `IXPlatWindowBackend` and raises the cross-platform events.
It does **not** duplicate `XPlatUiHost`; `window.Host` *is* the host.

```csharp
public sealed class XPlatWindow : IDisposable
{
	public XPlatWindow();
	public XPlatWindow(XPlatControl content);
	public XPlatWindow(string title, XPlatControl content);

	public XPlatControl? Content { get; set; }     // becomes Host.Root
	public XPlatUiHost Host { get; }
	public XPlatApplication? Application { get; }
	public IServiceProvider Services { get; }   // this window's scope; see §5.4
	public IXPlatWindowBackend Backend { get; }      // platform escape hatch; see §12
	public XPlatScreen? Screen { get; }
	public float ScaleFactor { get; }

	public string Title { get; set; }
	public SKSize ClientSize { get; set; }             // logical px
	public SKSize MinimumClientSize { get; set; }
	public SKSize MaximumClientSize { get; set; }
	public SKPoint Location { get; set; }              // logical px, screen space
	public bool Resizable { get; set; }
	public bool TopMost { get; set; }
	public XPlatWindowState State { get; set; }      // Normal/Minimized/Maximized/FullScreen
	public XPlatWindow? Owner { get; set; }
	public XPlatMenuBar? MenuBar { get; set; }       // null = inherit XPlatApplicationOptions.MenuBar

	public event EventHandler? Shown;
	public event EventHandler<CancelEventArgs>? Closing;   // cancelable
	public event EventHandler? Closed;
	public event EventHandler? Activated;
	public event EventHandler? Deactivated;
	public event EventHandler? ClientSizeChanged;
	public event EventHandler? ScaleChanged;               // moved to a monitor with a different DPI
	public event EventHandler? StateChanged;

	public SKPoint LogicalToScreen(SKPoint logical);   // menus and popups need screen space
	public void Show();
	public void Show(XPlatWindow owner);
	public XPlatDialogResult ShowDialog(XPlatWindow? owner);   // modal, pumps a nested loop
	public void Activate();
	public void Close();                                            // raises Closing, then Closed
	public void Dispose();
}
```

`Show()` before `Application.Run()` is legal: the window is created, but `Shown` is deferred until
the loop starts. This keeps `MainWindow = new XPlatWindow(...)` inside the object initialiser.

**Sizing.** If `ClientSize` is never set, the window sizes to `Content.DesiredSize` clamped to the
screen working area — the same "design size × scale, then clamp" logic that lives in the Win32
harness today. That logic moves into the model so all three platforms agree.

**`ScaleFactor` and `ScaleChanged`.** A window dragged between monitors changes DPI; `ClientSize`
stays logical, the framebuffer changes size. The backend reports it, the model re-runs layout and
invalidates. This is the same contract `XPlatUiHost.SetClientSize` already implements; it just needs
a cross-platform trigger.

### 4.4 Screens

```csharp
public sealed class XPlatScreen
{
	public SKRect Bounds { get; }         // logical px
	public SKRect WorkingArea { get; }    // excludes taskbar / dock / menu bar
	public float ScaleFactor { get; }
	public bool IsPrimary { get; }
}
```

`Application.Screens` exists so window placement, default sizing and "centre on the owner's screen"
are platform-independent. Today the Win32 harness does this inline and macOS/GTK do it differently.

### 4.5 Timers

```csharp
public sealed class XPlatTimer : IDisposable
{
	public XPlatTimer();
	public XPlatTimer(TimeSpan interval);
	public TimeSpan Interval { get; set; }
	public bool Enabled { get; set; }
	public event EventHandler? Tick;      // always on the UI thread
	public void Start();
	public void Stop();
	public void Dispose();
}
```

The application owns one internal timer at `Options.TickInterval` for caret blinking and drives it
through `Host.ProcessTick`. A single timer for all windows keeps the idle cost flat and matches the
existing `ProcessTick(TimeSpan)` contract. Public `XPlatTimer` is the seam animations and polling
will use later; it is a thin wrapper over the same platform timer.

### 4.6 Main window and multi-window

Multi-window is supported. `XPlatWindow` instances are independent: each owns its own `XPlatUiHost`,
its own control tree, its own focus and its own framebuffer. Nothing in the model assumes there is
exactly one.

#### Is a main window required?

**No.** No platform requires a window to be able to run its message loop. But each one needs
*something else* to stay alive with zero windows, and the model has to supply it:

| Platform | Loop with zero windows? | What is actually required |
|---|---|---|
| Windows | Yes — `GetMessage` / `MsgWaitForMultipleObjects` block happily with no `HWND` at all. | Something to post to in order to wake the loop: a message-only window (`HWND_MESSAGE`) or `PostThreadMessage`. The model creates a message-only window lazily when the last window closes under `OnExplicitShutdown`, and destroys it on exit. |
| macOS | Yes — `NSApplication.Run()` runs with no windows; the menu bar stays and the app stays in the Dock. This is the normal state after closing every window. | A real `.app` bundle with an `Info.plist`, plus `NSApp.SetActivationPolicy(.Regular)`. Without the bundle the process runs as an accessory/background app: no Dock icon, cannot become frontmost. `NSApp.MainWindow` simply becomes `nil` and the menu bar keeps working. |
| GTK4 | **No** — `Gtk.Application` quits when its last window is removed, because every window holds an application reference. | `GApplication.Hold()` to keep the app alive with zero windows, released by `Release()` before quitting. `Gtk.Application.ApplicationId` should also be set, for single-instance and DBus behaviour. |

So `MainWindow` is **identity and convenience, never a requirement**:

- it is the default `Owner` for `ShowDialog()` when no owner is passed;
- it is the window `XPlatApplication.Activate()` brings forward;
- it is the window re-shown when the user clicks the Dock icon or a second instance launches;
- it is the window `OnMainWindowClose` watches;
- on Windows its `HWND` is what taskbar grouping and `WM_DROPFILES` associate with.

Rules:

- `MainWindow` is nullable, and `Run()` is legal with no windows at all; the loop then runs until
  `Exit()`.
- If `MainWindow` is null when the first window is shown, that window becomes `MainWindow`.
- Closing `MainWindow` does **not** clear it, so an app can `Show()` it again later — that is the
  "reopen from the Dock" pattern. `Windows` is the authority on what is currently live.
- `XPlatApplication.Activated` is raised when the OS asks the app to come forward (Dock click,
  taskbar click, second-instance launch, `open -a`). The default action re-shows and activates
  `MainWindow`; handling the event suppresses the default.

#### Multi-window rules

- `Windows` returns a snapshot, so it is safe to enumerate while closing windows.
- `WindowOpened` fires after `Show()` succeeds; `WindowClosed` fires after `Closed` and after the
  window has left `Windows`.
- `Owner` must be set before `Show()`; setting it on a visible window throws
  `InvalidOperationException`. An owned window always sits above its owner, minimizes with it and is
  closed with it. `Show(owner)` is shorthand for setting `Owner` then `Show()`.
- `ShowDialog()` disables the owner and runs a nested loop; it returns once the window has closed.
- Process-wide services (`XPlatClipboard`, `XPlatSystemTheme`, `XPlatTheme`) are shared by every
  window. The context-menu presenter is per window, which the existing interface already implies.
- There is **one** tick timer for the process. It drives every visible, non-minimized window's
  `Host.ProcessTick`, so a hidden or minimized window costs nothing.
- Focus is per window. Closing the focused window does not clear another window's
  `Host.FocusedControl`.
- `ThemeChanged` is broadcast to every window, which then repaints.
- Windows may be created at any time on the UI thread, including from inside an event handler.
  `XPlatApplication.Current` is set for the whole process, so a control can reach the app without
  being handed one.

### 4.7 Shutdown modes

```csharp
public enum XPlatShutdownMode
{
    OnLastWindowClose,   // default
    OnMainWindowClose,
    OnExplicitShutdown,
}
```

| Mode | The loop ends when | Keep-alive needed with zero windows |
|---|---|---|
| `OnLastWindowClose` | the last entry in `Windows` closes | no |
| `OnMainWindowClose` | `MainWindow` closes | no — the loop ends while `MainWindow` still exists, so the keep-alive is never reached |
| `OnExplicitShutdown` | `Exit()` is called | **yes** — message-only window on Windows, `GApplication.Hold()` on GTK4, nothing on macOS |

`Exit()` closes every window first: each raises `Closing`, and cancelling **any** of them aborts the
shutdown (this matches WinForms, where `Application.Exit()` respects `FormClosing`). The
`XPlatApplication.Exit` event runs last and can also cancel. `ExitCode` is set by `Exit(int)`, or by
the backend when the platform ends the loop on its own (for example the macOS app menu's Quit).

---

## 5. Services and dependency injection

The application is the composition root. It owns a service container built on
`Microsoft.Extensions.DependencyInjection`, and three parties register into it: the backend
(platform services), the app (`Options.ConfigureServices`), and the model (its own defaults).
Controls reach the result through the same parent chain they already use to reach their host.

### 5.1 What is already a service

Three things in the library are pluggable today, and together they are the entire platform-service
surface:

| Service | Interface | Registered by | Read by |
|---|---|---|---|
| Clipboard | `IXPlatClipboard` | the backend | `XPlatTextBox` cut/copy/paste, `XPlatUiHost.CopySelection` |
| System appearance | `IXPlatSystemTheme` | the backend | `XPlatSystemTheme.Refresh` |
| Context menus | `IXPlatContextMenuPresenter` | the backend | `XPlatContextMenu.Show` |

That is six call sites in four files. Making them injectable is not the expensive part of this
design; §5.9 covers the part that is. `XPlatTheme` is deliberately *not* on the list, and §5.8
explains why.

### 5.2 Why MEDI, and what changed my mind

An earlier draft of this section proposed a hand-written container, on the grounds that MEDI costs
size and warns under AOT. Both halves of that were measured, and both were wrong.

**AOT and trimming are not a problem.** `Microsoft.Extensions.DependencyInjection` 10.0.14 carries
the .NET 10 `IsAotCompatible` assembly metadata. A project published with `PublishAot=true` and
`VerifyReferenceAotCompatibility=true`, using `AddSingleton<TService, TImpl>()`, `AddScoped`,
`AddTransient` and `BuildServiceProvider`, produces **zero** IL2xxx and zero IL3058 warnings. The
control case shows the analyzer is live rather than asleep: the same project referencing
`Newtonsoft.Json` 13.0.3 emits IL3058 immediately.

**Size is real, bounded, and smaller in context.** NativeAOT `win-x64` publish, measured:

| Probe | No MEDI | With MEDI | Delta |
|---|---|---|---|
| Console app, four services | 1,144 KB | 2,166 KB | +1,022 KB |
| Console app + SkiaSharp + XPlatUI | 1,475 KB | 2,141 KB | **+666 KB** |
| `WinFormsApp3` AOT harness as it ships today | 7.59 MB | ≈8.2 MB | ≈ +8.8% |

Only ~36 KB of the delta is MEDI's own IL. The rest is CoreLib machinery its resolve path needs -
reflection invoke, generic dictionaries, interface dispatch maps, constructed EE types - and a
SkiaSharp app already keeps most of that, which is why the marginal cost in a real XPlatUI binary
is 666 KB rather than a megabyte.

**And the argument this document was not weighing is interop.** The .NET ecosystem registers through
`IServiceCollection`: `AddLogging`, `AddOptions`, `AddHttpClient`, `AddDbContext`, `AddSerilog`,
`AddMediatR`, `AddRefitClient`, `AddHealthChecks` - all of them are extension methods on that one
interface. A container that is not `IServiceCollection` does not fail loudly; it forces an adapter or
a second container, and the app ends up with two service graphs and a seam between them. A UI
framework that makes the rest of .NET awkward to use has traded the wrong 666 KB.

**What MEDI genuinely does not do** is one thing: a scope created *with a payload*. `CreateScope()`
gives you a scope, not a scope bound to a window. §5.4 closes that in about forty lines inside the
model, so an app never sees it.

### 5.3 What the app sees

The public surface is MEDI's own, so ecosystem packages drop in unchanged:

```csharp
var app = new XPlatApplication(backend, new XPlatApplicationOptions
{
	ConfigureServices = services =>
	{
	    services.AddLogging(b => b.AddDebug());                   // any MEDI package
	    services.AddSingleton<IMainViewModel, MainViewModel>();
	    services.AddWindowScoped<IDocumentViewModel>(
	        (window, _) => new DocumentViewModel(window.Title));  // the one XPlat extension
	},
});
```

| Surface | Type | Notes |
|---|---|---|
| `XPlatApplicationOptions.ConfigureServices` | `Action<IServiceCollection>` | runs after the backend, before model defaults |
| `IXPlatApplicationBackend.RegisterServices` | `void (IServiceCollection)` | the backend's platform services (§5.6) |
| `XPlatApplication.Services` | `IServiceProvider` | the root provider |
| `XPlatWindow.Services` | `IServiceProvider` | that window's scope |
| `XPlatControl.Services` | `IServiceProvider?` | host → window scope → root; null when detached |
| `AddWindowScoped<TService>` | `IServiceCollection` extension | the only XPlat-specific registration (§5.4) |

`XPlatUI` references `Microsoft.Extensions.DependencyInjection`, so `IServiceCollection` and every
package's `Add*` extension arrive transitively and an app does not add the reference itself.

Two rules survive from the earlier draft:

- **Resolve never throws.** Use `GetService<T>()` - null when absent - not `GetRequiredService<T>()`.
  A control that was never attached to a window must still work, and the headless backend must
  render with no clipboard and no presenter.
- **Registration order is fixed**: backend, then `Options.ConfigureServices`, then model defaults.
  Last registration wins, so an app overrides a platform service by registering it again - which is
  how the Windows harness's `--classic-menus` switch becomes a one-line app registration instead of
  a static flag.

### 5.4 Three lifetimes, and why window scope needs a helper

MEDI's three lifetimes map one-to-one onto the model, which is itself an argument for using it:
every .NET developer already knows the vocabulary.

| Lifetime | MEDI call | One per | Examples | Disposed |
|---|---|---|---|---|
| Application | `AddSingleton` | process | `IXPlatClipboard`, `IXPlatSystemTheme`, `IXPlatDialogService`, app view models | root provider dispose (§8) |
| Window | `AddScoped` | window | `IXPlatContextMenuPresenter`, per-window view models, menu bar host | that window's scope dispose |
| Transient | `AddTransient` | resolve | short-lived helpers, option objects | by the caller |

The context-menu presenter is what forces the window lifetime. Today it is constructed as
`new Win32ContextMenuPresenter(window.Handle, window.LogicalToScreen)` and is bound to one `HWND`
for its whole life, so neither a singleton nor a flat container can express it.

The missing primitive is a scope created *with* the window. MEDI has `CreateScope()` but no
`CreateScope(payload)`, so the model carries a mutable accessor inside the scope and fills it in
immediately after creating the scope:

```csharp
// XPlatUI/Services/XPlatWindowScope.cs - the whole mechanism.
internal sealed class XPlatWindowAccessor { public XPlatWindow? Window { get; set; } }

public static class XPlatServiceCollectionExtensions
{
	/// <summary>One instance per window, built with the window, disposed when it closes.</summary>
	public static IServiceCollection AddWindowScoped<TService>(
	    this IServiceCollection services, Func<XPlatWindow, IServiceProvider, TService> factory)
	    where TService : class
	    => services.AddScoped<TService>(sp => factory(
	        sp.GetRequiredService<XPlatWindowAccessor>().Window
	            ?? throw new InvalidOperationException(
	                   "A window-scoped service was resolved outside a window scope."),
	        sp));
}

// XPlatWindow, when the platform creates it:
_scope = _application.Services.CreateScope();
_scope.ServiceProvider.GetRequiredService<XPlatWindowAccessor>().Window = this;
Services = _scope.ServiceProvider;
```

Verified under `PublishAot` with the AOT analyzers on: zero warnings, and two windows resolve
independent instances. Because it is `AddScoped` underneath, a window-scoped service can depend on
singletons normally, and anything the *app* registers `AddScoped` also becomes per-window - which is
the behaviour a view-model-per-window author expects.

It also answers the escape-hatch question left open in §12: the factory receives the `XPlatWindow`,
whose `Backend` is the platform's own `IXPlatWindowBackend`, so a backend registers native services
without the model ever knowing what an `HWND` is.

### 5.5 How a control reaches its services

`XPlatControl` already resolves its host up the parent chain:

```csharp
public XPlatUiHost? Host => _host ?? Parent?.Host;
```

Services use the same chain, so the accessor is the same one-liner:

```csharp
public sealed class XPlatControl
{
	public XPlatUiHost? Host => _host ?? Parent?.Host;
	public IServiceProvider? Services => Host?.Services;
	public TService? GetService<TService>() where TService : class
	    => Services?.GetService(typeof(TService)) as TService;
}
```

`XPlatUiHost.Services` is set by the window that owns the host. Lookup for a platform service then
reads:

    control → host → window scope → root provider → static `Current` → built-in fallback

The static slot stays in that chain on purpose. It is how a detached control still gets a clipboard,
and it is why the migration in §11 needs no flag day: existing harnesses keep assigning
`XPlatClipboard.Current` and keep working while the backends move over.

The call sites become one-line fallbacks:

```csharp
// XPlatTextBox
(GetService<IXPlatClipboard>() ?? XPlatClipboard.Current).SetText(SelectedText);

// XPlatContextMenu.Show(host, ...) - the host is already a parameter
var presenter = host.Services?.GetService<IXPlatContextMenuPresenter>()
    ?? XPlatContextMenuPresenter.Current;
```

### 5.6 The backend registers the platform services

This is the part that pays for itself immediately. Today all three harnesses hand-install the same
services, in the same order, with the same fallbacks, in `Program.cs`:

```csharp
XPlatClipboard.Current = new Win32Clipboard();
XPlatSystemTheme.Current = new Win32SystemTheme();
XPlatSystemTheme.Refresh();
XPlatContextMenuPresenter.Current = new Win32ContextMenuPresenter(window.Handle, window.LogicalToScreen);
```

Under the model the backend registers them and the app never mentions them. A single
`RegisterServices` replaces the five `Create*`/`Dialogs` members §7 used to carry, which also means
a new platform service no longer changes `IXPlatApplicationBackend` and breaks every backend:

```csharp
// XPlatUI.Backend.Win32
public void RegisterServices(IServiceCollection services) => services
	    .AddSingleton<IXPlatClipboard, Win32Clipboard>()
	    .AddSingleton<IXPlatSystemTheme, Win32SystemTheme>()
	    .AddWindowScoped<IXPlatContextMenuPresenter>((window, _) =>
	        new Win32ContextMenuPresenter(
	            ((Win32WindowBackend)window.Backend).Handle, window.LogicalToScreen));
```

The generic `AddSingleton<TService, TImpl>()` form is the AOT-safe one: the implementation type
carries `DynamicallyAccessedMembers`, so the trimmer keeps its constructor and no warning is
produced. See §5.10 for the rest of the safe subset.

### 5.7 What the container is not for

- **Controls are constructed, not resolved.** `new XPlatButton { ... }` stays how the tree is built,
  exactly as `DemoUi` does it. A container that builds controls has to answer "who sets `Parent`?",
  and every answer is worse than `Controls.Add`. Services are collaborators, not the tree.
- **No service location during paint.** Resolve in a constructor or an event handler. A
  `GetService` inside `OnPaint` is a dictionary lookup per frame and a lifetime bug waiting to
  happen.
- **The container being thread-safe does not make controls thread-safe.** MEDI's provider is safe to
  resolve from any thread; `XPlatControl` state is not. `Invoke`/`BeginInvoke` (§4.1) are still how
  other threads reach the UI.
- **It does not own the app's architecture.** No convention-based registration, no decorators, no
  interceptors. An app that wants those layers Scrutor or a source generator on top - and should
  read §5.10 first, because assembly scanning is the one MEDI-adjacent pattern that is not AOT-safe.

### 5.8 Why `XPlatTheme` stays static

`XPlatTheme` is read 67 times across 11 files, nearly all of them inside paint methods, and it is
not really a service: it is branding state, the way `SystemColors` and WPF's merged resource
dictionaries are process-wide. Injecting it would thread a parameter through every paint pass that
is the same object for every control in the process, and would make the documented one-liner for
dark mode - `XPlatTheme.Palette = XPlatPalette.Dark` - impossible without a provider handle.

So it stays static and stays out of the container. If per-window theming is ever wanted, the move
is to make `XPlatTheme` a facade over an `XPlatThemeData` instance in the application scope, with
`XPlatControl.Theme` defaulting to it: one class plus an accessor, not 67 call sites. That is
deliberately not v1.

### 5.9 What it costs

| Change | Where | Size |
|---|---|---|
| `Microsoft.Extensions.DependencyInjection` package reference | `XPlatUI.csproj` | +666 KB on a SkiaSharp AOT binary |
| `XPlatWindowScope` + `AddWindowScoped` | 1 new file in `XPlatUI/Services/` | ~40 lines |
| Platform services read from the provider | `XPlatControl`, `XPlatUiHost`, `XPlatTextBox`, `XPlatContextMenu` | 6 call sites |
| `Services` threaded through host and window | `XPlatUiHost`, `XPlatWindow`, `XPlatApplication` | ~20 lines |
| Backends register instead of exposing `Create*` | 3 backends | moves code, no new code |
| Harness `Program.cs` shrinks | 3 harnesses | ~6 lines each |

The 67 `XPlatTheme` reads are untouched, and that is the whole reason this is affordable now rather
than after the control set grows.

Against the hand-written container an earlier draft proposed, the trade is +666 KB and one package
dependency, in exchange for every MEDI-based package working unchanged, keyed services, `IOptions`,
scope validation, `IDisposable`/`IAsyncDisposable` tracking, and a container that is not ours to
debug. If binary size ever becomes the binding constraint, `Services` is only an `IServiceProvider`,
so a smaller provider can be swapped in behind it - the assumption is confined to
`XPlatUI/Services/`.

### 5.10 AOT and trimming guardrails

`XPlatUI` is already `IsAotCompatible`, which turns on `EnableTrimAnalyzer`, `EnableAotAnalyzer` and
`EnableSingleFileAnalyzer` for the library; apps get the same by setting `PublishAot`. Three
additions:

- **`VerifyReferenceAotCompatibility`** in the sample apps, so IL3058 flags any dependency that
  ships without the .NET 10 AOT metadata. Expect exactly one warning and do not blanket-suppress it:
  **SkiaSharp 4.154.0 does not carry the metadata** (measured), even though it works under AOT.
  Suppress that one assembly by name, with a comment saying why.
- **The AOT-safe MEDI subset**: `AddSingleton<TService, TImpl>()`, `AddSingleton<TService>(instance)`,
  `AddSingleton/AddScoped/AddTransient(Func<IServiceProvider, TService>)`, `AddWindowScoped`, and
  `BuildServiceProvider`. Avoid the three that warn: `ActivatorUtilities.CreateInstance` on types
  without `DynamicallyAccessedMembers`, `GetService(Type)` with a type built at runtime, and
  assembly scanning.
- **No `Microsoft.Extensions.Hosting`.** A UI app owns its own lifetime; `IHost` brings
  configuration, logging providers, hosted services and a shutdown model that fights §4.7. Use
  `ServiceCollection` + `BuildServiceProvider` directly, which is what §5.3 shows.

---

## 6. One project, three platforms

### 6.1 Project file

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
	<OutputType>Exe</OutputType>
	<Nullable>enable</Nullable>
	<PublishAot>true</PublishAot>
	<IsAotCompatible>true</IsAotCompatible>
	<!-- Flags dependencies that ship without .NET 10 AOT metadata. Expect exactly one
	     warning (SkiaSharp) and suppress that assembly by name (§5.10). -->
	<VerifyReferenceAotCompatibility>true</VerifyReferenceAotCompatibility>

	<!-- Exactly one TFM per OS: net10.0-macos needs the macOS workload and Xcode, which only
		 exist on a Mac, so it must never appear in a TFM list that Windows has to evaluate. -->
	<TargetFramework Condition="$([MSBuild]::IsOSPlatform('windows'))">net10.0-windows</TargetFramework>
	<TargetFramework Condition="$([MSBuild]::IsOSPlatform('osx'))">net10.0-macos</TargetFramework>
	<TargetFramework Condition="$([MSBuild]::IsOSPlatform('linux'))">net10.0</TargetFramework>
	<TargetFramework Condition="'$(TargetFramework)' == ''">net10.0</TargetFramework>
  </PropertyGroup>

  <ItemGroup>
	<ProjectReference Include="..\XPlatUI\XPlatUI.csproj" />
	<ProjectReference Include="..\XPlatUI.Backend.Headless\XPlatUI.Backend.Headless.csproj" />
  </ItemGroup>
  <!-- Microsoft.Extensions.DependencyInjection arrives transitively from XPlatUI (§5.3). -->

  <ItemGroup Condition="'$(TargetFramework)' == 'net10.0-windows'">
	<ProjectReference Include="..\XPlatUI.Backend.Win32\XPlatUI.Backend.Win32.csproj" />
  </ItemGroup>
  <ItemGroup Condition="'$(TargetFramework)' == 'net10.0-macos'">
	<ProjectReference Include="..\XPlatUI.Backend.AppKit\XPlatUI.Backend.AppKit.csproj" />
  </ItemGroup>
  <ItemGroup Condition="'$(TargetFramework)' == 'net10.0'">
	<ProjectReference Include="..\XPlatUI.Backend.Gtk4\XPlatUI.Backend.Gtk4.csproj" />
  </ItemGroup>
</Project>
```

Neither platform symbol is guesswork: both were measured by printing `$(DefineConstants)` from a build
on each machine, and by compiling `#if` probes.

| TFM (host) | Platform symbols defined |
|---|---|
| `net10.0-windows` (Windows) | `WINDOWS`, `WINDOWS7_0`, `WINDOWS7_0_OR_GREATER` |
| `net10.0-macos` (macOS) | `MACOS`, `__MACOS__`, `__UNIFIED__`, `MACOS26_0`, ... |
| `net10.0` portable (either host) | none |

`MACOS` is therefore correct as written and no `<DefineConstants>` fallback is needed.
`__MACOS__` is defined too — it is the older Xamarin name — but `MACOS` is preferred because it
matches `WINDOWS`. Linux has no OS-specific TFM at all (there is no `net10.0-linux`), so the
`#else` branch in `Backend.cs` is the only way to express it, not a shortcut.

The same measurement is what makes the `IsOSPlatform` block load-bearing rather than tidy: a
portable `net10.0` build **on a Mac** defines neither symbol, so `#if MACOS` there would silently
fall through to the GTK4 branch and produce a Linux binary on a Mac. Selecting one TFM per OS is what
keeps `Backend.cs` honest.

### 6.2 What each OS builds

| OS | TFM | Backend | Publish |
|---|---|---|---|
| Windows | `net10.0-windows` | Win32 | `dotnet publish -r win-x64` → single-file AOT exe |
| macOS | `net10.0-macos` | AppKit | `dotnet publish -r osx-arm64` → `.app` bundle |
| Linux | `net10.0` | GTK4 | `dotnet publish -r linux-x64` |
| Headless (CI, tests) | any of the above | Headless | In-process; renders a PNG, never opens a window |

CI keeps one job per OS, each on its own runner, each doing build → headless render → publish. No
change to the existing `.github/workflows/macos.yml` shape.

**Rejected alternative:** list all three TFMs unconditionally and pass `-f`. It makes a Windows
`dotnet build` try to restore the macOS workload, which fails on every machine without Xcode. The
`IsOSPlatform` condition costs one line and never fails.

### 6.3 Where the sample apps land

The three harnesses become **three thin wrappers over the model** during the migration (§11) and are
eventually deleted, replaced by one `XPlatUIDemo.App` project that is multi-TFM per §6.1.
`XPlatUIDemo` (the shared sample control tree + `RenderToPng`) is already the cross-platform content
and does not change.

---

## 7. Backend contract

```csharp
public interface IXPlatApplicationBackend
{
	void Initialize(XPlatApplication application);
	int Run();                                  // enter the platform loop; return the exit code
	void Quit(int exitCode);
	void Invoke(Action action, bool synchronous);

	IXPlatWindowBackend CreateWindow(XPlatWindow window);

	// Platform services, registered once before the app configures its own. See §5.6.
	void RegisterServices(IServiceCollection services);

	IReadOnlyList<XPlatScreen> GetScreens();
	void Shutdown();
}
```

```csharp
public interface IXPlatWindowBackend : IDisposable
{
	XPlatUiHost Host { get; }          // the backend creates and owns the host for its surface
	float ScaleFactor { get; }
	XPlatScreen? Screen { get; }

	string Title { get; set; }
	SKSize ClientSize { get; set; }            // logical px
	SKSize MinimumClientSize { get; set; }
	SKSize MaximumClientSize { get; set; }
	SKPoint Location { get; set; }
	bool Resizable { get; set; }
	bool TopMost { get; set; }
	XPlatWindowState State { get; set; }
	XPlatMenuBar? MenuBar { get; set; }         // per window; macOS mirrors the active bar

	void Show(IXPlatWindowBackend? owner);
	void ShowDialog(IXPlatWindowBackend? owner);
	void Activate();
	void Close();                              // request close; the window raises Closing
	void SetTitleBarAppearance(bool dark);     // dark chrome on macOS, dark caption on Windows

	// Raised by the platform; forwarded to XPlatWindow.
	event EventHandler? Shown;
	event EventHandler? CloseRequested;
	event EventHandler? Closed;
	event EventHandler? Activated;
	event EventHandler? Deactivated;
	event EventHandler? ClientSizeChanged;
	event EventHandler? ScaleChanged;
	event EventHandler? StateChanged;
}
```

`RegisterServices` replaces the `CreateSystemTheme` / `CreateClipboard` / `CreateContextMenuPresenter`
/ `Dialogs` members an earlier draft carried. A flat list of `Create*` methods makes every new
platform service a breaking change to `IXPlatApplicationBackend`; one registration hook does not. It
also makes the platform services lazy, so the headless backend never constructs a clipboard it will
not use.

**Input stays where it is.** The backend forwards pointer, key and text events straight into
`Host.ProcessMouseMove/Down/Up/Leave`, `ProcessKeyDown/Up`, `ProcessTextInput`, `ProcessContextMenu`
and `SetClientSize` — exactly as the harnesses do now. The model does not interpose on input; it
only adds the lifecycle, services and loop around it.

**Dialogs and menus are in the contract from day one** (D12, D13). `IXPlatDialogService` arrives
through `RegisterServices`, so the dialog implementations live in the backend without widening this
interface. What the contract does require is that a backend tolerate a **nested loop**: `ShowMessage`,
`OpenFile` and `ShowDialog` re-enter the platform loop and return only when the modal session ends,
while `Invoke` callbacks still dispatch to the same UI thread. `MenuBar` is per window; the AppKit
backend mirrors the active window's bar into the global one, which is the only place the two
presentations diverge.

---

## 8. Lifecycle

```
Main()
  └─ new XPlatApplication(backend, options)
	   ├─ backend.Initialize(this)          native Skia, DPI awareness, console, app object
	   ├─ build the container               ServiceCollection → backend.RegisterServices →
	   │                                    Options.ConfigureServices → BuildServiceProvider
	   ├─ install service slots             XPlatClipboard.Current, XPlatSystemTheme.Current,
	   │                                    XPlatContextMenuPresenter.Current, XPlatTheme.FontFamily
  └─ app.Run()
	   ├─ Startup                            cancellable via Exit()
	   ├─ show windows shown before Run()   Shown fires; MainWindow defaults to the first
	   ├─ backend.Run()                      ← the platform message loop
	   │    ├─ input  → Host.Process*        routed to the window that received it
	   │    ├─ tick   → Host.ProcessTick     one timer, every visible non-minimized window
	   │    └─ theme  → XPlatSystemTheme.Refresh() + ThemeChanged + invalidate all windows
	   ├─ loop ends: Exit() called, ShutdownMode condition met, or the platform quits
	   ├─ Exit event                         cancellable
	   └─ teardown, in this order:
			1. dismiss open context menus / popups
			2. close and dispose every window, then dispose that window's service scope
			3. dispose the tick timer
			4. dispose the root provider (disposes application-scoped services), then clear the static slots
			5. backend.Shutdown()
  └─ return ExitCode
```

Teardown order matters and is exactly what is hand-coded (differently) in all three harnesses today.
Step 2's "detach the surface before disposing the host" is the macOS `CGImage`/`SKSurface` lifetime
rule that the README documents; centralising it removes a class of crash-on-exit bug.

**Threading.** Everything public on `XPlatApplication`, `XPlatWindow` and `XPlatControl` is
UI-thread only. `Invoke`/`BeginInvoke` are the only cross-thread entry points, and `CheckAccess()`
lets application code assert. No internal locking: the existing library is single-threaded and
staying that way is a feature.

---

## 9. Dialogs

```csharp
public interface IXPlatDialogService
{
	XPlatDialogResult ShowMessage(XPlatMessageBoxOptions options, XPlatWindow? owner);
	string? OpenFile(XPlatFileDialogOptions options, XPlatWindow? owner);
	string? SaveFile(XPlatFileDialogOptions options, XPlatWindow? owner);
	string? PickFolder(XPlatFileDialogOptions options, XPlatWindow? owner);
}
```

Backed by `MessageBoxW` / `GetOpenFileNameW` on Windows, `NSAlert` / `NSOpenPanel` on macOS, and
`Gtk.AlertDialog` / `Gtk.FileDialog` on GTK. The service arrives through `RegisterServices` (§5.6)
rather than as a nullable member on the backend interface, so the headless backend supplies a scripted
implementation instead of a native one — which is also how modal-dialog code paths become testable.

`ShowDialog` on a window is a *nested* message loop on all three platforms: the modal session re-enters
the loop `Run()` started and returns when the dialog closes. That is why the nested-loop requirement is
part of the backend contract from migration step 1 (§7) instead of being bolted on later — it is the
first feature that makes "the backend owns the loop" observable, and retrofitting it after three
backends exist is how modal bugs get baked in.

Dialogs are **v1** (D12). Concretely:

- Every backend, headless included, registers an `IXPlatDialogService` in `RegisterServices` (§5.6).
- `XPlatWindow.ShowDialog` is a real modal window on all three platforms, not a model-side loop.
- The headless backend supplies a scripted service, so modal code paths are testable with no display.

`XPlatApplication.Dialogs` (§4.1) is the convenience accessor; `Services.GetService<IXPlatDialogService>()`
is the general path a control or view model uses (§5.5).

---

## 10. Everything else the model should own

The question "what else might such a model need?" is really "what do the three harnesses currently
do that an application should not have to". Inventory, with a recommendation for each. The three
tiers below are *feature* priorities; they are not the same axis as the migration steps in §11.
Tier 1 **is** the v1 scope (D12, D13).

### Tier 1 — required for the model to be worth having

| Feature | Why it belongs in the model |
|---|---|
| Message loop + lifecycle | The whole point. |
| Window creation, show/close, sizing, placement | Currently 759 lines of P/Invoke on Windows alone. |
| Platform-service bootstrap (Skia native load, clipboard, system theme, context-menu presenter, default font) | Three harnesses each wire it by hand; a missed one is a silent null-ref. |
| DPI awareness + `ScaleChanged` | `SetProcessDpiAwarenessContext` must run before any window; the model owns "before". |
| Single tick timer → `ProcessTick` | Caret blink, and the seam for animation later. |
| `Screens` + working-area clamping | Default window size/placement is currently platform-specific. |
| `Exit`/`ExitCode`, `ShutdownMode`, cancellable `Exit` | Needed to make `Run()` return sanely. |
| `Invoke`/`BeginInvoke`/`CheckAccess` | The only safe cross-thread entry point. |
| `UnhandledException` | An exception on the UI thread otherwise kills the loop with no hook. |
| Headless backend + `--render` | CI depends on it today; it is the only display-free test path. |
| Message box + file/folder dialogs | Every app needs "open", "save", "are you sure"; deferring it means apps reach for P/Invoke. v1 (D12). |
| Menu bar (`XPlatMenuBar`) | macOS needs one to look native *and* to get Cmd+Q, Cmd+W and the Edit-menu clipboard shortcuts; Windows and Linux want it per window. One model, two presentations. v1 (D13). |

### Tier 2 — needed to be usable for real apps

| Feature | Why |
|---|---|
| `XPlatTimer` (public) | Animations, polling, debounce — without it every app rolls its own `SetTimer`. |
| Window icons | Title bar + taskbar; three different APIs. |
| `Activated`/`Deactivated`, `TopMost`, `Owner` | Taskbar grouping and modality on Windows; window ordering on macOS. |
| App-scoped single instance / re-activate | Common enough that every app writes it wrong. |

### Tier 3 — native feel

| Feature | Why |
|---|---|
| Accelerator/shortcut dispatch | Currently each harness has its own (`SendAccelerator` on macOS). |
| Drag & drop + file open | OS-level, per platform, frequently requested. |
| Clipboard image/file formats | `XPlatClipboard` handles text today. |
| Native file-drop on window | `WM_DROPFILES` / `NSDraggingDestination` / `Gtk.DropTarget`. |
| IME / dead keys / composed input | Text boxes are unusable for CJK and accented input without it. |
| Notification / tray / dock badge | Deferred; the API shape is obvious and the payoff is small. |
| Accessibility (UIA / NSAccessibility / AT-SPI) | Large, and worth designing separately. |
| Custom window chrome | Only if the default chrome proves insufficient. |

**Explicitly not the model's job** (stays in `XPlatUI`, unchanged): rendering, controls, layout,
measure/arrange, theming palettes, selection, input routing, focus, hit testing. The model wraps the
library; it does not re-enter it.

---

## 11. Migration

Each step is independently buildable and mergeable. Nothing in the existing harnesses changes until
step 3.

| # | Step | Scope | Done when |
|---|---|---|---|
| 1 | Extract the backend interfaces | `XPlatUI/Platform/*.cs` (interfaces only, no implementations; the dialog service and the per-window menu bar are part of it) | Solution builds; no harness references them yet; the nested-loop requirement is documented on `IXPlatDialogService` |
| 2 | `XPlatApplication` + `XPlatWindow` over a headless backend | `XPlatUI.Backend.Headless`; `XPlatUIDemo.RenderToPng` reimplemented on it | The existing PNG is byte-identical (or pixel-identical) and CI's render job passes |
| 3 | Move the Win32 harness onto the model | `XPlatUI.Backend.Win32` from `WinFormsApp3/Native/*`; `Program.cs` shrinks to §2 | Windows harness behaves identically; window resize, DPI, context menus and text input unchanged; message/file dialogs and the menu bar work |
| 4 | Move the AppKit harness onto the model | `XPlatUI.Backend.AppKit` from `MacHarness/*`; `MacHarness.csproj` → `net10.0-macos` app project | Mac build + headless render pass on `ssh mac`; no clipping, no memory regression; dialogs and the app menu bar work |
| 5 | Move the GTK harness onto the model | `XPlatUI.Backend.Gtk4` from `GtkHarness/*` | GTK harness behaves identically; dialogs and the menu bar work |
| 6 | Replace the three harnesses with one multi-TFM app | §6.1 project file; delete `WinFormsApp3`, `GtkHarness`, `MacHarness` | Three CI jobs build one project; `dotnet publish` AOT succeeds per OS |

Steps 1–6 are v1, and v1 includes dialogs and the menu bar (D12, D13): they land inside steps 3–5,
alongside the window each backend is already building, rather than as a follow-up release.

Step 2 is the risky one: it is the only step that touches the painting path, and it must produce
the same pixels.

---

## 12. Decision status

| # | Decision | Status | Notes |
|---|---|---|---|
| D1 | Type names `XPlatApplication` / `XPlatWindow` | **Settled** | Confirmed. Supporting types follow the same `XPlat` + noun rule; `XPlatUiHost` keeps its name. |
| D2 | Backend as separate projects | **Settled** | Confirmed. Folders would put `net10.0-macos` code in a `net10.0` assembly, which cannot compile, and one multi-TFM backend project would force the macOS workload onto Linux machines (§6). |
| D3 | `#if` selection, no reflection | **Settled** | Confirmed. Reflection discovery breaks under trimming/AOT; the explicit factory is four lines and always visible in the call stack. |
| D4 | Application owns a service container; statics become fallbacks | **Settled** | Confirmed. Six call sites, not every control (§5.9). |
| D7 | Three shutdown modes, `OnLastWindowClose` default | **Settled** | Confirmed. `OnExplicitShutdown` as the default would surprise Windows and Linux users; macOS opts into it explicitly when it wants to outlive its windows (§4.7). |
| D8 | Headless as a real backend | **Settled** | Confirmed. Otherwise `--render` stays duplicated three times and untestable. It also makes migration step 2 the safe one, because the model is exercised end to end with no window server (§11). |
| D9 | One TFM per OS via `IsOSPlatform` | **Settled** | Confirmed. A multi-TFM list would make a Windows restore pull the macOS workload and fail without Xcode (§6.1). |
| D10 | Multi-window, with `MainWindow` optional | **Settled** | Confirmed. No platform requires a main window; each needs a keep-alive when there is none (§4.6). |
| D11 | MEDI as the container, three lifetimes, `AddWindowScoped` | **Settled** | Confirmed by measurement: zero AOT warnings, +666 KB on a SkiaSharp binary, window scope closed by `AddWindowScoped` (§5.2, §5.4, §5.9). |
| D12 | Dialogs in v1 | **Settled: v1** | Confirmed. Nested-loop support moves into the step-1 contract; the headless backend keeps the modal paths testable (§9). |
| D13 | Menu bar in v1 | **Settled: v1** | Confirmed. macOS needs it for Cmd+Q, Cmd+W and Edit-menu clipboard shortcuts, not just polish; per-window on Windows and Linux (§10). |

Every design decision is settled, and both scope questions are answered as v1 (D12, D13). Nothing in
this document is still open; what remains is implementation, sequenced by §11.

One assumption is worth stating explicitly, because it is a design choice rather than a fact:

**`XPlatWindow` is one type with a backend, not a partial type per platform.** D2 and D3 settle the
backend design and keep the model in `net10.0`; the partial-class design is what WinForms/WPF/Avalonia
do and gives each platform room to expose platform-only members (an `NSWindow` handle, an `HWND`).
Partial classes also make the headless backend awkward, since it would need its own partial file for
every platform type it deliberately does not have. The escape hatch that argument was reaching for is
`window.Backend` (§4.3): a backend casts it to its own `IXPlatWindowBackend` and reaches `NSWindow` or
`HWND` without the model ever exposing a platform type.
