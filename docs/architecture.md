# TerrariaWindowKit architecture

This document describes the **current implementation** of TerrariaWindowKit, a source-only toolkit compiled into consumer mods. It covers internal boundaries, coordination and the existing TerrariaModder Core integration. Consumer-visible calls and callback obligations belong to the [API reference](api.md); verification procedures belong to [Testing](testing.md).

## Design boundaries

The toolkit owns root-window lifecycle, geometry and coordinated input. The consumer owns window content, drawing, visual controls, pointer hit testing, text focus, popup state and widget-level wheel delivery.

The source tree separates those concerns:

| Area | Responsibility | Principal source paths |
| --- | --- | --- |
| Shared window layer | Root lifecycle, options, geometry, pointer operations, callbacks and local backend contracts | `src/Shared/Windows/` |
| Shared coordination | Shared provider, window ordering, Escape and wheel arbitration, runtime leases | `src/Shared/Coordination/SharedCoordinator.cs` |
| Shared compatibility | Keyboard policy and Harmony scroll-boundary implementation | `src/Shared/Compatibility/` |
| Core adapter | TerrariaModder Core rendering/input registrations, private metadata checks and game event integration | `src/Core/` |
| Consumer | Content rendering, hit testing and owned UI state | Outside this toolkit, inside each importing mod |

Dependencies point from `WindowHost` to local backend contracts and the shared coordinator; the Core adapter implements those contracts using TerrariaModder Core. Consumer callbacks provide content behavior without making the shared window layer depend on game-specific widget classes. The Core runtime depends on Terraria, TerrariaModder Core and Harmony, whereas the shared window/coordinator layer can be compiled without Core integration.

## Source-only build and cross-assembly boundary

`src/TerrariaWindowKit.Source.projitems` imports all `Shared/**/*.cs` into the consumer's compilation. It imports `Core/**/*.cs` only when `TerrariaWindowKitIncludeCore` is `true`. `src/TerrariaWindowKit.shproj` is the Visual Studio shared-source project; `src/TerrariaWindowKit.sln` also contains the compile-check and contract-test projects.

Every importing assembly has its **own** copies of `internal` toolkit types and static fields. As a result, sharing a `WindowHost` instance or casting another assembly's toolkit type is not a cross-mod integration contract.

`SharedCoordinator.Connect()` connects compatible source-only copies through the current AppDomain. The first connection places a provider API dictionary in `AppDomain.CurrentDomain` under the stable slot `Escarval.TerrariaWindowKit.Coordinator`; compatible later connections join that provider. The cross-assembly ABI uses BCL values (`IDictionary<string, object>`, strings, primitives, delegates and `IDisposable`), not the toolkit's assembly-local classes.

Connection requires protocol major `2`, the update-coordinate wheel-priority capability (`wheelPriorityAtVersion >= 1`), the mandatory `escapePassThroughIds` operation, and provider operations for registration, activation, frame processing, wheel routing, closing, runtime leasing and snapshots. That operation returns open toolkit panel IDs whose current Escape action is `0` as a `string[]`, allowing a Core backend compiled into another consumer to recognize pass-through roots without casting its toolkit types. The provider is tied to its creating UI thread; operations on a different thread throw. This is shared in-process coordination, not synchronization across Terraria processes or AppDomains. Incompatible provider protocols are rejected regardless of consumer initialization order.

### Registration and activation

A host registers its globally combined `modId:windowId` identity with the provider. Duplicates are rejected. The provider maintains a list of participating windows in activation order, rather than relying on any individual consumer's static state. Activating one window moves it to the end of that list and releases text focus on the other participating windows when they report having it.

The backend also raises the Core panel through `UIRenderer.BringToFront`, keeping the shared window ordering and renderer priority aligned where possible. Core's own higher-priority panel checks remain relevant when the two orders differ.

## Window lifecycle and ownership

