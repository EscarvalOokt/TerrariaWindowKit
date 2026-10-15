# TerrariaWindowKit API and integration contracts

This reference describes the **current source-only API available inside an assembly that imports TerrariaWindowKit sources**. The declared types are `internal`; importing the sources makes them available within the consumer's assembly, but does not make them public types of a separate TerrariaWindowKit DLL. Do not exchange or cast these toolkit-defined types between independently compiled consumer assemblies.

For setup and a minimal integration example, see the [README](../README.md). For cross-assembly and runtime implementation details, see [Architecture](architecture.md).

## Creating a window

Create a backend on the owning UI thread, after the consumer's UI content is ready. With the TerrariaModder Core integration included (`TerrariaWindowKitIncludeCore=true`), the existing path is:

```csharp
using Escarval.TerrariaWindowKit;

var coordinator = SharedCoordinator.Connect();
var backend = new CoreWindowBackend(coordinator);
var options = new WindowOptions(new WindowBounds(100, 80, 380, 240))
{
    MinimumWidth = 200,
    MinimumHeight = 120,
    Draggable = true,
    Resizable = true,
    Pinnable = true
};
var callbacks = new WindowCallbacks { Draw = RenderOwnedContent };
var host = new WindowHost("my-mod", "inventory", options, backend, callbacks);

host.Open();

// Dispose hosts before their shared backend, on the UI thread:
host.Dispose();
backend.Dispose();
```

`RenderOwnedContent` is a consumer-provided draw callback. The consumer is responsible for creating, storing and releasing its own UI content and for choosing appropriate initialization and shutdown hooks in the mod. A backend can serve several hosts; do not dispose it until all of its hosts have been disposed.

`WindowHost` constructs the combined identity as `modId:windowId`. Both parts must be nonempty and must not contain `:`. The combined identity must be unique among windows registered with the shared coordinator, including those from other consumers.

## Window configuration and geometry

| Type/member | Current contract |
| --- | --- |
| `WindowBounds(int x, int y, int width, int height)` | Rectangle in UI coordinates; width and height must be positive. The initial position may be outside the viewport before fitting. |
| `WindowBounds.Contains(x, y)` | Includes the left/top edge and excludes the right/bottom edge. |
| `WindowBounds.Fit(viewportWidth, viewportHeight, minimumWidth, minimumHeight)` | Fits size and position to the viewport. Viewport dimensions are at least 1; a smaller viewport takes precedence over the configured minimum. |
| `WindowOptions.InitialBounds` | Required initial rectangle. Options are copied at host construction. |
| `WindowOptions.MinimumWidth`, `MinimumHeight` | Positive minimum dimensions in UI units; default `1` for each. |
| `WindowOptions.Draggable`, `Resizable`, `Pinnable` | All default to `false`; enabling them permits the corresponding host operations. |
| `WindowHost.Bounds` | Current fitted rectangle, retained after closing. |
| `WindowHost.Id`, `IsOpen`, `IsPinned`, `IsDragging`, `IsResizing` | Read-only current identity and window/operation state. |

`WindowHost` automatically fits its geometry when opened and before drawing. `RefreshGeometry()` can also fit it explicitly; `BoundsChanged` is called only when the rectangle actually changes. Core's viewport adapter derives UI dimensions from screen pixels and the validated UI scale.

### Window operations

| Member | Effect |
| --- | --- |
| `Open()` | Opens a closed window, fits and activates it, and resets pin state; returns `false` if the in-game options menu forbids opening. Returns `true` if already open. |
| `Toggle()` | Closes an open window or attempts to open a closed one. |
| `Close()` | Explicit close with `CloseReason.User`; while undisposed, runs owned focus/popup cleanup even if already closed. Does nothing after disposal. |
| `Activate()` | Raises an open window and releases text focus held by other participating windows. |
| `SetPinned(bool)` | Suppresses root closing by Escape while pinned; `true` requires `Pinnable`, and pin only persists while open. |
| `BeginDrag(x, y)` | Starts drag from a consumer-tested pointer hit if open, enabled and not blocked. |
| `BeginResize(x, y)` | Starts bottom-right resize from a consumer-tested hit if open, enabled and not blocked. |
| `UpdatePointer(x, y, down, blocked)` | Advances drag or resize; release or blocked input cancels the operation. Call on each draw while open, including blocked draws. |
| `RefreshGeometry()` | Refits to the current viewport and minimum dimensions. |
| `Dispose()` | Closes and unregisters the host, including its draw callback and shared registration. It does **not** dispose the backend. |

Hit testing of the draggable region and resize grip, their appearance, content drawing and widget input are not supplied by `WindowHost`. `BeginDrag` and `BeginResize` do not perform those hit tests for the consumer.

## Content callbacks

Pass a `WindowCallbacks` instance to the host constructor, or omit it for empty callbacks. The instance is **retained by reference**; the `WindowOptions` instance is copied.

| Callback | Consumer responsibility / behavior |
| --- | --- |
| `Draw` (`Action`) | Draw and hit-test owned content while the root is open. Called after fitting geometry. |
| `BoundsChanged` (`Action<WindowBounds>`) | React to changed geometry after any active input bounds registration is updated. Registration may be absent while closed or temporarily suspended. |
| `HasTextFocus` (`Func<bool>`) | Report owned text focus for Escape arbitration. |
| `ReleaseTextFocus` (`Action`) | Release owned focus on Escape, activation of another participating window, and every close. Must be safe when already closed. |
| `HasPopup` (`Func<bool>`) | Report a consumer-owned popup for Escape arbitration. |
| `ClosePopup` (`Action`) | Close owned popup state on Escape and every close, including repeated cleanup. |
| `CanReceiveKeyboard` (`Func<bool>`) | If supplied, completely overrides optional backend keyboard eligibility; otherwise the host uses `IWindowKeyboardInput` when present. |
| `WheelAt` (`Func<int, int, int>`) | Pure ownership decision at the given UI-coordinate point; `null` is pass-through. |
| `WheelInputBlocked` (`Func<int, int, bool>`) | If supplied, overrides update-coordinate input-priority checking; otherwise the host uses `IWindowUpdateInput` when present, then the backend's general block check. |
| `Closed` (`Action<CloseReason>`) | Invoked once on each open-to-closed transition, after state reset and attempted bounds/content cleanup. |

