# TōSh Backlog

Open work items by area, roughly ordered by priority within each section.
Completed items prior to 2026-05-07 live in
[BACKLOG-archive.md](BACKLOG-archive.md).

> **Active language stabilization:** The prioritized ToastScript repair
> program, semantic decisions, and acceptance gates live in
> [TOASTSCRIPT_STABILIZATION.md](TOASTSCRIPT_STABILIZATION.md). Update that
> document rather than duplicating its item statuses here.

Last updated: May 7, 2026. Lambda return-type annotations, postfix
`if`/`unless` on `return`/`break`/`continue`/`throw`/`yield`, lazy
parenthesised generator comprehensions `(body <| for ...)`, the rune
base set, and a backlog audit against the actual implementation all
landed in this pass. Earlier additions: Line Editor Phase 1, user-defined
error types, top-level signal flow fix, `Tosh.Compiler` IR + IL emitter
spike, streaming display sinks, `iterate`/`recur` builders, VS Code
extension, MCP `run_snippet`/`explain_error` tools.

Recent additions: First-Class .NET Citizenship section (Waves 1–3) reflecting
the 2026-05-06 audit; spec restructured with new `\part{Compilation}`. See
[FIRST_CLASS_DOTNET_STATUS.md](FIRST_CLASS_DOTNET_STATUS.md) and
[SPEC_STATUS.md](SPEC_STATUS.md) for the full audit/roadmap pairing.

---

# External-Program I/O Compact (2026-05-13) — P0

**Status:** M1–M4 landed 2026-05-13. M5 (polish) deferred. See
[TSSP.md §11](TSSP.md#11-producing-tssp-from-net-toshclient) for the
`Tosh.Client` surface; [examples/tsspdemo](../examples/tsspdemo) is
the canonical second-consumer demo.

Building first-party tools (`crumb`, future helpers) for daily use as
login-shell children exposed a sharp gap: there is no clean,
documented way for an external program to render structured output to
TōSh *and* read interactive input from the user at the same time.

The forcing example: `crumb -S <many AUR pkgs>` at a bare TōSh prompt.
Crumb was on the `KnownStructuredCommands` allowlist in
[ExternalProcessCommand.cs](../src/Tosh.Stdlib/ExternalProcessCommand.cs),
which forces the piped path so TōSh can parse TSSP frames. That path
also redirects stdin and never calls `tcsetpgrp`, so the child opens
`/dev/tty` but TōSh's REPL still owns the controlling terminal — every
keystroke is contended. Symptom: prompts that accept no input.

Immediate mitigation: the allowlist is empty. Interactive children
take the full passthrough path (`tcsetpgrp` → foreground group →
inherited stdio). TSSP rendering for `crumb` at a bare prompt is
suspended until the proper plumbing lands.

### Goals

1. **Hybrid passthrough mode** — pipe stdout *only*, inherit stdin and
   stderr, hand off the foreground process group, parse TSSP framing
   from the piped stdout while child I/O on `/dev/tty` works normally.
2. **A documented client contract** that external programs can adopt:
   - Negotiation envvars (`TOSH_STRUCTURED_STDOUT`, `TOSH_STDOUT_CONSUMER`,
     `TOSH_TSSP_VERSION`, `TOSH_STDIN_ACCEPTS`, color/width hints,
     `TOSH_TTY`, `TOSH_STDIO_MODE`).
   - Where to write human status (stderr / `/dev/tty`).
   - Where to write structured data (stdout, TSSP frames only).
   - Where to read input (always `/dev/tty`, with `TCIFLUSH` drain).
   - Job-control expectations (child becomes group leader, parent
     `tcsetpgrp`s the child, child handles SIGINT/SIGTSTP/SIGQUIT).
3. **A reusable client library** so we stop reimplementing this per app:
   - C#: `Tosh.Client` package (`src/Tosh.Client`, `net10.0`, zero deps
     on the rest of the tree). TSSP frame writer, status/prompt helpers
     backed by `/dev/tty`, env-var negotiation, color detection.
     Replaces `Tosh.Crumb/Output/{Confirm,Tty,TtyRedirect}.cs`.
   - Shipped as both a ProjectReference (in-tree) and a NuGet package.
   - Eventually mirror libraries for other languages.
4. **Worked example + docs** wired into `docs/ARCHITECTURE.md` and a
   new `docs/EXTERNAL_PROGRAMS.md` so anyone building a tool for
   TōSh has one canonical reference.

### Milestones (locked 2026-05-13)

**M1 — Hybrid spawn mode in ToSh.** ✅ Landed 2026-05-13.
`ExecuteWithHybridAsync` in
[ExternalProcessCommand.cs](../src/Tosh.Stdlib/ExternalProcessCommand.cs):
stdout piped (TSSP parser), stdin/stderr inherited, child placed in its
own pgrp with `TrySetForegroundGroup`, `WaitForForegroundChild` for full
Ctrl-C/Z/D job control. Opt-in via `$tosh.Config.External.HybridConsumers`
(default seeded with `crumb`). `ApplyTsspEnvironment` adds
`TOSH_TTY` and `TOSH_STDIO_MODE`. Frame parser is liberal: non-TSSP
bytes on hybrid stdout are echoed verbatim with a one-time
`tosh: tssp.unframed_output` stderr warning.

**M2 — `Tosh.Client` library.** ✅ Landed 2026-05-13.
New `src/Tosh.Client` project. `ToshHost.Current` exposes `Info`,
`Status` (/dev/tty-first), `Prompt` (/dev/tty + TCIFLUSH per call),
`OpenFrameWriter(schema)` returning a thread-safe `ToshFrameWriter`.
`ChildTtyScope.Acquire()` provides a dup/dup2 fd-swap for child
spawns that should drive the terminal directly.

**M3 — Crumb migration.** ✅ Landed 2026-05-13.
Deleted `src/Tosh.Crumb/Output/{Tty,TtyRedirect}.cs`. `Confirm.cs`
rewritten as an 8-line shim over `ToshHost.Current`. `PackageFormatter`
uses `ToshFrameWriter` (`CrumbTsspMetaFrameTests` passes — wire-
compatible). 4 `TtyRedirect.Acquire()` call sites repointed to
`Tosh.Client.ChildTtyScope`. End-to-end smoke under hybrid spawn:
`crumb list --explicit | first 3` renders the full 30-column table.

