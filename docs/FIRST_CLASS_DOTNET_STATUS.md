# TōSh as a first-class .NET language — status & roadmap

> **Frozen.** This is the roadmap for the compiled backend, which the separation plan
> freezes (`TOAST_SEPARATION_PLAN.md`, Phase 0). Its waves are not tracked as plan
> items; they describe work on a component leaving the build.
>
> Three things from its deferred list *were* still live and are now filed separately:
> `TOAST-0011` (native callbacks), `TOAST-0012` (`Span<T>`/`Memory<T>` native shapes)
> and `PLAN-0002` (suite flakiness). Accurate as of 2026-07-30; not maintained.

Snapshot of where compiled tosh stands on the path to a "real .NET
citizen," and what's left. Companion to [COMPILED_TOSH.md](COMPILED_TOSH.md),
which tracks the per-feature emit table.

## Current read

TōSh now has a real buildable .NET executable subset. The compiler path is
not just packaging the interpreter: it parses, binds, lowers, type-checks, and
emits a managed PE with embedded portable PDBs. The project is now in the
middle stage between "compiled shell script" and "first-class .NET language."

The remaining gap is not one missing feature. It is a cluster of product and
ABI work:

- finish SDK productization beyond the now-working ordinary
  `dotnet build`/`run`/`publish`/`clean` lifecycle;
- give emitted TōSh libraries a stable public CLR shape;
- close the whole-language compiler feature matrix across `permissive`,
  `runtime`, and `pure` profiles;
- prove accepted `pure` artifacts are host-independent with a post-emit
  dependency audit (`TS-P1-20`);
- make more type declarations true CLR metadata instead of source replay;
- deepen type checking until `pure` profile is a real compatibility gate;
- finish debugging, publishing, and cross-language consumption polish.

## What works today

### Compiler driver

- `tosh --compile a.tosh [b.tosh ...] -o out.dll` produces a managed
  assembly via `System.Reflection.Metadata.PersistedAssemblyBuilder`.
- Multi-file inputs concatenate into one bound unit, so cross-file
  `partial module` declarations merge cleanly.
- The compile path runs parse diagnostics, binder diagnostics, lowering,
  compile-time type diagnostics, emitter diagnostics, and profile-tier
  diagnostics before writing a successful artifact.
- Partial output is deleted on failed emit so stale `.dll`s are not run by
  accident.
- Profiles are implemented: `permissive`, `runtime`, and `pure`.
  `permissive` allows executable source replay; `runtime` allows
  compiler-host dependencies but rejects Tier 3 replay; and `pure` is intended
  to allow only host-independent IL plus stable, engine-independent
  `Tosh.Runtime` primitives. The current tier gate records that intent;
  `TS-P1-20` tracks artifact-level dependency enforcement.

### IL features lit up

- Top-level `func` declarations emit real `public static` methods with IL
  bodies. Fully typed functions also get a typed CLR primary method, with the
  legacy object-shaped shim retained for dynamic/runtime dispatch.
- Top-level `var`, captured top-level locals, reassignment, control flow,
  `try`/`catch`/`finally`, `throw`, `match`/`switch`, ranges, spreads,
  list/dict/set literals, interpolated strings, and many expressions emit IL.
- Multi-stage pipelines are buildable. Builtins and most command-shaped calls
  dispatch through `ToshHost`, while the surrounding program flow is emitted
  IL.
- User functions can participate as pipeline stages through the dynamic shim.
- Subcommand trees are buildable as executable entry points, but currently
  use Tier 3 source replay for the dispatcher.

### Modules → real CLR types

- `module Foo { ... }` emits `public sealed abstract class <asm>.Foo`
  (the CLR encoding of `static class`).
- `module Foo.Bar` emits a nested static class (`Foo+Bar` in metadata).
- `partial module Foo` accumulates into one `TypeBuilder` across files.
- Module-scope `var x = expr` emits a `public static object x` field with
  initializer code in the type's `.cctor`.
- Module-scope `func name(args)` emits a `public static object name(args)`
  method with a real IL body.
- `[ToshModule(QualifiedName, SpanStart, SpanLength)]` assembly attributes
  are emitted recursively for tooling and discovery.
- Source replay is still registered in parallel so tosh-side qualified access
  keeps the interpreter's semantics.

