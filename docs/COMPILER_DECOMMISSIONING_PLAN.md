# Architectural Plan: Decommissioning the Compiler & Compiled Path in Tōast / TōSh

> **Document ID:** `TOAST-ARCH-01`  
> **Status:** Approved Architectural Decision  
> **Target:** Tōast Language Runtime & TōSh Shell Suite  
> **Related Plans:** [`docs/TOAST_SEPARATION_PLAN.md`](file:///home/komrad/projects/tosh/docs/TOAST_SEPARATION_PLAN.md), [`docs/SCIENTIFIC_COMPUTING_AND_CAS_DESIGN.md`](file:///home/komrad/projects/tosh/docs/SCIENTIFIC_COMPUTING_AND_CAS_DESIGN.md), [`docs/UNIT_SYSTEM_STABILIZATION.md`](file:///home/komrad/projects/tosh/docs/UNIT_SYSTEM_STABILIZATION.md)

---

## 1. Executive Summary & Strategic Rationale

Tōast was originally architected with a twin-track execution model: an interpreted AST evaluator (`ToshEngine`) and an ahead-of-time (AOT) intermediate language compiler (`Tosh.Compiler`) emitting .NET assemblies via `PersistedAssemblyBuilder`.

Over the course of language development, this dual-track model has imposed an unsustainable **twin-track tax**:
1. **Double Implementation**: Every language feature—symmetric operator dispatch, generic classes, compound assignments (`**=`, `//=`), tuple destructuring, defer statements, postfix conditionals—required two distinct implementations: one in the evaluator and one in the IL emitter.
2. **Parity Drift**: Subtle semantic discrepancies between interpreted and compiled execution accounted for an inordinate share of defects, diagnostics, and test failures.
3. **The Static IL Constraint**: Attempting to force an expressive, dynamically typed, homoiconic, shell-integrated language into static CLR class/method metadata fundamentally constrained language design. Advanced capabilities—such as a native Computer Algebra System (CAS), flexible runtime physical units, and homoiconic expression rewriting—cannot be cleanly compiled to static PE assemblies without cumbersome runtime shims.
4. **Architectural Contortions**: Compiled assemblies were forced to instantiate an internal static `ToshEngine` within [`ToshHost.cs`](file:///home/komrad/projects/tosh/src/Tosh.Compiler.Runtime/ToshHost.cs) to resolve dynamic calls, leading to process-wide class registry collisions (`TS-P1-48`) and memory bloat.

### The Decision
**The compiler (`Tosh.Compiler`), runtime bridge (`Tosh.Compiler.Runtime`), MSBuild SDK tasks (`Tosh.Sdk`), and the `--compile` CLI pathway are decommissioned entirely.**

Tōast transitions to a **single, unified, high-performance evaluated runtime**. Standalone executable distribution will be accomplished via modern self-extracting application host bundles (akin to Deno, Bun, and Python `zipapp`) rather than IL emission.

---

## 2. Deep Scan: Codebase Areas Radically Improved

Excising the compiler is not merely subtractive; it liberates the core language architecture across seven major subsystems:

```
                  ┌────────────────────────────────────────────────────────┐
                  │          Language Simplification Opportunities         │
                  └────────────────────────────────────────────────────────┘
                                               │
         ┌───────────────────┬─────────────────┼───────────────────┬───────────────────┐
         ▼                   ▼                 ▼                   ▼                   ▼
    [Physical Units]   [Class Model]    [Dynamic Variables] [Operator Dispatch]   [Native CAS]
    Direct object      Indexed slot     No CLR slot         Single source         Homoiconic
    references;        arrays; engine-  types; dynamic      of truth in           expression
    no string tags     scoped registry  reassignment        OperatorEvaluator     rewriting
```

### 1. Physical Units & Quantities ([`Quantity.cs`](file:///home/komrad/projects/tosh/src/Toast.Runtime/Units/Quantity.cs))
- **Prior Workaround**: Because compiled PE assemblies could not serialize live CLR unit definition objects, compiled quantity literals had to serialize bare strings `(magnitude, unitSymbol)` and perform expensive lookups against a global registry at runtime. To accommodate this, `Quantity.FromParsed` discarded conversion scale factors, leading to derived arithmetic bugs (`TS-P3-07`).
- **Post-Decommissioning**: `Quantity` holds an immutable, direct reference to its resolved [`UnitDefinition`](file:///home/komrad/projects/tosh/src/Toast.Runtime/Units/UnitDefinition.cs) and `SemanticKind`. Unit parsing occurs once. Compound arithmetic computes scaling directly with zero registry lookup overhead.

### 2. Object & Class Model ([`ToshClassDefinition.cs`](file:///home/komrad/projects/tosh/src/Tosh.Language/ToshClassDefinition.cs))
- **Prior Workaround**: Compiled code required class types to be globally discoverable. `ToshHost` maintained a process-wide static engine (`s_engine`), causing cross-assembly naming collisions when two scripts defined the same class name (`TS-P1-48`). Furthermore, member access was constrained by reflection and `TypeBuilder` rules.
- **Post-Decommissioning**:
  - Class definitions are strictly engine-scoped, eliminating all process-wide collisions.
  - Instance property storage can be refactored from string-keyed dictionaries to **direct indexed slot arrays (`object[] _slots`)** determined during binding, yielding an immediate 5x–10x speedup in property access.

### 3. Variable Reassignment & Type Invariance
- **Prior Workaround**: The compiler assigned fixed CLR types to unannotated variables based on their initializers. If an unannotated local was reassigned to a different type later in the function, compiled execution broke or drifted from the interpreter (`TS-P1-04`).
- **Post-Decommissioning**: A unified, predictable variable model. Unannotated variables are dynamic slots; annotated variables (`var x: int = 42`) enforce conversion upon assignment without battling CLR local slot constraints.

### 4. Operator Dispatch & Arithmetic Parity ([`OperatorEvaluator.cs`](file:///home/komrad/projects/tosh/src/Toast.Runtime/OperatorEvaluator.cs))
- **Prior Workaround**: The compiler frequently bypassed `OperatorEvaluator` to emit raw IL arithmetic opcodes (`add`, `sub`), causing operator overloads (`**=`, `//=`, symmetric class dispatch) to diverge between compiled and interpreted execution (`019f9862`).
- **Post-Decommissioning**: `OperatorEvaluator` becomes the single, authoritative dispatcher for all operations, comparisons, and overloads.

### 5. Exception Handling, Defer, and Control Flow
- **Prior Workaround**: The compiler maintained hundreds of lines of complex IL state-machine and exception-filtering logic (`BoundUnitEmitter.Defer.cs`) to ensure that `ToshDiagnosticException`, `OperationCanceledException`, and `defer` blocks matched interpreter semantics.
- **Post-Decommissioning**: Async streams, cooperative cancellation, and deferred cleanup run through native C# language constructs with zero emission gymnastics.

### 6. Built-in Computer Algebra System (CAS)
- **Prior Workaround**: Compiling symbolic expressions and term-rewriting rules (`expr /. pattern => replacement`) into ahead-of-time IL would have required generating dynamic assemblies or extensive runtime reflection.
- **Post-Decommissioning**: The CAS operates directly on the native AST and hash-consed expression DAGs (`SymExpr`), making symbolic math a zero-friction, first-class citizen of Tōast.

### 7. Sibling Ecosystem Simplification ([`tosh-plot`](file:///home/komrad/projects/tosh-plot/AGENTS.md))
- **Prior Workaround**: Sibling projects like `tosh-plot` had to maintain dedicated compiler-parity test suites, strict-annotation compile profiles, and probe harnesses (`tests/compiled-handles.tosh`, `tests/probes/required-type/*`).
- **Post-Decommissioning**: `tosh-plot` drops all compiler-probe harnesses and tests against the unified Tōast runtime, eliminating hundreds of lines of fragile scaffolding.

---

## 3. Master Execution Plan: `TOAST-ARCH-01`

```
  TOAST-ARCH-01: Decommission Compiler & Compiled Path
  ├── Sub-item 1: Solution Unwiring & Project Dependency Severance
  ├── Sub-item 2: CLI Surface & Driver Cleanup (Tosh.Cli)
  ├── Sub-item 3: Bound IR Absorption & Tosh.Compiler.IR Deletion
  ├── Sub-item 4: Language Engine Cleansing (Tosh.Language & Tosh.Runtime)
  ├── Sub-item 5: Physical Deletion of Compiler Projects
  ├── Sub-item 6: Test Suite & Sibling Repository Cleanup (tosh-plot)
  └── Sub-item 7: Documentation & Specification Synchronization
```

### Sub-item 1: Solution Unwiring & Project Dependency Severance
- **Target Files**: [`Tosh.slnx`](file:///home/komrad/projects/tosh/Tosh.slnx), [`src/Tosh.Cli/Tosh.Cli.csproj`](file:///home/komrad/projects/tosh/src/Tosh.Cli/Tosh.Cli.csproj), [`src/Tosh.Language/Tosh.Language.csproj`](file:///home/komrad/projects/tosh/src/Tosh.Language/Tosh.Language.csproj).
- **Actions**:
  1. Remove the following projects from [`Tosh.slnx`](file:///home/komrad/projects/tosh/Tosh.slnx):
     - `src/Tosh.Compiler/Tosh.Compiler.csproj`
     - `src/Tosh.Compiler.IR/Tosh.Compiler.IR.csproj`
     - `src/Tosh.Compiler.Runtime/Tosh.Compiler.Runtime.csproj`
     - `src/Tosh.Sdk/Tosh.Sdk.csproj`
     - `src/Tosh.Sdk.Tasks/Tosh.Sdk.Tasks.csproj`
  2. Remove project references to `Tosh.Compiler` from [`Tosh.Cli.csproj`](file:///home/komrad/projects/tosh/src/Tosh.Cli/Tosh.Cli.csproj).
  3. Remove package reference to `System.Reflection.MetadataLoadContext` from [`Tosh.Cli.csproj`](file:///home/komrad/projects/tosh/src/Tosh.Cli/Tosh.Cli.csproj).
  4. Temporarily maintain `Tosh.Compiler.IR` reference in `Tosh.Language.csproj` until Sub-item 3.

### Sub-item 2: CLI Surface & Driver Cleanup (`Tosh.Cli`)
- **Target Files**: `src/Tosh.Cli/CommandLineOptions.cs`, `src/Tosh.Cli/Program.cs` (or equivalent driver files).
- **Actions**:
  1. Remove the `--compile` / `-c` compilation mode flag and its handler.
  2. Remove compilation-specific flags: `--assembly-name`, `--output-dir`, `--no-apphost`, `--profile`, `--require-tier`.
  3. Remove references to `ToshPublisher`, `CompileProfile`, and `BoundUnitEmitter`.
  4. Ensure `tosh script.toast` and interactive REPL execution remain clean and functional.

### Sub-item 3: Bound IR Absorption & `Tosh.Compiler.IR` Deletion
- **Target Files**: [`src/Tosh.Compiler.IR/BoundNode.cs`](file:///home/komrad/projects/tosh/src/Tosh.Compiler.IR/BoundNode.cs), [`src/Tosh.Compiler.IR/BoundType.cs`](file:///home/komrad/projects/tosh/src/Tosh.Compiler.IR/BoundType.cs), `src/Tosh.Language/`.
- **Actions**:
  1. Relocate necessary AST/Bound structures from `src/Tosh.Compiler.IR/` into `src/Tosh.Language/Binding/` (or `src/Toast.Language/Binding/`).
  2. Strip out compiler-specific emitter fields (such as CLR `TypeBuilder`, `MethodBuilder`, or emission slot metadata) from bound nodes.
  3. Remove `Tosh.Compiler.IR` reference from [`Tosh.Language.csproj`](file:///home/komrad/projects/tosh/src/Tosh.Language/Tosh.Language.csproj).
  4. Delete directory `src/Tosh.Compiler.IR/`.

### Sub-item 4: Language Engine Cleansing (`Tosh.Language` & `Tosh.Runtime`)
- **Target Files**: [`ToshEngine.cs`](file:///home/komrad/projects/tosh/src/Tosh.Language/ToshEngine.cs), [`ToshEngine.Refinements.cs`](file:///home/komrad/projects/tosh/src/Tosh.Language/ToshEngine.Refinements.cs), [`docs/diagnostic-codes.md`](file:///home/komrad/projects/tosh/docs/diagnostic-codes.md).
- **Actions**:
  1. Remove compiler diagnostic codes (e.g. `unsupported_compile_shape`, `compiler_internal_error`).
  2. Sever hooks and comments referencing `Tosh.Compiler.Runtime.ToshHost.CheckType` in `ToshEngine.Refinements.cs`.
  3. Clean up the binder: remove IL signature generation and compiler-targeted shape restrictions.
  4. Ensure `BoundEvaluator` operates directly on internal language binding types.

### Sub-item 5: Physical Deletion of Compiler Projects
- **Target Directories**:
  - `src/Tosh.Compiler/`
  - `src/Tosh.Compiler.Runtime/`
  - `src/Tosh.Sdk/`
  - `src/Tosh.Sdk.Tasks/`
  - `src/Tosh.Templates/` (if any template exclusively generated compiled `.toshproj` assemblies).
- **Actions**: Remove directories from source control.

### Sub-item 6: Test Suite & Sibling Repository Cleanup (`tosh-plot`)
- **Target Files**: `tests/Tosh.Tests/`, [`/home/komrad/projects/tosh-plot/`](file:///home/komrad/projects/tosh-plot/).
- **Actions**:
  1. In `tests/Tosh.Tests/`:
     - Delete compiler feature matrix tests (`CompilerFeatureMatrixTests.cs`).
     - Remove tests asserting IL byte emission, PE headers, or `ToshHost` invocation.
  2. In `tosh-plot`:
     - Delete compiler probes (`tests/probes/required-type/*`, `tests/compiled-handles.tosh`).
     - Update [`tosh-plot/AGENTS.md`](file:///home/komrad/projects/tosh-plot/AGENTS.md) to remove compiler-parity verification rules.

### Sub-item 7: Documentation & Specification Synchronization
- **Target Files**: [`docs/COMPILED_TOSH.md`](file:///home/komrad/projects/tosh/docs/COMPILED_TOSH.md), [`docs/CLR_ABI_v1.md`](file:///home/komrad/projects/tosh/docs/CLR_ABI_v1.md), [`docs/TOAST_SEPARATION_PLAN.md`](file:///home/komrad/projects/tosh/docs/TOAST_SEPARATION_PLAN.md), [`README.md`](file:///home/komrad/projects/tosh/README.md).
- **Actions**:
  1. Mark `COMPILED_TOSH.md` and `CLR_ABI_v1.md` as archived / superseded by `TOAST-ARCH-01`.
  2. Update `docs/TOAST_SEPARATION_PLAN.md` to reflect that the evaluator is the single execution engine.
  3. Update `README.md` project tables to remove compiler entries.

---

## 4. Evaluator Evolution Roadmap

With the compiler excised, the execution engine roadmap centers on the evaluator:

```
  Phase A: Monolith Split (Separation Plan Phase B)
  ├── Extract ToshEngine partials into modular evaluator visitors
  ├── Separate Shell host primitives from core Tōast Language evaluator
  └── Establish clean AST walking pipelines

  Phase B: Bound AST & Slot-Indexed Environment Optimization
  ├── Lower syntax directly to bound AST nodes in Tosh.Language
  ├── Replace string dictionary variable lookups with slot index arrays
  └── Add fast-path numeric and unit dispatch in OperatorEvaluator

  Phase C: Future Execution Engine Assessment (Optional Bytecode VM)
  └── Profile hot loops; if needed, implement a compact, in-memory
      register/stack bytecode VM without external PE assembly emission.
```

---

## 5. Modern Application Bundling Strategy

To preserve the ability to distribute standalone applications without an AOT IL compiler, Tōast will adopt the **Self-Extracting Host Bundle** model:

```
┌────────────────────────────────────────────────────────────────────────┐
│                   Single-File Standalone Executable                    │
├────────────────────────────────────────────────────────────────────────┤
│ 1. Precompiled Native Tōast Host Engine (Native AOT / Single-File)    │
│ 2. Appended Compressed Application Archive (.toast sources, manifests) │
│ 3. Entry point metadata & embedded assets                              │
└────────────────────────────────────────────────────────────────────────┘
```

1. **How It Works**:
   - Running `tosh bundle main.toast -o myapp` packages the script and dependencies into a zip/tar payload and appends it to a pre-compiled native host stub.
   - When executed, the host detects its appended payload, mounts it in an in-memory virtual filesystem, and immediately begins evaluation.
2. **Advantages**:
   - **Zero Compilation Constraints**: The application retains 100% of Tōast's dynamic capabilities, physical units, and CAS.
   - **Zero Build-Time Parity Bugs**: The code evaluated by the bundled binary runs the identical engine that powers the interactive shell.
   - **Instant Packaging**: Bundling completes in milliseconds (simple archive concatenation) compared to heavy IL compilation.

---

## 6. Acceptance Criteria

- [ ] `Tosh.slnx` builds completely without `Tosh.Compiler*` or `Tosh.Sdk*`.
- [ ] `tosh` runs scripts and interactive REPL sessions with zero references to `ToshHost` or compilation flags.
- [ ] All remaining unit and integration tests in `Tosh.Tests` pass.
- [ ] `tosh-plot` builds and passes its test suite purely against interpreted execution.
- [ ] The codebase is completely free of compiler workarounds in `Quantity.cs` and `ToshClassDefinition.cs`.