`WindowHost` registers its draw callback on construction while the root is closed. `Open()` checks whether the in-game options menu forbids opening; on an open transition it fits geometry, registers bounds, activates the host and resets pin. `Close()` removes owned blocking bounds, clears pin and pointer operations, and runs focus/popup cleanup. A host retains the last geometry while closed.

`WindowOptions` is copied when the host is created, so subsequent changes to the passed options do not reconfigure the host. Dragging, bottom-right resizing and pinning are independent opt-in flags. `WindowBounds.Fit` clamps the rectangle to the viewport; when the viewport is smaller than the configured minimum, the viewport takes precedence. `PointerOperation` enforces exclusive drag/resize operations and cancels on release or blocked input.

Ownership is explicit:

- The consumer owns callbacks and content state; cleanup callbacks must release only their own resources and tolerate repeated calls.
- The host owns its coordinator registration and draw registration, but **not** the backend.
- The backend owns Core draw/bounds registrations and one runtime lease; one backend may serve several hosts.
- The shared runtime owns Core event subscriptions and Harmony patches for a compatible runtime family.

Hosts must be disposed before their backend. The coordinator and Harmony scopes use disposable leases for temporary state. Cleanup operations attempt to preserve or restore owned state; failures are surfaced rather than silently hidden. In particular, last-lease runtime cleanup can be retried and blocks new acquisition of that runtime family until cleanup succeeds.

## Coordinated input

### Escape

`CoreRuntime` begins the shared frame in `FrameEvents.OnPreUpdate`, using the game's Escape state and options-menu state; it completes the pending action on `Main.OnTickForThirdPartySoftwareOnly`. The coordinator considers open windows in reverse activation order. For each window it obtains the existing action (`release text focus`, `close popup`, `close unpinned root`, or `0`). A pinned root without a focus/popup action returns `0` and is skipped, so the next eligible unpinned root may close even if the pinned window is active. A window with a nonzero action but denied keyboard eligibility stops selection; Escape is not redirected below a real blocker. One physical press still schedules only one action.

The Core backend asks the shared provider for the IDs of open roots that pass Escape through. `CoreInputPolicy` preserves `UIRenderer.ShouldBlockForHigherPriorityPanel` unless a toolkit pass-through panel caused the block; in that case it reevaluates the higher Core panels using the existing z-order/registration and `UIRenderer.IsMouseOverPanel`, excluding **only** those pass-through IDs. Other Core panels retain their priority. Foreign keyboard blockers, Core text ownership/release, native text owners and chat remain eligibility barriers. The toolkit's own temporary Escape blocker is recognized by `KeyboardInputPolicy` so it does not cancel the deferred action; the block remains until physical key release. This keyboard adjustment does not unregister bounds, change z-order or alter the wheel Harmony wrappers.

The in-game options menu closes all participating roots; inventory and ordinary pause are not classified as that menu. World unload and return to the main menu also close roots regardless of pinning. These events are bridged through `CoreRuntime`.

### Wheel routing and panel bounds

`WindowCallbacks.WheelAt` describes how the consumer handles the wheel at a given UI-coordinate point: pass through (`0`), deliver to content (`1`), or capture without delivery (`2`). The coordinator evaluates the registered windows' visibility, hit testing, input priority and routing decisions **before** mutating any input rectangles. It inspects candidates from front to back. A blocked candidate can be skipped; an eligible top candidate returning pass-through prevents delivery to lower participating roots.

To let the wheel reach its selected owner (or the game on pass-through), the provider temporarily suspends registered bounds of the other open participating windows. It leaves unrelated Core panels' registrations untouched. Nested scroll scopes share one suspension depth and restore bounds after the outermost scope completes.

The Core update-coordinate priority check relies on private `UIRenderer` panel ordering/bounds. This is distinct from draw-time `WidgetInput.BlockInput`, which is only meaningful in Core's draw phase. `UiViewport` and the Core adapter translate screen dimensions and update mouse coordinates into UI units using the validated UI scale.