### Classes and records → partial CLR shells

- Simple records now emit real `public sealed class` shells with positional
  constructors and public `object` fields.
- Simple classes with primary-constructor properties and plain methods can
  emit real CLR class shells. Eligible instance methods become real CLR
  instance methods, `$this` maps to `ldarg.0`, shell fields can be read with
  direct `ldfld`, and eligible `new TypeName(...)` calls lower to direct
  `newobj`.
- Conservative fallbacks remain for traits, interfaces, abstract
  and hermit classes, secondary constructors, computed properties,
  getters/setters, lazy properties, static methods, rest/optional parameters,
  captures, and other shapes that do not yet have a stable CLR ABI.
- Inheritance is now a real CLR type hierarchy: `class Dog extends Animal`
  emits `Dog` with `Animal` as its CLR `BaseType`. Instance methods on the
  base open a `Virtual | NewSlot` vtable slot, and `overrule` methods carry
  `DefineMethodOverride` metadata so polymorphic dispatch through a base-typed
  reference hits the derived implementation.
- Type declarations are still also registered with `ToshHost` by source span
  so dynamic call sites and interpreter-visible type behavior keep working.

### Type system and diagnostics

- `BoundType`, `TypeNameResolver`, `TypeInferrer`, and `TypeChecker` are now
  real compiler components rather than just design notes.
- Compile annotations are enforced for functions and dynamic-sensitive
  variables unless dynamic is explicitly allowed.
- User function call arity and argument type checks exist.
- Command metadata now carries richer type information, including argument
  type names, CLR type references, refinements, option value types, output
  types, side effects, and pipeline input metadata.

### Debug info

- Embedded Portable PDBs are emitted into the compiled `.dll`.
- Statement-granularity sequence points map IL back to `.tosh` source lines.
- No companion `.pdb` is required.
- Source Link is not emitted yet.
- Multi-file compile currently uses a concatenated synthetic source document,
  not one PDB document per input file.

### SDK / project model

- `src/Tosh.Sdk` and `src/Tosh.Sdk.Tasks` exist.
- `.toshproj` compilation works through ordinary `dotnet build`, `dotnet run
  --project`, `dotnet publish`, and `dotnet clean` lifecycles.
- The SDK can use either an in-process task or an `Exec` fallback to the CLI.
- Runtime DLL staging, `.deps.json`, `runtimeconfig.json`, apphost output,
  single-file bundle output, and SDK-triggered reference assembly emission are
  implemented.
- Packaged-SDK `PackageReference` projects can restore through a generated
  NuGet restore project, stage package runtime assemblies next to the compiled
  app before `.deps.json` emission, and carry those DLLs through `dotnet run`
  and `dotnet publish`. Current coverage includes a transitive package
  dependency.
- TōSh `ProjectReference` coverage now verifies build/stage/deps behavior plus
  `dotnet run --project` and published execution.

### Audit verification

- `dotnet build Tosh.slnx --no-restore /m:1 /v:minimal` passed with
  0 warnings.
- Focused compiler/type/SDK/matrix tests passed: 256 passed, 0 failed.
- Latest user-reported full suite result: 2298 passed, 0 failed. The full
  suite was not re-run during this docs-only update.
- SDK lifecycle tests cover direct-import `dotnet build`, `dotnet run
  --project`, `dotnet publish`, apphost, single-file publish, refasm emission,
  runtime staging, `Clean`, multi-source compilation, `ProjectReference`
  build/stage/deps/run/publish behavior, packaged-SDK `PackageReference`
  restore/stage/deps/run/publish behavior with a transitive package dependency,
  C# refasm consumption, and packaged-SDK build/run smoke coverage.

### Language-surface compiler matrix

`tests/Tosh.Tests/CompilerFeatureMatrixTests.cs` now pins intended gate
acceptance across all three compile profiles. It is intentionally a baseline
test, not an aspirational one: when a feature moves from unsupported to
permissive source replay, from replay to runtime-hosted execution, or from
runtime support to host-independent IL plus stable runtime primitives, the
expected profile flags should change in the same commit. Matrix acceptance does
not replace the post-emit dependency audit tracked by `TS-P1-20`.

Current matrix read:

