# TerrariaWindowKit API and integration contracts

This reference describes the **current source-only API available inside an assembly that imports TerrariaWindowKit sources**. The declared types are `internal`; importing the sources makes them available within the consumer's assembly, but does not make them public types of a separate TerrariaWindowKit DLL. Do not exchange or cast these toolkit-defined types between independently compiled consumer assemblies.

For setup and a minimal integration example, see the [README](../README.md). For cross-assembly and runtime implementation details, see [Architecture](architecture.md).

## Contents

- [Creating a window](#creating-a-window)
- [Window configuration and geometry](#window-configuration-and-geometry)
- [Content callbacks](#content-callbacks)
- [Input ownership](#input-ownership)
- [Backend contract and runtime ownership](#backend-contract-and-runtime-ownership)
- [Current compatibility boundary](#current-compatibility-boundary)

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
| `Open()` | Checks the in-game options menu first and returns `false` while it is open, even for an already open host. Otherwise returns `true`; a closed-to-open transition fits and activates the window and resets pin state. |
| `Toggle()` | Closes an open window or attempts to open a closed one. |
| `Close()` | Explicit close with `CloseReason.User`; while undisposed, runs owned focus/popup cleanup even if already closed. Does nothing after disposal. |
| `Activate()` | Raises an open window and releases text focus held by other participating windows. |
| `SetPinned(bool)` | Suppresses root closing by Escape while pinned; `true` requires `Pinnable`, and pin only persists while open. |
| `BeginDrag(x, y)` | Starts drag from a consumer-tested pointer hit if open, enabled and not blocked. |
| `BeginResize(x, y)` | Starts bottom-right resize from a consumer-tested hit if open, enabled and not blocked. |
| `UpdatePointer(x, y, down, blocked)` | Advances drag or resize; release or blocked input cancels the operation. Call on each draw while open, including blocked draws. |
| `TryUpdateContent(x, y, updateContent)` | Runs a synchronous consumer-owned update with a backend content-input scope (or temporary suspension of its own bounds), provided the root is open, the menu is closed and input is eligible; returns whether the update ran. |
| `RefreshGeometry()` | Refits to the current viewport and minimum dimensions. |
| `Dispose()` | Closes and unregisters the host, including its draw callback and shared registration. It does **not** dispose the backend. |

Hit testing of the draggable region and resize grip, their appearance, content drawing and widget input are not supplied by `WindowHost`. `BeginDrag` and `BeginResize` do not perform those hit tests for the consumer. Optional Core-only resize-grip and pin-button presentation helpers are described below.

### Updating consumer-owned content

Use `WindowHost.TryUpdateContent(int x, int y, Action updateContent)` when the consumer's retained UI needs to update while the toolkit-managed root has Core panel bounds registered:

```csharp
// uiMouseX/uiMouseY are update-phase UI coordinates; UpdateOwnedContent is consumer code.
bool updated = host.TryUpdateContent(uiMouseX, uiMouseY, UpdateOwnedContent);
```

The method first checks whether the window is open and the backend's in-game menu condition is clear. It then checks input eligibility with all bounds still registered, using `IWindowUpdateInput.IsInputBlockedAt(host.Id, x, y)` when available or `IWindowBackend.IsInputBlocked(host.Id)` otherwise. If the root is closed, the menu is open or input is blocked, it returns `false` without running the callback.

`CoreWindowBackend` keeps all panel bounds registered during the callback. Its content-input scope lets an eligible window's native UI receive pointer input where a lower panel overlaps it. Native game restrictions and unrelated patches remain effective. The backend rechecks the host's registration and update-point priority during the update; opening a higher panel over that point or closing the host ends its eligibility. Scope disposal restores the preceding input policy, including after nested callbacks or exceptions.

For a backend without `IWindowContentInput`, the host instead temporarily unregisters only its own bounds and restores them when appropriate. Nested fallback calls do not restore the outer suspension early; closing or disposing the host during the update does not restore obsolete registrations.

The consumer remains responsible for updating its native widgets and delivering wheel input. This method neither routes the wheel nor modifies another host's or a foreign Core panel's bounds. It throws `ArgumentNullException` for a null callback and `ObjectDisposedException` for a disposed host. Callback failures propagate; if both a callback and scope cleanup fail, both are reported in an `AggregateException`. Fallback bounds-suspension rollback can also report combined failures.

The Core content-input scope validates its additional dependency contracts on first use. Missing or incompatible members throw `MissingMemberException`, including its `MissingMethodException` subtype. These checks occur separately from backend construction; see [Core integration details](architecture.md#core-integration-and-version-boundary) for the required dependency members.

### Optional Core resize-grip presentation

With `TerrariaWindowKitIncludeCore=true`, `CoreResizeGripRenderer` offers a default **visual only**:

| Member | Current contract |
| --- | --- |
| `CoreResizeGripRenderer.DefaultGripSize` | `18` UI units; a reusable default grip size, not an automatically registered hit target. |
| `CoreResizeGripRenderer.Draw(x, y, width, height, hovered, isResizing)` | Draws a centered `◢` using the Core small-text renderer in the supplied UI-space rectangle, if the glyph and its padding fit. |

The glyph uses `UIColors.TextDim` normally and `UIColors.Accent` when `hovered` or `isResizing` is true. The caller supplies correct hover eligibility and owns the grip's hit testing, tooltip, resize initiation and pointer updates. The helper does not create a widget or alter `WindowHost` behavior; consumers may draw a different grip instead.

### Optional Core pin-button presentation

With `TerrariaWindowKitIncludeCore=true`, `CorePinButtonRenderer` supplies a default **visual only** for a consumer-owned pin button:

| Member | Current contract |
| --- | --- |
| `CorePinButtonRenderer.DefaultMinimumWidth` | `36` UI units; the lower bound used when measuring the button, not an automatically registered widget or hit target. |
| `CorePinButtonRenderer.MeasureWidth(pinText, unpinText)` | Measures both caller-provided labels using Core text measurement and returns one stable width for either pin state, including horizontal padding and the default minimum. |
| `CorePinButtonRenderer.Draw(x, y, width, height, isPinned, hovered, pinText, unpinText)` | Draws the button in a caller-provided UI-space rectangle. It displays `pinText` when unpinned and `unpinText` when pinned, with the selected background and outline for pin and hover state. |

The renderer uses `UIColors.Button`/`ButtonHover` normally, `UIColors.ItemActiveBg` with an `UIColors.Accent` outline when pinned, and `UIColors.Text` for centered text. It truncates the chosen label to the supplied width and omits text if the rectangle is too short or Core's truncated result still exceeds the available width. Nonpositive dimensions produce no drawing.

The consumer supplies labels (including localized labels), positions and sizes the control, determines eligible hover and click hits, and provides its own tooltip. Read the current pin state from `WindowHost.IsPinned` and call `WindowHost.SetPinned(...)` from the consumer's accepted click handler; remeasure when labels change. The renderer does not create a widget, handle input, own pin state, set tooltips or change `WindowHost` behavior.

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
| `WheelInputBlocked` (`Func<int, int, bool>`) | If supplied, overrides the update-coordinate priority check for wheel selection only; otherwise the host uses `IWindowUpdateInput` when present, then the backend's general block check. It does not control `TryUpdateContent`. |
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

For consumer-owned wheel delivery, call `SharedCoordinator.SelectWheel(int x, int y)` on the owning UI thread with the current update-phase UI coordinates, before updating the content. It evaluates ownership without changing panel bounds or delivering wheel input. The returned `IDictionary<string, object>` contains:

| Key | Value |
| --- | --- |
| `ownerId` | The selected host's combined ID as a `string`, or `null` when no participating window owns the wheel. |
| `disposition` | An `int` corresponding to `WheelDisposition`; `0` when `ownerId` is `null`. |

Using `callbacks`, `host` and `coordinator` from [Creating a window](#creating-a-window), first configure the window's wheel regions during setup. With `WheelAt` left unset, the host reports `PassThrough` and cannot be selected for delivery. Here `IsScrollableContentAt(x, y)` is a pure, consumer-provided hit test of its scrollable content in UI coordinates:

```csharp
callbacks.WheelAt = (x, y) => IsScrollableContentAt(x, y)
    ? (int)WheelDisposition.Deliver
    : (int)WheelDisposition.PassThrough;
```

Then, during the consumer's update, deliver the pending wheel only when this host is the selected owner with `Deliver`. In this example, `pendingWheel` is the delta captured by the consumer, and `UpdateOwnedWidgets` is consumer code that accepts a wheel delta:

```csharp
var selection = coordinator.SelectWheel(uiMouseX, uiMouseY);
string ownerId = (string)selection["ownerId"];
int disposition = (int)selection["disposition"];
int widgetWheel = ownerId == host.Id && disposition == (int)WheelDisposition.Deliver
    ? pendingWheel
    : 0; // CaptureOnly and ownership by another window deliver no wheel to these widgets.

host.TryUpdateContent(uiMouseX, uiMouseY, () => UpdateOwnedWidgets(widgetWheel));
```

When `ownerId` is `null`, preserve pass-through to the game or other UI. `SelectWheel` does not consume or restore the game's wheel state; the consumer manages that state around its own widget update.

## Backend contract and runtime ownership

`IWindowBackend` is a **local** integration interface: `ViewportWidth`, `ViewportHeight`, `IsMenuOpen`, `IsInputBlocked`, draw registration/unregistration, bounds registration/unregistration, `BringToFront` and `AcquireEscapeBlock`. Implementations manage only their own registrations and provide a non-null, idempotently disposable Escape-block lease.

Optional interfaces are `IWindowUpdateInput` (`IsInputBlockedAt`), `IWindowKeyboardInput` (`CanReceiveKeyboard`) and `IWindowContentInput` (`BeginContentUpdate`). `CoreWindowBackend` implements all four interfaces and also `IDisposable`.

`IWindowContentInput.BeginContentUpdate(id, x, y)` is called after the host's input-priority check. Its scope covers the synchronous content callback, must preserve panel registrations and native game input restrictions, and must support nesting and idempotent disposal. Return a non-null `IDisposable`; the host rejects `null` with `InvalidOperationException`. On disposal, restore the preceding input policy without ending an outer scope early.

Recommended ownership order for an existing Core-backed consumer:

1. On the owning UI thread, call `SharedCoordinator.Connect()` before constructing the Core backend.
2. Create the backend after content initialization, then the window hosts that use it.
3. Dispose each host and its owned content before disposing the backend.
4. Dispose the backend to release its registrations and its shared Core runtime lease.

Successful `CoreWindowBackend.Dispose()` calls are idempotent. If backend cleanup throws, a later `Dispose()` can retry unfinished cleanup. A disposed `WindowHost` remains disposed after a cleanup failure and does not retry its cleanup, as described under [Content callbacks](#content-callbacks).

The coordinator rejects duplicate window IDs and incompatible providers. Runtime leasing refuses an incompatible runtime signature and guards against double installation or reacquisition while last-lease cleanup is incomplete. Integration also depends on the required contracts of the loaded Core and Harmony assemblies, including private Core metadata; failed checks can throw during backend construction.

## Current compatibility boundary

The Core adapter validates the **required loaded dependency contracts**, not exact Core or Harmony assembly version numbers. Missing or incompatible startup contracts prevent runtime attachment during backend construction; an unrelated version-number change by itself does not. The optional content-input scope performs its additional checks on first use, as described under [Updating consumer-owned content](#updating-consumer-owned-content). Required dependency members and the validation mechanism belong to [Core integration details](architecture.md#core-integration-and-version-boundary).

This structural acceptance is a condition for attempting integration, **not proof of runtime behavior**. See the [validated configurations](../README.md#requirements-and-current-compatibility) and their limits in the README. Passing the structural checks does not establish compatibility with untested Core/Harmony versions or interactions.

Source-only consumers coordinate only through a compatible shared-provider protocol and runtime signature. The current coordinator uses protocol major `2`, and the current Core runtime requires the `input=3` contract revision. Incompatible generations are rejected rather than silently sharing one runtime, so participating consumers must be rebuilt together when these contracts change. See [Architecture](architecture.md) for the internal identifiers and lease implementation, and [Testing](testing.md) for validation boundaries.