**M4 — Docs + second consumer.** ✅ Landed 2026-05-13.
[docs/TSSP.md §11](TSSP.md#11-producing-tssp-from-net-toshclient)
documents the `Tosh.Client` surface and hybrid-spawn opt-in.
[examples/tsspdemo](../examples/tsspdemo) is a 30-line second consumer
proving the contract isn't crumb-specific.

**M5 — Polish (deferred).** Real `crumb.install-plan` schema with
box-drawing TōSh display profile. Optional: TSSP `progress` frame
routed to a Tome progress bar. Optional: probe-based auto-discovery
for hybrid-capable binaries when config lists are inconvenient.
Line-buffered forwarding of unframed hybrid output.

### Acceptance

- Crumb's `Output/` directory replaced with `Tosh.Client` calls.
- `crumb -S` at a bare ToSh prompt: prompts work, status streams live,
  install-plan summary renders as a TōSh-styled table (TSSP frame).
- `crumb -Ss dotnet | from json` still produces structured records.
- Smoke test: a second tiny consumer demonstrates the contract end-to-end.

### Non-Goals

- A full pty multiplexer or terminal-emulator layer.
- Forcing every external command to participate — programs that
  ignore the envvars must keep working exactly like they do today.

---

# Crumb (Pacman + AUR Helper) Polish — 2026-05-13 — P2

After the M1–M4 TSSP work plus the upgrade/install/removal UX pass
(boxed colorized tables, summary matrix, group expansion, quiet
makepkg by default), the project review surfaced the items below as
the next batch of polish. None block daily use; ordered roughly by
leverage.

## Resolved quick wins

- **Crumb coverage exists.** Focused tests now live in
  [tests/Tosh.Tests/Crumb*.cs](../tests/Tosh.Tests/), covering
  pacman-style flag expansion, option parsing (including `--limit`),
  formatter/TSSP selection, privilege probing, and version comparison.
- **Colour detection is centralized.**
  [ColorSupport.cs](../src/Tosh.Crumb/Output/ColorSupport.cs) owns
  stdout/status colour gating and truecolor detection; formatters route
  through it.
- **Startup validates cache prerequisites.**
  [Program.cs](../src/Tosh.Crumb/Program.cs) now rejects an environment
  with neither `$HOME` nor `$XDG_CACHE_HOME` before AUR/cache paths are
  touched.
- **`--limit N` shipped.** `CrumbOptions.Parse` accepts `--limit` and
  `--limit=N`; search/news commands trim results accordingly.

## P2 — medium features

- **Honest stub handling.** ✅ Landed 2026-05-13. `-Sw` now maps to
  `install --download-only`, using `pacman -Sw` for repo targets and
  fetching AUR PKGBUILDs without building. `-Suw` / `-Syuw` download
  pending repo upgrades. `-U <file...>` maps to `install-file` and
  delegates to `pacman -U`.
- **Split `UpdateAsync` and `InstallAsync` into phase methods.** ✅
  Landed 2026-05-13. Install now separates validation/planning,
  rendering/confirmation, repo execution, and AUR fetch/build phases.
  Update now separates repo upgrade/download, AUR discovery, review,
  download-only, and rebuild phases.
- **`crumb logs` subcommand.** ✅ Landed 2026-05-13. `crumb logs`
  lists newest build logs from `$XDG_CACHE_HOME/crumb/log/`; supports
  `--pkg <name>`, `--tail`, `--clean`, `--limit N`, and `--dry-run`.
- **Config file** (`~/.config/crumb/crumb.toml`). Today everything is
  env vars (`CRUMB_SUDO`, `CRUMB_PAGER`, `CRUMB_REVIEW`,
  `CRUMB_NO_TRUECOLOR`, `CRUMB_NO_COLOR`, `TOSH_TTY`). A TOML config
  with env-var overrides would let users persist build flags, default
  `--quiet`/`--verbose`, an exclude list for `-Syu` (e.g. skip
  `*-git`), pager, and truecolor preference.
- **Improved conflict-resolution UX.** `DependencyResolver` already
  detects conflicts; the prompt is binary proceed/abort. Show which
  installed package the conflict is with, and offer granular
  remove-or-skip for each.

## P3 — larger / optional

- **Pacnew/pacsave detection** after install — paru-style.
- **Downgrade support** via the Arch archive.
- **`--aur-base-url`** env var / config for testing against mock or
  mirror AUR endpoints (`AurClient` already accepts the constructor
  arg; just no CLI wiring).
- **Document implicit behaviour**: pager precedence
  (`pagerOverride` > `CRUMB_PAGER` > `PAGER` > `less`), pacman-flag
  expansion semantics, format-flag last-wins.

### Acceptance

- P1 batch landed before the next user-visible feature pass.
- Long methods in `CrumbCommands.Update.cs` /
  `CrumbCommands.Install.cs` either split or annotated with phase
  comments — whichever serves clarity better.
- `crumb --help` lists no commands that throw `not implemented`.

### Non-Goals

- A mirror ranker (pacman owns mirrors).
- An alpm FFI binding (the on-disk DB parser is sufficient).
- Repo management (`crumb` is a client, not an admin tool).

---



A holistic project review surfaced seven priorities for the next quarter,
ordered by leverage. They cluster into three themes: **closing language
gaps that force user boilerplate** (#1), **lowering the onboarding tax for
polyglot developers** (#2, #7), and **tightening project identity** (#3,
#4, #5, #6). Items #1, #2, and #7 landed on 2026-05-08.

## 1. Numeric Generics / Trait-Like Constraints — P1 — closed (2026-05-08)

The current generic system has no equivalent of C# 11 static-abstract
interface members (`INumber<T>`, `IAdditionOperators<T,U,R>`), F# inline
+ SRTP, or Rust trait bounds. The forcing example is
[examples/point.tosh](../examples/point.tosh) — a generic `Point2D<T1, T2>`
must enumerate `+`/`-`/`*`/`/` overloads four times (one per right-hand
operand type) because the language cannot say "T must support `+`".

### Goals

- Express constraints like `where T: Add` / `where T: INumber` so a single
  `func +(other: T)` covers every numeric `T`.
- At minimum, recognise the four CLR static-abstract numeric interfaces
  (`IAdditionOperators<,,>`, `ISubtractionOperators<,,>`, `IMultiply…`,
  `IDivision…`) and surface them as built-in shorthand (`Numeric`,
  `Addable`, etc.).
- Extend the binder to verify operator-arithmetic statements against the
  declared bound at parse-time, not at value-flow time.
- Reduce the `point.tosh` body to a single overload set per operator.

### Non-goals

- Full Haskell-style typeclass system.
- User-defined trait declarations on top of CLR interfaces (defer until a
  pattern emerges).

### Priority: P1 — **closed (2026-05-08)**

Initial implementation:
- Parser accepts `where T: <Constraint>[, <Constraint>…]` clauses after
  the class header (multiple `where` clauses allowed).
- Built-in constraint registry (`Numeric`/`Number`/`INumber`,
  `Add`/`Sub`/`Mul`/`Div`, `Comparable`, `Eq`) — see
  [src/Tosh.Language/ToshTypeParameterConstraintRegistry.cs](../src/Tosh.Language/ToshTypeParameterConstraintRegistry.cs).
- Validation runs at instantiation; violations throw a structured
  diagnostic citing the failing constraint.
- Unknown constraint names are accepted conservatively (reserved for
  user-defined trait constraints in a future pass).
- Followups: surface constraints in LSP hover, propagate to operator
  dispatch type-checking, allow user-defined constraints.

### Phase 1.x / Phase 2 update — 2026-05-09

Generics evolved past the original "trait-like constraints" goal into a
fuller C#-style system. Landed in this round:

- **Phase 1.2** — `type-of` on a generic instance returns a
  `BoundGenericTypeDescriptor` whose `Name` / `FullName` /
  `IsGenericType` / `TypeArguments` reflect the bound substitution
  (e.g. `Point2D<Int32>`). See
  [src/Tosh.Language/BoundGenericTypeDescriptor.cs](../src/Tosh.Language/BoundGenericTypeDescriptor.cs).
- **Phase 1.3** — User-defined constraints. A `where T: SomeName`
  whose name is not in the built-in registry now resolves through
  `ToshEngine.TryResolveTypeName` and accepts any CLR type assignable
  to it (so `where T: IDisposable` works without a built-in entry).
  See `ToshClassDefinition.TrySatisfyUserConstraint`.
- **Phase 2.1** — `func name<T>(...)` / `func map<T,U>(...)` parse and
  execute. `EraseTypeParameter` recursively strips type-parameter
  names from nested generic annotations (`list<T>` → `list`).
- **Phase 2.2** — Per-call inference. `BindFunctionParameters` returns
  an inferred-type table; `ApplyGenericBinding` records the first
  binding and strict-validates later parameters. Mismatch raises
  `tosh.runtime.generic_argument_type_mismatch`.
- **Phase 2.3** — `where T: …` clauses on free functions; reuses the
  built-in registry plus the CLR-interface fallback.
- **Phase 2 deferred** — explicit call-site type args (`box<int> 42`)
  are blocked on parser disambiguation: `<` is overloaded for input
  redirection. Plan: input redirection is always followed by `(`
  (`<( … )`), so `foo<X>` with a non-`(` next token is unambiguously
  a generic call. Capture in Phase 3.3 below.

### Phase 3 — Inference depth & call-site polish — P1

Next round, in priority order:

1. **Nested-shape inference** (`func first<T>(items: list<T>) -> T`).
   ✓ DONE (2026-05-09) — annotation walker unifies
   element / key / value types into the per-call binding table; nested
   `dict<K, list<V>>` etc. work. Inference now runs *before*
   `ConvertFunctionParameterValue` so element types aren't widened.
2. **Return-type contribution.** ✓ DONE (2026-05-09).
   When `T` only appears in the return type and the call site has a
   target type (`var x: int = identity<T> 42`), the LHS annotation
   propagates through an `AsyncLocal<string?>` set at the
   variable-declaration boundary, stamped onto `CommandInvocation`,
   and unified annotation-vs-annotation against the function's
   `RawReturnTypeName` to seed the per-call binding table. Nested
   shapes (`var xs: list<int> = wrap 42`) work via recursive
   head/arg matching.
3. **Explicit call-site type args.** ✓ DONE (2026-05-09).
   Disambiguation is trivial because the lexer already emits a single
   `<(` token for input redirection — a bare `<` immediately after a
   command name (no whitespace) is unambiguously a generic argument
   list. Inferred-binding table is seeded from the parsed type-args
   before parameter conversion. Operator-detection lookahead skips
   over generic-arg lists at depth 0 to avoid mis-parsing
   `foo<int> 1 2` as a comparison.
4. **Generic methods on classes.** ✓ DONE (2026-05-09).
   Parser, type-parameter erasure (combined class+method scope), and
   class-method invocation all carry the method's `TypeParameters` /
   `TypeParameterConstraints`. `ToshClassDefinition.ExecuteMethodBlock`
   now constructs a synthetic `CommandContext` from the method's
   source info + parameter spans and calls
   `ToshEngine.InferMethodTypeBindings` to populate a method-scoped
   binding table, which is merged with any class-level bindings
   carried by the instance. Strict per-call validation fires when
   different arguments imply different bindings for the same `U`,
   matching the diagnostic shape used for free functions.

### Phase 4 — Constraint richness — P2

5. **Recursive / parameterized constraints**
   (`where T: IComparable<T>`). ✓ DONE (2026-05-09).
   Parser now consumes `<…>` after the constraint bareword via
   `ParseTypeNameSuffix`, producing a constraint string like
   `IComparable<T>`. The runtime constraint check substitutes
   type-parameter references with their inferred bindings (the
   currently-binding T plus any other type parameters already in
   `typeBindings`) before resolving via `TryResolveTypeName`. Mixed
   bindings flow correctly: `IDictionary<K, V>` resolves with both
   parameters substituted.
6. **C#-style multiple constraints** (`where T: A, B`).
   ✓ DONE (2026-05-09). The parser already supported comma-separated
   constraints in a single clause, and multiple separate `where`
   clauses also work (`where A: Numeric where B: Comparable`).
   Each constraint name is checked independently in registration order.
7. **Special constraints** — `new()`, `class`, `struct`, `notnull`,
   `unmanaged`. ✓ DONE (2026-05-09). Added to
   `ToshTypeParameterConstraintRegistry`:
   - `new` / `new()` — public parameterless ctor (value types always pass).
   - `class` — non-value type (reference type / interface).
   - `struct` — non-nullable value type.
   - `notnull` — accept-all (CLR types are never null).
   - `unmanaged` — recursive predicate over fields.
   Parser passes `new` as a bareword constraint; the registry alias
   for `new()` covers users who write the C# form.
8. **`default(T)` expression.** *Deferred* — requires a new expression
   AST node, parser support for `default(TypeName)`, and pushing the
   per-call `typeBindings` table into a scope visible from the
   function body so `T` can resolve to its bound CLR type. Workaround:
   pass a default-valued argument explicitly, or use `null` for
   reference types.

### Phase 5 — Generics on other declarations — P2

9. ✓ DONE — Generic records (`record Pair<A,B>(first: A, second: B)`).
   - Parser: type-parameter list and `where` clauses (both pre- and
     post-field positions).
   - `ToshRecordDefinition` carries `TypeParameterNames` /
     `TypeParameterConstraints`; `CreateGenericInstance` validates
     constraints and builds bound instances. Field annotations matching
     a type-parameter name are strict-checked (`IsInstanceOfType`),
     mirroring class-parameter behavior.
   - Engine `new` dispatch handles records analogously to classes.
   - Structs / unions / enums deferred — out of scope until concrete use
     cases surface.
10. ✓ DONE — Generic interfaces (`interface IRepo<T>`) with substitution
    at `fulfills` check time.
    - Interface parser accepts `where` clauses; runtime carries
      `TypeParameterNames` / `TypeParameterConstraints`.
    - At `class … fulfills IRepo<int>` sites, `ValidateInterfaceTypeArguments`
      enforces arity, rejects bare references to generic interfaces, and
      validates concrete type arguments against the interface's
      where-clauses. Type arguments that forward the implementing
      class's own type parameters are deferred (validated at
      instantiation).
    - New diagnostics: `tosh.runtime.missing_interface_type_arguments`,
      `tosh.runtime.unexpected_interface_type_arguments`,
      `tosh.runtime.interface_type_argument_arity_mismatch`,
      `tosh.runtime.interface_type_argument_constraint_violation`.
11. ✓ DONE — Type-alias transparency in the type checker.
    - Plain aliases (`type Id = int`) and refinement aliases (`type
      Positive = int where _ > 0`) both project to a `RefinementType`
      wrapper around the resolved base; the type checker now unwraps
      that wrapper inside `IsAssignable`, so alias names compare
      transparently to their bases without false `tosh.type.mismatch`
      diagnostics in script-mode.
    - Generic aliases (`type MyList<T> = list<T>`, `type Bounded<T> = T
      where _ > 0`) work at use sites by recursing through the alias's
      template base — leaning on `Dynamic`-element placeholders and the
      structural list/array/dict element-recursion now in `IsAssignable`.
      Precise structural substitution of type parameters is a separate
      follow-up.
    - List-literal `IList` source compatibility: a raw list literal
      (currently lowered as `BoundType.FromClr(typeof(IList))`) now
      flows freely into any `ListType` / `ArrayType` slot and likewise
      for raw dictionaries.
    - `EnsureRefinementAliasNameDoesNotConflictWithType` no longer
      consults the wide CLR-resolver fallback, fixing spurious
      `tosh.runtime.type_name_conflict` errors on alias names like
      `Pair` that happen to collide with arbitrary loaded-assembly
      types. Conflicts now only fire against user-declared named types.
    - Tests: 6 new cases in `tests/Tosh.Tests/TypeCheckerTests.cs`
      lock in alias transparency for plain, refinement, generic-
      refinement, parameterized-base, and forwarding-generic aliases,
      plus a negative case ensuring real mismatches still report.

### Phase 5 — Followups still open

- ✓ DONE — Precise structural substitution of generic-alias type
  parameters. `TypeNameResolver.ResolveGeneric` now detects when a
  user-type template is a `RefinementType` carrying a
  `TypeAliasStatementSyntax` with declared type parameters, validates
  arity, overlays each `T -> arg` mapping into a child resolver, and
  re-resolves the alias's `BaseTypeName`. The result is a precise
  `RefinementType(substitutedBase, "MyList<int>", alias)` instead of
  the previous `Dynamic`-erased `GenericInstanceType` wrap.
  Diagnostic emitted on arity mismatch and on type arguments applied
  to a non-generic alias. 4 new tests in `TypeCheckerTests.cs` cover
  int/string substitution, two-parameter aliases, and arity errors.

### Phase 6 — Advanced features — P3

12. ✓ **Variance (`out T` / `in T`).** *Done 2026-05-09.* The
    parser recognises optional `out` / `in` prefixes inside a
    type-parameter list and threads them through
    `InterfaceDefinitionStatementSyntax.TypeParameterVariances`,
    `ToshInterfaceDefinition.TypeParameterVariances`, and the
    `UserInterfaceType` registry entry. `TypeChecker.IsAssignable`
    now consults the per-parameter variance when comparing two
    `GenericInstanceType`s wrapping the same interface template:
    covariant slots use one-way `IsAssignable(fromArg, toArg)`,
    contravariant slots flip it, invariant slots require
    bidirectional assignability. Variance is honored only for
    interface templates — classes/records/structs stay invariant,
    matching C#. 4 new tests in `TypeCheckerTests.cs` cover
    covariant widening, invariant rejection of widening,
    contravariant flow in reverse, and covariant rejection of
    narrowing.
13. *Skipped for now — reflection builtins.* `is-generic-type`,
    `type-arguments`, `generic-definition`, `make-generic-type` would
    be cheap to add but pile onto the ~209-builtin surface that
    section 3 below already flags as needing pruning. Revisit after
    the command-audit pass settles which families consolidate.
14. **Compiler-emit (`tosh --compile`) lowering of generic call
    sites.** Largest effort. The interpreter path is the source of
    truth; `BoundUnitEmitter` currently bails on generic-instance
    member access and constrained dispatch. Needs parallel work for
    type-param-keyed locals, generic-method dispatch, and runtime
    `IsInstanceOfType` checks at member boundaries.
15. **Constraint expressiveness — user-interface constraints.** ✓ DONE
    `where T: ISomeUserInterface` is now enforced at generic-class
    instantiation: `ToshClassDefinition.TrySatisfyUserConstraint` looks
    up the constraint name as a `ToshInterfaceDefinition` and walks the
    bound type-arg's `ToshClassDefinition.ImplementedInterfaces` chain
    (including base classes) for membership. Inherited interfaces from
    a parent class satisfy the constraint. Built-in registry constraints
    (Numeric, Comparable, op_Add, …) and CLR interface constraints
    (`IDisposable`, etc.) continue to work as before. Truly unknown
    constraint names remain conservatively accepted. Records and
    interfaces still accept user-interface constraints conservatively
    — mirroring the new class behavior is a small follow-up.
16. **Type inference at call sites.** ✓ DONE
    Ctor-position inference now binds type parameters from the
    runtime types of `new ClassName(args)` arguments — both the
    bare-T case (`class Box<T>(initial: T)` ⇒ `T = int` for
    `new Box(42)`) and nested annotations (`class Box<T>(values:
    list<T>)` peeks the list's element type). Unified via a small
    recursive `UnifyCtorAnnotationWithValue` that handles
    list/array/dict shapes and any generic CLR type with matching
    arity. Applies to both classes and records. Constraint
    validation still fires after inference, so `new Box("hi")` on
    a `where T: Numeric` class is rejected. Method-call inference
    on instance / static methods remains explicit-`<T>`-only and
    is a follow-up.

---

## 2. Standard-Name Aliases for Class Modifiers — P1 — closed (2026-05-08)

The flavored modifier set (`shy`, `proud`, `guarded`, `vital`, `overrule`,
`hollow`, `hermit`, `fading`, `fixed`) renames concepts that already have
universal industry names. Every C#/Java/Swift/Kotlin/TypeScript developer
must learn a translation layer with no semantic payoff, and LLM tooling
mis-suggests TōSh code accordingly.

### Goals

- Accept these canonical aliases in the parser **without removing the
  flavored forms**:

  | Canonical (new) | Flavored (kept) |
  |-----------------|-----------------|
  | `private`       | `shy`           |
  | `public`        | `proud`         |
  | `protected`     | `guarded`       |
  | `required`      | `vital`         |
  | `override`      | `overrule`      |
  | `abstract`      | `hollow`        |
  | `static`        | `shared` (existing)/`hermit` (class) |
  | `readonly`      | `fixed`         |
  | `obsolete`      | `fading`        |

- Document canonical forms as the recommended style, with flavored forms
  preserved as synonyms.
- Update LSP completions, hover text, and AGENTS.md to lead with the
  canonical names; mention the flavored synonyms in a short table.
- Run a single pass over `examples/` to convert at least the headline
  examples (e.g. `examples/point.tosh`) to canonical names so search
  results land on canonical syntax.

### Non-goals

- Removing flavored forms (would break `examples/`, profile.tosh
  ecosystems, and stylistic charm).
- Changing IL emission for these modifiers — they already lower to the
  same CLR semantics.

### Priority: P1 — **closed (2026-05-08)**

- Parser accepts `private`, `public`, `protected`, `required`,
  `override`, `abstract`, `static`, `readonly`, `obsolete` as direct
  synonyms for the flavored modifiers (member-level), and `abstract` /
  `static` at the class level. See
  [src/Tosh.Language/Parsing/ToshParser.cs](../src/Tosh.Language/Parsing/ToshParser.cs).
- AGENTS.md modifier tables now lead with the canonical name.
- Followups: LSP completions/hover prefer canonical names; convert
  example sources opportunistically.

---

## 3. Surface-Area Pruning — P2 — audit complete + first wave landed (2026-05-10)

255 builtins is PowerShell-scale and growing. Several clusters duplicate
each other or expose unsafe primitives by default.

### Audit pass — DONE 2026-05-09

Full audit lives at [`docs/SURFACE_AUDIT.md`](SURFACE_AUDIT.md), driven
by the `--export-command-metadata` JSON dump (255 commands). Every
command is tagged **Keep / Fade / Move / Consolidate / Rename** with
rationale. Counts: 196 keep, 12 fade, 6 move, 30 consolidate, 11 rename.

### First-wave consolidation — DONE 2026-05-10

- **CLR verb-fade landed.** `call`, `call-method`, `get-prop`,
  `get-props`, `get-methods`, `set-prop`, `del-prop`, `has-prop`,
  `has-method` carry `[CommandDeprecated("26.05.0.10")]` with notes
  pointing at the canonical syntax (`$obj.Method($args)`, `$obj.Prop`,
  `$obj.Prop = value`, `members has X`, …).
- **`members` and `methods` got subcommands.** Both accept
  `has <name>` and `get <name>`. `members` additionally accepts
  `props` / `fields` / `methods` / `events` to slice by member kind.
  `props` and `funcs` are top-level shortcuts.
- **`get` is now the canonical column-picker** with variadic field
  projection (`get name size extra`). `select` and `pick` remain as
  soft aliases.
- **`row` is the new canonical row-picker** — variadic on indices,
  list literals, and ranges (`row 7 8 9`, `row [3,1,0]`, `row 1..3`).
  Bad indices throw `tosh.row.index_out_of_range`.

### Remaining action items

1. **Gate native FFI behind `tosh-interop` module** — six `native-*`
   commands (and short aliases) load only when explicitly imported.
   Default off. (OWASP A04.) _Open._
2. **Streaming/throughput contract** (item 6 below) — uses this audit
   as the authoritative command list. Tag each Pipeline command
   lazy/eager/short-circuiting in `help`. _Open._
3. **`prompt <segment>` subcommand consolidation** — spec migration
   path; keep `prompt-*` as fading aliases for one major. _Design-first._
4. **Alias-fade mechanism.** `RegisterAlias` has no "soft-deprecated"
   flag. Either extend the registry, or document the secondary aliases
   (`pick`, `select`, `foreach`, `avg`, `sort-by`, `stddev`, `summary`)
   as docs-only fading until a registry change lands. _Open._
5. **Pin canonical names for soft-alias rows** in AGENTS.md so
   completion + LLM tooling rank canonical first
   (`average` over `avg`, `each` over `foreach`, `get` over
   `pick`/`select`, `sort` over `sort-by`, `stdev` over `stddev`,
   `summarize` over `summary`, `forget` over `unset`). _Open — doc only._

### Priority: P2 — *first wave landed (2026-05-10); remaining items are mechanical or design-first*

---

## 4. Operator-Overload IL Emission Uses CLR Conventions — P2 — closed (2026-05-13)

`func +(other) { … }` currently lowers to a method named after the
symbol. CLR consumers (C#, F#, PowerShell) cannot resolve TōSh-defined
operators because they expect `op_Addition`, `op_Subtraction`, etc.

### Goals

- Emit both names (or emit only the CLR-canonical name and accept the
  symbolic form as syntax sugar that resolves to it).
- Verify a TōSh class's `+` is callable from a C# consumer in the
  `Tosh.Tests` cross-language sample.
- Map the full overloadable set: `+ - * / % == != < <= > >=` (and the
  corresponding `op_*` names; `=~`/`!~` and `**`/`//` need either custom
  attribute-tagged dispatch or a TōSh-specific calling convention).

### Priority: P2 — **closed (2026-05-13)**

- `ToClrOperatorName` helper in
  [src/Tosh.Compiler/BoundUnitEmitter.Functions.cs](../src/Tosh.Compiler/BoundUnitEmitter.Functions.cs)
  maps the full canonical set (`+` → `op_Addition`, `-` → `op_Subtraction`,
  `*` → `op_Multiply`, `/` → `op_Division`, `%` → `op_Modulus`, `==`,
  `!=`, `<`, `<=`, `>`, `>=`). Symbolic-only operators with no CLR
  convention (`**`, `//`, `=~`, `!~`) get stable `op_Tosh*` names.
- Applied at both `DefineMethod` sites in
  [BoundUnitEmitter.Classes.cs](../src/Tosh.Compiler/BoundUnitEmitter.Classes.cs)
  (abstract method stub + regular instance method).
- Regression coverage:
  [BoundUnitEmitterTests.Compiled_operator_overload_emits_clr_canonical_method_name](../tests/Tosh.Tests/BoundUnitEmitterTests.cs)
  asserts `op_Addition` lands on the emitted type.

### Follow-up (not blocking closure)

TōSh operator methods are instance methods with `HasThis`, so a C#
consumer sees `box.op_Addition(other)` rather than `box + other`. Native
C# `+` syntax additionally requires the method to be `public static`
with both operands as parameters — a future change can synthesise a
static wrapper that forwards to the instance method.

---

## 5. Identity Statement in README — P2

The README sells three things at once: interactive shell, scripting
language, compiled-program target. Most example traffic
(`scripts/build.tosh`, `~/.local/bin/headset`, profile autoload modules)
suggests **scripting is the dominant identity**. Pick one (or rank
them) so future feature decisions have a north star.

### Goals

- Write a single-paragraph "what is TōSh" lede that ranks the three
  identities and explains the pitch in one sentence.
- Reorder the README's feature highlights to match.
- Consider stripping the compiled-program pitch from the headline if
  Wave 2 of First-Class .NET Citizenship isn't shipping in this cycle.

### Priority: P2

---

## 6. Streaming/Throughput Contract — P2 — closed (2026-05-13)

`first N` short-circuits today, but there's no documented contract that
says so. Users (and our own renderer optimisations) need a written
guarantee about which builtins are lazy, which are eager, and which
require materialisation.

### Goals

- Document each pipeline builtin's behaviour: **lazy** (`where`, `each`,
  `map`, `filter`, `take`/`first`, `skip`, `flatmap`), **eager**
  (`sort`, `sort-by`, `reverse`, `group-by`, `summarize`, `to json`,
  `count` when consuming the whole stream), **partial** (`first N`
  short-circuits; `last N` still drains).
- Add focused tests for each lazy builtin proving short-circuit
  behaviour against an infinite generator.
- Surface the contract in `help` topics (a one-line "Streaming: lazy /
  eager / short-circuiting" field on each builtin).

### Priority: P2 — **closed (2026-05-13)**

- `StreamingBehavior` enum (`Lazy` / `Eager` / `ShortCircuit`) + a
  class-level `[CommandStreaming(...)]` attribute live in
  [src/Tosh.Runtime/CommandStreamingAttribute.cs](../src/Tosh.Runtime/CommandStreamingAttribute.cs);
  reflected into `CommandMetadata.Streaming` by
  [ShellCommand.cs](../src/Tosh.Runtime/ShellCommand.cs) with a
  humanised form ("lazy" / "eager" / "short-circuit").
- ~56 pipeline commands under `src/Tosh.Stdlib/Pipeline/*` are tagged.
- `help` topics render a `Stream:` line inside the Pipeline sub-box
  ([HelpTopicSummaryRenderer.cs](../src/Tosh.Runtime/HelpTopicSummaryRenderer.cs))
  and the LSP markdown surface
  ([ToshLanguageFeatures.cs](../src/Tosh.LanguageServices/ToshLanguageFeatures.cs)).
- Short-circuit regression tests against an infinite generator land in
  [tests/Tosh.Tests/StreamingContractTests.cs](../tests/Tosh.Tests/StreamingContractTests.cs).

---

## 7. `$env.X = "value"` Assignment Sugar — P3 — closed (2026-05-08)

Documented as gotcha #1 in [AGENTS.md](../AGENTS.md). There is no
semantic reason `$env.X = "v"` should not desugar to `export X = "v"`;
the asymmetry exists because `$env` is currently a read-only namespace.

### Goals

- Recognise assignment to `$env.<name>` as sugar for `export <name> =
  <value>` at the binder/lowering stage; reject only on
  case/format-conflict edge cases that `export` itself rejects.
- Strip the gotcha from AGENTS.md and any other docs that warn about it.
- Add a regression test verifying both forms produce identical
  environment after execution.

### Priority: P3 — **closed (2026-05-08)**

- `ShellEnvironmentNamespace.TrySetMember` now routes through
  `ToshRuntime.ExportEnvironmentVariable`, the exact same path used by
  `export NAME = …`. See
  [src/Tosh.Runtime/ShellEnvironmentNamespace.cs](../src/Tosh.Runtime/ShellEnvironmentNamespace.cs).
- Case-insensitive lookup picks the canonical existing name, so
  `$env.path = "…"` updates `PATH`.
- AGENTS.md gotcha removed; both forms are documented as equivalent.

---



## Phase 1 Checklist

- [x] Preserve multiline draft while navigating wrapped lines
- [x] Fix continuation indentation growth in multiline editing
- [x] Add foundational undo/redo support in `LineEditorBuffer`
- [x] Add edit transaction grouping (coalesced typing, completion-accept as single transaction)
- [x] Add explicit draft snapshot/restore model around history traversal
- [x] Add multiline history traversal modifiers (`Alt+Up` / `Alt+Down`) without draft clobber
- [x] Add focused tests for key behavior matrix in multiline + completion contexts

### Priority: P0 — Complete

---

# Language Features & Paradigms (Planned)

## Live / streaming command output ✓ Shipped

Long-running commands now stream rows incrementally to the REPL.
`IDisplaySink` ([src/Tosh.Cli/IDisplaySink.cs](../src/Tosh.Cli/IDisplaySink.cs)) is
implemented by `BufferingDisplaySink`, `StreamingTableSink`, and
`AutoDisplaySink` (the auto-decision wrapper). The CLI consumes pipelines
directly via `await foreach` over `engine.ExecuteAsync(...)`. Append-only
bordered rendering, TTY/streaming-hint decision rule, and Ctrl-C bottom-border
cancellation are all in place.

### Out of scope (still deferred)

- Re-render-on-update for dynamic widths (cursor-up + redraw).
- Alternate-screen `htop`-style live tables (belongs in `Tosh.Tui` widgets, not the shell).
- Pagination interaction (height overflow auto-falls-back to append-only).

### Priority: P1 — closed

## Comprehensions ✓ Shipped

Core comprehensions (list, set, dict, generator) are implemented, along
with Cartesian product (`for x in [1,2], y in [10,20]`), parallel/zip
(`for x in $a || y in $b`), and tuple destructuring (`for (a, b) in $pairs`).

The **bracket form `[...]` is eager** (materialised list); the
**parenthesised form `(...)` is lazy** — it returns a `LazySequence`
that evaluates the body on demand and composes naturally with infinite
sources like ranges, `iterate`, and `recur`.

Verified 2026-05-07:

```tosh
[($x + $y) <| for x in [1,2], y in [10,20]]      # Cartesian → [11, 21, 12, 22]
[$x + $y <| for x in [1,2,3] || y in [10,20,30]] # zip       → [11, 22, 33]
[$a + $b <| for (a, b) in [(1,2),(3,4)]]         # destruct  → [3, 7]
($x ** 2 <| for x in 1..) | first 5              # lazy      → [1, 4, 9, 16, 25]
```

See [ParseGeneratorComprehension](../src/Tosh.Language/Parsing/ToshParser.cs#L6581)
and the `GeneratorComprehensionArgumentSyntax` evaluation path in
[ToshEngine.cs](../src/Tosh.Language/ToshEngine.cs#L5444), which
returns a [`LazySequence`](../src/Tosh.Runtime/LazySequence.cs).

### Priority: P2 — closed

---

## Lazy Infinite Lists & Self-Referential Sequences

Haskell-style lazy evaluation for infinite, self-referential data structures.

### Status (2026-05-07)

Most of this section is already shipped:

- ✓ **`iterate` and `recur` built-ins** — see
  [IterateCommand.cs](../src/Tosh.Stdlib/Functional/IterateCommand.cs)
  and [RecurCommand.cs](../src/Tosh.Stdlib/Functional/RecurCommand.cs).
  Pair with `first` / `take-while` / `take-until` to bound:
  `iterate 1 func(x) => ($x * 2) | first 10`,
  `recur (0, 1) func($a, $b) => $a + $b`.
- ✓ **Lazy `<|` generator form** — the parenthesised comprehension
  `($x ** 2 <| for x in 1..)` produces a [`LazySequence`](../src/Tosh.Runtime/LazySequence.cs).
- ✓ **Infinite ranges** — `1..` is a `ToshRange { IsInfinite: true }`
  recognised by Cartesian product, comprehensions, and the
  `IsLazyOrInfinite` helper in [ToshEngine.cs](../src/Tosh.Language/ToshEngine.cs#L11084).
- ✓ **`lazy` modifier on class properties** — defers initializer until
  first access (see [ToshClassPropertyDefinition.IsLazy](../src/Tosh.Language/ToshClassPropertyDefinition.cs)
  and `_lazyInitialized` cache in [ToshClassInstance.cs](../src/Tosh.Language/ToshClassInstance.cs#L177)).
- ✏ Remaining: `lazy […]` literal syntax with **self-referential bindings**
  (`var fibs = lazy [0, 1, ...zip-with (+) $fibs ($fibs | skip 1)]`).
  Requires thunk/memoised-cell runtime semantics for the inner
  forward-reference to resolve as the same sequence being constructed.
  This is the only true gap; everything else listed under "Syntax
  Proposals" below already works in some form.

### Motivation

Stateful sequences like Fibonacci can't be expressed tersely with comprehensions
alone (comprehensions map/filter over sources; they don't carry accumulator state).
TōSh already has `unfold` for this, but it's verbose:

```tosh
# Current TōSh — functional but dense:
func f(c) => unfold [0, 1] { [$_[0], [$_[1], ($_[0] + $_[1])]] } | first $c
```

With lazy infinite lists and self-reference, this becomes:

```tosh
# Haskell-style self-referential lazy list:
var fibs = lazy [0, 1, ...zip-with (+) $fibs ($fibs | skip 1)]
$fibs | first 20
```

### Syntax Proposals

#### `lazy` keyword for deferred evaluation
```tosh
var naturals = lazy [1, 2, ...]          # inferred continuation (sugar for 1..)
var fibs = lazy [0, 1, ...zip-with (+) $fibs ($fibs | skip 1)]
var powers = lazy [$x ** 2 <| for $x in 1..]   # lazy comprehension (already covered by generator expr)
```

#### `unfold` comprehension shorthand
```tosh
# Terse unfold-style with tuple destructuring:
var fibs = [($a, $b) = (0, 1) <| unfold ($b, $a + $b)]

# Or with a dedicated iterate/recurrence form:
var fibs = iterate (0, 1) func($a, $b) => ($b, $a + $b) | map first
```

#### `recur` / `iterate` built-in
```tosh
# iterate applies a function repeatedly to a seed:
var powers_of_2 = iterate 1 func($x) => $x * 2
# => lazy [1, 2, 4, 8, 16, ...]

# recurrence relation (multi-value seed):
var fibs = recur (0, 1) func($a, $b) => $a + $b
# => lazy [0, 1, 1, 2, 3, 5, 8, ...]
```

### Design Notes

- `lazy [...]` produces a lazily-evaluated sequence (like Haskell lists or C# `IEnumerable`).
- Self-referential bindings require the runtime to support thunks / memoized lazy cells.
- `...expr` inside a lazy list = spread/continuation from a generator expression.
- The `iterate` / `recur` forms are syntactic sugar over `unfold` but much more readable.
- Lazy sequences compose naturally with pipelines: `$fibs | where (_ % 2 == 0) | first 10`.
- Memoization: once a cell is forced, cache the result (avoid recomputation on re-traversal).

### Open Questions

- Should all sequences default to lazy, or only when explicitly marked `lazy`?
- Should `unfold` syntax integrate with comprehension `<|` form?
- How does laziness interact with side-effects in pipelines?
- Should lazy lists support `take-while` natively for termination?

### Priority: P1

---

## Macro System (Runes) — ✓ Shipped

Boo-inspired AST-level macros are implemented under the name **runes**
(distinguishing them from the runtime-evaluated `func`).

### Surface syntax

```tosh
# Definition — looks like a function but receives unevaluated arg thunks:
rune unless(condition, body) {
    if (not $condition) {
        $body
    }
}

# Usage — looks like a built-in:
unless $failed (echo "ok")
with-retry 3 { http get $url }
benchmark "fetch" { http get $url }
dbg ($x + 1)
```

### Implementation

- Parser: `ParseRuneDefinitionStatement` ([ToshParser.cs:2420](../src/Tosh.Language/Parsing/ToshParser.cs#L2420)),
  `LooksLikeRuneDefinition` ([ToshParser.cs:9188](../src/Tosh.Language/Parsing/ToshParser.cs#L9188)).
- Runtime: [`RuneCommand`](../src/Tosh.Language/Bridge/RuneCommand.cs),
  [`RuneDefinition`](../src/Tosh.Language/RuneDefinition.cs),
  [`RuneThunk`](../src/Tosh.Language/RuneThunk.cs) (deferred-evaluation
  argument thunks).
- Built-ins: [`BuiltinRunes.cs`](../src/Tosh.Language/BuiltinRunes.cs)
  ships `dbg`, `unless`, `benchmark`, `with-retry`.
- Rune-level modifiers parsed today: `sealed` (default), `leaky`,
  `fixed`, `lazy`.

### Status: ✓ Closed

The shipped base set (`dbg`, `unless`, `benchmark`, `with-retry`) covers
the common ergonomic cases. Additional runes (`timeout`, `suppress`,
`with`, `transaction`, `parallel`, `watch`) are deliberately left for
user code — the whole point of runes is that user-defined macros are
first-class. Community runes can ship as ordinary `.tosh` modules.

### Priority: P2 — closed

---

## Help Topic Display Profile — `Default` column for Options

The `HelpTopicSummaryRenderer` (added 2026-05-08) renders single
`HelpTopic` instances with a layout modeled on `$tosh`: title bar,
description, and sub-boxes for Usage / Arguments / Options /
Pipeline / Examples / Related, with REPL-style syntax highlighting
on examples.

The Options sub-box now renders a third `Default` column when any
option in the list carries a literal default. Defaults flow through
`CommandOptionAttribute.Default` → `CommandOptionMetadata.Default` →
`HelpOptionInfo.Default` and are surfaced in `to json` /
`--export-command-metadata` and the MCP `command_metadata` tool.

### Work
- ✅ Extend `HelpOptionInfo` with `Default` (string?, optional).
- ✅ Backfill defaults in stdlib `[CommandOption]` metadata where
  obvious (`ls --sort`/`--time`, `ping --count`/`--timeout`/`--size`/
  `--interval`, `head`/`tail -n`, `cut -d`, `http --as`/`--bind`/
  `--index`, `prompt-time --format`, `tui --page-size`).
- ✅ Render a third column in `RenderOptionsSubBox` when any option
  in the list carries a default; keep two-column layout otherwise.
- ✅ Surface the field in JSON `to json` output and the MCP
  `command_metadata` tool.

### Priority: P3 — closed (2026-05-08)

---

## Enhanced LINQ-like Features

### Query Expression Syntax
```tosh
var result = from $p in $products
             where $p.Price > 50
             join $c in $categories on $p.CategoryId == $c.Id
             orderby $p Price descending
             select {| Name: $p.Name, Category: $c.Name, Price: $p.Price |}

var summary = from $sale in $sales
              group $sale by $sale.Region into $g
              select {| Region: $g.Key, Total: ($g | sum _.Amount), Count: ($g | count) |}
```

### New Pipeline Operators

| Operator | Example | Notes |
|----------|---------|-------|
| `join` | `$orders \| join $customers on _.CustomerId == _.Id` | Inner join |
| `left-join` | `$orders \| left-join $customers on _.CustomerId == _.Id` | Left outer join |
| `cross-join` | `$xs \| cross-join $ys` | Cartesian product |
| `group-join` | `$depts \| group-join $employees on _.Id == _.DeptId` | Hierarchical grouping |
| `orderby` | `$data \| orderby _.Score descending, _.Name` | Multi-key sort with direction |
| `distinct-by` | `$items \| distinct-by _.Category` | Keep first per key |
| `aggregate` | `$data \| aggregate {| Sum: (sum _.Val), Avg: (average _.Val) |}` | Multi-aggregate |
| `window-func` | `$rows \| window-func (rank) over (partition-by _.Dept orderby _.Salary)` | SQL window functions |
| `let` | `$data \| let _.Score = (_.Hits / _.Views * 100) \| where _.Score > 75` | Computed columns |
| `into` | `... \| into $varName` | Capture mid-pipeline |
| `tee` | `... \| tee { $_ \| to json > debug.json } \| ...` | Side-effect tap |

### Method-Style Chaining on Collections
```tosh
var top5 = $products
    .where(_.Price > 10)
    .orderby(_.Rating, descending)
    .select(_.Name)
    .take(5)
```

### Priority: P2

---

## Scientific & Mathematical Features

### Matrix / Vector Types
```tosh
var m = matrix [[1,2,3],[4,5,6],[7,8,9]]
var v = vector [1, 2, 3]

$m * $m                         # matrix multiplication
$m | determinant
$m | inverse
$m | transpose                  # already exists
$m | eigenvalues
$v | magnitude                  # 3.7416...
$v | normalize
dot $v1 $v2
cross $v1 $v2
```

### Complex Numbers
```tosh
var z = 3 + 4i
$z | magnitude                  # 5.0
$z | phase                      # 0.9272 rad
$z | conjugate                  # 3 - 4i
$z | to-polar                   # (5.0, 0.9272)
```

### Symbolic Math (aspirational)
```tosh
var expr = sym "x^2 + 2*x + 1"
$expr | differentiate "x"       # 2*x + 2
$expr | integrate "x"           # x^3/3 + x^2 + x
$expr | factor                  # (x + 1)^2
$expr | solve "x"               # [-1]
```

### Units System Expansion
```tosh
var force = 10kg * 9.8m/s^2     # 98 N (auto-derive)
var energy = $force * 5m        # 490 J
$energy | convert-to kWh
100°C | convert-to °F           # 212°F
```

### Priority: P2 (matrix/vector, complex), P3 (symbolic, units)

---

## Relativistic Programming Concepts

Inspired by relativistic programming (RCU, causal consistency) — design
concurrent primitives that tolerate ordering conflicts rather than preventing them.

### Parallel Pipelines with Relaxed Ordering
```tosh
parallel {
    http get "https://api-a.com/data" | as $a
    http get "https://api-b.com/data" | as $b
    http get "https://api-c.com/data" | as $c
} | merge --causal
```

### Structured Concurrency
```tosh
# First result wins, others cancelled:
race {
    http get "https://primary.api/data"
    http get "https://backup.api/data"
}

# Wait for all, collect successes and failures:
settle {
    ping host-a
    ping host-b
    ping host-c
} | where _.Status == "fulfilled"
```

### Reactive / Observable Pipelines
```tosh
var config = watch "config.yaml" --snapshot
# Consumers see consistent snapshots even as the file changes
```

### Shared State with Atomic Operations
```tosh
shared var $counter = 0
parallel-each $urls func($url) {
    $result = http get $url
    atomic { $counter = $counter + 1 }
}
```

### Stream Processing with Temporal Windows
```tosh
tail -f /var/log/app.log
    | parse "{timestamp} {level} {message}"
    | window 5s sliding 1s
    | group-by _.level
    | summarize {| Count: count, Rate: (count / 5) |}
```

### Priority: P3

---

## Boo-Inspired Ergonomics

### Slicing syntax ✓ Shipped
```tosh
$list[1:3]                      # elements 1..2
$str[:-1]                       # all but last char
$arr[::2]                       # every other element
```
Bracket-slice notation is in. `get` also accepts `ToshRange` for the
pipeline form (`... | get 2..5`, see [GetCommand.cs](../src/Tosh.Stdlib/Pipeline/GetCommand.cs#L40)).

### `is` / `is a` operator ✓ Shipped
```tosh
if $obj is Point { echo $obj.X }
if $obj is a Point { echo $obj.X }
```
No separate `isa` keyword is planned — `is` (and the multi-word
`is a` / `is not` / `is in` / `is not in`) covers it.

### `unless` keyword ✓ Shipped

Implemented as a built-in rune, not a parser keyword:

```tosh
unless $failed (echo "ok")
unless false (echo 5)              # → 5
```

See [BuiltinRunes.cs](../src/Tosh.Language/BuiltinRunes.cs) and the
macro/rune system above.

### Callable values ✓ Shipped

Typed-parameter lambdas with optional return-type annotations can be
bound to variables and invoked with `$name(args)`:

```tosh
var test = func(x: bool) -> bool { return (not $x) }
$test(true)                         # → false

var dbl = func(x: int) -> int => ($x * 2)
$dbl(7)                             # → 14
```

Parameter type annotations (`x: bool`, `n: int`, etc.) and the new
lambda-level return type annotation (`-> bool`) both flow through
`AnonymousFunctionArgumentSyntax.ReturnTypeName` into the runtime's
`CreateFunctionDefinition`. A dedicated `func(int) -> bool` first-class
signature *type* (for storing in a variable annotation) is not yet a
parser construct, but the practical capability is in.

### Postfix conditionals ✓ Shipped

```tosh
return $x if ($x > 5)               # early return when condition holds
break unless ($i < 5)               # break when condition fails
continue if ($i % 2 == 0)
throw "negative" if ($x < 0)
yield $row if ($row.Active)
```

Applies to `return`, `break`, `continue`, `throw`, and `yield`.
Implemented as a syntactic wrapper in the parser: when one of these
statements is followed (on the same line, no boundary) by `if <cond>`
or `unless <cond>`, the statement is wrapped in an
`IfStatementSyntax`. `unless` places the inner statement in the else
branch so the condition expression is preserved verbatim. See
`TryWrapPostfixConditional` in
[ToshParser.cs](../src/Tosh.Language/Parsing/ToshParser.cs).

### Priority: P2 — closed

---

## Miscellaneous Questions

- Should we allow user-defined collection types?
- Should comprehensions be first-class values (passable as arguments)?
- Should macros be able to define new comprehension forms?
- Should `<|` be overloadable for custom monadic comprehensions?

---

### Priority Legend

| Level | Meaning |
|-------|---------|
| P1 | Highest priority — core language improvement |
| P2 | High priority — major ergonomics / extensibility |
| P3 | Advanced — scientific, concurrency, or aspirational |

---

## Class & Type System

| Feature | Status |
|---------|--------|
| Generics on classes / type aliases | ✓ Shipped — `class Stack<T>` / `type Pair<A, B> = ...` parse via `ParseTypeParameterList` ([ToshParser.cs:4637](../src/Tosh.Language/Parsing/ToshParser.cs#L4637)). End-to-end runtime support landed 2026-05: type-argument resolution at instantiation, strict no-coercion enforcement of constructor / method / property bindings, return-type substitution, inheritance binding propagation through `extends Base<T>`, generic-class annotations recognised by `IsKnownAnnotatedType`, and compiled-mode dispatch via `ToshHost.NewObject(typeName, bareTypeName, string[] typeArgs, object?[] args)`. See [GenericClassTests.cs](../tests/Tosh.Tests/GenericClassTests.cs). |
| Function / method overloading | ✓ Shipped — same-name funcs with distinct arities/typed signatures merge through `OverloadedFunctionCommand` ([src/Tosh.Language/Bridge/OverloadedFunctionCommand.cs](../src/Tosh.Language/Bridge/OverloadedFunctionCommand.cs)) and `ToshClassDefinition` overload resolution. |
| Operator overloading | ✓ Parser-level wired — `IsOverloadableOperatorToken` lets classes declare `+`, `-`, `*`, `/`, `[]`, etc. as method names ([ToshParser.cs:4903](../src/Tosh.Language/Parsing/ToshParser.cs#L4903)). |
| Interface default methods | Open — allow method bodies in interfaces as default implementations. |

## Unix Command Parity

### Adapters

| Command | Remaining gaps |
|---------|----------------|
| `ip` | remaining: monitor, stats, macsec, l2tp, xfrm, fou, ila, ioam, seg6 |

## TUI Platform

- Build future full-screen tools on the shared runtime
- Form editors and structured input widgets

## AI Companion Interop

### Tools
- ✓ VS Code extension: syntax highlighting, bracket matching, comment toggling for `.tosh` files — ships at [editor/vscode/tosh.tosh-lang](../editor/vscode/tosh.tosh-lang).
- ✓ MCP server enhancements: `run_snippet` and `explain_error` tools shipped in [src/Tosh.Mcp/ToshMcpServer.cs](../src/Tosh.Mcp/ToshMcpServer.cs); `suggest_command` is still open.
- Structured error output mode for machine-consumable diagnostics

### Documentation
- Language reference: formalize the existing LaTeX spec into a living reference AI agents can query
- Expand `AGENTS.md` as the language and shell evolve

### Project Memory
- Create a persistent / scalable project memory storage that can be used by any AI Companion

---

# First-Class .NET Citizenship

Derived from the 2026-05-06 codebase audit. Companion to
[FIRST_CLASS_DOTNET_STATUS.md](FIRST_CLASS_DOTNET_STATUS.md) (full roadmap of
14 items) and [SPEC_STATUS.md](SPEC_STATUS.md) Gap §10. The waves below are
the execution order; each task lands with conformance rows and a doc update.

## Wave 1 — "a tosh DLL feels like a .NET DLL"

Reassessed 2026-05-06 with reflection probes against `--compile` output:

- **Async `Task<T>` for user funcs (originally Wave 1 #1):** *Not a gap.*
  Typed funcs already emit as sync `T`-returning CLR methods
  ([BoundUnitEmitter.cs:4892](../src/Tosh.Compiler/BoundUnitEmitter.cs#L4892)).
  There is no `async func` surface syntax, and reflection on a probe DLL
  shows ordinary sync signatures (`add(Int32, Int32) -> Int32`). The audit
  finding was a misread of the pipeline-stage code path. **De-scoped.**
- **Single typed CLR method per func (originally Wave 1 #2):** *Already
  done.* Reflection on an overloaded probe (`func pick(a: int)` and
  `func pick(a: int, b: int)`) shows exactly two typed methods, no
  `object`-shaped peer shim. **De-scoped.**
- **Metadata-only reference assemblies (Wave 1 #3):** *Still real.* This
  is the only remaining Wave 1 item.

### Metadata-only reference assemblies

`--emit-refasm` already stamps `[ReferenceAssembly]` but ships fat method
bodies. Strip bodies in the refasm pass so `.ref.dll` is a real contract
surface (prerequisite for ABI v1 work and library NuGet packaging).

- [x] Replace body-bearing emit with metadata-only emit in the refasm pass at `BoundUnitEmitter.cs:697`.
- [x] Verify metadata parity between implementation and refasm via reflection diff test.
- [x] Conformance: C# direct compile against refasm; runtime resolution against the implementation DLL. _2026-05-07: validated via the A3 cross-language smoke test — a C# console with `<PackageReference Include="GreeterLib" />` compiled against `ref/net10.0/GreeterLib.dll` and at runtime resolved `lib/net10.0/GreeterLib.dll`, calling the tosh-defined `greet("C# consumer")` and printing `Hello, C# consumer!`._
- Tracks: [FIRST_CLASS_DOTNET_STATUS.md](FIRST_CLASS_DOTNET_STATUS.md) item 8.
- Priority: P0.

## Wave 2 — "ship to NuGet"

The distribution gap. Today only `Tosh.Sdk` is packable; user libraries
have no NuGet path.

### Standalone library NuGet packages

- [x] Set `IsPackable=true` and pack metadata for `Tosh.Runtime`. _2026-05-07: shared metadata centralised in `Directory.Build.props`; nupkg lands in `artifacts/packages/`._
- [x] Set `IsPackable=true` and pack metadata for `Tosh.Compiler.Runtime`. _2026-05-07: ProjectReferences (`Tosh.Runtime`, `Tosh.Stdlib`, `Tosh.Language`) correctly serialise as NuGet `<dependency>` entries — also packed transitively._
- [x] Validate restore from a clean machine. _2026-05-07: validated end-to-end via the A2 `dotnet new tosh-lib`/`tosh-app` smoke test — a fresh project pointed at `artifacts/packages/` as a NuGet feed restored Tosh.Sdk + Tosh.Runtime + Tosh.Stdlib + Tosh.Language + Tosh.Compiler.Runtime + Tosh.Tui and produced a working DLL/apphost._
- Tracks: [FIRST_CLASS_DOTNET_STATUS.md](FIRST_CLASS_DOTNET_STATUS.md) item 9 (audit follow-up).
- Priority: P1.

### `dotnet new` templates

- [x] `dotnet new tosh-lib` template (library). _2026-05-07: ships in `Tosh.Templates`; defaults to `<OutputType>Library</OutputType>` with `ToshEmitReferenceAssembly=true`._
- [x] `dotnet new tosh-app` template (executable). _2026-05-07: ships in `Tosh.Templates`; defaults to `<OutputType>Exe</OutputType>` with apphost._
- [x] Smoke test: `dotnet new tosh-lib && dotnet build && dotnet pack` succeeds. _2026-05-07: lib builds to `MyLib.dll` + `MyLib.ref.dll`, app builds and runs (`Hello from a tosh-app!`). `dotnet pack` for `.toshproj` is A3 work and tracked there._
- Tracks: [FIRST_CLASS_DOTNET_STATUS.md](FIRST_CLASS_DOTNET_STATUS.md) item 9 (audit follow-up).
- Priority: P1.

### `dotnet pack` for `.toshproj`

- [x] Wire a `<ToshPack>` / `dotnet pack` flow in `Tosh.Sdk` so user-authored `.toshproj` libraries produce a NuGet consumable from C#. _2026-05-07: `Tosh.Sdk` now defaults `IsPackable=true` for `OutputType=Library` and emits a wrapper csproj under `obj/<cfg>/<tfm>/pack-wrapper/` that invokes `Microsoft.NET.Sdk` Pack to produce a nupkg with `lib/<tfm>/<asm>.dll`, `ref/<tfm>/<asm>.dll`, and transitive `<dependency>` entries for `Tosh.Runtime` / `Tosh.Stdlib` / `Tosh.Language` / `Tosh.Compiler.Runtime`. Forwards user `<PackageReference>` items, stamps `ToshRuntimeVersion` into the packed `Sdk.props`._
- [x] Cross-language smoke test: pack a tosh library, reference it from a C# project via `<PackageReference>`, call into it. _2026-05-07: `dotnet new tosh-lib -n GreeterLib && dotnet pack` produced `GreeterLib.1.0.0.nupkg`; a separate C# console added it via `dotnet add package GreeterLib`, restored Tosh.* runtime deps from the local feed, and successfully invoked `Greeter.greet` and the top-level `greet` over reflection — matching the conformance row above._
- Depends on Wave 1 #3 (real refasm).
- Priority: P1.

### Reproducibility, SourceLink, symbol packages

- [x] Turn on `Deterministic`, `ContinuousIntegrationBuild`, `EmbedUntrackedSources` in `Directory.Build.props`. _2026-05-07: `Deterministic=true` always, `ContinuousIntegrationBuild=true` when any of `CI`/`TF_BUILD`/`GITHUB_ACTIONS` is set, `EmbedUntrackedSources=true` always, `PublishRepositoryUrl=true` so the nuspec carries the commit-pinned `<repository url=… commit=…>`._
- [x] Wire SourceLink (`Microsoft.SourceLink.GitHub` or equivalent) across all `Tosh.*` projects. _2026-05-07: `Microsoft.SourceLink.GitHub` 8.0.0 added as a global build-only `<PackageReference>` in `Directory.Build.props`. `GitRepositoryRemoteName=github` overrides the default `origin` (which is a private host on dev machines). Verified: PDBs now contain `{"documents":{"/home/komrad/projects/tosh/*":"https://raw.githubusercontent.com/.../<commit>/*"}}`._
- [x] Decide strong-naming policy (sign with project SNK, or explicitly opt out and document). _2026-05-07: deliberately **not** strong-named. Documented in the `Directory.Build.props` block — strong naming would force every consumer (tosh-lib NuGets, plugins) to either sign too or `InternalsVisibleTo` dance around it, with no meaningful security benefit for an OSS shell. Revisit if a Windows/Defender or GAC requirement ever appears._
- [x] Emit `.snupkg` symbol packages alongside implementation packages. _2026-05-07: `IncludeSymbols=true; SymbolPackageFormat=snupkg` plumbed via `Directory.Build.targets` (so `<IsPackable>` is settled before evaluation). `dotnet pack Tosh.slnx` now produces 5 `.snupkg` files (one per C# library) next to the 7 `.nupkg`s. `Tosh.Sdk` and `Tosh.Templates` are content-only and explicitly opt out._
- Tracks: [FIRST_CLASS_DOTNET_STATUS.md](FIRST_CLASS_DOTNET_STATUS.md) item 9 (audit follow-up).
- Priority: P2.

## Wave 3 — "tooling parity"

### LSP capability gaps

The binder and symbol resolution already support these; the LSP just needs
to advertise and route them.

- [x] `textDocument/references` (find all references). _2026-05-06: scope-aware via `DeclarationIndex.FindReferences`; covers variables, function overloads, classes/modules/enums/records; respects shadowing._
- [x] `textDocument/rename` + `textDocument/prepareRename`. _2026-05-06: `BuildRenameEdits` returns a `WorkspaceEdit`; `PrepareRename` returns the editable range at the cursor (strips leading `$` for variable refs)._
- [ ] `textDocument/formatting` and `textDocument/rangeFormatting` wired to a deterministic tosh formatter. _Deferred — see **Source Formatter** below._
- Tracks: [FIRST_CLASS_DOTNET_STATUS.md](FIRST_CLASS_DOTNET_STATUS.md) item 12 (audit follow-up).
- Priority: P1.

### Source Formatter

A deterministic source-code formatter for `.tosh` files. **Phase 1
shipped 2026-05-07.** Lives at
[`src/Tosh.Language/Formatting/ToshFormatter.cs`](../src/Tosh.Language/Formatting/ToshFormatter.cs);
exposed as the `format` builtin
([`src/Tosh.Language/Bridge/Scripting/FormatCommand.cs`](../src/Tosh.Language/Bridge/Scripting/FormatCommand.cs)).

#### Phase 1 — shipped

- [x] Pure round-trip: re-renders top-level structure
      (statement separators, indentation, blank lines, brace placement,
      keyword spacing) and uses original-source slices for inner
      expressions and unsupported statement kinds, so output is
      always valid.
- [x] Style: 4-space indent, single blank line between top-level
      declarations, opening braces same line, no trailing semicolons.
- [x] Idempotent: `format(format(x)) == format(x)` (verified by
      `Format_is_idempotent` test).
- [x] Coverage: `var`/`const`/`export`/`global`/`shy` declarations,
      pipelines, `return`/`yield`/`break`/`continue`/`throw`,
      `if`/`else`, `for`, `while`, `func` definitions; brace-delimited
      decls (class/enum/record/struct/interface/trait/union/module/rune)
      slice through to the matching `}` to work around a parser-side
      span quirk.
- [x] CLI: `format <path>` (rewrite in place), `format --check <path>`
      (non-zero exit if any file would change), `format --stdout <path>`,
      `format --diff <path>`, `format -` (read stdin).
- [x] Tests: [`tests/Tosh.Tests/FormatterTests.cs`](../tests/Tosh.Tests/FormatterTests.cs)
      (9 cases — var, func, if/else, blank-line separators, class
      closing-brace, idempotency, parse-error fallback, trailing
      newline, postfix conditionals).

#### Phase 2 — open

- [ ] Real-AST formatting for inner expressions (drops the
      source-slice fallback) so spacing inside arithmetic,
      member-access, function calls, etc. is normalised.
- [x] Comment preservation. Lexer captures every `#` line comment
      (full-line + trailing) into a parallel `LineComment` list
      surfaced via `ParseResult.LineComments`; the formatter flushes
      pending full-line comments before each statement (preserving
      blank-line gaps between groups) and re-attaches trailing
      same-line comments to the line they came from. Works inside
      block bodies via the `WriteStatement` flush hook.
- [x] Structural coverage for `try`/`catch`/`finally`, `switch`/`case`,
      and variable/member assignments (`$x = expr`, `$x += expr`,
      `$obj.field = expr`). `match` expressions and lambda bodies
      still take the source-slice path for now.
- [x] Wire LSP `textDocument/formatting` and `textDocument/rangeFormatting`
      (range currently delegates to whole-document formatting) plus
      `documentFormattingProvider` / `documentRangeFormattingProvider`
      capabilities.
- [ ] `match` arms and lambda bodies — currently slice-fallback;
      promote to AST emit so nested decls reformat consistently.
- Priority: P2.

### XML doc comments (CLR-visible documentation)

Tosh already keeps `##` lines as `DocComment` tokens with structured
`@param`/`@returns`/`@example`/`@throws`/`@see`/`@since`/`@deprecated`
tags. Make them surface to other .NET languages by emitting an ECMA-334
sidecar `<assembly>.xml` next to the compiled `.dll` so Roslyn, Rider,
IntelliSense and DocFX pick them up the same way they would for a
C#-authored library.

No new tosh syntax is required — `## <summary>...</summary>` already
parses today because the lexer keeps the post-`## ` text verbatim. The
work is on the emit + parsing-shape side.

- [ ] Extend [`DocComment`](../src/Tosh.Language/Parsing/DocComment.cs)
      to also capture **raw XML pass-through** lines (lines that begin
      with `<` after stripping the `## `) into a new
      `IReadOnlyList<string> XmlBlocks` member. Keep the existing
      `@`-tag parsing for ergonomic authoring; both can coexist on a
      single declaration.
- [ ] Auto-translate `@`-tags to standard XML on emit:
      `Description` → `<summary>`, `@param=name desc` →
      `<param name="name">desc</param>`, `@returns` → `<returns>`,
      `@example` → `<example><code>…</code></example>`, `@throws T msg`
      → `<exception cref="T">msg</exception>`, `@see ref` →
      `<seealso cref="ref"/>`, `@since v` → `<remarks>Since v.</remarks>`.
      `@deprecated` is already a CLR concern — keep emitting
      `[ObsoleteAttribute]` and additionally translate the message into
      a `<remarks>` block.
- [ ] New `XmlDocWriter` next to
      [`BoundUnitEmitter`](../src/Tosh.Compiler/BoundUnitEmitter.cs)
      that walks types/methods/properties/fields/events as they are
      defined and accumulates `<member name="…">…</member>` entries
      keyed by ECMA-334 doc-IDs (`T:Ns.Type`,
      `M:Ns.Type.Method(System.Int32)` with mangled parameter type
      names, `P:`, `F:`, `E:`). Generic arity uses the ECMA backtick
      form (`` `1 ``).
- [ ] Wire writer flush into
      [`ToshPublisher`](../src/Tosh.Compiler/ToshPublisher.cs) so the
      `.xml` lands beside the `.dll` in `bin/<config>/<tfm>/` and is
      copied to the publish output and the ref-asm package layout
      (`lib/<tfm>/Foo.xml` + `ref/<tfm>/Foo.xml` for nupkg).
- [ ] Stamp `<doc><assembly><name>{asm}</name></assembly>` header.
      Honour CLR's "no XML comment" warning suppression — emit only
      `<members>` entries for declarations that actually had `##`.
- [ ] Tests:
      1. Parse-level: `## <summary>desc</summary>` + `## @param=x foo`
         on the same func produces both an `XmlBlocks` entry and a
         `Parameters[x]` entry without losing either.
      2. Emit-level: compile a `library`-profile script with `func
         add(a: int, b: int) -> int` carrying `## adds two numbers`
         and `## @param=a first` and `## @returns sum`; assert the
         emitted `.xml` has `<member
         name="M:…add(System.Int32,System.Int32)">` containing
         `<summary>` + `<param name="a">` + `<returns>`.
      3. Roundtrip: a C# consumer hovers the tosh-emitted method and
         Roslyn surfaces the summary (xunit + Roslyn workspace).
- [ ] Doc-ID generator must respect mangling rules from `CLR_ABI_v1`
      (Tosh-original-name → CLR name) so the IDs match the methods
      Roslyn sees, not the source-language identifiers.
- Tracks: [FIRST_CLASS_DOTNET_STATUS.md](FIRST_CLASS_DOTNET_STATUS.md).
- Priority: P2 — nice ergonomics for downstream .NET consumers, no
  blocker for ABI v1.

### CLR ABI v1 spec document

Lock the public rules once Waves 1–2 produce a stable shape. This is the
"we promise not to break this" artefact downstream consumers need.

- [x] Draft ABI v1 covering: assembly identity, type/method naming and mangling, visibility mapping, overload rules, library vs executable mode, attribute set (`ToshOriginalNameAttribute`, `ToshTypeAttribute`), nullability/refinements/dynamic erasure rules. _DONE 2026-05-07. Spec lives at [`docs/CLR_ABI_v1.md`](CLR_ABI_v1.md), normative + frozen at v1.0. Emitter changes: `guarded`→`Family`, `local`→`Assembly` on fields & methods; `[assembly: ToshAbi(1)]` stamp; `ParameterAttributes.HasDefault` + `SetConstant` on typed params with literal defaults; `[ParamArrayAttribute]` on rest params with array CLR type._
- [x] ABI test set: reflection, Roslyn C# compile against refasm, `ProjectReference`, `PackageReference`, runtime `ToshHost` invocation. _DONE 2026-05-07 via the GreeterLib cross-language pack+consume smoke test (Wave 2 above): C# consumer with `<PackageReference Include="GreeterLib" />` compiles against refasm and runs against impl. Reflection / `ProjectReference` paths are exercised in the existing test suite (2315/2315 pass)._
- Tracks: [FIRST_CLASS_DOTNET_STATUS.md](FIRST_CLASS_DOTNET_STATUS.md) item 5.
- Priority: P2.

### `--profile=library` alias

Promote `runtime` to the official redistributable-library contract: alias
`--profile=library` to `runtime` plus typed-public-signature enforcement
plus metadata-only refasm. Document `permissive`-compiled assemblies as
*executable bundles*, not libraries.

- [ ] Add the alias in `CliInvocationResolver`.
- [ ] Add an SDK property `<ToshLibraryMode>true</ToshLibraryMode>` shorthand.
- [ ] Spec update: extend `\part{Compilation}` with a "library mode" sub-section.
- Tracks: [FIRST_CLASS_DOTNET_STATUS.md](FIRST_CLASS_DOTNET_STATUS.md) item 3 (audit follow-up).
- Priority: P2.

## Deferred (post-wave)

- **Tier-3 reduction** ([FIRST_CLASS_DOTNET_STATUS.md](FIRST_CLASS_DOTNET_STATUS.md) item 4). High-leverage long-term, high-cost short-term. Revisit after Waves 1–2.
- **Rune model decision** ([FIRST_CLASS_DOTNET_STATUS.md](FIRST_CLASS_DOTNET_STATUS.md) item 13). Research-shaped; defer.
- **Native interop expansion** beyond primitives (struct marshalling, callbacks, `Span<T>`, `[MarshalAs]`). Defer until a real user need surfaces.

---

## Completed

Historical "✓ Shipped" entries through 2026-05-07 have been moved to
[BACKLOG-archive.md](BACKLOG-archive.md) to keep this file focused on
open work. The archive preserves the full text of each completed item
(macros, generics, comprehensions, slicing, IL emitter spike, streaming
display sinks, VS Code extension, MCP tools, AOT performance findings,
etc.).