| Surface | Current compiler state |
|---------|------------------------|
| Core expressions, typed top-level functions, simple class shells, simple record shells | Accepted by the current `permissive`, `runtime`, and `pure` gates; pure artifact conformance remains subject to `TS-P1-20`. |
| Simple integral enum declarations | Accepted by all three current gates and emitted as real CLR enum metadata. Qualified member access emits the integral constant directly, avoiding CLR short-name collisions and preserving ToastScript names in formatted output. |
| Builtin command dispatch and pipeline blocks | Accepted by `permissive` and `runtime`; rejected by `pure` because they depend on `ToshHost`. |
| Simple module shells | Accepted by `permissive` and `runtime`; rejected by `pure` because module registration/qualified access still uses runtime support. |
| Inheritance, interface implementation, hermit/static-only classes, interfaces, traits, unions, structs, aliases/refinements, events, overload sets, declared optional/rest top-level parameters, native `bind`, modules with nested simple class declarations, and non-integral/dynamic-value enum forms | Broad coverage exists and is still being expanded. Rows should stay explicit about which pieces are native IL / Tier 1, runtime-hosted / Tier 2, source replay / Tier 3, or deliberately unsupported. |
| Runes | Clean only in `permissive` through whole-script source replay, because rune invocation is an engine expansion step rather than a normal command call. |

The current matrix no longer has any "rejected by every profile" rows in the
latest documented audit. That is real progress, but it is not yet proof that
the entirety of ToastScript is compilable: the matrix is representative and
growing, not exhaustive. The next audit task is to keep expanding it until
every syntax and semantic family is covered.

## What's still dynamic / shell-y

- Source replay remains part of the `permissive` compatibility story for
  features whose semantics still depend on engine execution, especially runes,
  residual block/subcommand fallback paths, and dynamic escape hatches.
- Several formerly replay-only or all-profile-rejected families have moved
  forward. The compiler feature matrix, not this paragraph, is the source of
  truth for whether a given form is native IL / Tier 1, runtime-hosted /
  Tier 2, source replay / Tier 3, or deliberately unsupported.
- Builtin command calls are mostly runtime calls through `ToshHost`; command
  argument binding is not yet statically checked the way C# method calls are.
- Most emitted public surfaces are still object-shaped. This keeps the
  compiler moving, but it does not yet give C#, F#, or VB consumers a rich
  contract.
- The runtime host uses global ambient state, which is convenient for simple
  executables but not isolation-friendly when multiple compiled TōSh
  assemblies are loaded into the same process.
- The REPL remains tree-walked. That is an acceptable design point, but it
  means the interpreter and compiler must keep sharing semantics carefully.

## First-class .NET blockers

### 1. SDK productization

The SDK now makes the normal .NET gesture work:

```bash
dotnet build MyTool.toshproj
dotnet run --project MyTool.toshproj
dotnet publish MyTool.toshproj
```

First-class status now needs the remaining SDK behavior to be as complete as a
normal .NET SDK:

- package-reference support is now covered for packaged SDK restore, runtime
  assembly staging, `.deps.json`, `dotnet run`, `dotnet publish`, and
  transitive package dependencies, but it is not yet full NuGet/MSBuild asset
  semantics: central package management, RID/native/resource assets,
  buildTransitive/analyzers, and compile-time reference exposure still need
  design and tests;
- project-reference coverage now includes `dotnet run` and publish, but still
  needs C# project references, transitive project references, and richer
  metadata/refasm semantics;
- task diagnostics should convert spans to line/column positions;
- reference assemblies are emitted from the SDK and can be consumed by a C#
  project via direct refasm reference, but they are still body-bearing rather
  than metadata-only.

### 2. Cross-language ABI

External .NET consumers need a stable metadata contract:

- name mangling and original-name attributes;
- visibility mapping for `shy`, `guarded`, `local`, `proud`, and defaults;
- library vs executable output;
- explicit entry-point selection;
- XML docs or equivalent metadata for IDEs;
- nullability, generics, refinements, and dynamic erasure rules;
- broader C# consumer coverage for project references, package references,
  overloads, visibility, library output, and richer typed metadata.

### 3. Real CLR type definitions

Simple classes and records now have CLR shells, but first-class status needs:

