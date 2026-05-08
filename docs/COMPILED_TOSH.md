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

**Verdict / current state:** These are the language features that should remain
part of compiled ToastScript. They should not be dropped. As of May 2026,
the compiler has working paths for the core expression/control-flow surface,
typed top-level functions, class and record shells, CLR-polymorphic
`overrule`, inheritance, modules, enum metadata, interfaces, unions, overload
binding, named/splat argument shapes, `require`-as-build-input cases, native
`bind` lifting for supported P/Invoke signatures, and SDK build/run/publish
flows. The exact source of truth is now the compiler feature matrix rather
than this prose paragraph: every feature should be tracked as native IL /
Tier 1, runtime-hosted / Tier 2, source replay / Tier 3, or deliberately
unsupported. Runes remain the most important unresolved design surface because
invocation is an engine expansion step with lazy thunk semantics.

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

#### Scaffolding new projects

The `Tosh.Templates` package ships `dotnet new` templates so users can
scaffold projects without hand-writing the `.toshproj`:

```bash
dotnet new install Tosh.Templates
dotnet new tosh-app  -n MyTool        # OutputType=Exe + apphost
dotnet new tosh-lib  -n MyLib         # OutputType=Library + reference assembly
```

Each generated project pre-references `Tosh.Sdk`, sets a sensible
`TargetFramework` (`net10.0`), and includes a starter `Program.tosh`.

### 3. Semantic / binding pass

The interpreter binds names lazily. The compiler needs an eager binder:

- Symbol tables and scope analysis (replaces `_scopes` runtime stack)
- Closure capture analysis → display classes
- Command resolution at compile time (with `Dynamic` fallback bucket)
- Type inference for `var x = ...`, parameter inference, pipeline-stage typing
- A bound IR: typed AST with every reference resolved or marked dynamic

This is the foundational layer. Codegen comes after.

### 3a. Gradual type system

TōSh ships a structural, optional type system that doubles as the gate
between dynamic-typed scripting and statically-typed compilation:

- **Type syntax** — `func add(a: int, b: int) -> int`,
  `var xs: list<int> = [1, 2, 3]`, `var m: dict<string, int> = …`,
  `T?` for nullable, `T[]` and `(T1, T2)` for arrays / tuples,
  user-defined classes / records / enums / aliases. The
  `TypeNameResolver` (Tosh.Language.Binding.TypeNameResolver) turns
  syntactic strings into the `BoundType` hierarchy used by every
  downstream pass.
- **Two strictness levels.** In interactive mode and in scripts, the
  type checker emits warnings (lifecycle `Preview`) so existing
  dynamic code keeps running unchanged. The compile path
  (`tosh --compile`) promotes those same diagnostics to errors and
  refuses to write the artifact when:
  - any `func` is missing its return-type annotation (or has a
    parameter without `: T`) — `tosh.compile.missing_type_annotation`.
  - any `var` is implicitly dynamic (no annotation, inferrer can't
    pin a concrete type) — `tosh.compile.implicit_dynamic`.
- **Opt-in dynamic.** Users who want to compile partially-untyped
  programs pass `--compile-allow-dynamic` (alias `--allow-dynamic`).
  This suppresses `tosh.compile.implicit_dynamic` for `var`s but
  still requires every `func` to be annotated. Per-parameter
  opt-out is available by writing `: dynamic` (or `: any` /
  `: object`) explicitly.
- **Codegen rule.** Fully-typed functions emit a typed CLR primary
  (`add(int, int) -> int`) plus a thin `Func_<name>(object,…) ->
  object` shim that forwards through `Convert.ChangeType` /
  castclass. Internal call sites use the typed primary directly so
  arithmetic and arg passing happen without per-call boxing.
  Untyped (or mixed-dynamic) functions stay on the legacy
  `Func_<name>` shape — the typed primary is only emitted when
  every parameter and the return are concrete and non-`object`.
- **Diagnostics knob.** `TOSH_DISABLE_TYPECHECK=1` skips the
  type-checker pass entirely (escape hatch for compiler bring-up).

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

## Implementation status (May 2026)

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

| Step                                                                                | Status      | Notes |
|-------------------------------------------------------------------------------------|-------------|-------|
| Phase 1 binder: command-name resolution + typo detection                            | done        | `3cae6b9` |
| Bind-time `[ShellOnly]` enforcement                                                  | done        | `e7df0bd` |
| Phase 2 binder: variable-name scope analysis                                         | done        | `516d18f` — flags `$nme` for `$name`; covers `var`, destructuring, function/lambda params, for/catch vars, class properties, modules |
| Precise spans for typos inside interpolated strings                                  | done        | `a1fa021` |
| Bound IR data structures                                                             | done        | Phase A start `ae0bf5a`; carves through `9af8bf5` (decls/literals), `8805900` (try/throw/return/match/switch/types), `cc0fb3b` (closures/blocks/lambdas), `4ced237` (if/for/while/break/continue), `9dfb3da` (assignment/array/interp). Lives in `src/Tosh.Language/Binding/BoundNodes/`. |
| Lowering pass `ParseResult` → `BoundUnit`                                            | done        | `Lowerer.cs`, ~1.8k lines |
| Bound evaluator parity harness                                                       | done        | `8e41e69` — interpreter runs against the bound IR for differential testing |
| Light type inference for numeric pipelines                                           | done        | `17ac053` (Phase A) |
| Constant folding (parse-tree side-table)                                             | done        | `73251de` (Phase B Option A) |
| `sort | first` pipeline fusion via bounded heap                                      | done        | `95433ae` (Phase B Option B) |
| Type / refinement checks in the binder                                               | partial     | `BoundType`, `TypeNameResolver`, `TypeInferrer`, and `TypeChecker` now exist. Compile annotations are enforced for functions and dynamic-sensitive vars; user-function call arity/type checks exist. Still shallow: builtin command args/options, class/module bodies, member access, pipeline contracts, refinement predicates, and many expression forms are not fully statically checked. |

