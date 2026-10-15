# TerrariaWindowKit

TerrariaWindowKit is a **source-only window-management toolkit** for Terraria mods that integrate with TerrariaModder Core. It manages root-window visibility, geometry and input coordination, including windows compiled into different consumer assemblies. It does not provide a finished widget set or draw a mod's window contents.

The current project version is **1.0.0**.

## What the toolkit provides

- Root windows that can be opened, closed, activated and, when enabled, dragged, resized or pinned.
- Coordinated Escape handling across participating windows: a pinned window without a pending text/popup action does not prevent an unpinned window from closing.
- Wheel-input ownership across overlapping participating windows, while respecting TerrariaModder Core's panel priority.
- Shared coordination of windows and Core integration across independently compiled source-only consumers.
- A Core backend that registers draw callbacks and panel input bounds with TerrariaModder Core.

Your mod supplies the window's content, drawing, hit testing, widgets, focus and popup state, and any content-specific scroll handling.

## Requirements and current compatibility

- Consumer code targets **.NET Framework 4.8** (`net48` in this repository).
- Building the Core integration requires references to Terraria, TerrariaModder Core, Harmony and the Microsoft XNA assemblies used by the project.
- The Core adapter checks required dependency contracts at runtime. A version change alone does not establish compatibility or incompatibility.

In-game integration has been validated on **TerrariaModder Core 0.4.1 and 0.4.2**. These results apply to the tested configurations and scenarios; compatibility with other dependency versions or untested mod combinations is not established. See [API compatibility](docs/api.md#current-compatibility-boundary) for integration conditions and [Testing](docs/testing.md) for validation boundaries.

## Getting started

### 1. Obtain and import the sources

For a versioned source snapshot, use [GitHub Releases](https://github.com/EscarvalOokt/TerrariaWindowKit/releases), or clone the repository. GitHub provides ZIP and TAR.GZ source archives for release tags. No separately compiled TerrariaWindowKit DLL is required.

Import `src/TerrariaWindowKit.Source.projitems` in the consumer's C# project rather than referencing the repository's compile-check assembly. Adapt the relative import path to the consumer project's location:

```xml
<PropertyGroup>
  <TargetFramework>net48</TargetFramework>
  <TerrariaWindowKitIncludeCore>true</TerrariaWindowKitIncludeCore>
</PropertyGroup>
<Import Project="..\..\src\TerrariaWindowKit.Source.projitems" Label="Shared" />
```

`Shared` sources are always imported. Setting `TerrariaWindowKitIncludeCore` to `true` additionally imports `Core` sources, including `CoreWindowBackend`. The consumer must supply the references needed by the selected sources; the `.projitems` import does not configure those external references.

Types in the imported sources are `internal` **within each consumer assembly**. The toolkit is not a common runtime DLL whose types can be exchanged between mods. The cross-assembly coordination mechanism is described in [Architecture](docs/architecture.md).

### 2. Create and own a window

After the mod's UI content is ready, on the owning UI thread, connect a coordinator and construct a backend. One Core backend may serve multiple window hosts:

```csharp
using Escarval.TerrariaWindowKit;

var backend = new CoreWindowBackend(SharedCoordinator.Connect());
var host = new WindowHost(
    "example-mod",
    "main",
    new WindowOptions(new WindowBounds(80, 80, 400, 260)),
    backend,
    new WindowCallbacks { Draw = RenderMyWindowContent });

host.Open();

// During the consumer's shutdown, in this order:
host.Dispose();
backend.Dispose();
```

`RenderMyWindowContent` represents a draw method implemented by the consumer; this snippet does not define its contents or wire it into a particular mod's startup lifecycle. Use distinct `modId:windowId` identities for simultaneously registered windows. Dispose every host before disposing of its backend.

For options, callbacks, input rules, geometry and cleanup contracts, see the [API reference](docs/api.md).

## Important usage limits

- Dragging, resizing and pinning are opt-in; their visual affordances and pointer hit tests belong to the consumer.
- Pinning prevents only that window's root from closing on Escape. A pinned window may still release its text focus or close its popup; Escape can close a different, unpinned participating window regardless of activation. The in-game options menu or world unload closes participating windows regardless of pinning.
- Keyboard eligibility still respects other input owners and higher-priority foreign Core panels. Holding Escape does not close multiple windows in one press.
- All host and coordinator operations use the owning UI thread. Backend, host and consumer-owned resources have separate lifetimes.
- Independently compiled source copies can coordinate only when their shared protocol and runtime contracts are compatible. Consumers must be rebuilt together when those contracts change incompatibly.

## Documentation

- [API reference](docs/api.md) — consumer-facing types, callbacks, window behavior and integration obligations.
- [Architecture](docs/architecture.md) — implementation layers, shared coordination and runtime boundaries.
- [Testing](docs/testing.md) — build/test entry points, existing test coverage and limits of the evidence.

## For maintainers

The source project is `src/TerrariaWindowKit.shproj`, with `src/TerrariaWindowKit.Source.projitems` defining the source import. The solution `src/TerrariaWindowKit.sln` includes the compile-check and contract-test projects.

For builds of **this repository's solution**, copy [`docs/TerrariaWindowKit.Local.props.example`](docs/TerrariaWindowKit.Local.props.example) to `src/TerrariaWindowKit.Local.props` and set the local Terraria/Core/Harmony/XNA paths. This file is intentionally ignored by Git; consumer projects configure their own references. The repository selects .NET SDK `8.0.303` with `latestFeature` roll-forward from `src/global.json`. Build and test commands belong to [Testing](docs/testing.md); internal runtime details belong to [Architecture](docs/architecture.md). Versioned releases are identified by Git tags (for example, `v1.0.0`) and use GitHub-generated source archives rather than separately uploaded DLLs, NuGet packages or manually packaged release assets.