- typed fields/properties rather than mostly `object`;
- properties with real getters/setters and init/read-only semantics;
- record equality/hash/deconstruction/with-style behavior;
- constructors and overload rules;
- inheritance, interfaces, traits, virtual/override, abstract members;
- static members and hermit/static-only classes;
- structs, enums, unions, interfaces, traits, events, and type aliases as
  real metadata where appropriate.

### 4. Type checking depth

The checker should eventually validate:

- builtin command argument and option types from command metadata;
- pipeline input/output element types;
- conditions and operator compatibility;
- member access and method calls against emitted type metadata;
- class, record, and module bodies;
- return completeness and control-flow-aware narrowing;
- refinements and contracts at compile boundaries.

### 5. Runtime isolation and dependency model

Compiled programs need a clearer host model:

- assembly-scoped source registration instead of one global source map;
- multiple compiled TōSh assemblies loaded into one process without state
  collisions;
- runtime guards for shell-only commands even when dynamic/source replay paths
  are used;
- a normal `.deps.json`/runtime dependency story for publish and packaging.

### 6. Debugging and publishing polish

Still needed:

- Source Link debug directory;
- one PDB document per source file in multi-file builds;
- library-mode output with no implicit `Main`;
- real reference assemblies rather than body-bearing assemblies stamped with
  `[ReferenceAssembly]`.

## Recommended next order

ToastScript has crossed from compiler experiment into a real buildable
.NET-language path: `dotnet build`, `dotnet run`, SDK smoke tests,
runtime/refasm output, overload metadata, inheritance/override dispatch,
interfaces, unions, package/project references, and broad compiled execution
coverage are now present. The next phase is less about proving that a DLL can
be emitted and more about making the contract exhaustive, predictable,
documented, and pleasant from the rest of the .NET ecosystem.

This is the current ordered path to first-class .NET status. Each item should
land with focused tests, feature-matrix coverage, and a documentation update.

1. **Stabilize the current correctness baseline.**
   - Keep the redirection/input baseline covered, including nested
     input/output/error redirection scope interactions.
   - Preserve direct compiled probes for `read-line in<`, `cat in<`,
     `wc in<`, `grep in<`, output append, error redirection, and mixed
     pipeline/redirection forms.
   - Add regression tests whenever a fix touches `Console.In`,
     `CommandContext.Input`, `ToshHost` redirection state, or generated
     `try/finally` restoration code.
   - Exit criteria:
     - `CompilerFeatureMatrixTests` has rows for the redirection families.
     - Focused compiled-output probes pass without command-specific hacks.
     - Runtime input/output state is restored after success and failure.

2. **Make the compiler feature matrix exhaustive.**
   - Generate the checklist from four sources:
     - parser syntax nodes and token/operator tables;
     - bound nodes, lowerer outputs, type-checker paths, and emitter dispatch;
     - language spec sections in `docs/spec/toastscript-spec.tex`;
     - command metadata exported by the CLI and parity tool.
   - For every language family, add:
     - a profile expectation row (`permissive`, `runtime`, `pure`);
     - at least one compiled execution conformance test;
     - a reflection/metadata test when public CLR shape matters;
     - a negative test when the feature is intentionally profile-limited.
   - Track every row as one of:
     - host-independent IL plus stable `Tosh.Runtime` primitives / Tier 1;
     - runtime-hosted / Tier 2;
     - source replay / Tier 3;
     - deliberate unsupported diagnostic.
   - Exit criteria:
     - no major ToastScript feature exists only as "we think it works";
     - no parser or bound-node family is missing from the matrix;
     - all all-profile rejections are explicitly justified.

3. **Freeze the compilation profile contract.**
   - Define the profiles as a product promise:
     - `permissive`: the full language may compile by mixing IL,
       runtime-hosted execution, and source replay;
     - `runtime`: app/library code may depend on the runtime host but should
       avoid whole-script source replay for ordinary language constructs;
     - `pure`: CLR-first codegen only, suitable for APIs that must look and
       behave like conventional .NET members.
   - Add CI lanes for:
     - all feature-matrix rows under `permissive`;
     - all ordinary app/library samples under `runtime`;
     - selected public-ABI samples under `pure`.
   - Exit criteria:
     - profile failures are design signals, not incidental emitter gaps;
     - emitted artifacts pass an independent `AssemblyRef`/`MemberRef` audit
       for disallowed compiler-host and replay dependencies;
     - docs describe why a feature belongs to each profile.
   - **Audit follow-up (2026-05-06).** Promote the `runtime` profile to the
     official *redistributable-library contract*: a `--profile=library`
     alias of `runtime` that additionally enforces typed public signatures
     and metadata-only refasm output. Document `permissive`-compiled
     assemblies as *executable bundles*, not libraries, since they may
     carry their own source for replay.