### Layer 3 — Compiler

| Step                                                                                | Status      | Notes |
|-------------------------------------------------------------------------------------|-------------|-------|
| `Tosh.Compiler` project + `Tosh.Compiler.Runtime` host shim                          | done        | `3ff32d7` walking-skeleton; emitter uses `PersistedAssemblyBuilder` to produce a real PE + emits `<out>.runtimeconfig.json` so `dotnet <out>.dll` runs |
| `tosh --compile script.tosh [out.dll]` CLI flag                                       | done        | `Program.cs CompileScriptAsync` |
| Strict `--compile` (binder runs, fail-fast on diagnostics, no half-baked output)      | done        | `Program.cs CompileScriptAsync` runs `Binder.Bind(parseResult, ...)` against the concatenated unit; binder diagnostics, parse errors, and emitter unsupported shapes all return non-zero. Partial `.dll` is deleted on emitter failure so `dotnet <out>.dll` can't run stale output. |
| `--profile=permissive|runtime|pure` flag + tier-based diagnostic gate                 | done        | See [COMPILED_PROFILE.md](COMPILED_PROFILE.md). Three-tier model: pure IL (1) / IL + `ToshHost` (2) / source-replay (3). `BoundUnitEmitter.RequireTier` emits dedup'd diagnostics into `EmitResult.UnsupportedShapes` when an emit site exceeds the active profile's allowed tier; CLI deletes the partial `.dll` on failure. Default profile is `permissive`. |
| Compiler feature matrix across `permissive`/`runtime`/`pure`                          | done / expanding | `tests/Tosh.Tests/CompilerFeatureMatrixTests.cs` is the executable ledger for compiled language coverage. The matrix now covers broad syntax, declaration, callable, module, metadata, interop, and SDK-facing families across all three profiles, with no all-profile-rejected rows in the latest documented audit. It must keep expanding until it is exhaustive rather than representative; every row should identify whether the implementation is native IL / Tier 1, runtime-hosted / Tier 2, source replay / Tier 3, or a deliberate unsupported diagnostic. Runes and any remaining source-replay paths should stay explicit profile decisions rather than accidental emitter gaps. |
| Literals, arithmetic, string interpolation                                           | done        | `6044476` |
| `var` / locals + `$x` reads + reassignment + compound assignment (`+= -= *= /= %=`)  | done        | `6044476`, `85392ea` |
| `if` / `while` / `until`                                                              | done        | `ef9dcfa` |
| `for i in start..end` (range fast-path) + generic `for x in expr` over iterables     | done        | `85392ea`, `b848547` |
| User-defined functions (top-level), recursion, `return`                              | done        | `545d323`, `236b127` (object-typed numeric paths). Fully-typed funcs (`func add(a: int, b: int) -> int { … }`) emit a typed CLR primary `add(int,int) -> int` and a thin `Func_add(object, object) -> object` shim that forwards to it; internal calls use the typed primary directly so arg coercion happens in IL rather than via boxed-object Convert.ChangeType round-trips. Untyped funcs continue on the legacy `Func_<name>(object,…) -> object` shape. |
| Member access (`$x.Foo.Bar`, null-safe `?.`), index access (`$x[i]`)                  | done        | `b848547` |
| List literals `[...]`, dict literals `{ "k" => v }`                                  | done        | `b848547` |
| Multi-stage pipelines — commands → commands                                           | done        | Phase 1, `fc06935`. Each stage chains through `ToshHost.RunStage`; terminal `DrainStatement` / `DrainValue`. |
| Block arguments (`where { _ > $x }`, `map { _ * 2 }`) with capture                   | done        | Phase 2, `4ba4b56`. Source text registered with the host; blocks materialized at runtime by source-span lookup. |
| Expression-seeded pipelines (`[1,2,3] | first 2`, `42 | first 1`, `$xs | …`)         | done        | Phase 3, `85c162f`, via `ToshHost.SeedFromValue`. |
| Live stdlib bridge (non-inlined commands routed through `ToshHost`)                  | done        | `70dd69b` — `pwd`, `whoami`, `ls`, `which`, etc. dispatch to the same registry the interpreter uses |
| `break` / `continue`                                                                 | done        | `LoopFrame` stack + `OpCodes.Leave` — works inside `while`, range-`for`, and foreach (break leaves through the foreach dispose try) |
| `try` / `catch` / `finally` / `throw`                                                | done        | `BeginExceptionBlock` / `BeginCatchBlock` filtering on `ThrowSignalException`; catch-var bound via `ToshHost.ThrownValueOf` |
| Splat / named arguments                                                              | done        | All call shapes that the parser produces with splat or named args lower through the shared `EmitArgsArrayCore(name, args)` emitter: fast `newarr` path when no splat, `List<object?>` + `ToArray()` + `ToshHost.SpreadArgs` when present, with named entries wrapped as `NamedArgument(name, value)`. Covers command calls, callable invocation (`$fn(...)`), `new TypeName(...)`, static method calls, instance method calls, user-function pipeline stages, and the union-variant constructor fallback. Runtime side: `ReflectionInvoker.TryBindParameters` unwraps `INamedArgument` (implemented by `Tosh.Language.NamedArgument`) and binds by parameter name; `ToshHost.InvokeMember` reflection fallback now routes through `Runtime.Invoker.InvokeInstance`; `ToshHost.RunUserFuncStage` and `ToshHost.TryBuildOverloadInvocation` split named/positional and bind by name. |
| User functions as pipeline stages                                                    | done        | `ldtoken` the `Func_<name>` dynamic shim's `MethodBuilder` → `MethodInfo`; `ToshHost.RunUserFuncStage` dispatches by arity (drain-once vs. one invocation per input item). The shim is always emitted — even for fully-typed funcs — so reflection.Invoke sees a uniform `(object,…) -> object` signature regardless of typedness. |
| Closures over top-level variables                                                    | done        | Tier 2 #5. `BoundUnitEmitter.PromoteCapturedSymbols` walks every nested `BoundFunctionDefinition.Captures`, promotes captures of top-level `var` symbols into `private static` fields on the program type, and rewrites reads/writes to `Ldsfld`/`Stsfld`. Captures of peer top-level functions resolve through the `_userFunctions` map. Deeper-than-top-level captures still emit a diagnostic. |
| Classes / records / methods                                                          | partial     | Records: real CLR `public sealed class` with positional constructor + public `object` fields per field; `new Rec(a,b,c)` lowers to direct `newobj` and `$r.Field` lowers to direct `ldfld` when target's static type is the shell. Classes with primary-ctor properties + plain methods now also lower fully natively: each method becomes a real CLR instance method on the shell with `$this` mapped to `Ldarg_0`, `$this.Field` to direct `ldfld`, and `$this.method(args)` to direct `callvirt` against the shell's `MethodBuilder`. `new TypeName(...)` uses direct `newobj` whenever every member can be represented on the shell. Conservative fallbacks to engine dispatch (via `ToshHost.NewObject` / `ToshHost.InvokeMember`) for: inheritance, traits, interfaces, abstract / hermit classes, secondary constructors, computed properties (getter/setter bodies), lazy props, static methods, methods with rest/optional params or captures. CLR-shell-emitted class/record declarations skip Tier 3 source replay; fallback class/record forms still register from source. |
| Enums                                                                                 | done        | Simple integral enums emit real CLR `enum` metadata with `[ToshType("enum", ...)]`, CLR-safe type/member names, `[ToshOriginalName]` on mangled names, correct integral underlying types, auto-incremented members, and literal explicit member values. Non-integral underlying types and dynamic/literal-only member values (e.g. `enum Label: string { Good = "good" }`) emit a CLR static class shell (`public sealed abstract class` with one `public static readonly object` field per member, populated in `.cctor`); `EnumName.Member` lowers to `ldsfld`. Both shapes are clean in `permissive`, `runtime`, and `pure`. Member access on integral enums resolves through the loaded CLR enum type (Tier 2 host lookup); member access on the static-shell shape is direct `ldsfld` (Tier 1). |
| `match` / `switch` (lowered to `if`/`else` chains)                                   | done        | Tier 2, branch labels per arm; pattern tests (literal equality, comparison `_ op N`, ranges, guards) call `OperatorEvaluator.Matches` / `AreEqual`; scrutinee bound on `_underscoreStack` so `_` references resolve |
| Spread elements (`[...$xs]`), record `{ name: ... }`, slice indices                  | done        | Spread elements: list literals call `ToshHost.SpreadArgs` for `...expr` items. Record literal `:` separator now accepted by parser alongside `=`. Slice `$xs[1..3]`: `ShellIndexingUtilities.GetSlice` materialises `ToshRange.Enumerate()` into the target collection (string / array / IList / IEnumerable); compiler emits `ToshRange` values via `BoundUnitEmitter.EmitRange`. |
| User-function shadowing of builtin command names                                     | done        | Parser pre-scans tokens for `func <name>` declarations and bypasses the current-item-expression and `get`/`select`/`pick` argument-parsing branches when the name is shadowed (e.g. `func describe(n) {...}` then `(describe 5)` no longer wraps `5` in a block argument). |
| Modules — `module Foo { ... }`, dotted `module Foo.Bar`, `partial module`             | done        | Tier 2. Top-level `BoundModuleDefinition`s are re-evaluated through the engine via `ToshHost.RegisterModuleFromSource`; dotted `Foo.Bar` desugars into nested partial modules in the parser, partial declarations merge through a shared `ModuleExportTable`. `BoundStaticMemberAccess` (`Lib.greeting`) and `BoundStaticMethodCall` (`Lib.greet()`) dispatch through `ToshHost.ResolveQualifiedAccess` / `InvokeQualifiedMethod`. Each top-level module emits one `[ToshModule(QualifiedName, SpanStart, SpanLength)]` assembly attribute (recursive for nested modules) so external tooling can enumerate compiled modules via reflection. |
| Modules → real CLR `static partial class`                                             | done        | Tier 2. Each top-level `module Foo` emits a `public sealed abstract class` (the CLR encoding of `static class`) at `<assembly>.Foo`; nested `module Foo.Bar` becomes a nested static class. Module-scope `var x = expr` becomes a `public static object x` field initialised in the type's `.cctor` (initializer expression compiled through the standard `EmitVariableDeclaration` path). Module-scope `func name(...)` becomes a `public static object name(...)` method with a real IL body — module-scope `var`s are registered in `_staticFields` so func bodies access them via `ldsfld`. Cross-file `partial module Foo` declarations accumulate into one `TypeBuilder`. Source-replay is still emitted in parallel so tosh-side qualified access keeps working unchanged; pure declarations (literals, function defs) avoid double-execution because their initializers have no observable side effects. |
| Multi-file `--compile a.tosh b.tosh c.tosh -o out.dll`                                | done        | Tier 2. CLI accepts any number of positional inputs before `-o`; sources are concatenated with file-header comments and parsed/lowered/emitted as one bound unit. Cross-file partial modules merge through the existing `ModuleExportTable` plumbing. |
| Portable PDB                                                                         | done        | Tier 2. `BoundUnitEmitter` calls the 3-arg `PersistedAssemblyBuilder.GenerateMetadata(out ilStream, out fieldData, out pdbBuilder)` overload, builds a `PortablePdbBuilder`, and embeds it in the PE via `DebugDirectoryBuilder.AddEmbeddedPortablePdbEntry`. Each `EmitStatement` call marks an `ISymbolDocumentWriter` sequence point at the statement's `BoundNode.Span` (1-based line/col, computed from a cached `_lineStarts` index over `ParseResult.SourceText`). Stack traces from compiled `.dll`s now point at `.tosh` file/line. Output is single-file — no companion `.pdb` — matching modern .NET defaults. Multi-file compile still uses one concatenated synthetic source document rather than one PDB document per input file. |
| Source Link                                                                          | not started | No Source Link JSON/debug directory is emitted yet, so debuggers cannot fetch original `.tosh` text from a published source location. |
| Subcommand-tree entry point (`subcommand` blocks → CLI dispatcher)                   | done        | Tier 3. When the bound unit contains any `BoundSubcommandStatement` or `BoundScriptInputStatement`, the emitter forces the source-registration prologue and replaces the per-statement `Main` body with a single `ToshHost.RunSubcommandScript(args)` call. The host helper sets `Runtime.InvocationArguments` from argv and replays the registered source through `ToshEngine.ExecuteToListAsync`, which already implements argv parsing, nested flag/arg binding, eager/hollow/vital semantics, and auto-help. Re-implementing dispatch in IL would dwarf the rest of the emitter for no benefit — the engine has the canonical implementation. Verified end-to-end: `tosh --compile script.tosh -o out.dll && dotnet out.dll math add 3 4` prints `7`; `dotnet out.dll --help` shows the auto-generated subcommand list. |
| `Tosh.Sdk` MSBuild SDK + `.toshproj`                                                 | partial     | `src/Tosh.Sdk` and `src/Tosh.Sdk.Tasks` exist. The SDK has props/targets, in-process task support, CLI `Exec` fallback, default `Build`/`Run`/`Publish`/`Clean` targets, runtimeconfig writing, runtime DLL staging, apphost/single-file support, packaged-SDK smoke tests, direct-import lifecycle tests with paths containing spaces, multi-source direct-import coverage, `ProjectReference` build/stage/deps/run/publish coverage, packaged-SDK `PackageReference` restore/stage/deps/run/publish coverage including a transitive package dependency, and a C# consumer test that compiles against a generated TōSh refasm. Remaining polish: task diagnostics should report source file/line/column positions rather than raw span offsets; package-reference support is runtime-asset staging rather than full NuGet/MSBuild asset semantics (central package management, RID/native/resource assets, buildTransitive/analyzers, etc.); project-reference support still needs C# project references, transitive project references, and richer metadata semantics; and the refasm remains body-bearing. |
| Reference-assembly emission                                                          | partial     | CLI `--emit-refasm` and SDK `<ToshEmitReferenceAssembly>true</ToshEmitReferenceAssembly>` emit a sibling `.ref.dll` stamped with `[ReferenceAssembly]`. Refasm output now uses target-framework reference metadata for public core types, so a C# project can compile against top-level functions, module functions/fields, class constructors/methods, and record constructors/fields from the `.ref.dll` while running against the implementation assembly. SDK `Clean` removes the refasm. The artifact is still emitted through the normal emitter and remains body-bearing rather than metadata-only. |
| Name-mangling rules / cross-language ABI                                             | partial     | Basic CLR-safe identifier mangling and `[ToshOriginalName]` are present. A stable ABI spec is still needed for casing, keywords, visibility, overloads, properties vs fields, nullability, generics, refinements, XML docs, and how C#/F#/VB consumers should see TōSh APIs. |
| Apphost / single-file publish                                                        | partial     | CLI and SDK paths can create apphost wrappers and the custom single-file bundle used by the current smoke tests. This is usable for isolated execution, but still needs a deliberate long-term dependency/publish model. |
| Library-style output                                                                 | not started | Every emitted assembly currently has an implicit `Program.Main`. `OutputType=Library`, explicit `func main` selection, and no-entrypoint library mode remain to be designed and implemented. |