Queries used during wheel selection must not mutate registrations. Cleanup callbacks must be idempotent and release **only resources owned by their consumer**. The host can invoke focus/popup cleanup after clearing `IsOpen`; a callback must not require the root still to be visible in order to release stale state.

Closing and disposal attempt all applicable cleanup steps and report failures in an `AggregateException`. Disposal marks the host as disposed before cleanup; later `Dispose()` or `Close()` calls do not retry cleanup, including after a failure.

### Close reasons

`CloseReason` values are `User`, `Escape`, `GameMenu`, `WorldUnload` and `Disposed`. Explicit close/toggle uses `User`; opening the in-game options menu closes roots even if pinned; world unload or return to the main menu uses `WorldUnload`. Only an actual open-to-closed transition triggers `Closed`.

## Input ownership

### Escape

A single Escape press selects **one action** from open participating windows, considered from front to back. Each window's action priority is:

1. Release consumer text focus, if present.
2. Otherwise close a consumer popup, if present.
3. Otherwise close an unpinned root window.

A pinned window without its own text-focus or popup action has no Escape action and is **skipped**; this allows an unpinned window below it to close regardless of which window was last activated. A pinned window with an internal action handles that action first, without closing another root on the same press. If both windows are pinned and neither has an internal action, the toolkit leaves Escape to Terraria.

The first window with an action must pass its keyboard-eligibility check. If it is blocked, Escape is **not** redirected to a lower participating window. Eligibility still respects foreign keyboard/text owners, chat and higher-priority foreign Core panels; a pinned toolkit panel without an Escape action does not itself block an eligible recipient below it. The chosen action is completed after the relevant input phase, and the keyboard block is retained until physical key release, so holding Escape does not close multiple roots.

### Mouse wheel

`WheelAt(x, y)` returns an integer corresponding to `WheelDisposition`:

| Value | Name | Meaning |
| --- | --- | --- |
| `0` | `PassThrough` | Leave the wheel to the game or other UI; this is a barrier to falling through to a lower participating window. |
| `1` | `Deliver` | The consumer owns the wheel in its content region and delivers it using its own widgets. |
| `2` | `CaptureOnly` | Own the wheel without content delivery, such as when a popup blocks scrolling behind it. |

Keep `Deliver` ownership while the pointer is in a scrollable content region, including at scroll limits or when the content does not overflow, if it should still shield lower windows. Selection considers all participating windows against the original registration state before temporarily suspending non-owner input bounds. A blocked higher-priority panel is skipped; an eligible top window returning `PassThrough` prevents delivery to lower participating windows.

The Core adapter's update-phase wheel priority uses UI-coordinate mouse positions, not draw-phase mouse coordinates or transient draw blocking flags. The consumer must apply appropriate input suppression to its own widgets for `CaptureOnly`.

## Backend contract and runtime ownership

`IWindowBackend` is a **local** integration interface: `ViewportWidth`, `ViewportHeight`, `IsMenuOpen`, `IsInputBlocked`, draw registration/unregistration, bounds registration/unregistration, `BringToFront` and `AcquireEscapeBlock`. Implementations manage only their own registrations and provide a non-null, idempotently disposable Escape-block lease.

Optional interfaces are `IWindowUpdateInput` (`IsInputBlockedAt`) and `IWindowKeyboardInput` (`CanReceiveKeyboard`). `CoreWindowBackend` implements all three interfaces and also `IDisposable`.

Recommended ownership order for an existing Core-backed consumer:

1. On the owning UI thread, call `SharedCoordinator.Connect()` before constructing the Core backend.
2. Create the backend after content initialization, then the window hosts that use it.
3. Dispose each host and its owned content before disposing the backend.
4. Dispose the backend to release its registrations and its shared Core runtime lease.

The coordinator rejects duplicate window IDs and incompatible providers. Runtime leasing refuses an incompatible runtime signature and guards against double installation or reacquisition while last-lease cleanup is incomplete. Integration also depends on the required contracts of the loaded Core and Harmony assemblies, including private Core metadata; failed checks can throw during backend construction.

## Current compatibility boundary

The Core adapter validates the **required loaded dependency contracts**, not exact Core or Harmony assembly version numbers. It checks the public methods and constructors it calls for CLR-compatible signatures, including parameter types, return types and static/instance form; required properties must have the expected types and accessible accessors, not merely fields of the same name. Required private Core metadata and the scroll patch targets are checked separately. A missing or incompatible required contract prevents runtime attachment; an unrelated version-number change by itself does not.

This structural acceptance is a condition for attempting integration, **not proof of runtime behavior**. See the [validated configurations](../README.md#requirements-and-current-compatibility) and their limits in the README. Passing the structural checks does not establish compatibility with untested Core/Harmony versions or interactions.

Source-only consumers coordinate only through a compatible shared-provider protocol and runtime signature. The current coordinator uses protocol major `2`, and the current Core runtime requires the `input=3` contract revision. Incompatible generations are rejected rather than silently sharing one runtime, so participating consumers must be rebuilt together when these contracts change. See [Architecture](architecture.md) for the internal identifiers and lease implementation, and [Testing](testing.md) for validation boundaries.