4. **Reduce Tier-3 source replay to the smallest intentional surface.**
   - Audit every remaining call to:
     - `Register*FromSource`;
     - `RunScriptFromSource`;
     - source-based subcommand dispatch;
     - `RequireTier(3, ...)` in `BoundUnitEmitter`.
   - Prioritize removal in this order:
     - ordinary declarations inside modules;
     - cross-assembly `require`;
     - residual subcommand tree fallback shapes;
     - block argument closure/re-evaluation gaps;
     - native interop shapes that can be represented as metadata;
     - rune call sites, after the rune model is decided.
   - Exit criteria:
     - normal app/library code can use `--profile runtime`;
     - remaining Tier-3 rows are documented language-design choices.

5. **Write and enforce the CLR ABI v1 spec.**
   - Lock the public rules for:
     - assembly identity, root type names, module type names, and nested types;
     - identifier mangling and collision diagnostics;
     - visibility, `shy`, `guarded`, `local`, `proud`/`public`, and generated
       helper visibility;
     - overloads, optional/rest parameters, constructor binding, and erased
       dynamic signatures;
     - library mode vs executable mode and `func main` rules;
     - attributes such as `ToshOriginalNameAttribute` and
       `ToshTypeAttribute`;
     - nullability, refinements, docs metadata, and compatibility guarantees.
   - Add ABI tests from the viewpoint of:
     - reflection;
     - Roslyn C# compilation against refasm;
     - `ProjectReference`;
     - `PackageReference`;
     - runtime invocation through `ToshHost`.
   - Exit criteria:
     - C#, F#, VB, reflection, and build tools have a stable metadata story.

6. **Finish type declaration metadata.**
   - Classes:
     - secondary constructors;
     - static/shared members;
     - computed, lazy, fixed, and vital properties;
     - `hollow`, `hermit`, `strict`, `sealed`, `partial`, visibility, and
       inheritance edge cases.
   - Interfaces and traits:
     - requirement metadata;
     - default implementation rules;
     - conflict resolution;
     - dispatch through interface-typed and trait-composed values.
   - Unions:
     - stable discriminated-union ABI;
     - variant constructors and payload fields/properties;
     - pattern matching over compiled unions;
     - C# consumption shape.
   - Structs, events, aliases, and refinements:
     - typed fields and layout rules;
     - validation/refinement metadata where possible;
     - event add/remove/invoke shape;
     - clear fallback rules for dynamic-only cases.
   - Exit criteria:
     - type declarations are CLR metadata first and source replay last.
   - **Audit follow-up (2026-05-06).** Async user functions:
     - emit `Task<T>` / `Task` return types for `async func` declarations
       whose annotated return type is non-stream (today only
       `IAsyncEnumerable<object?>` is produced, even for scalar-returning
       async funcs);
     - accept a conventional trailing `CancellationToken` parameter and
       propagate it through `ToshHost` pipeline drains;
     - keep `IAsyncEnumerable<T>` as the explicit shape for stream-returning
       functions;
     - add reflection tests asserting the emitted return-type shape per
       declaration form.
   - **Re-audit (2026-05-06).** *De-scoped.* Reflection probe of
     `--compile` output confirms typed funcs already emit as ordinary
     synchronous `T`-returning CLR methods. Tosh has no `async func`
     surface syntax, so there is no Task-wrapping gap to close. The
     original audit conflated the pipeline-stage IAsyncEnumerable code
     path with the user-callable method shape.
   - **Audit follow-up (2026-05-06).** Native interop:
     - widen `bind native` beyond primitives to cover `struct` marshalling,
       `out`/`ref` parameters, function pointers / callback delegates, and
       `[MarshalAs]` customization;
     - expose `Span<T>` / `Memory<T>` as first-class parameter shapes for
       buffer-style P/Invoke;
     - add a conformance row per shape under `interop.native-*`.