## Core integration and version boundary

`CoreWindowBackend` implements `IWindowBackend`, `IWindowKeyboardInput`, `IWindowUpdateInput` and `IDisposable`. It bridges draw callbacks, hit boxes, activation, keyboard blocking and update-phase priority into `UIRenderer` and related Core APIs. It acquires a shared runtime lease during construction, after the coordinator has been connected.

`CoreRuntime.Attach()` checks the **required loaded API contracts** rather than comparing Core or Harmony assembly versions for exact equality:

- `CoreCompatibilityChecks.ValidateCoreContracts` verifies the public renderer operations used by the backend, including exact CLR parameter/return types and static form. It also requires public static `bool` properties for `WidgetInput.BlockInput` (getter and setter) and `WidgetInput.MouseLeftClick` (getter), not interchangeable fields.
- `CoreCompatibilityChecks.ValidateHarmonyContracts` checks the required Harmony constructors and `GetPatchInfo`, `Patch` and `Unpatch` methods, plus the instance property `Patches.Owners` with a public getter of type `ReadOnlyCollection<string>`.
- `CoreInputPolicy.ValidateMetadata` and the scroll-target resolver check required private Core fields and nonpublic static methods for their exact types and signatures. The private `UIRenderer` patch targets are:

| Target | Required signature |
| --- | --- |
| `OnPreUpdateConsumeScroll` | `void ()` |
| `PlayerUpdateScrollBlockPrefix` | `void (Terraria.Player)` |
| `DoScrollingInInventoryPrefix` | `bool ()` |

Failure to match a required structural contract prevents attachment. An unrelated change to the dependency version number alone does not. These checks are necessary for safe use of the compiled calls, but do **not** establish behavioral compatibility with untested versions or independently prove runtime behavior.

### Runtime family and Harmony ownership

`SharedCoordinator.AcquireRuntime` shares an installation by runtime family and requires an identical opaque runtime signature from each attached consumer. The stable family is `escarval.terrariawindowkit.core.runtime`; the current implementation contract is `escarval.terrariawindowkit.core.runtime/2;scroll=1;input=3`. `CoreCompatibilityChecks.CreateRuntimeSignature` appends type-handle identities of the **actual loaded CLR dependency types**, so an identically named but different loaded dependency does not silently join the same runtime. The first compatible consumer installs the Core runtime; later ones acquire leases without duplicate event handlers or patches. The last lease releases that installation. An incompatible signature is rejected without invoking a second installer.

The project version is `1.0.0` (`src/Directory.Build.props`), and the provider API also reports `providerVersion = "1.0.0"`. These descriptive version values do not replace coordinator protocol major `2` or the runtime signature, which determine whether independent source-only consumers can share the provider and runtime.

`HarmonyScrollBoundary` installs only the explicitly resolved patch targets (not `PatchAll`), using the stable Harmony owner `escarval.terrariawindowkit.core.scroll`. It checks for conflicting ownership, rolls back partial patch installation and uses a prefix/finalizer pair to restore suspended bounds even if a target throws. Both the family and Harmony owner are independent of external Core/Harmony version numbers; they are not renumbered for each compatible dependency release.

## Compatibility states and verification

Compatibility is assessed against required Core/Harmony contracts and the shared-provider protocol/runtime signature, not version numbers alone. A structural check that passes permits attempted use; a demonstrated incompatibility or broken required contract must be rejected or adapted. Runtime acceptance of a specific configuration remains separate from these checks.

Automated cross-assembly scenarios exercise the coordinator with synthetic backends and targets; the compile-check project references the actual Core-facing APIs selected for that build. In-game validation is a **separate evidence boundary**: results for specific configurations do not establish behavior for future or untested dependencies. See [validated configurations](../README.md#requirements-and-current-compatibility) for the current validation boundary and [Testing](testing.md) for the validation policy.