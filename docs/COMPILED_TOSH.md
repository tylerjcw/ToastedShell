# Compiled ToastScript: Design Considerations

## Context

ToastScript today is a tree-walking interpreter hosted inside the TōSh shell process. The vision is a *buildable* .NET language: `toshc mytool.tosh` produces a self-contained `.dll`/`.exe`, runnable without the shell.

The hard question isn't "how does the parser become a code generator" — that's mechanical. The hard question is: **what's the *language*, and what's just *the shell*?** Today they're tangled. Compiling forces a clean separation.

This document lays out what would have to be dropped, added, and changed, then proposes a direction.

---

## The core split: language vs shell

The 256+ built-in commands and language constructs sort into three buckets. The bucket determines what happens to each at compile time.

### Bucket A — Pure language (always keep, always compile)

Constructs that translate cleanly to .NET concepts.

- **Type system:** classes, records, structs, unions, traits, enums, modules, generics, refinements, type casts (`as`), pattern matching
- **Control flow:** `if`, `for`, `while`, `until`, `switch`, `match`, `try/catch/finally`, `defer`, `break`, `continue`, `return`, `throw`, `yield`
- **Functions:** `func`, anonymous lambdas, arrow functions, typed parameters, return types, splat (`...`), named arguments
- **Variables:** `var`, `const`, scope modifiers, destructuring, tuple unpacking
- **Expressions:** literals, string interpolation, ranges, comprehensions (list/set/dict/generator), member access, null-safe `?.`, null-coalescing `??`, ternary
- **Operators:** arithmetic, comparison, regex (`=~`/`!~`), logical, membership (`is in`/`is not in`/`contains`/`starts-with`/`ends-with`), type (`is`/`is not`/`as`)
- **Pipelines:** `|` as an async-iterator composition primitive
- **CLR interop:** `new`, `call`, `cast`, member access, static method calls
- **Async:** `async`/`await`, generator functions
- **Modules:** `using` (CLR namespaces), `require` (TōSh modules — semantics shift, see [What semantically changes](#what-semantically-changes))

**Verdict:** These all compile. No language feature in this bucket needs to be dropped.

### Bucket B — Library (keep, but move)

Commands that are useful in compiled programs but don't need to be language built-ins. Today they're hard-coded into the runtime; tomorrow they live in standard-library DLLs that compiled binaries reference.

| Standard library | Commands |
|---|---|
| `Tosh.Stdlib.Filesystem` | `ls`, `cd`, `pwd`, `mv`, `rm`, `cp`, `chmod`, `chown`, `mkdir`, `mkdir-temp`, `touch`, `ln`, `readlink`, `realpath`, `dirname`, `basename`, `find`, `glob`, `is-dir`, `is-file`, `is-link`, `stat`, `tempfile`, `tree` |
| `Tosh.Stdlib.IO` | `cat`, `read-file`, `read-bytes`, `read-line`, `read-lines`, `read-to-end`, `write-file`, `write-bytes`, `open-file`, `as-file`, `close`, `flush`, `seek`, `tee`, `lines` |
| `Tosh.Stdlib.Process` | `ps`, `kill`, `signal`, `spawn`, `wait-for`, `exec`, `jobs`, `bg`, `fg`, `timeout` |
| `Tosh.Stdlib.System` | `ip`, `ping`, `findmnt`, `lsblk`, `lscpu`, `lsfd`, `lsipc`, `df`, `du`, `journalctl`, `systemctl`, `loginctl`, `networkctl`, `hostname`, `hostnamectl`, `uname`, `uptime`, `ulimit`, `umask`, `id`, `whoami`, `free`, `mounts` |
| `Tosh.Stdlib.Text` | `grep`, `tr`, `cut`, `parse`, `template`, `replace`, `match`, `wc`, `sort`, `head`, `tail`, `uniq` |
| `Tosh.Stdlib.Data` | `where`, `each`, `map`, `filter`, `first`, `last`, `count`, `length`, `sort`, `sort-by`, `reverse`, `dedup`, `distinct`, `frequencies`, `group-by`, `group-while`, `partition`, `chunk`, `window`, `step-by`, `enumerate`, `zip`, `flatten`, `flat-map`, `chain`, `interleave`, `intersperse`, `cycle`, `cartesian-product`, `combinations`, `permutations`, `transpose`, `select`, `pick`, `take-until`, `take-while`, `skip`, `skip-until`, `skip-while`, `summarize`, `summary`, `min`, `max`, `sum`, `avg`, `median`, `percentile`, `stddev`, `variance`, `position`, `find-index`, `iterate`, `unfold`, `recur`, `repeat`, `repeatedly`, `scan`, `reduce`, `converge`, `collect`, `from`, `to`, `seq` |
| `Tosh.Stdlib.Concurrency` | `async`, `await`, `parallel`, `race`, `settle`, `channel`, `channel-send`, `channel-recv`, `channel-close`, `channel-select` |
| `Tosh.Stdlib.Net` | `http` |
| `Tosh.Stdlib.Time` | `date`, `time`, `timespan`, `sleep` |
| `Tosh.Stdlib.Crypto` | `hash`, `guid` |
| `Tosh.Stdlib.Display` | `styled`, `view` (output-tuning subset only — see Bucket C for shell-state subset) |

**Verdict:** ~200 commands. Available to both REPL and compiled programs via library reference. Compiled binaries `using Tosh.Stdlib.Filesystem` (or whatever the syntax becomes) just like a C# program references `System.IO`.

### Bucket C — Shell-only (drop from compiled programs)

Commands that depend on REPL-process state. They have no meaning outside an interactive shell.

| Surface | Commands / features |
|---|---|
| Command history | `history`, `history-search`, `!!`, `!237`, history expansion |
| Directory stack | `dirs`, `back`, `forward` |
| REPL display tuning | `view detail`, `view compact`, `view columns`, `view size`, `view datetime` (the runtime-mode-toggling forms) |
| Prompt rendering | `prompt-dir`, `prompt-git`, `prompt-time`, `prompt-userhost`, `prompt-history`, `prompt-jobs`, `prompt-text`, `prompt-newline`, `prompt-duration`, `prompt-exit` |
| Help system (interactive) | `help browse`, `help --cli`, `apropos` (could keep `help <name>` if doc-strings are emitted as attributes) |
| Config (interactive UI) | `config browse` |
| TUI infrastructure | `tui pick --cli`, the inline-prompt provider |
| REPL session state | `vars` (introspecting the REPL scope), `forget` (REPL-scope removal) |
| Login/logout | `logout`, `clear` |

**Verdict:** ~40 commands. Dropped at compile time, OR present as stubs that throw `NotAvailableOutsideShell`. The cleanest answer is to make them shell-host extension methods that don't exist in the compiled environment.

---

## What to add

A compiled language needs infrastructure that interpreted ToastScript doesn't.

### 1. Entry-point resolution

Compiled programs need a `Main` equivalent. Three viable shapes:

| Shape | Looks like | Best for |
|---|---|---|
| **Top-level statements** | Statements outside any function run as the entry | Quick scripts, one-off tools |
| **Explicit `func main()`** | A specially-named function is the entry | Library-shaped programs |
| **Subcommand tree IS the entry** | Top-level `subcommand` blocks define the CLI surface; no body needed | CLI tools (the `build.tosh` shape) |

Recommended: **all three are valid**, in priority order — subcommand tree if any `subcommand` blocks exist; otherwise top-level statements; `func main()` only as an opt-in override. Matches what scripts do today and gives compiled tools first-class CLI ergonomics for free.

### 2. Project model

A `Tosh.Sdk` (MSBuild SDK) so users can write:

```xml
<Project Sdk="Tosh.Sdk/1.0.0">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Tosh.Stdlib" Version="1.0.0" />
    <PackageReference Include="MyOtherLib" Version="2.3.0" />
    <ProjectReference Include="../shared/SharedTypes.toshproj" />
  </ItemGroup>
</Project>
```

`.toshproj` files glob `*.tosh` from the project directory. Compiled output is a normal `.dll`/`.exe`/`.pdb`.

### 3. Semantic / binding pass

The interpreter binds names lazily. The compiler needs an eager binder:

- Symbol tables and scope analysis (replaces `_scopes` runtime stack)
- Closure capture analysis → display classes
- Command resolution at compile time (with `Dynamic` fallback bucket)
- Type inference for `var x = ...`, parameter inference, pipeline-stage typing
- A bound IR: typed AST with every reference resolved or marked dynamic

This is the foundational layer. Codegen comes after.

### 4. Cross-language ABI

When a `.toshproj`-built `.dll` is referenced from C# (or vice versa), the boundary needs rules:

- **Name mangling.** `kebab-case` ToastScript names → `PascalCase` CLR names? Or preserve via `[CompilerGenerated]`?
- **Visibility.** `shy` → `internal`/`private`. `export` → `public`. `global` → ?
- **Refinements at the boundary.** Either dropped (lose the contract) or expressed as `RefinementAttribute` runtime checks.
- **Pipelines as method signatures.** A function returning a pipeline is `IAsyncEnumerable<object?>` from C#'s view — already CLR-native.
- **Type erasure for dynamic values.** Untyped `var` becomes `object?`.
- **Doc comments → XML doc comments** so IntelliSense works in C# consumers.

### 5. Reference assemblies

For users to consume compiled ToastScript libraries, the .dll needs full type metadata, ideally with both runtime and reference forms (the latter for compile-only metadata, smaller). Roslyn does this automatically; an SRE-based emitter would have to opt in.

### 6. Source link / debug info

Map `TextSpan`s back to `.tosh` source coordinates so a compiled program's stack trace points at the right file/line in a debugger. Roslyn does this for free if we go source-to-C#; SRE requires manual PDB emission.

### 7. NuGet integration

`Tosh.Stdlib`, `Tosh.Sdk`, and any future libraries ship as NuGet packages. Compiled binaries reference them like any .NET project would.

---

## What semantically changes

Even within Bucket B (the standard library), some semantics shift between shell-host and compiled-binary contexts.

### `$env.PATH = ...` and environment mutation

- **Today (REPL):** mutates the shell's process env. Visible to subsequent commands in the same session.
- **Compiled:** mutates the compiled program's process env. **Doesn't propagate to the parent shell** that launched the program. (Same as a C# program — POSIX subprocess semantics.)

This is a real semantic change. Documented limitation, not a bug. Tools that need to mutate the parent shell's env have to print exports (`echo "export PATH=$NEW"`) and the shell `eval`s them — the standard Unix pattern.

### `$tosh.*` runtime namespace

- **Today (REPL):** `$tosh.Last.Result`, `$tosh.Session.JobCount`, `$tosh.IsLoginShell` etc. are populated by the interpreter.
- **Compiled:** Most still make sense (`$tosh.Script.Path`, `$tosh.Function.Args`). REPL-specific paths (`$tosh.Session.NextHistoryId`) become unavailable or stub. Need to define which paths survive.

### `require` semantics

- **Today:** runtime load of a `.tosh` file, evaluated in the current engine.
- **Compiled:** compile-time reference to another `.toshproj`/`.csproj`/NuGet package. Can no longer take a runtime-computed path argument unless the compiled binary bundles the interpreter as an opt-in.

### `source` and `eval`

- **Today:** runtime evaluation of arbitrary source.
- **Compiled:** require the interpreter at runtime → `using Tosh.Eval;` opt-in package that fattens the binary. Without it, `source` is a compile error.

### Refinement check sites

- **Today:** every annotation crossing checks the predicate.
- **Compiled:** the compiler emits the same checks inline. Same semantics, same cost — but now the compiler can hoist or eliminate redundant checks (e.g., a `where _ > 0` on a parameter doesn't need to re-check inside the function body for every use of that parameter).

### Doc-comment-driven help

- **Today:** `help <function>` reads doc comments from the source via the parser.
- **Compiled:** doc comments either become CLR `[Description]`-style attributes (extra binary size, but `help` still works), or are dropped (no `help` for compiled functions).

---

## What's genuinely hard

### Runes (AST quoting)

`quote { ... }` captures unevaluated AST. Two paths:

- **Compile-time only.** Runes become source generators that fire at compile time. Limits dynamic use cases.
- **Runtime preserved.** Compiled binaries embed quoted ASTs as data and call back into a rune evaluator. Adds a runtime dependency that isn't otherwise needed.

Pick early — it shapes the IR. Recommendation: compile-time only for v1; loosen later if there's demand.

### Truly dynamic dispatch

Some calls are *unknowable* at compile time (external commands, runtime-defined functions, `eval`'d code). Handle via `Tosh.Runtime.Cmd.Invoke("name", args, ctx)` — explicit helper rather than DLR `dynamic`. Better stack traces, smaller IL.

### REPL coexistence

The REPL has to keep working. So compiled mode is for scripts and modules; REPL lines stay tree-walked. Compiled output is *also* loadable into the REPL (as referenced libraries). Two modes, one runtime — the REPL is one of the runtime's hosts, not the only one.

### Subset-vs-superset decision

Three stances on the relationship between "TōSh shell language" and "compiled ToastScript":

1. **Same language, different host.** Compiled binaries can do everything the shell can, but REPL-only features fail at runtime. *Pragmatic, slightly leaky.*
2. **Strict sublanguage.** Compiled programs use a defined subset. REPL-only features are compile errors. *Cleanest, but creates a compatibility cliff.*
3. **Two profiles, one language.** ToastScript-Shell and ToastScript-Lang are profiles; profile-specific features error in the wrong profile. *Most flexible, most complex.*

Recommended: start with **(1)** as the v1 default — REPL features error at *compile* time with a clear "this construct is only available in interactive mode" diagnostic. Move toward (3) as the surface stabilizes.

---

## Recommended direction

A pragmatic v1, in three layers:

### Layer 1 — Library split (no compiler yet)

Reorganize `Tosh.Core` into the bucket structure above. Move ~200 commands into `Tosh.Stdlib.*` namespaces. Mark ~40 commands as `[ShellOnly]`. Make the shell load `Tosh.Stdlib.*` automatically; otherwise the shell behavior is unchanged.

This is **free of compiler work** but pays off immediately:

- Forces the language/shell split to be made explicit.
- The boundary becomes the thing the compiler eventually targets.
- Surfaces hidden coupling early.

### Layer 2 — Bound IR + binder

Build the binder over the existing AST. No codegen yet. Run the interpreter off the bound IR. Improves diagnostics and unblocks every later step. Adds a `[ShellOnly]` check that errors at bind time.

### Layer 3 — Compiler (source-to-C# + Roslyn)

Generate C# from the bound IR; let Roslyn do the heavy lifting. Subset-driven: start with pure functions and typed parameters; expand outward to closures, classes, dynamic dispatch, etc.

### What to drop from v1 scope

- Native runes (defer to v2)
- `source`/`eval` of runtime paths (v2 — opt-in `Tosh.Eval` package)
- Direct IL emission via `PersistedAssemblyBuilder` (v3 — only if S2C# is too slow)
- Per-platform ahead-of-time NativeAOT (the architecture doc already noted this is incompatible with TōSh's reflection use)

### What to nail in v1 design

- The REPL-only / library / language three-bucket split, made explicit in code via attributes
- Entry-point resolution rules (subcommand tree → top-level → explicit main)
- The shape of the bound IR
- The cross-language ABI rules (name mangling, visibility mapping, refinement encoding)

---

## Implementation status (April 2026)

Snapshot of how far each layer has actually moved. Commits are on `master`.

### Layer 1 — Library split

| Step                                                     | Status      | Notes |
|----------------------------------------------------------|-------------|-------|
| Carve `Tosh.Core` into `Tosh.Runtime` + `Tosh.Stdlib`    | done        | `469e9f9` |
| `[ShellOnly]` attribute (in `Tosh.Runtime`)               | done        | `c0f2085` |
| `[Stdlib(StdlibCategory.X)]` attribute + enum              | done        | `c0f2085` |
| ~16 commands marked `[ShellOnly]`                         | done        | `c0f2085` |
| ~243 stdlib commands tagged into buckets                  | done        | `c0f2085` |
| Folder layout `src/Tosh.Stdlib/<Bucket>/*.cs`             | done        | `d211127` |
| Namespace-based category inference (drop redundant attrs) | done        | `0dea4a3` |
| Real assembly split into `Tosh.Stdlib.<Bucket>.dll`       | deferred    | stays as a single assembly with namespace partitions until Layer 3 demands separate compile units |

### Layer 2 — Bound IR + binder

| Step                                                     | Status      | Notes |
|----------------------------------------------------------|-------------|-------|
| Phase 1 binder: command-name resolution + typo detection | done        | `3cae6b9` |
| Bind-time `[ShellOnly]` enforcement                      | done        | `e7df0bd` |
| Phase 2 binder: variable-name scope analysis             | done        | `516d18f` — flags `$nme` for `$name`; covers `var`, destructuring, function/lambda params, for/catch vars, class properties, modules |
| Precise spans for typos inside interpolated strings      | done        | `a1fa021` — lexer now records each `{expr}` hole's source span |
| Bound IR data structures                                  | not started | binder still emits diagnostics over the parse tree; no separate IR yet — see [Next milestone](#next-milestone-bound-ir) |
| Type / refinement checks in the binder                    | not started | depends on Bound IR |

### Layer 3 — Compiler

Not started. See [Next milestone](#next-milestone-bound-ir) — the Bound
IR is the natural bridge from the existing tree-walking interpreter to
an IL backend.

### Adjacent infrastructure

| Step                                                     | Status      | Notes |
|----------------------------------------------------------|-------------|-------|
| Diagnostic-code manifest extraction                       | done        | `scripts/extract_diagnostic_codes.py` + `Tosh.Runtime.Generated.DiagnosticCodeManifest` |
| BenchmarkDotNet harness (`bench/Tosh.Benchmarks/`)        | done        | `acf773b` |
| Binder benchmarks                                         | done        | `acf773b` — see [BENCHMARKS.md](BENCHMARKS.md) |
| Parser + evaluator benchmarks                             | done        | `f2264c1` — `WhereSort` is the standout: 150 µs / 466 KB on a 100-element `where|sort|first` due to boxed pipeline elements |
| `BoundCommand` parse-time fast-path                       | deferred    | the binder runs in 1–30 µs on realistic inputs; perf does not justify a fast-path today |

---

## Next milestone: Bound IR

The existing binder is a *visitor* over the parse tree that emits
diagnostics in-place. To compile, we need to *materialize* the result
of binding — every name resolved (or marked dynamic), every type
inferred (or marked dynamic), every closure capture identified.

That's what a Bound IR is. It's the same shape as the parse tree but
each node carries the resolved meaning. Concretely, for the existing
binders:

- **Phase 1 (commands):** every command call site gets a
  `BoundCommandReference` pointing at the registered metadata, or a
  `BoundDynamicCommand` if the name is dynamic at compile time.
- **Phase 2 (variables):** every variable reference gets a
  `BoundVariableReference` pointing at its declaring scope frame and
  slot, or a `BoundDynamicVariable` if it came from `$env`, `$tosh`,
  or an external `source`.
- **New (types):** every expression carries a static type — possibly
  `dynamic` for now — that the compiler later uses to pick concrete
  IL operations and avoid boxing.

The benchmark numbers point at why this matters: `1..100 | where >50 |
sort | first 5` allocates 466 KB and runs in 150 µs because every
integer is boxed to `object`. A Bound IR with `int` element types
makes that pipeline a sequence of `IEnumerable<int>` operations on
unboxed values; an order-of-magnitude improvement is realistic.

The Bound IR is also what unblocks Layer 3 (the IL backend). Codegen
walks the *bound* tree, not the parse tree — by then every choice has
already been made.

Concrete next steps, in order:

1. Define `BoundNode` hierarchy mirroring `ArgumentSyntax` /
   `StatementSyntax`. Each node has a `Type` slot (dynamic-default).
2. Add a `Lower` pass that converts `ParseResult` → `BoundUnit`,
   running the existing binder logic but emitting bound nodes
   instead of in-place diagnostics.
3. Re-implement the evaluator on top of the Bound IR. This is a
   refactor with no user-visible change; it validates the IR is
   sufficient and gives us a baseline to measure compiled IL
   against.
4. Type inference for the trivial cases (literals, arithmetic,
   pipeline element types) — enough to drive specialized codegen
   for numeric pipelines.

---

## Files this design would touch

- [src/Tosh.Core/Commands/](../src/Tosh.Core/Commands/) — split into namespaced subdirectories matching the standard library layout.
- [src/Tosh.Core/CommandMetadataAttributes.cs](../src/Tosh.Core/CommandMetadataAttributes.cs) — add `[ShellOnly]`, `[Stdlib(category)]` attributes.
- New: `src/Tosh.Syntax/` — extracted lexer + parser + AST + diagnostics.
- New: `src/Tosh.Compiler/` — binder, bound IR, codegen.
- New: `src/Tosh.Runtime/` — helpers compiled code calls into.
- New: `src/Tosh.Stdlib.*/` — namespaced standard libraries (one assembly per bucket, or one assembly with namespace-only split).
- New: `src/Tosh.Sdk/` — MSBuild SDK + targets.
- New: `src/Tosh.Compiler.Cli/` — `toshc` executable.

## Verification approach

For each layer:

- **Layer 1 (library split):** existing test suite passes unchanged. Adding a `[ShellOnly]` command to a script-mode test should fail with a clear diagnostic.
- **Layer 2 (binder):** new test category — bind a script, assert the bound IR matches expected shape. Diagnostics for unresolved names, type errors, etc.
- **Layer 3 (compiler):** for each supported feature, write a `.tosh` test, compile, run, assert output equals the interpreter's output for the same input. Differential testing — interpreter is the oracle.

## Open questions

These are the design calls that shape the v1 scope. Each is documented but not decided.

1. **Subset vs profile.** Stance #1 (same language, REPL features error at compile time) or #3 (explicit profiles)?
2. **Stdlib packaging.** One `Tosh.Stdlib` NuGet (one big DLL) or namespaced subpackages (`Tosh.Stdlib.Filesystem`, `Tosh.Stdlib.System`, etc.)?
3. **Runes in v1.** Compile-time only, or runtime preserved (interpreter dependency)?
4. **Entry-point default.** Subcommand tree as primary, or top-level statements?
5. **Doc comments at compile.** Embedded as attributes (binary cost) or dropped?

---

## Related

- [SPEC_STATUS.md](SPEC_STATUS.md) — gaps in the language specification that the compiler work will surface.
- [ARCHITECTURE.md](ARCHITECTURE.md) — current architectural shape and the project structure this plan builds on.
- [ROADMAP.md](ROADMAP.md) — overall project direction; this document slots into the long-term assembly-shape note.