7. **Move from object-shaped metadata to typed metadata.**
   - Use annotations and inference for public member signatures wherever the
     language can prove a type.
   - Keep object/dynamic shims for ToastScript's late-bound dispatch, but do
     not let those shims be the only visible public API when a typed API is
     known.
   - Add reflection assertions for:
     - return types;
     - parameter types;
     - field/property types;
     - by-ref/native interop signatures;
     - enum/union/member shapes.
   - Exit criteria:
     - C# consumers see useful signatures instead of an all-`object` surface.
   - **Audit follow-up (2026-05-06).** When a `func` has typed parameters
     and a typed return, emit a *single* typed CLR method as the canonical
     surface; keep the `object`-shaped shim as a private helper rather than
     a peer overload that confuses C# resolution. Reflection tests should
     assert the public visible method count matches the source `func` count
     for fully-typed declarations.
   - **Re-audit (2026-05-06).** *Already done.* Reflection probe of an
     overloaded typed func (`func pick(a: int) -> string` and
     `func pick(a: int, b: int) -> string`) shows exactly two typed
     methods, no `object`-shaped peer shim. Untyped funcs separately emit
     as `Func_<name>(Object) -> Object` to avoid colliding with potentially
     mangled identifiers; this is the intended shape, not a shim leak.

8. **Make reference assemblies real.**
   - Replace the current body-bearing `.ref.dll` with metadata-only output.
   - Verify metadata parity between implementation assembly and refasm.
   - Cover:
     - direct C# compile against refasm;
     - C# `ProjectReference` to a `.toshproj`;
     - package restore using the refasm for compile assets;
     - clean/publish behavior for runtime vs reference artifacts.
   - Exit criteria:
     - `.ref.dll` behaves like a normal .NET reference assembly.

9. **Productize the SDK path.**
   - Package references:
     - central package management;
     - RID-specific native/runtime assets;
     - resources/content files;
     - `build`, `buildTransitive`, analyzers, and source generators;
     - transitive runtime/package assets.
   - Project references:
     - Tosh-to-Tosh;
     - C#-to-Tosh;
     - Tosh-to-C#;
     - transitive project references;
     - publish transitivity.
   - Diagnostics:
     - MSBuild file/line/column diagnostics;
     - profile-specific error codes;
     - consistent CLI and task output.
   - **Audit follow-up (2026-05-06).** Library distribution:
     - publish `Tosh.Runtime` and `Tosh.Compiler.Runtime` as standalone
       NuGet library packages (today only `Tosh.Sdk` sets `IsPackable=true`);
     - add a `dotnet new tosh-lib` template (and a `tosh-app` template) so
       the standard .NET project-creation gesture works out of the box;
     - wire a `<ToshPack>` / `dotnet pack` flow for `.toshproj` libraries
       so user-authored tosh modules can ship as ordinary NuGet packages
       consumable from C#;
     - turn on `Deterministic`, `ContinuousIntegrationBuild`,
       `EmbedUntrackedSources`, and SourceLink across every `Tosh.*`
       project (none are configured today);
     - decide on strong-naming policy (sign with a project SNK, or
       explicitly opt out and document why) so consumers with policy-pinned
       loaders are unblocked;
     - emit `.snupkg` symbol packages alongside implementation packages
       (PDBs are embedded today, which precludes stripped-symbol
       distribution).
   - Exit criteria:
     - `.toshproj` feels like a normal SDK-style project.

10. **Isolate the runtime host.**
    - Replace global source/runtime state with assembly-scoped state.
    - Support multiple compiled ToastScript assemblies in one process.
    - Make redirection/input/runtime state nestable and thread-safe.
    - Add `AssemblyLoadContext` load/unload tests.
    - Exit criteria:
      - host state does not leak across assemblies, tests, threads, or nested
        invocations.

11. **Deepen static type checking.**
    - Use command metadata for builtin argument, option, and pipeline contracts.
    - Flow pipeline element types through `where`, `map`, `get`, aggregation,
      and conversion commands.
    - Check member/method/index access against emitted or referenced types.
    - Enforce return completeness and typed return compatibility.
    - Add narrowing for `is`, `is not`, pattern arms, refinements, null checks,
      and union variants.
    - Exit criteria:
      - common mistakes fail before runtime, with spec-linked diagnostics.