### Adjacent infrastructure

| Step                                                     | Status      | Notes |
|----------------------------------------------------------|-------------|-------|
| Diagnostic-code manifest extraction                       | done        | `scripts/extract_diagnostic_codes.py` + `Tosh.Runtime.Generated.DiagnosticCodeManifest` |
| BenchmarkDotNet harness (`bench/Tosh.Benchmarks/`)        | done        | `acf773b` |
| Binder benchmarks                                         | done        | `acf773b` — see [BENCHMARKS.md](BENCHMARKS.md) |
| Parser + evaluator benchmarks                             | done        | `f2264c1` |
| `BoundCommand` parse-time fast-path                       | deferred    | binder runs in 1–30 µs on realistic inputs; perf does not justify a fast-path today |

---

## Next milestone: productizing the .NET language path

The procedural core is now mostly in place. The current gap is less
"can this emit a runnable DLL?" and more "does this behave like a normal
.NET language when used from the SDK, a debugger, and another .NET project?"

The canonical ordered roadmap lives in
[FIRST_CLASS_DOTNET_STATUS.md](FIRST_CLASS_DOTNET_STATUS.md#recommended-next-order).
From this compiler document's viewpoint, the same work breaks down as follows:

1. **Protect the correctness baseline first.**
   Keep the recently fixed input/redirection behavior covered from compiled
   code, including `Console.In`, `CommandContext.Input`, nested redirect scopes,
   pipeline input, output append, error redirection, and `try/finally`
   restoration after failures.

2. **Turn `CompilerFeatureMatrixTests` into the language-surface gate.**
   The matrix should be generated or audited against parser nodes, bound nodes,
   lowerer/emitter cases, the language spec, and command metadata. Every row
   should state its `permissive`/`runtime`/`pure` expectation and whether it is
   native IL, runtime-hosted, source replay, or deliberately unsupported.

3. **Freeze the profile contract.**
   `permissive` is the full-language compatibility profile, `runtime` is the
   normal app/library profile, and `pure` is the CLR-first public-ABI subset.
   CI should treat profile failures as contract regressions once a row is
   assigned.

4. **Drive Tier-3 source replay down to deliberate design choices.**
   Audit `Register*FromSource`, `RunScriptFromSource`, source-based subcommand
   dispatch, and every `RequireTier(3, ...)` site. Ordinary module/type/function
   code should run under `runtime`; remaining Tier-3 usage should be reserved
   for features such as unresolved rune semantics.

5. **Make the CLR ABI v1 spec normative.**
   Before expanding public shapes further, lock assembly/type/member naming,
   mangling, visibility, overloads, optional/rest parameter shape, constructor
   rules, executable vs library output, `ToshOriginalNameAttribute`,
   `ToshTypeAttribute`, dynamic erasure, nullability, refinements, and
   compatibility policy.

6. **Complete declaration metadata.**
   Classes, interfaces, traits, unions, structs, events, aliases, refinements,
   nested module declarations, and non-CLR enum shapes should become CLR
   metadata first and source replay last. Each public shape needs reflection,
   C# consumer, and runtime invocation tests.

7. **Replace object-shaped public APIs with typed metadata where possible.**
   Use annotations and inference for public signatures, while keeping dynamic
   shims only where ToastScript semantics require them.

8. **Replace body-bearing refasm output with true metadata-only reference
   assemblies.**
   Verify metadata parity against the implementation assembly and keep direct
   Roslyn, `ProjectReference`, `PackageReference`, clean, and publish tests.

9. **Productize the SDK path.**
   Extend the current build/run/publish/package coverage into full NuGet and
   MSBuild semantics: central package management, RID/native/resource assets,
   buildTransitive/analyzers, Tosh-to-Tosh references, C#-to-Tosh references,
   Tosh-to-C# references, transitive references, publish transitivity, and
   file/line/column diagnostics.

10. **Isolate the runtime host.**
    Move global source/runtime state behind assembly-scoped registration, make
    redirection/input state nestable and thread-safe, and verify multiple
    compiled ToastScript assemblies can coexist in one process and unload via
    `AssemblyLoadContext`.

11. **Deepen static checking.**
    Use command metadata, emitted type metadata, pipeline element flow, return
    completeness, union/refinement narrowing, and member/index access checks to
    catch common mistakes before runtime.

12. **Finish the .NET tooling experience.**
    Emit one PDB document per input file, add Source Link, verify stack traces
    and stepping, and surface compiler/spec metadata through LSP hover,
    completion, diagnostics, and go-to-definition.

13. **Decide runes as a language-design item, not an emitter shortcut.**
    Choose compile-time macro expansion, runtime-preserved quoted ASTs, or a
    documented permissive-only replay model. Do not partially lower rune calls
    until local-variable capture and lazy thunk semantics are solved.

14. **Keep the docs in lockstep.**
    Emitter/SDK/refasm changes update this document, first-class status changes
    update `FIRST_CLASS_DOTNET_STATUS.md`, and language-contract changes update
    `docs/spec/toastscript-spec.tex` plus `SPEC_STATUS.md`.

### Public CLR ABI v1 (Draft, normative)

> **Authoritative spec:** [`docs/CLR_ABI_v1.md`](CLR_ABI_v1.md) is the
> normative, frozen v1 contract — including the visibility ladder for
> `shy` / `guarded` / `local`, parameter-default and `params` rules,
> the `[ToshAbi(1)]` assembly stamp, and a full worked example. The
> sections below are kept here as a quick rationale companion; if the
> two ever disagree, the dedicated spec wins.

This section defines the contract that external .NET consumers should rely on.
It intentionally separates stable ABI promises from implementation details.

#### 1) Naming and identity

- Assembly name: output stem (`-o foo.dll` => assembly `foo`).
- Root program type: `<AssemblyName>.Program` (public static-class shape).
- Identifier mangling rule (stable):
  - If first char is a digit, prefix `_`.
  - Any non `[A-Za-z0-9_]` char maps to `_`.
  - Otherwise name is preserved verbatim.
- Collision policy: if two distinct tosh names mangle to the same CLR name in
  the same bucket (type bucket or top-level-function bucket), compile fails with
  `tosh.compile.name_mangling_collision`.
- Original-name recovery: when mangling changed a symbol name, emit
  `[ToshOriginalName("...")]` on the generated type/method/field.

#### 2) Visibility and surface area

- Top-level user functions:
  - Typed form: `public static <TReturn> <MangledName>(...)`.
  - Dynamic form: `public static object Func_<MangledName>(object, ...)`.
- Program entrypoint: `public static void Main(string[] args)` for executable
  outputs.
- Module shells:
  - Top-level module: `public sealed abstract class` (CLR static class).
  - Nested module: `nested public sealed abstract class`.
  - Module vars/functions are `public static` members.
- Class shells:
  - Shell type is `public sealed class`.
  - `shy` properties map to private backing fields; non-`shy` map to public
    backing fields.
  - `fixed` maps to `initonly` field semantics where applicable.
- Record shells:
  - `public sealed class` with public instance data members and positional ctor.

#### 3) Properties vs fields (v1 rule)

- v1 ABI uses fields, not CLR properties, for emitted class/record data slots.
- Rationale:
  - Keeps source-compat with current shells.
  - Avoids accidental ABI breaks from auto-property accessor synthesis.
- Future evolution rule:
  - If/when CLR properties are introduced, field ABI is preserved for one major
    version via dual emission (property plus compatibility field or explicit
    redirect), then migrated with a declared breaking-change window.

#### 4) Records

- Record declaration emits `public sealed class` (not C# `record` metadata form
  in v1).
- Constructor ABI: positional ctor with one parameter per declared record field
  in source order.
- Record field ABI: public instance fields typed as `object` in v1.
- Equality/hash/deconstruct are not part of the v1 ABI contract.

#### 5) Modules

- Every module path has a stable qualified identity (`Foo`, `Foo.Bar`, ...).
- Shell identity mapping:
  - Type is stamped with `[ToshModuleShell("Qualified.Name")]`.
  - Assembly is stamped with one `[ToshModule("Qualified.Name", spanStart, spanLength)]`
    per declared module (recursive for nested).
- Partial modules merge into one CLR shell type by qualified module name.

#### 6) Generated attributes (required set)

- Required and stable in v1:
  - `[ToshOriginalName]` on mangled symbols only.
  - `[ToshType(kind, spanStart, spanLength)]` on emitted class/record shells.
  - `[ToshModuleShell(qualifiedName)]` on generated module shell types.
  - `[ToshModule(qualifiedName, spanStart, spanLength)]` at assembly level.
- Optional/runtime-mode attributes:
  - `[ReferenceAssembly]` only on `--emit-refasm` output.
  - Refasm emission uses target-framework reference metadata for core public
    types; implementation emission keeps runtime-core method bodies so the
    runnable DLL remains executable.

#### 7) Nullability

- v1 public metadata nullability is conservative:
  - Dynamic-erased values surface as `object`/`object?` depending on emission
    site internals; consumers must treat dynamic slots as nullable/unknown.
  - No stable guarantee yet for `NullableAttribute`/`NullableContextAttribute`
    completeness across all emitted members.
- Contract rule for consumers: nullability annotations are informational in v1,
  not a compatibility boundary.
- v2 target: deterministic NRT metadata for every public member.

#### 8) Generics

- User-defined generic classes (`class Foo<T1, T2> { ... }`) are
  supported end-to-end in compiled mode as of 2026-05. The compiler
  does **not** emit a CLR generic shell type for them; instead, the
  declaration is registered via source replay so the engine can reify
  the class at runtime.
- `new Foo<int, string>(args)` is emitted through a typed host overload
  `ToshHost.NewObject(typeName, bareTypeName, string[] typeArgs, object?[] args)`.
  The host resolves each type-argument string against the engine's
  named-type registry and CLR fallback, then dispatches to
  `ToshClassDefinition.CreateGenericInstance`.
- Type-parameter-bound parameters and return values use **strict
  no-coercion** semantics: `new Box<string>(42)` rejects rather than
  stringifying. This matches REPL behavior exactly.
- The v1 emitted public ABI surface is still non-generic. tosh type
  parameters do not currently surface as CLR generic parameter
  metadata on emitted public contracts; consumers from C#/F#/VB see
  the source-replay registration through the engine, not as a
  `Foo<>` open generic.
- v2 target: emit user-defined generic classes as proper CLR generic
  type definitions so they participate in cross-language ABI.
- Contract rule: ECMA-style backtick arity (`Type\`1`, `Method\`1`)
  is reserved for the v2 lowering; no compatibility guarantee until
  the feature is declared shipped.

#### 9) Library vs executable behavior

- Executable mode (current default):
  - Emits `<Assembly>.Program.Main(string[] args)`.
  - CLI host may also emit apphost/single-file wrappers.
- Library mode (design contract for upcoming implementation):
  - No `Main` emitted.
  - Top-level exported symbols (functions/modules/types) still emitted as public
    metadata for C#/F#/VB consumption.
  - Entry resolution (`subcommand`/top-level statements/`func main`) is ignored
    in library mode and treated as compile diagnostics when conflicting with
    library intent.
- Reference assembly mode:
  - `.ref.dll` must be metadata-contract-equivalent to runtime assembly for
    public symbols.
  - Body stripping is an implementation detail; metadata parity is the ABI
    requirement.
  - Current implementation is a body-bearing refasm; consumers compile against
    it, but must execute against the implementation assembly.

#### 10) Compatibility policy

- ABI compatibility for v1 is defined by:
  - public type/member names after mangling,
  - member kind (field/method/type),
  - signature shape,
  - required generated attributes listed above.
- Non-breaking changes in v1:
  - adding new non-conflicting public members,
  - adding private/internal helper members,
  - improving method bodies, diagnostics, or internal host routing.
- Breaking changes (major-version only):
  - changing mangling rules,
  - changing field <-> property representation without compatibility shims,
  - changing record/module/class shell identity rules,
  - removing required attributes.

**Tier 3 — reduce source replay:**

8. Keep source replay as the compatibility path, but make `runtime` and
   `pure` profiles practical CI gates by moving high-value Tier 3 features
   down into Tier 2/Tier 1.
9. Priorities: advanced type declarations currently covered only by replay
   (inheritance, interfaces, unions, structs, aliases/refinements,
   hermit/static-only classes, nested declarations in modules, and remaining
   non-CLR enum shapes), followed by subcommand dispatch and any remaining
   module registration/qualified-access replay.
10. Split global `ToshHost` state by compiled assembly/module so multiple
    TōSh assemblies can be loaded into one process without source-map or
    runtime-state collisions.

**Tier 4 — richer type metadata:**

11. Turn simple class/record shells into semantic CLR types: typed fields or
    properties, init/read-only behavior, constructors, value equality for
    records, static members, virtual/override, abstract members, interfaces,
    traits, structs, enums, unions, events, and type aliases.
12. Use command metadata for static checking of builtin command arguments,
    options, pipeline inputs, outputs, and side-effect boundaries.
13. Lift `TypeInferrer` results into emit decisions consistently so "type
    check passes, emit fails" cases become rare and actionable.

**Tier 5 — explicitly deferred:**

- Native rune lowering and `quote` metadata (v2 per the original plan;
  permissive currently uses whole-script source replay)
- `source` / `eval` of runtime paths (v2; opt-in `Tosh.Eval` package)
- NativeAOT (incompatible with TōSh's current reflection and dynamic runtime
  model)

### Working stance (updated May 2026)

- **Type system: gradual, dynamic-compatible, increasingly enforced at
  compile time.** `BoundType`, `TypeNameResolver`, `TypeInferrer`, and
  `TypeChecker` now participate in the compile path. The checker now covers:
  function annotations, implicit-dynamic checks, returns, user-function calls,
  condition typing (`if`/`while`/guards), builtin command arity (metadata),
  pipeline input shape contracts (`[PipelineInput]`), member/method/index
  validation on concrete CLR targets, and basic operator compatibility checks.
  Many shapes still flow as `object` and route through `ToshHost`; the next
  step is deeper builtin options/named-arg checks, class/module body semantic
  checks, and richer expression-form typing.

- **Source replay is a compatibility fallback, not the destination.** It keeps
  compiled programs behaviorally aligned with the interpreter while emitter
  support grows, but Tier 3 sites should continue shrinking so `runtime` and
  `pure` profiles become practical build gates.

- **The compiler feature matrix is now the truth table.**
  `CompilerFeatureMatrixTests` records which representative language forms are
  accepted by `permissive`, `runtime`, and `pure`. A feature is not considered
  moved forward unless the matrix expectation moves with it.

- **Bound IR is an immutable record tree.** Same shape as the parse
  tree (`ArgumentSyntax` / `StatementSyntax`), one `BoundNode` per
  syntax node, with the symbol table separate from the tree. Mirrors
  Roslyn's split.

- **The IR lives in `src/Tosh.Language/Binding/BoundNodes/`.** It
  conceptually belongs to `Tosh.Compiler`, but lifting it now means
  a project rename per commit. Today `Tosh.Compiler` (IL emission)
  and `Tosh.Compiler.Runtime` (host shim) live as their own
  projects; `Tosh.Compiler` references `Tosh.Language` for the IR.

- **Differential testing is the oracle.** Every emitter feature
  ships with a test in `tests/Tosh.Tests/BoundUnitEmitterTests.cs`
  that compiles a snippet, loads the assembly into the test
  process, and asserts captured stdout matches the interpreter's
  output for the same source.

---

## Files this design now touches

- [src/Tosh.Language/](../src/Tosh.Language/) — parser, binder, lowerer,
  bound IR, type resolver, type inferrer, and type checker.
- [src/Tosh.Compiler/](../src/Tosh.Compiler/) — `PersistedAssemblyBuilder`
  emitter, compile profiles, CLR shell emission, PDB emission, and ABI work.
- [src/Tosh.Compiler.Runtime/](../src/Tosh.Compiler.Runtime/) — helpers
  compiled code calls into for runtime dispatch, source replay, pipelines,
  dynamic objects, and subcommand execution.
- [src/Tosh.Runtime/](../src/Tosh.Runtime/) — shared command metadata,
  shell/stdlib attributes, typed type refs, and emitted assembly/type
  attributes.
- [src/Tosh.Stdlib/](../src/Tosh.Stdlib/) — one assembly with namespace
  partitions today; still a possible future split into bucket-specific
  packages.
- [src/Tosh.Sdk/](../src/Tosh.Sdk/) and
  [src/Tosh.Sdk.Tasks/](../src/Tosh.Sdk.Tasks/) — MSBuild SDK, targets, and
  in-process compile task.
- [src/Tosh.Cli/](../src/Tosh.Cli/) — CLI entry point for `--compile`,
  runtime staging, reference assembly flag, and deferred publish hooks.

## Verification approach

For each layer:

- **Layer 1 (library split):** existing test suite passes unchanged. Adding a
  `[ShellOnly]` command to a script-mode test should fail with a clear
  diagnostic.
- **Layer 2 (binder/type system):** bind/lower scripts, assert the bound IR
  shape, resolved symbols, inferred types, and diagnostics for unresolved
  names, missing annotations, type mismatches, and unsupported compiled shapes.
- **Layer 3 (compiler):** for each supported feature, compile a `.tosh` test,
  run the emitted assembly, and compare output/behavior with the interpreter
  where the feature is intended to share semantics. Add reflection assertions
  for public metadata whenever a feature is intended to be consumed from other
  .NET languages.
- **Layer 4 (SDK/product):** smoke-test packed SDK consumption with ordinary
  `dotnet build`, `dotnet run --project`, `dotnet publish`, `Clean`, paths with
  spaces, multi-file projects, reference assemblies, and C# consumer projects.

Current audit snapshot:

- `dotnet build Tosh.slnx --no-restore /m:1 /v:minimal` passed with
  0 warnings.
- `dotnet test tests/Tosh.Tests/Tosh.Tests.csproj --no-build --filter
  "FullyQualifiedName~CompilerFeatureMatrixTests"` passed: 23 passed, 0
  failed.
- Focused compiler/type/SDK/matrix tests passed: 256 passed, 0 failed.
- Latest user-reported full suite result: 2298 passed, 0 failed. The full
  suite was not re-run during this docs-only update.
- SDK lifecycle tests cover direct-import `dotnet build`, `dotnet run
  --project`, `dotnet publish`, apphost, single-file publish, refasm emission,
  runtime staging, `Clean`, multi-source compilation, `ProjectReference`
  build/stage/deps/run/publish behavior, packaged-SDK `PackageReference`
  restore/stage/deps/run/publish behavior with a transitive package dependency,
  C# refasm consumption, and packaged-SDK build/run smoke coverage.

## Open questions

These are the design calls that still shape the first-class .NET scope.

1. **Profile contract.** `permissive`, `runtime`, and `pure` exist; the exact
   feature list and compatibility promise for each profile still need to be
   frozen.
2. **Stdlib packaging.** One `Tosh.Stdlib` NuGet (one big DLL) or namespaced
   subpackages (`Tosh.Stdlib.Filesystem`, `Tosh.Stdlib.System`, etc.)?
3. **Runes in v1.** Keep the current permissive whole-script replay fallback,
   move to compile-time expansion/source generation, or preserve runtime quoted
   ASTs through an interpreter dependency?
4. **Entry-point default.** Subcommand trees and top-level statements work;
   explicit `func main` priority and library/no-entrypoint mode still need
   final rules.
5. **Doc comments at compile.** Embedded as attributes/XML docs (binary cost)
   or dropped from compiled output?
6. **Reference assemblies.** Emit true metadata-only reference assemblies from
   the SRE backend, or generate them through a secondary metadata-only path?

## Emitter coverage gap matrix (May 2026)

The lowerer intentionally understands more syntax than the IL emitter currently
materializes. The remaining emitter backlog ("type-check passes, emit fails")
is now explicitly tracked below.

### Already closed recently

- Compiled subcommand dispatch (Family 4) with source-replay fallback only.
- Tuple literal emission (`BoundTupleLiteral`) to `ToshTuple`.
- Set literal emission (`BoundSetLiteral`) to `HashSet<object?>`.
- Command/process substitution expression nodes now emit through
  `EmitPipelineAsValue` rather than hard-failing.
- `using` statements now emit as a no-op in compiled mode (binder/type-level
  effect only).
- Member-path assignment emission (`BoundMemberAssignment`) now supports
  plain and compound assignment (`=`, `+=`, `-=`, `*=`, `/=`, `%=`).
- Destructuring declaration emission (`BoundDestructuringDeclaration`) now
  supports array and record patterns.
- Defer statement emission (`BoundDeferStatement`) now lowers to nested
  block-scoped `try/finally`, matching LIFO unwind semantics.
- Yield statement emission (`BoundYieldStatement`) now materializes in compiled
  function bodies and no longer hard-fails emitter dispatch.

### Remaining high-priority gaps

- Whole-language acceptance: the current representative matrix has no
  all-profile-rejected rows, but it is not exhaustive yet. New syntax-family
  rows should be added until this becomes a true language-surface gate.
- Advanced callable/closure interop without source replay.
- Type-declaration emission completeness: advanced class/trait/interface/union/
  enum/alias/event semantics currently represented by shells or replay paths.
- Operator surface parity: remaining operators that still report unsupported in
  IL (including non-numeric and niche language operators).

### Next implementation slices

Use the canonical roadmap above for ordering. At the emitter level, the next
work should be sliced by contract surface rather than by whichever unsupported
node happens to appear first:

1. **Feature-matrix expansion**: cover every parser node, bound node,
   operator, modifier, declaration family, command concept, and spec section.
2. **Source-replay retirement**: reduce `Register*FromSource`,
   `RunScriptFromSource`, subcommand replay, and `RequireTier(3, ...)` sites
   until remaining replay is deliberate and documented.
3. **Public ABI metadata**: lock and test CLR names, visibility, overloads,
   constructors, attributes, typed signatures, refasm parity, and C# consumer
   behavior.
4. **Type-declaration completeness**: finish CLR-first classes, interfaces,
   traits, unions, structs, events, aliases, refinements, nested module
   declarations, and dynamic-only fallback rules.
5. **SDK/refasm productization**: make `.toshproj`, `dotnet run`, publish,
   package references, project references, clean, and metadata-only refasm act
   like ordinary SDK-style .NET project features.
6. **Spec and tooling loop**: update the language spec, compiled-status docs,
   feature matrix, parity checks, LSP metadata, and generated references in the
   same milestone rather than after the fact.

## Related

- [SPEC_STATUS.md](SPEC_STATUS.md) — gaps in the language specification that the compiler work will surface.
- [ARCHITECTURE.md](ARCHITECTURE.md) — current architectural shape and the project structure this plan builds on.
- [ROADMAP.md](ROADMAP.md) — overall project direction; this document slots into the long-term assembly-shape note.
