# TerrariaWindowKit testing and validation

This guide describes the repository's build and test mechanisms and the boundaries of what each check establishes. It is a **verification policy**, not a log of individual builds, in-game runs or publication events.

## Build environment

The repository targets .NET Framework 4.8 and selects .NET SDK `8.0.303` with `latestFeature` roll-forward in `src/global.json`. On a configured Windows build environment, the Core compile-check uses references to Terraria, TerrariaModder Core, Harmony and Microsoft XNA from local MSBuild properties.

Create `src/TerrariaWindowKit.Local.props` from the repository's [`TerrariaWindowKit.Local.props.example`](TerrariaWindowKit.Local.props.example) and configure your paths. The file is imported by `src/Directory.Build.props`, which also checks `HarmonyDll`. `tests/Directory.Build.props` imports the same repository-wide properties. `tests/CompileCheck/CompileCheck.csproj` additionally validates the Terraria executable and Core/XNA paths. The local props file is intentionally ignored by Git.

Run the following commands **from `src`**, so the local `global.json` selects the intended SDK:

```powershell
dotnet --version
dotnet build TerrariaWindowKit.sln -c Release
dotnet test ../tests/TestHost/TestHost.csproj -c Release
```

Successful execution depends on the installed SDK, .NET Framework targeting/runtime support and the configured external binaries. These commands are reusable verification steps; the results of specific runs belong to their corresponding task records.

## Build-time compatibility check

`tests/CompileCheck/CompileCheck.csproj` imports both `Shared` and `Core` sources (`TerrariaWindowKitIncludeCore=true`) and references the selected Terraria executable, TerrariaModder Core, Harmony and XNA assemblies.

A successful compilation establishes that the imported sources compile against **those referenced assemblies**. It does **not** prove that the loaded game's private Core metadata, actual Harmony patch targets, input timing or gameplay behavior are compatible. `CoreRuntime.Attach()` performs required dependency-contract checks in the loaded process; those checks also do not independently establish runtime behavior.

## Cross-assembly and contract tests

`tests/ConsumerA/ConsumerA.csproj` and `tests/ConsumerB/ConsumerB.csproj` compile `Shared` sources into two independent consumer assemblies, without the Core adapter. Both use `tests/SharedFixtures/Facade.cs`, which supplies a BCL-only facade and a synthetic backend, and `ContentInputFacade.cs` for independently leased native-input Harmony prefixes.

`tests/TestHost/TestHost.csproj` uses NUnit 4.6.1, NUnit3TestAdapter 6.2.0 and Microsoft.NET.Test.Sdk 18.8.1. Cross-assembly scenarios execute in both consumer-initialization orders in isolated AppDomains with non-parallel NUnit execution. A separate compatibility suite checks used Core/Harmony dependency signatures with synthetic types and the loaded Harmony assembly. Adapter tests link the production host, Core backend, input policy and content boundary against synthetic Core/Terraria APIs, including an actual Harmony-patched native-input getter; runtime event startup is excluded. Presentation tests link the production pin renderer against a recording Core drawing facade. These tests do not launch Terraria.

The scenarios cover:

| Area | Automated contract boundary |
| --- | --- |
| Source-only coordination | One BCL provider across distinct assemblies; duplicate IDs; protocol major and mandatory provider capabilities; independent disposal. |
| Runtime ownership | Compatible leases install once; mismatched signatures reject a second installation; failure and last-lease cleanup/retry paths. |
| Escape and closing | One action per physical press, text/popup/root precedence, pin pass-through regardless of activation, protection by foreign higher-priority panels and keyboard blockers, menu/world cleanup. |
| Geometry | Opt-in drag/resize/pin, minimum/viewport fitting and canceled pointer operations. |
| Wheel and panel priority | Decisions before bounds changes, stale activation versus Core priority, nested suspension, restoration and wheel dispositions. |
| Guarded content updates | Priority checks with all bounds registered; eligible native input over lower toolkit/foreign panels without bounds removal; higher-panel rejection, native game blockers and stale activation; fallback own-bounds suspension; nested updates, callback errors, geometry changes, closing, reopening and disposal. |
| Native-input boundary | Local backend reference counts, independent prefixes in both source-copy orders, native and foreign blockers, current scope eligibility, UI thread ownership, rollback/retry and rebuilding already JIT-compiled native Harmony wrappers after attachment or cleanup. |
| Pin-button presentation | Narrow Core truncation fallback cannot draw outside the button; exact-fit and centered labels, state colors, short/empty rectangles and stable width measurement. |
| Harmony boundaries | Synthetic patch targets, nested calls, exception restoration, conflicting owners and preservation of foreign patches. |
| Dependency compatibility | Exact required public Core/Harmony method return/parameter types, property accessors and member kinds; private fields/targets; positive version variation and negative structural changes. |

The fake Core panel priority and synthetic Harmony targets cannot confirm Core's actual renderer state, game event ordering, clipping or the runtime installer selected inside Terraria.

## Runtime validation boundaries

Three evidence levels remain distinct:

1. **Source and configuration inspection** identifies implemented behavior and build requirements but is not a successful build or test run.
2. **Compilation and automated tests** establish the configuration and test scenarios actually executed, not correctness of the real game integration.
3. **In-game runtime validation** confirms only the observed scenarios on the stated Terraria/Core/Harmony installation and consumer builds. Rebuilding or changing a dependency can require targeted revalidation; a previous acceptance result does not automatically transfer to that change.

In-game checks relevant to TerrariaWindowKit include window rendering and activation, pinned/unpinned Escape with more than one source-only consumer, foreign panel/keyboard priority, wheel and Vanilla click handling, guarded updates of consumer-owned UI, menu/world transitions, viewport scaling and release of shared runtime/Harmony resources. Cross-consumer coordination guarantees belong to the toolkit; individual consumers need only test their own integrations. Synthetic ConsumerA/ConsumerB tests do not replace simultaneous installed-mod acceptance.

The optional Core-only renderers require consumer-side visual checks: `CoreResizeGripRenderer` for positioning, hover/active-resize appearance and UI scaling; `CorePinButtonRenderer` for placement, both labels, width remeasurement after localization changes, hover/pinned appearance and UI scaling. Consumers separately verify pin-button hit testing, input blocking, toggling through `WindowHost.SetPinned(...)`, and pin reset after close/reopen. Compiling these helper calls against selected Core assemblies does not verify their drawing in Terraria. An observed integration in one consumer does not establish equivalent visuals or input behavior in other consumers.

A structural contract pass permits a configuration to attempt attachment, but does not mean it has been tested in-game. Successful in-game validation of specific dependency configurations does not establish compatibility with future Core or Harmony versions or untested circumstances.

## Reporting a verification result

Record the verification commands or gameplay scenarios, the relevant environment and dependency versions **when a claim depends on them**, the actual outcome, and meaningful limitations in the corresponding task or validation record. Distinguish compiled, automated and in-game evidence. Include exact binary identity or revision when available and material to a claim, but do not require hashes, an exhaustive scenario ledger or a new permanent report for every run.

When a test was not run, evidence was not recorded or a gameplay case was not exercised, state that limitation instead of inferring success from source shape, synthetic tests or another revision's acceptance. Do not turn this policy guide into a history of results.