12. **Improve debugging and tooling.**
    - Emit one PDB document per source file.
    - Add Source Link when repository metadata is available.
    - Verify stack traces, breakpoints, stepping, and generated helper hiding.
    - Feed compiler/spec metadata into LSP hover, completion, diagnostics, and
      go-to-definition.
    - Exit criteria:
      - debugging compiled ToastScript feels like debugging a normal .NET
        language.
   - **Audit follow-up (2026-05-06).** Close the LSP capability gaps in
     `src/Tosh.Lsp/ToshLanguageServer.cs`:
     - advertise and implement *find all references*
       (`textDocument/references`);
     - advertise and implement *rename* (`textDocument/rename`,
       `prepareRename`);
     - add a document formatter (`textDocument/formatting`,
       `textDocument/rangeFormatting`) wired to a deterministic tosh
       formatter (the Microsoft-grade tooling expectation).
      - compile-time macro expansion with re-binding;
      - runtime-preserved quoted ASTs with explicit profile limits;
      - permissive-only source replay with a documented diagnostic in stricter
        profiles.
    - Do not half-lower runes until both variable-scope coupling and lazy
      thunk semantics are solved.
    - Exit criteria:
      - runes have a documented profile story and no accidental semantic split
        between interpreter and compiler.

14. **Refresh docs after every milestone.**
    - Update this document with status, test names, matrix counts, and
      remaining source-replay sites.
    - Update [COMPILED_TOSH.md](COMPILED_TOSH.md) when the emitter, SDK,
      refasm, or ABI contract changes.
    - Update [SPEC_STATUS.md](SPEC_STATUS.md) and the language spec when the
      user-visible language contract changes.
    - Regenerate generated spec artifacts rather than hand-editing them.
    - Exit criteria:
      - docs describe the current system, not last month's audit.

### Other ongoing tracks (not gating first-class status)

- Keep growing the compiler feature matrix until it covers every syntax
  family in the language reference.
- Keep `permissive` at zero all-profile rejections as new families arrive.
- Productization polish: SDK edge cases, broader C# consumer coverage,
  metadata-only refasm, Source Link, multi-file PDB documents, library mode.
- Type-checker depth: builtin command argument/option validation,
  pipeline element typing, refinement/contract enforcement at boundaries.
- Runtime isolation: assembly-scoped source registration, multi-assembly
  process safety.

### Remaining source-replay surface (audit, May 2026)

At the May 2026 audit point, source replay was concentrated in these
`BoundUnitEmitter` call-site families. Re-audit this list whenever roadmap
step 4 removes a fallback:

1. **Rune call sites** (`RequireTier(3, "whole-script replay (rune
   expansion)")` at [BoundUnitEmitter.cs#L1169](../src/Tosh.Compiler/BoundUnitEmitter.cs#L1169)).
   Blocked on step 6 phase 2; see design notes above.
2. **Subcommand-tree dispatch fallback** (`RequireTier(3,
   "subcommand-tree dispatch (argv-driven entry point)")` at
   [BoundUnitEmitter.cs#L1158](../src/Tosh.Compiler/BoundUnitEmitter.cs#L1158)).
   Only fires when `CanCompileSubcommandDispatch()` returns false. The
   compiled path is the dominant case; remaining shapes are
   eager/hollow/vital flag interactions and nested `flag … from …`
   delegation. Incremental.
3. **Block argument re-evaluation** (`RequireTier(3, "block argument
   (re-evaluates source at runtime)")` at
   [BoundUnitEmitter.cs#L6717](../src/Tosh.Compiler/BoundUnitEmitter.cs#L6717)).
   Fires for block arguments whose lowering can't be expressed
   statically (closures over runtime-only frames). The compiled
   `MakeCompiledBlock` path covers the dominant case; this branch is
   the residual escape hatch.

Per-declaration source replay (Tier 3 via
`RegisterDeclarationFromSource`) still fires for:

- `rune` definitions (registers RuneCommand; requires engine-side
  body AST for thunked expansion).
- `require` statements whose target is not a build-time sibling
  (`interop.require-statement` matrix row, runtime/pure rejected).
- `bind` blocks the P/Invoke lifter cannot represent (struct-by-value
  marshaling, by-ref strings, custom calling conventions beyond
  `cdecl`/`stdcall`/`winapi`/`thiscall`/`fastcall`).

Per-declaration replay is retired for CLR-shell-eligible type-definition
shapes. Advanced/generic declarations that exceed the shell path can still
register source and remain explicitly Tier 3.

### Operator and syntax-family coverage (audit, May 2026)

A scan of the parser/binder enumerated ~50 statement records and
~30 expression families. After this round of work the compiler emits
clean IL for the families below. Each is locked in by a row in
`CompilerFeatureMatrixTests` (63 cases total at last count).

**Operators (canonical across interpreted and emitted execution; eager emit
sites are intended Tier 1):**

- Arithmetic: `+`, `-`, `*`, `/`, `%`, `**` (power), `//` (floor-div).
- Comparison: `==`, `!=`, `<`, `>`, `<=`, `>=`.
- Type/membership: `is`, `is-not`, `as`, `in`, `not-in`, `is-in`,
  `is-not-in`, `contains`, `starts-with`, `ends-with`.
- Regex: `=~`, `!~`.
- Short-circuit: `and`, `or` (inline IL using
  `OperatorEvaluator.ToBoolean`).
- Null-coalesce: `??` (inline branchless IL).
- Unary: `-`, `!`, `not` (the latter routes through
  `OperatorEvaluator.EvaluateUnary`).

The default branches of `EmitBinaryOperator` and `EmitUnaryOperator`
now defer to `OperatorEvaluator.EvaluateBinaryWithDiagnostics` /
`EvaluateUnary` rather than emitting an "unsupported operator"
diagnostic. New operators still require parser/bound-node support and explicit
parity cases; sharing the runtime dispatcher prevents a second semantic
implementation.

`CompilerOperatorParityTests` compares CLR type, value, canonical stdout, and
structured diagnostics across interpreted and emitted execution.
`BoundUnitEmitterTests` retains broader emission-shape coverage.

**Statements / expression families implemented and matrix-pinned:**

- Control flow: `if`/`else`, `while`, `until`, `switch`, `try`/`catch`/
  `finally`, `throw`, `defer`, `break`/`continue`, `yield` (inside
  generator functions).
- Variables: `var`, destructuring (array + record), tuple assignment,
  ordinary and compound assignment across local, captured, member, and index
  targets (`+=`, `-=`, `*=`, `**=`, `/=`, `//=`, `%=`), and
  null-coalescing assignment (`??=`) as its separate short-circuiting path;
  `using`/`import`.
- Expressions: ranges (`a..b`), list/dict/set/tuple literals,
  string interpolation, lambda/`func()=>` callables, spread (`...`),
  member/index access.
- Strings: heredoc; regex literals (Tier 2 — runtime profile).
- Pipelines / redirections: `out>` and friends; multi-stage builtin
  pipelines (Tier 2 — runtime profile).
- Modifiers: `fixed var` (Tier 2), refinement-typed `var`.
- Subcommand trees: `flag` (Tier 2), `subcommand` (Tier 2).
- Concurrency: `async`/`await` as Tier-2 commands.

**Known acceptance gaps outside the current representative matrix:**

1. `BoundAllocStatement` — `alloc buf = 1024`. Native buffer
   allocation for unsafe interop. Will likely route through a
   helper on `ToshHost` (allocate via `NativeMemory.Alloc`,
   register cleanup) once the interop surface is finalised.
2. `async func`-modifier prefix — the parser accepts `async func f()`
   but the lowering produces an empty pipeline. Either the
   modifier should be rejected with a clear diagnostic or it
   should desugar to `func f() { return async { … } }`. Pending
   surface decision.

These findings must be added as explicit feature-matrix rows before the matrix
can be called exhaustive. They do not contradict the current statement that no
existing matrix row is rejected by every profile.


## Decision points already settled

- Modules map to **`public static partial class`**-shaped CLR types, not CLR
  namespaces.
- Module `var` fields are mutable `public static object` fields by default;
  `fixed` is the lever for read-only semantics.
- Source replay remains a compatibility fallback while the emitter grows, but
  it is explicitly Tier 3 and must be removable from strict compiled profiles.
- The REPL stays tree-walked. Compiled TōSh is for scripts, modules, CLIs, and
  library assemblies.
