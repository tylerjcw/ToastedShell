# ToastScript Stabilization Plan

**Status:** Active  
**Started:** July 25, 2026  
**Scope:** ToastScript language semantics, interpreter/compiler parity,
runtime safety, parser composability, and language tooling

This document is the source of truth for the stabilization work that
followed the July 2026 language review. It records:

- the semantic behavior TōSh intends to guarantee;
- confirmed defects and their priority;
- the order in which they will be fixed;
- the acceptance tests required to close each item; and
- larger language changes that must not be slipped into an unrelated
  bug fix.

The specification remains the user-facing language contract. When this
plan intentionally changes that contract, the implementation, executable
conformance tests, specification, help, LSP, and MCP metadata must change
together.

## Why Stabilization Comes First

ToastScript already has a broad and compelling surface: structured
pipelines, CLR interop, functions, classes, generics, records, modules,
concurrency, an interpreter, a compiler, and language services.

The principal risk is now duplicated semantics. Truthiness, operators,
assignment, callable binding, class construction, and language metadata
are each implemented in more than one place. Small syntax or execution
mode changes can therefore change program meaning.

Until the P0 and P1 work below is complete:

1. Safety and semantic convergence take priority over new syntax.
2. A fix is incomplete if it only repairs one execution surface.
3. New behavior requires an executable conformance example.
4. Optimized compiler paths may specialize only when they preserve the
   canonical runtime semantics.

## Working Semantic Decisions

These decisions are accepted as the working direction for stabilization.
If one is reconsidered, add a dated entry to the decision log rather than
silently changing an implementation.

| Area | Decision | Status |
|---|---|---|
| Truthiness | Keep the broad truthiness model documented by the specification. Implement it once in `Tosh.Runtime` and use it from conditions, logical operators, predicates, the compiler host, and tooling. | Accepted |
| Function output | Functions are stream producers. `return` terminates the function and may emit a final value; it does not erase values already emitted. `yield` remains explicitly streaming. | Accepted |
| Default parameters | Evaluate omitted defaults at call time in lexical scope, left-to-right, with earlier bound parameters visible. | Accepted |
| Casts | `as` is a safe cast and returns `null` on failure. `cast` is the explicit throwing/converting operation. | Accepted |
| Immutability | Add `let` for a runtime immutable local. Reserve `const` for expressions that satisfy the language's constant-expression rules. | Direction accepted; design required |
| Operator fallback | Reusing the right operand's ordinary overload is not valid for noncommutative operators. Design reverse/static two-operand hooks before changing public syntax. | Design required |
| Pipeline shape | A pipeline is an asynchronous stream of object values. Collection values should not require cardinality lookahead to decide whether they are values or streams. Any compatibility-affecting change requires an RFC and migration note. | Design target |
| Comparison | Ordering is strict and symmetric: booleans are unordered, a string orders only against a string, and conversion is attempted in both directions so `a < b` and `b > a` always agree. Equality keeps conversion-backed coercion but has no case-insensitive `ToString` fallback. | Accepted |
| Chained comparison | `a < b < c` is real chaining, desugaring to `(a < b) and (b < c)` with each middle operand evaluated once and short-circuit preserved. | Accepted |
| `$this` in defaults | A method parameter default may reference `$this`. A constructor default may not: it would observe an instance whose properties are not yet initialised, so it gets a targeted diagnostic instead. | Accepted |
| Breaking changes | TōSh has two users and no external consumers, so backward compatibility is not a constraint. Where a filed defect is caused by the grammar rather than the implementation, the grammar may change. Each such change lands with its specification, examples, and test updates in the same slice. | Accepted 2026-07-26 |
| Intrinsic literals | Temporal literals use only the documented exact ISO forms; canonical IPv4 requires four decimal octets. Storage suffixes are typed in expression context but remain strings as raw command arguments. `ToshRange` remains signed 32-bit integer-only. | Accepted |
| Brace delimiters | In ordinary expression and command-argument grammar, `{ ... }` is a block. Records use `{| ... |}`, dictionaries `{% ... %}`, and sets `{: ... :}`. Grammar-owned structural groups such as member, arm, destructuring, accessor, and projection braces remain plain braces. | Accepted 2026-07-28 |

### Truthiness policy

`TS-P1-01` makes the specification's broad truthiness table executable:

1. `ToshTruthiness.IsTruthy` in `Tosh.Runtime` is the sole semantic
   primitive. Existing `OperatorEvaluator.ToBoolean` and compiler-host
   helpers remain compatibility wrappers that delegate to it.
2. Null and boolean false are falsy. Numeric zero is falsy; non-zero is
   truthy. `float`, `double`, `Half`, and complex values containing NaN
   are falsy; infinities are truthy.
3. Empty strings and collections are falsy. Count-bearing collections
   use their count. A general synchronous `IEnumerable` is probed for at
   most one item and its enumerator is disposed; this can observably
   advance a deliberately single-pass enumerable.
4. Every other non-null value is truthy.
5. Conditions, comprehension and match guards, event `when` guards,
   `not`/`and`/`or`, and standard-library predicates use the same
   primitive in interpreted and compiled execution. Logical operators
   still return booleans and retain short-circuit evaluation.
6. Explicit type conversion is not truthiness:
   `TypeConversion.TryConvert(..., typeof(bool), ...)` remains the
   conversion path for typed values. Refinement `where` clauses retain
   their documented strict-boolean contract, while refinement coercion
   guards use broad truthiness.
7. The type checker accepts every value type in a truthiness context and
   must not steer the compiler back toward boolean-only conditions.

### Class-construction policy

`TS-P0-03` adopts one construction protocol for interpreted and compiled
classes:

1. Each class layer selects and binds its own constructor arguments.
2. The layer initializes its immediate base before its own properties and
   constructor body. Recursion therefore produces base-to-leaf order.
3. `extends Base(args)` is the canonical constructor initializer. An
   explicitly empty `extends Base()` is preserved rather than treated as
   an absent clause.
4. A leading `$super(args)` remains supported as the body-form
   alternative. It is lifted to the constructor-initializer phase and must
   be the first executable statement.
5. A class may use the header form or the body form, never both. Direct
   duplicate and non-leading initializers produce structured diagnostics
   before a constructor body runs; a nested or otherwise dynamic
   `$super(...)` cannot reinitialize a completed base layer.
6. With neither form, a matching zero-argument base constructor is invoked
   implicitly. A base that cannot be constructed without arguments
   produces a missing-initializer diagnostic.
7. Every class layer and CLR base is initialized at most once.

This intentionally changes two accidental behaviors: parameterless base
constructor bodies that were previously skipped now run, and base
constructors no longer observe already-initialized derived properties.
Programs that redundantly combine `extends Base(args)` with
`$super(args)` must remove the `$super` call.

### Defer-unwinding policy

`TS-P0-05` adopts one defer protocol for interpreted and compiled
execution:

1. A `defer` is registered only when execution reaches its statement.
   An unreachable declaration does not participate in unwinding.
2. When the enclosing scope exits, every registered cleanup is attempted
   exactly once in reverse registration order. Values produced by a
   cleanup block are discarded; its side effects remain observable.
3. Normal completion, `return`, `break`, `continue`, cancellation, and a
   body failure are retained as the pending exit while cleanup runs.
   Values produced by the ordinary body before that exit are preserved;
   the mere presence of `defer` does not erase them.
4. One cleanup failure never prevents an earlier registered cleanup from
   running. Failure order is deterministic: the body failure first, when
   present, followed by cleanup failures in their actual LIFO execution
   order.
5. With no cleanup failure, the pending exit resumes unchanged. A sole
   failure retains its original exception identity and stack. Competing
   failures use `ToshDeferAggregateException`; nested defer aggregates are
   flattened without flattening unrelated aggregate or diagnostic types.
6. A cleanup failure supersedes a pending `return`, `break`, or
   `continue`; the jump is not itself included in the failure set.
7. Cleanup is shielded from the cancellation token that caused the scope
   to exit, so every reached cleanup receives one opportunity to run.
   When cancellation is the primary exit, the original
   `OperationCanceledException` remains outward-facing and carries any
   ordered cleanup failures through the public defer-failure metadata.
8. For compatibility, `return`, `break`, and `continue` raised by a
   cleanup block are local to that cleanup and suppressed. They cannot
   replace the enclosing scope's pending exit.

The public CLR carrier and helper surface is documented in
`docs/CLR_ABI_v1.md`. Unhandled competing failures become ordered
diagnostics; TōSh `catch` and CLR callers can still inspect the original
failure objects.

### Callable default-binding policy

`TS-P1-05` adopts one default-binding protocol for free functions,
lambdas, class methods, and constructors in both execution modes:

1. A default expression is evaluated only when the caller supplies
   neither a positional nor a named argument for its parameter, and it
   is re-evaluated on every such call.
2. Defaults evaluate in the callable's lexical environment — the same
   environment the body observes — never in the caller's argument list.
3. Evaluation is left-to-right. A default sees every earlier parameter
   that is already bound (explicitly or by an earlier default); it can
   never see its own or a later parameter. A forward reference fails
   with the ordinary unknown-variable diagnostic.
4. Overload selection never evaluates a default: the binder records
   pending defaults per candidate, and only the single winning overload
   applies them. Losing and ambiguous candidates produce no default
   side effects.
5. An evaluated default passes through the same annotation and
   refinement conversion as an explicitly supplied argument. An
   unconvertible default fails with
   `tosh.runtime.parameter_default_conversion_failed`.
6. In compiled mode, default expressions are lowered inside the
   callable's scope (recording outer references as captures) and are
   emitted into the callable body behind the missing-argument sentinel.
   A compiled callable that carries optional, defaulted, or rest
   parameters receives packed arguments, so its prologue first resolves
   named-argument wrappers into their positional slots
   (`ToshHost.NormalizePackedArguments`). Only then does the positional
   prologue bind, which is what lets `f(1, c = 99)` leave `b` to its
   declared default. Unknown names remain ignored there, matching the
   interpreter, until `TS-P1-06` diagnoses them.
   Classes whose constructors or methods declare optional, defaulted,
   or rest parameters cannot be represented by a fixed-arity CLR shell
   and remain Tier-3 source replay, so compiled construction and
   dispatch resolve through the engine's binder. The `runtime` and
   `pure` profiles reject those class shapes with the ordinary tier
   diagnostic instead of mis-executing them.

This intentionally changes one accidental behavior: a free-function
default previously truncated a multi-value pipeline to its first value;
defaults now use the standard value-context collapse (none → null, one
→ the value, several → an array), matching compiled emission.

### Async class-execution policy

`TS-P0-06` makes the interpreter's class protocol asynchronous end to
end:

1. Instance methods, static methods, constructors, base construction,
   properties, refinements, operators, indexing, enumeration, bulk
   member access, and thrown-error metadata receive the active execution
   token.
2. The interpreter selects the asynchronous runtime protocol for
   `new`, fluent calls, legacy `call`/`call-method`, member access,
   destructuring, spread, equality, membership, switch matching, and
   interpolation, operator, and diagnostic string conversion.
3. A cancelled lazy initializer does not commit its value or initialized
   state. A later access may retry it; recursive re-entry remains an
   error.
4. Synchronous runtime-interface members remain compatibility adapters
   for existing CLR consumers and the synchronous compiled ABI. They are
   not selected by an asynchronous interpreted class path.
5. CLR reflection is not made pre-emptible: cancellation is checked at
   the host boundary, while a synchronous third-party CLR call retains
   ordinary CLR semantics.

### Recursion-depth policy

`TS-P0-07` adopts one execution-depth protocol for interpreted and
compiled ToastScript:

1. Active scripts, functions, methods, lambdas, constructors, compiled
   blocks, and nested `eval`/`source` execution contribute frames to one
   asynchronous-flow-local count.
2. The default and highest safe limit are 128 frames. A session may set
   `$tosh.Config.Shell.MaxRecursionDepth` to a stricter value from 1
   through 128.
3. Entering the first frame beyond the configured boundary produces
   `tosh.runtime.recursion_limit_exceeded` with a compact innermost-first
   ToastScript frame summary. No raw `StackOverflowException` reaches
   the user.
4. Every frame is released through exception-safe unwinding. A handled
   depth diagnostic leaves the engine and REPL usable for later input.
5. Emitted functions, methods, lambdas, blocks, and lowerable
   constructors enter the same runtime guard. Interpreter-backed
   compiled fallbacks inherit the interpreter guard.

### Channel-receive policy

`TS-P0-08` completes the channel protocol established by `TS-P0-02`:

1. `ShellChannel.ReceiveResultAsync` is the explicit one-item primitive.
   `HasValue: true, Value: null` is a received null payload;
   `HasValue: false` means closed and drained.
2. Readiness is advisory with multiple consumers. A receiver that loses
   the atomic `TryRead` race loops back to readiness instead of reporting
   a fabricated null or completion.
3. The existing `ReceiveAsync` return type remains source-compatible,
   but closed-and-drained now raises `ChannelClosedException`; a returned
   null is therefore always a payload.
4. `channel-recv` retains its streaming contract: null is emitted as one
   value, while closure ends the stream without an extra value.
5. `channel-select` retains the non-destructive readiness/commit protocol
   from `TS-P0-02`. Cancellation abandons no destructive receive and
   leaves the channel usable.

### Pipeline value-context policy

`TS-P1-20` gives compiled execution the interpreter's subexpression
rule for a parenthesized pipeline:

1. A pipeline used where a single value is expected — a variable
   initializer, assignment right-hand side, `return` operand, argument,
   or parameter default — collapses to that value. Nothing yielded
   becomes `null`; exactly one item becomes the item itself.
2. More than one produced value is a failure, not a silently returned
   collection. Both modes raise
   `tosh.runtime.subexpression_requires_single_value`.
3. An iteration source (`for name in (pipeline)`) is not a single-value
   context: it receives every produced item.
4. Stage count does not change meaning. A single-stage value pipeline
   already collapsed through the host's `InvokeValue`; multi-stage
   pipelines now use `DrainSubexpressionValue` so both agree, while
   `DrainValue` is retained for sequence contexts.

The compiler previously returned `List<object>` from every value-context
pipeline. The rationale recorded on that helper — that call sites needed
a uniform list so `(cmd | first 3)` would work — did not match the
interpreter, which rejects that shape outright; the collapse rule above
is what both modes actually implement.

## Definition of Done

An item is closed only when all applicable conditions hold:

- the smallest public reproduction has a regression test;
- interpreter and compiled behavior agree, or an unsupported compiler
  shape produces a deliberate structured diagnostic;
- cancellation and streaming behavior are tested where relevant;
- diagnostics use a stable `tosh.*` code rather than leaking raw CLR
  exceptions;
- the specification and generated/user-facing metadata agree;
- focused tests and the full solution test suite pass;
- this document's status and evidence are updated; and
- the work is committed. Validated work that exists only in the working
  tree is one command from loss, which no amount of test coverage
  protects against.

## Active Work

This section is derived from the item tables below. When a status
changes there, change it here in the same edit; the Progress Log records
each slice's reasoning and validation and is not summarized again here.

**Closed.** Every P0 item. Twelve P1 items — `TS-P1-01`–`TS-P1-06`,
`TS-P1-14`, `TS-P1-15`, and `TS-P1-20`–`TS-P1-23` — with `TS-P1-17`
withdrawn as misfiled rather than fixed. Eleven P2 items:
`TS-P2-04`–`TS-P2-08`, `TS-P2-12`–`TS-P2-16`, and `TS-P2-25`. All three
July 26 semantic decisions (comparison, chained comparison, `$this` in
method defaults) and the July 28 paired-delimiter decision are
implemented.

**In progress** (as of July 30, 2026 — 98 items, 56 complete; P0 8/8,
P1 25/37, P2 21/42, P3 2/11; plus one partially complete and two
withdrawn. Suite 3,732 passing, 1 skipped. Counted from the tables
below by status prefix; an earlier snapshot in this section said 36 of
72 and had not been re-derived since July 29.)

- `TS-P1-24`, duplicated sync/async semantics. The inventory ratchet is built
  (`SyncAsyncTwinInventoryTests`), and building it corrected the audit twice
  over: reflection finds 63 twins where two successive text searches found 23
  and 29. Rescoped July 29 — the 30 pairs mandated by the project's own
  dual-surface interfaces are deliberate, so the item's remaining work is the
  **29 parallel internals**, led by `ThrowDetailedSingleConstructorMismatch`
  (55 lines), `ApplyPendingParameterDefaults` (50), `InvokeQualifiedMethod`
  (47), and `ConvertPropertyValue` (44).
- `TS-P2-23`, parse-time identity. Clauses 1 and 3 are met — `ParseContext`
  now carries commands, modules, **and** types, and a lower-case type alias
  resolves where it previously reported `unknown_command`. **Clause 2 is
  blocked on `TS-P2-10`**, which is still Planned: there is no
  language-surface registry to drive keyword recognition from, and the
  hardcoded spelling comparisons have drifted 160 → 182.
- `TS-P2-11`, parser expression layers. The mode-tracking lexer,
  declaration table, and characterization corpus are built. `TS-P2-24` and
  `TS-P2-25` are complete, so the structural layer this depends on is done.

**Remaining.**

- P1: `TS-P1-07` (partial — the defer case is closed, other nested
  control-flow shapes are not), `TS-P1-08`–`TS-P1-13`, `TS-P1-16`,
  `TS-P1-18`, and `TS-P1-19`.
- P2: `TS-P2-01`–`TS-P2-03`, `TS-P2-09`, `TS-P2-10`,
  `TS-P2-17`–`TS-P2-22`, and `TS-P2-26`–`TS-P2-27`.
- P3: `TS-P3-01`–`TS-P3-09`, of which `TS-P3-04` and `TS-P3-07` are
  research rather than proposals.

**Sequencing note, revised July 29.** The July 26 audit put `TS-P1-24`
ahead of the next individual P1 repair. That reasoning no longer holds
unchanged: the item is now blocked on a design decision, and its risk is
contained by a standing ratchet rather than by convergence. The
outstanding *blocker* is `TS-P2-10` — the only Planned item that another
in-progress item cannot proceed without — so it has the strongest claim on
being moved up. `TS-P2-26` is the cheapest high-value item: three
specification examples were found broken by executing them, and the
conformance corpus structurally cannot reach the ones most likely to be
copied.

A second review pass on July 25 verified the completed P0 fixes live and
filed `TS-P0-07`–`TS-P0-08`, `TS-P1-14`–`TS-P1-19`, `TS-P2-12`–`TS-P2-20`,
and `TS-P3-05`–`TS-P3-07`. Every item it called highest-impact — the
process-killing recursion overflow and the silent wrong-answer cluster
in string escapes, literal coercion, and storage suffixes — is now
closed.

## P0 — Safety, Data, and Binding Invariants

| ID | Status | Problem | Required acceptance |
|---|---|---|---|
| `TS-P0-01` | Complete — 2026-07-25 | Tuple assignment redeclares targets, bypassing `const`, annotations, and nearest-scope assignment. | RHS evaluates once; all targets resolve before mutation; `const` and type checks apply; outer bindings mutate rather than shadow; failure cannot partially commit. |
| `TS-P0-02` | Complete — 2026-07-25 | `channel-select` starts destructive receives on every arm, consumes losing values, and conflates a valid `null` payload with closure. | Exactly one ready item is committed; losing queues retain their values; `null` is selectable; cancellation leaves no abandoned receive. |
| `TS-P0-03` | Complete — 2026-07-25 | Base property initialization uses leaf locals and construction does not reliably recurse through the complete base chain. | Base arguments bind before base initialization; each constructor runs once in base-to-leaf order; intermediate non-generic classes do not break the chain. |
| `TS-P0-04` | Complete — 2026-07-25 | `??=` throws for an existing null binding, evaluates the RHS for non-null targets, and is unsupported by the compiler. | Variable/member/index targets evaluate once; non-null targets skip RHS; null targets assign RHS; undeclared variables retain the existing diagnostic; interpreted and compiled cases agree. |
| `TS-P0-05` | Complete — 2026-07-25 | An exception in one `defer` prevents earlier LIFO cleanup blocks from running. | Every reached defer runs once in LIFO order; body output is preserved and cleanup output discarded; the primary and cleanup failures are retained in documented order; cancellation receives shielded cleanup; interpreted and compiled behavior agree. |
| `TS-P0-06` | Complete — 2026-07-25 | Class methods and constructors use sync-over-async execution with `CancellationToken.None`. | Cancellation reaches class calls; MCP/REPL timeouts stop promptly; no blocking bridge remains on an asynchronous interpreted class path. |
| `TS-P0-07` | Complete — 2026-07-25 | ToastScript recursion beyond roughly 420 ordinary-function frames, or roughly 250 heavier class-method frames, terminates the whole process with a raw CLR stack-overflow dump; an interactive session cannot survive it. | A documented recursion depth limit produces a structured `tosh.runtime.*` diagnostic instead of process death; the REPL session survives and remains usable; compiled execution either shares the limit or fails through its own structured diagnostic; the limit is configurable or generous enough for realistic scripts. |
| `TS-P0-08` | Complete — 2026-07-25 | `ShellChannel.ReceiveAsync` performs one `WaitToReadAsync` + `TryRead` without looping, so a concurrent receiver that loses the wake-up race observes a spurious `null` on an open channel; the plain receive surface also still conflates a valid `null` payload with closed-and-drained (the `TS-P0-02` fix covered selection only). | Receive loops readiness/commit until an item is atomically taken or the channel is closed and drained; concurrent receivers never observe a spurious `null`; the command surface distinguishes a `null` payload from closure; cancellation leaves no abandoned wait. |

## P1 — Canonical Language Semantics

| ID | Status | Problem | Required acceptance |
|---|---|---|---|
| `TS-P1-01` | Complete — 2026-07-25 | Conditions, logical operators, `ToshHost`, the type checker, and compiler disagree about truthiness and NaN. | One runtime primitive and a table-driven corpus for null, booleans, numerics, NaN, strings, collections, general enumerables, and objects. |
| `TS-P1-02` | Complete — 2026-07-25 | Collection `contains` searches the collection's CLR type-name string rather than its elements. | String containment remains ordinal; dictionaries follow a documented key/value rule; other enumerables use canonical equality; no type-name false positives. |
| `TS-P1-03` | Complete — 2026-07-25 | Compiled equality, ordering, concatenation, repetition, and polymorphic arithmetic bypass interpreter semantics. | Differential operand corpus produces equivalent typed values, stdout, and diagnostics in interpreted and compiled modes. |
| `TS-P1-04` | Complete — 2026-07-25 | Compound assignment bypasses ToastScript class operator dispatch despite the documented desugaring rule. | `x += y` and `x = x + y` use the same overload protocol and conversion behavior. |
| `TS-P1-05` | Complete — 2026-07-26 | Constructor/method defaults become null; free defaults and compiled captures use incompatible scopes. | One callable binder evaluates defaults according to the working decision for functions, methods, and constructors in both execution modes. |
| `TS-P1-06` | Complete — 2026-07-26 | Unknown/duplicate named arguments are accepted and mixed named/rest calls can drop or wrap positional arguments incorrectly. | Unknown and duplicate names are diagnosed; rest contains only unconsumed positional values in source order. |
| `TS-P1-07` | Partially complete — defer case 2026-07-25 | The defer-specific loss of values emitted before `return` is addressed; other nested control-flow shapes can still materialize or change streaming behavior. | Previously emitted values stream unchanged; optional return value is final; nested control flow does not alter output semantics or introduce unnecessary materialization. |
| `TS-P1-08` | Complete — 2026-07-30; the headline symptom was already fixed, the surplus pull is now too | Nested generator statements materialize output, while short-circuit consumers peek a second upstream item. **`take-while` does not short-circuit an infinite generator at all.** `recur (0, 1) func(a, b) => ($a + $b) \| take-while { _ < 100 }` (`LazySequenceTests.Recur_fibonacci_take_while`) should stop at 89; instead it generates Fibonacci without bound. Because the values are arbitrary-precision integers whose digit count grows linearly, total memory grows quadratically: an instrumented run reached **104,741 MB in 57 seconds** and exhausted a 128 GB machine. The sibling `iterate 1 func(x) => ($x * 2) \| take-while { _ <= 64 }` fails instead with `'iterate' operations must produce exactly one value per input item`. | Nested `yield` streams promptly; `first`/`any` do not evaluate an unnecessary next item; **`take-while`/`skip-while` short-circuit without materializing an unbounded upstream**; infinite-source tests complete under a bounded memory cap. |
| `TS-P1-09` | Planned | Class hierarchy lookup loses generic bindings, inherited overloads, `vital` validation, and private visibility rules. | Recursive hierarchy test matrix covers generic intermediaries, overload sets, required members, private/protected access, and partial statics. |
| `TS-P1-10` | Complete — fixed 2026-07-30 | Anonymous-record equality depends on dictionary insertion order. | Records with the same names and canonically equal values compare equal regardless of insertion order. |
| `TS-P1-11` | Complete — fixed 2026-07-30 | `_` in destructuring is bound and overwritten instead of discarding the matched value. | Every `_` target skips without creating or modifying a binding; nested/rest patterns are covered. |
| `TS-P1-12` | Planned | `const` currently accepts arbitrary runtime pipelines and behaves as a readonly binding rather than a constant. | Constant-expression rules are specified and enforced; `let` covers runtime immutability before compatibility behavior is removed. |
| `TS-P1-13` | Planned | Compiled ordinary member/index assignments evaluate target components before the RHS, while the interpreter preserves RHS-first order; only `??=` intentionally uses target-first order. | Side-effecting target, index, and RHS probes produce the same ordering in interpreted and compiled modes for every assignment operator. |
| `TS-P1-14` | Complete — 2026-07-26; audited 2026-07-29, see `TS-P1-26` | Cross-type equality and ordering are incoherent: `==` coerces numerically (`1 == "1"` is true) and falls back to case-insensitive `ToString` comparison for mixed types while string-to-string stays case-sensitive; ordered comparison converts right-to-left only, so `"abc" < 5` silently string-compares to `false` while `5 > "abc"` throws; booleans participate in ordering, so `1 < 2 < 3` silently evaluates to `false`. | One documented equality/ordering conversion matrix implemented once and used by every surface (extends the `TS-P1-01`/`TS-P1-03` corpus); conversion is symmetric or produces a structured diagnostic; no silent lexicographic fallback for mixed numeric comparisons; the chained-comparison shape is either supported or diagnosed; interpreted and compiled modes agree. |
| `TS-P1-15` | Complete — 2026-07-26 | Enum values are not orderable or number-comparable: `E.A < E.C` throws (`ToshEnumValue` cannot be compared) and `E.B == 1` is false, despite the specification's numeric-backed enum examples. | Enum values compare and order canonically against members of the same enum and against their underlying numeric values; diagnostics for genuinely incompatible enum comparisons name the shell-level enum type; the specification's `Permissions : int` examples pass as conformance cases. |
| `TS-P1-16` | Complete — fixed 2026-07-30 | Float division-by-zero gave two different answers. Filed as depending on the zero operand's type — `10.0 / 0` threw while `10.0 / 0.0` returned `Infinity` — but the real split is **folded versus evaluated**: literal operands are constant-folded with C# semantics and yield `Infinity`, while the same doubles held in variables reach `OperatorEvaluator`, whose floating lambda threw. Two implementations of one operation, which is `TS-P1-24`'s shape on the arithmetic axis. | One documented rule per numeric family (integral, float, decimal) for division and modulo by zero; the zero operand's declared type does not change the outcome; interpreted and compiled modes agree. |
| `TS-P1-17` | Withdrawn — 2026-07-26 | Filed as "the empty brace literal `{}` evaluates to an internal type-definition object instead of an empty record". Re-examination showed the then-current `{}` was a correct empty record; the observation was `type-of` rendering, fixed as `TS-P1-23`, plus the positional ambiguity later resolved by `TS-P2-25`. Under the accepted paired-delimiter grammar, `{}` is a block and `{||}` is an empty record. | n/a — not a defect as filed. |
| `TS-P1-18` | Complete — fixed 2026-07-30 | A class declaring both a primary constructor and an explicit constructor of the same signature registered duplicate overloads, so **every** instantiation failed with `Multiple constructor overloads matched class 'C' with 1 argument(s): C(y: int); C(x: int)` — a class reported as ambiguous with itself, at the point of use, naming neither declaration as the thing to fix. | `ToshClassDefinition.ValidateConstructorSignatures` rejects it **where it is declared** (`tosh.runtime.duplicate_constructor`), called before `DeclareType` on the plain path and **after** `MergePartial` on the partial path, since a later part can contribute the collision and neither part is wrong alone. The rule compares **type annotations positionally, not arity**, and that distinction is the whole design: probing first established that same-arity constructors are legal and working — `G(n: int)` beside `G(s: string)`, and a primary `H(x: int)` beside an explicit `H(s: string)`, both resolve correctly — so the blanket arity rule the item's wording implied would have broken working code. Rejecting only *identical* signatures also means no working program can break: a class this refuses could never have been constructed at that signature. Deliberately **not** rejected: a typed parameter beside an untyped one (`K(n: int)` and `K(o)`). That is ambiguous for an `int` argument but usable for everything else, and the resolver names both constructors the author actually wrote — which is an ordinary overload ambiguity, not a class ambiguous with itself. Assumed the opposite while designing the rule; measuring it changed the design, and the case is pinned in the tests. Explicit constructors were **undocumented in the specification** — not in the class-features list at all — which is plausibly why this survived; the spec now documents the member form, the type-based overload rule, and this diagnostic. Compiled construction is not aligned here, per the July 30 decision that the interpreter is authoritative while semantics move. |
| `TS-P1-19` | Planned | An infinite generator invoked in command position (`gen \| first 3`) silently produces no output and exits cleanly, while the call form (`gen() \| first 3`) hangs; both diverge from the accepted stream-producer decision (companion to `TS-P1-08`). | Command-position and call-position generator invocations produce identical streams; infinite generators stream promptly and terminate under `first`/`any`; the silent-empty shape is covered by a regression test. |
| `TS-P1-20` | Complete — 2026-07-26 | A compiled multi-stage pipeline in value context never applied the interpreter's single-value subexpression rule: `var n = ([1, 2, 3] \| count)` produced a one-element `List<object>` rather than `3`, and a pipeline yielding several values returned a list silently where the interpreter raises `tosh.runtime.subexpression_requires_single_value`. Single-stage value pipelines already collapsed through `InvokeValue`, so the two shapes disagreed inside the host itself. Found while validating `TS-P1-05`. | Value-context pipelines collapse identically in both modes (none → `null`, one → the item, several → the shared diagnostic); iteration sources still receive every item; literal, variable, and command seeds behave alike; conformance rows and differential regressions cover each shape. |
| `TS-P1-21` | Complete — 2026-07-26 | A parameter default on a class method or constructor cannot reference `$this`: `func m(a, b = $this.V)` fails with `tosh.runtime.unknown_variable` because defaults are evaluated during callable binding, before the `this`/`super` bindings are seeded. `TS-P1-05` made this an explicit failure rather than the previous silent null. Needs a recorded decision, not just a fix: an instance method default may clearly see `$this`, but a **constructor** default would observe a partially-constructed instance whose properties have not been initialized yet (base-to-leaf construction binds arguments first), so allowing it exposes uninitialized state while rejecting it makes methods and constructors inconsistent. | A decision-log entry states whether `$this` is in scope for method defaults, constructor defaults, or both; the callable default binder seeds the agreed bindings; the rejected case keeps a targeted diagnostic naming `$this` rather than the generic unknown-variable help; interpreted and compiled modes agree; the specification's default-value semantics section records the rule. |
| `TS-P1-22` | Complete — 2026-07-26 | `a < b < c` parses left-associatively, so `1 < 2 < 3` compares `true < 3` and silently answers `false`. The accepted decision is real chaining. | `a < b < c` evaluates as `(a < b) and (b < c)` with each operand evaluated once and short-circuit preserved, in interpreted and compiled modes; the parser, binder traversal, type checker, and emitter all handle the new shape; precedence and formatting round-trip. |
| `TS-P1-23` | Complete — 2026-07-26; structural display paths 2026-07-27 | `type-of` yields a shell type descriptor for shell-typed values, but the descriptor rendered as its own CLR class name, so `type-of [1, 2]` reported `Tosh.Runtime.BuiltInShellTypes+BuiltInShellTypeDefinition` instead of the type being asked about. | Displaying a built-in shell type descriptor shows the shell type name; `type-of` reports usable names for lists, records, and other shell-typed values; CLR values are unaffected. |
| `TS-P1-24` | In progress — rescoped 2026-07-29 to the 29 convergeable internals | The interpreter carries sync/async twin methods that are *parallel implementations* rather than delegations, so a semantic fix can land on one surface and silently miss the other. This has happened twice: `OperatorEvaluator.AreEqual` versus `ToshEngine.AreEqualAsync` (`TS-P1-14`/`TS-P1-15`) and `ToshHost.DrainValue` versus `InvokeValue` (`TS-P1-20`). A corrected audit on 2026-07-26 counted 23 truly parallel pairs against 6 that delegate. The refinement cluster, the largest, is now converged. Remaining largest duplications: `ThrowDetailedSingleConstructorMismatch` (55 lines), `TryGetInstanceMember` (51), `ApplyPendingParameterDefaults` (50), `InvokeQualifiedMethod` (47), `ConvertPropertyValue` (44), `TrySetInstanceMember` (43), `SelectBestCallableMatches` (41), `GetInstanceMembers` (38), `ConvertConstructorParameterValue` (35). | Each pair either delegates to one implementation or is removed; a test or analyzer fails when a new parallel sync/async pair is introduced; behaviour is unchanged, evidenced by the existing suite plus the annotated-conversion drift guard. **Rescoped 2026-07-29 by decision.** The dual surface is deliberate: `IShellRecordObject`, `IObjectAccessor`, `IShellInvocableObject`, `IShellEnumerableObject`, and `IShellStaticType` each declare both a sync and an async member because the interpreter serves both kinds of caller with genuinely different member-dispatch semantics — `GetIndexedValueAsync` avoids re-entering the synchronous record API on purpose, not by oversight. So the 30 contract-imposed pairs are intended, not debt, and clause 1 applies to the **29 parallel internals** only. Retiring the synchronous surface behind one blocking bridge was considered and rejected as a much larger change than this item describes; if it is ever wanted it gets its own item. Clause 2 is met: `SyncAsyncTwinInventoryTests`. |
| `TS-P1-25` | Complete — 2026-07-29 | (Filed 2026-07-26 under a duplicate `TS-P1-20`; renumbered 2026-07-27.) The pure compiler profile can report a Tier-1-clean artifact while emitted IL still unconditionally calls `ToshHost.Initialize`/`RegisterCompiledAssembly` from `Main` and `ToshHost.EnterExecutionFrame` from functions, methods, lambdas, and blocks. | A pure artifact contains no metadata references or calls to `Tosh.Compiler.Runtime`, `ToshHost`, or `ToshEngine`; bootstrap is omitted or conditional; recursion guarding uses a stable `Tosh.Runtime` primitive; and a post-emit IL dependency audit fails independently of `RequireTier` diagnostics. Verified 2026-07-27: the emitted IL references exactly `System.Console`, `System.Private.CoreLib`, `Tosh.Compiler.Runtime`, and `Tosh.Runtime`, so only the three unconditional `ToshHost` members stand between the artifact and purity; the over-declared `deps.json` is a separate packaging concern. |
| `TS-P1-26` | Complete — 2026-07-29 | Equality is asymmetric for bool against string: `true == "true"` is `true` while `"true" == true` is `false`. Numeric pairs are symmetric in both directions (`1 == "1"` and `"1" == 1`), as are bool-against-number, so only the string-on-the-left-of-a-bool direction fails to coerce. Both `OperatorEvaluator.AreEqual` and `ToshEngine.AreEqualAsync` agree with each other, so this is one rule applied in one direction rather than a sync/async drift. Found by `EqualityParityTests` on its first run. `TS-P1-14` promised symmetry explicitly for ordering and did not state it for equality, which is why it survived that item. | A decision records whether a string coerces to bool for equality at all: either `"true" == true` becomes `true` (extend coercion, matching the numeric rule) or `true == "true"` becomes `false` (drop bool/string coercion). Equality is symmetric for every pair in the corpus afterwards, on both implementations; the characterization entry in `EqualityParityTests` is inverted in the same change. |
| `TS-P1-27` | Complete — implemented 2026-07-30 | ToastScript's concurrency system and the CLR's did not meet. `async`/`await` are builtin commands over `ShellFuture`, and a CLR method returning `Task`/`Task<T>` was never awaited by anything: the task flowed into the pipeline untouched, `await` refused it with `await_requires_future`, and it displayed as `AsyncStateMachineBox\`1` — the compiler's state machine type, since `Task` does not override `ToString`. The only route to a value was `.Result`, which blocks and can deadlock. Reported from real code: `async { $p.SendPingAsync(…) }` then `await $reply`. | Decided C#-identical and explicit: a task-returning call yields a task and you await it. `await` accepts `Task`, `Task<T>`, `ValueTask`, `ValueTask<T>` and a `ShellFuture`, **flattens** so one `await` unwraps a future whose output is a task, emits nothing for a declared-`Task`, honours cancellation via `WaitAsync`, and surfaces a faulted task's *own* message rather than `AggregateException`'s. Tasks stay values so work can overlap — asserted by timing. An un-awaited task renders `Task<PingReply> (pending)`. Auto-awaiting at the call site was rejected: it removes concurrency and would have to land on both surfaces of the dual-surface interfaces. Specification gains an Asynchrony section — it had none. Negative control: 6 of 10 cases fail unfixed. |
| `TS-P1-28` | Complete — fixed 2026-07-30 | A computed `static`/`shared` property answered **`null`**, silently, in every spelling — arrow-bodied and accessor-block alike, in a `hermit class` and a plain one. Static properties were only ever *initialized*, never *evaluated*: both initialization sites read `IsStatic && Initializer is not null && !IsComputed`, so a computed one never entered `_staticValues`, and `TryGetStaticMember` fell through to a line commented `// null default`. Stored static properties worked throughout, which is why the report read as "static properties do not work at all" rather than "computed ones do not". No diagnostic at any point. Reported from a real library whose `hermit class State` exposed `shared prop Icmp => …`. | `TryGetStaticMember` evaluates a computed static property's getter through `ExecuteClassBlockSync`, mirroring the instance path — `CreateLocals` already accepts a null instance and omits `$this`, so no new plumbing was needed. Every spelling returns its value, on each read rather than cached, and the stored path is unchanged. Negative control across both fixes in this slice: 8 of 12 cases fail unfixed. |
| `TS-P1-29` | Planned — filed 2026-07-30 | `ShellRecordUtilities.TryGetFields` throws on an object-keyed dictionary: "Unable to cast object of type 'KeyValuePair`2[Object,Object]' to type 'DictionaryEntry'". Its `IDictionary` branch iterates as `DictionaryEntry`, which is what the *non-generic* enumerator yields, but a `Dictionary<object, object?>` reached through `IEnumerable` yields `KeyValuePair<object, object?>`. A `{% … %}` dict literal is object-keyed, so any caller that hands one to `TryGetFields` crashes with `unexpected_exception` rather than a diagnostic. Found while fixing `TS-P1-10`, which had to be narrowed to string-keyed records to avoid it. | `TryGetFields` handles both enumerator shapes, so an object-keyed dictionary yields its entries instead of throwing; a crash in a utility that formatters and equality both call is covered by a test that passes a `{% … %}` literal directly. |
| `TS-P1-30` | Complete — fixed 2026-07-30; interpolation follows in `TS-P1-32` | **At a TTY, an external command's output is never captured.** `var x = git rev-parse --show-toplevel` prints the path to the terminal and leaves `$x` as `null`; `(git rev-parse …)` answers `null`; `$"{git rev-parse …}"` and `$(…).Trim()` answer empty. All five forms print correctly and capture nothing. `DetermineSpawnMode` decides between passthrough and piped using `hasTerminal = !context.IsPipelined && !Console.IsOutputRedirected && …`, so the question "is my output being consumed?" is answered by `IsPipelined` alone — which is true only when a *downstream stage* exists. Assignment, subexpression, and interpolation all consume the value without being pipelined, so they take `SpawnMode.TerminalPassthrough`, where stdout is inherited and nothing is captured. **Invisible to the entire suite**: 3,602 tests pass because a test process never has a TTY, and without one the same code captures correctly. Reproduced by allocating a pty with `script`. | `CommandContext` carries whether its output will be consumed, distinct from `IsPipelined`, and `DetermineSpawnMode` treats a consuming context as it treats a pipelined one. The scope decision is *which* contexts count — assignment, subexpression, and interpolation at minimum; `return`, conditions, and command arguments need deciding. Note the existing comment's tension is not an obstacle here: forcing the piped path redirects stdin and skips the foreground-group handoff, which breaks an *interactive* child — but a child whose output is being captured is not one the user is interacting with, so piping is correct exactly in the captured case. Tests must run under a pty, or the fix is unverifiable by the suite that missed it. |
| `TS-P1-32` | Complete — fixed 2026-07-30 | An interpolation hole did not capture external output: `echo $"{git rev-parse --abbrev-ref HEAD}"` printed the branch to the terminal and interpolated the empty string. Same root cause as `TS-P1-30` reached by a different route — a hole **re-parses its text** and runs it as a whole statement, arriving at the pipeline through `EvaluateAsync` → `EvaluateParseResultAsync` → `EvaluateStatementAsync` rather than through any of the consuming sites `TS-P1-30` marked. | The capture flag threads that route, defaulted at every hop so no existing call site changed, and the interpolation hole is its **only** caller passing `true`. Two wrinkles the plan had not predicted: the three-parameter `EvaluateAsync(string, string, CancellationToken)` is an `IShellEvaluator` interface member, so adding a defaulted parameter stopped it implementing the interface — it is kept exactly as declared and delegates to a four-parameter overload; and `EvaluatePipelineAsync`'s seventh positional parameter is a `PipelineExitStatusTracker`, so the forwarding had to be by name. Verified under a **pty**, without which every assertion re-tests the branch that already worked. The characterization `An_interpolation_hole_does_not_yet_capture` — which asserted the empty `GOT[]` precisely so that closing the gap would be a deliberate edit — is flipped to its positive form, and a second case covers a hole inside a function body so the flag is shown to survive a call frame. Negative control failed exactly those two and left the other seven green. |
| `TS-P1-33` | Planned — filed 2026-07-30 | `members` on a `ShellTextLine` lists only `Text`, while every `string` member is callable on it — `.Trim()`, `.Length`, `.ToUpper()`, `.Split()`, `== "…"`, `cast string`, and a `string` annotation all work, because `ReflectionObjectAccessor.ResolveSegment` unwraps to the underlying string. Introspection therefore contradicts behaviour, and that mismatch is what convinced both the reporter and the author that the type was not string-like — leading to a proposed fix for a defect that did not exist. | `members` and `methods` on a `ShellTextLine` list the string surface it actually exposes, or state that it forwards to `string`; completion does the same. Discoverability matching behaviour is the whole of it. |
| `TS-P1-34` | Complete — fixed 2026-07-30 | **A module-qualified type name could not be used as an annotation at all.** `var x: ToastLib.Math.IntPercent = 60` raised `tosh.runtime.annotation_unknown_type` while the bare `IntPercent` worked and enforced. Reported for refinement types, but the cause was general: every lookup on the annotation path — `TryGetRefinementType`, `TryGetNamedType`, and the CLR resolver — took a **flat** name with no notion of a dotted module path, so a qualified `class` and `record` failed identically (`var v: Outer.Inner.Widget = …`). The third place this programme has needed "follow a dotted path through modules", after `require`'s nested export walk (`TS-P2-35`) and qualified command arguments. | Both lookups fall through to one shared walk, `TryResolveQualifiedModuleMember`: resolve the leading segment as a module, walk nested modules by `ExportTable.Modules`, then look the leaf up among the final module's refinement types and named types. Placed *beneath* the flat lookups in both callers, so an unqualified name resolves exactly as before and only a dotted name reaches the walk. Fixing the lookups rather than the known-check matters: the annotation must not merely be *accepted* but resolve to the same definition, so the refinement still coerces and still rejects. |
| `TS-P1-35` | Complete — fixed 2026-07-30 | **A module shadowing a CLR type made that type unreachable from inside itself.** `coerce Math.Clamp(_, 0, 100)` declared inside `module Math` failed with "Member 'Clamp' was not found on module 'Math'" — the module name won and there was no fallback. Latent until `TS-P1-34`: while the qualified annotation was broken, the reporter's refinement was only ever reachable through the name that leaked unqualified into the requiring scope, where `Math` was not a bound module and fell through to `System.Math`. Resolving the qualified name evaluates the coercion with the module in scope, so fixing `TS-P1-34` alone would have moved the reporter from one error to another. Same collision as `TS-P2-37` (`file` versus `System.IO.File`). | `ToshModuleObject.InvokeInstanceMethod` falls back to the shadowed CLR type via `TryResolveTypeName` and `Runtime.Invoker.InvokeStatic` **on a member miss only** — a module's own export still wins, so no existing call changes which member it resolves to; the fallback can only turn a hard failure into a hit. A miss on a module that shadows nothing still errors, so the fallback does not swallow real mistakes. |
| `TS-P1-36` | Withdrawn — misfiled 2026-07-30; the decision it prompted is recorded and guarded | Filed as "a `partial module`'s exported types resolve *unqualified* in the requiring scope while a plain `module`'s do not", from a probe during `TS-P1-34` that reported a bare `IntPercent` resolving and enforcing after a `require`. **The leak could not be reproduced.** Re-probed against the reporter's library and against a recreation of the exact file shape recorded at the time: a bare export — type, class, or command — resolves from neither a `partial` nor a plain module. The likely confusion is that an autoloaded file in the reporter's config declares a refinement type at **top level** (`type NonEmptyList<T> = …` in `autoload/refinements.tosh`), which is genuinely global and correctly so, because a top-level declaration is not a module export. A failing probe proves something about the probe, not about the language — the same lesson as the four words wrongly reported missing from the keyword registry. | No code change: the rule the decision chose was already in force. The decision itself stands and is now **enforced rather than merely true** — `QualifiedModuleTypeTests` asserts that an export leaks from neither module form, for types, classes and commands, and that the qualified spelling reaches all three, since "not bare" is only correct if the module name genuinely works. `partial` is a declaration-splitting modifier and must never widen visibility. |
| `TS-P1-37` | Planned — filed 2026-07-30 | **Primary-constructor parameters are unbound when construction goes through an explicit constructor.** `class R(x: int) { prop X: int = x; R(a: int, b: int) { … } }` constructs fine as `new R(5)` but `new R(2, 3)` fails with `Command 'x' was not found` at the property initializer — the initializer references a primary parameter that the explicit constructor never bound. Found while fixing `TS-P1-18`; a distinct defect with its own design decision, so filed rather than folded in. | Decide the rule and enforce it. C# answers this by *requiring* an explicit constructor to chain to the primary one (`: this(…)`), which guarantees the parameters exist; TōSh has `$super(…)` for a base class but no chaining form for its own primary constructor. Either add one and require it when initializers reference primary parameters, or bind those parameters to their declared defaults and diagnose when there is no default. Whichever way, the failure must not be a bare `unknown_command` naming a parameter as though it were a shell command. |

## P2 — Parser, Binder, Diagnostics, and Surface Generation

| ID | Status | Problem | Required acceptance |
|---|---|---|---|
| `TS-P2-01` | Planned | Lowercase user calls such as `f()` do not compose normally inside operator expressions. | Calls are ordinary postfix expressions independent of capitalization or surrounding operators. |
| `TS-P2-02` | Planned | Unary variable negation is lexically/runtime broken and binds on the wrong side of exponentiation. | `-$x`, `- $x`, folded literals, and compiled forms agree; `-2 ** 2` follows the documented precedence. |
| `TS-P2-03` | Planned | Ranges bind at primary precedence instead of below additive expressions. | Precedence corpus covers both range bounds and explicit-parenthesis controls. |
| `TS-P2-04` | Complete — 2026-07-26 | The documented compact `$value?.Member` syntax silently becomes a bareword. | Fused safe navigation tokenizes correctly or produces a targeted diagnostic; spacing does not change meaning. |
| `TS-P2-05` | Complete — 2026-07-26 | Numeric separator validation permits forms such as `1__2`, `_1` is misclassified, and large binary/octal values leak overflow exceptions. | Lexer distinguishes identifiers from numerics, validates separator placement, and recovers with structured overflow diagnostics. |
| `TS-P2-06` | Complete — 2026-07-26; audited and consolidated 2026-07-29 | Newline statement detection omits legal expression starts; unterminated block comments are silently accepted. | All expression-start tokens share one source of truth; unterminated comments report a span-aware diagnostic. |
| `TS-P2-07` | Complete — 2026-07-26 | Binder and variable-binder visitors miss pipe-forward, substitution, and other nested forms. | One exhaustive syntax walker visits every child; a reflection/exhaustiveness test fails when a new syntax node lacks traversal. |
| `TS-P2-08` | Complete — 2026-07-26 | The raw function-name pre-scan can reinterpret later commands after unrelated text containing `func`. | Declarations are discovered structurally without non-local token poisoning. |
| `TS-P2-09` | Planned | LSP maps warnings to errors and MCP `explain_error` stops runtime analysis when only warnings exist. | Severity is preserved end-to-end; warnings do not suppress independent runtime explanations. |
| `TS-P2-10` | In progress — registry and guard 2026-07-29 | Operators, keywords, document symbols, help, MCP, LSP, and spec tables are hand-maintained and have drifted. Measured: **eight consumers holding 115 distinct words between them, of which 7 appeared in all eight.** The CLI highlighter knew 59, the Tome colorizer 21, the REPL classifier 15, the LSP feature table 93. Consequences were ordinary and visible — `const`, `defer`, `yield`, `union`, `rune`, `event`, `import`, and `interface` went unhighlighted at the prompt, and the Tome coloured no control-flow keyword. (An earlier version of this row said the LSP documented three nonexistent keywords; that was wrong — see the July 29 correction entry.) | A machine-readable language-surface registry generates or validates every consumer. **Landed:** `LanguageSurface` in `Tosh.Runtime` carries **103** words by category, each **execution-validated** by a probe in `LanguageSurfaceParityTests`; the CLI highlighter and Tome colorizer derive from it; `ParseClassMember`'s 22-branch modifier chain is one registry lookup, taking the parser's spelling comparisons from 182 to 142; the guard proves no consumer names a word the registry lacks across five consumers, that every member modifier works in member position, and that the visibility family is exactly what `ParseDeclarationModifier` accepts. **Remaining:** the three prose-carrying consumers still hold their own key sets — the LSP under-documents 11 registry words, mostly the `TS-P2-30` aliases — and operators and document symbols are untouched. |
| `TS-P2-11` | In progress — characterization corpus 2026-07-26 | Parser expression layers rely on scattered lookahead and special cases. | Adopt an explicit precedence/postfix architecture, preferably Pratt-style, without changing accepted syntax unintentionally. |
| `TS-P2-12` | Complete — 2026-07-25 | String escape semantics violate the specification's quoting table: single-quoted strings process escape sequences (`'a\nb'` has length 3) despite being documented as raw, and unknown escapes in double-quoted strings silently drop the backslash (`"\d+"` becomes `d+`), so `("a1" =~ "\d")` is false and the specification's own `=~ "\.cs$"` example matches incorrectly. No single-line quote form preserves a backslash literally. | Single-quoted strings are raw (no escape processing); unknown double-quote escapes are preserved verbatim or produce a targeted diagnostic; every quote form has a conformance case; a migration note records the contract change. |
| `TS-P2-13` | Complete — 2026-07-25 | Expression-position barewords silently coerce to `DateTimeOffset` through the permissive `DateTimeOffset.TryParse` fallback: `1.2.3` and the malformed range `1.5..3` both evaluate to dates in 2003. Relatedly, float-headed and negative-headed ranges (`1.5..3`, `-1..5`) never lex as ranges at all (companion to `TS-P2-03`). | Intrinsic temporal literals parse only through the exact documented format list; dotted-number typos yield barewords or diagnostics, never dates; float and negative range bounds lex correctly or produce a targeted diagnostic. |
| `TS-P2-14` | Complete — 2026-07-25 | Storage-size suffix forms are only recognized as binary-operator operands: `var s = 10kb` fails as unknown command `10kb`, `10kb + 10kb` concatenates to the string `"10kb10kb"`, and `(10kb > 5kb)` silently returns `false` via lexicographic string comparison (the specification says `true`). | Suffix forms lex as typed literals in every expression position (mirroring backtick unit literals), or the suffix syntax is formally deprecated in favor of unit literals with a migration note; the specification's `var small = 10kb` and `(10kb > 5kb)` examples pass as conformance cases; no silent string fallback remains. |
| `TS-P2-15` | Complete — 2026-07-26 | Named arguments are whitespace-sensitive with silent misbehavior: `f(host = "x")` binds the parameter while `f(host="x")` lexes as one bareword and is silently passed positionally as the literal text `host="x"` (companion to `TS-P1-06`). | `name=value` and `name = value` parse identically inside call argument lists, or the fused form produces a targeted diagnostic; a bareword containing `=` is never silently forwarded as a positional argument. |
| `TS-P2-16` | Complete — 2026-07-26 | Module-qualified command dispatch is casing-sensitive despite the documented any-casing promise: `geo.area 2` dispatches, while `Geo.area 2` is a parse error because the capitalized form routes into static CLR member parsing (companion to `TS-P2-01`). | Module-qualified dispatch is independent of module-name casing; the corpus covers capitalized, kebab, underscore, and nested module names in both command and expression position. |
| `TS-P2-17` | Planned | Dictionary-comprehension keys reject operator expressions: `{% $x % 2 => $x <\| for x in 1..4 %}` fails with a missing-list-separator parse error. | Key expressions accept the same operator grammar as value expressions, or the diagnostic explicitly says to parenthesize the key; conformance cases cover operator keys, parenthesized keys, and the specification's examples. |
| `TS-P2-18` | Planned | Member diagnostics leak internal implementation types and misdescribe visibility: denied `shy` access reports "Member 'Secret' was not found on type 'Tosh.Language.ToshClassInstance'", and enum comparison failures name `ToshEnumValue`. | Diagnostics name the shell-level type (`S`, the enum's name) and the true cause (private access versus absence); no `Tosh.Language.*` implementation type name appears in user-facing diagnostics. |
| `TS-P2-19` | Planned | An unparenthesized postfix conditional (`return "big" if $x > 5`) fails with a generic "insert a newline or ';'" error instead of the documented `tosh.parser.expected_postfix_condition` guidance. | Unparenthesized operator conditions after a postfix `if`/`unless` produce a targeted diagnostic that suggests parenthesizing the condition. |
| `TS-P2-20` | Planned | `nameof($foo.Bar)` returns `"foo"` — the parser strips member access and reports the root identifier. | `nameof` on a member chain returns the final segment (matching C#) or produces a targeted diagnostic; the specification documents the chosen behavior. |
| `TS-P2-21` | Planned | A `new` expression cannot take named arguments at all: `new D(1, b = 7)` and `new R("w", Qty = 5)` both fail while parsing with `tosh.parser.assignment_in_predicate`, so the runtime binder is never reached. Function and method calls accept the same syntax. This bounds `TS-P1-06`: constructor named-argument validation is unreachable until the parser accepts the form. | `new Type(name = value)` parses as a named argument for classes, records, and structs; the runtime binder's unknown/duplicate diagnostics apply; a genuine assignment mistake keeps a targeted diagnostic rather than the predicate-assignment message. |
| `TS-P2-22` | Planned | The type checker does not walk class-member annotations, so static checking is materially weaker inside class bodies. `var x: int = "42"` and `func f(x: int)` both report `tosh.type.mismatch`, while the equivalent `prop X: int = "42"`, constructor parameter, method parameter, and property assignment report nothing. Runtime behaviour is consistent (all convert), so this is a static-coverage hole rather than a semantic divergence. | Class property, constructor-parameter, method-parameter, and property-assignment annotations are checked with the same rule and severity as `var` and `func` annotations; a corpus covers matching and mismatching cases in both positions. |
| `TS-P2-23` | In progress — type table populated 2026-07-29; **remainder blocked on `TS-P2-10`** | Parse-time identity decisions rest on *spelling* rather than on facts the runtime already holds. Both casing tests now consult the host's type table first and fall back to `char.IsUpper` only for names no table covers; 182 hardcoded `Current.Text == "…"` comparisons still decide keyword and construct identity, and cannot be driven from a registry that does not exist yet. `TS-P2-16` narrowed one such rule but did not remove the guess. The parser cannot do better today because `ToshParser.Parse` receives only source text, while the command, module, and type registries arrive later at `Lowerer.Lower`. | Identity is resolved against a real table rather than inferred from capitalization: either the parser is given the registries, or the decision is deferred to a later phase that has them. Keyword and construct recognition is driven by the generated language-surface registry (`TS-P2-10`) rather than by scattered literal comparisons. A capitalized module and a lowercase CLR type both resolve correctly. **Status 2026-07-29:** clauses 1 and 3 are met and tested — `ParseContext` carries commands, modules, and types, and a lower-case type alias now resolves where it previously reported `unknown_command`. Clause 2 is blocked: `TS-P2-10` is Planned, so there is no registry to drive keyword recognition from. This item cannot close before that one. |
| `TS-P2-24` | Complete — closed 2026-07-29 on the programme owner's call | Step 2 of the parser roadmap. Structural questions — where a statement ends, where a pipeline stage divides — are answered by heuristics scattered through the recursive-descent parser, each re-deriving the answer with local lookahead. `LiteParser` decides them once over the whole token stream, with paired delimiter frames so a separator inside a nested construct does not split the enclosing statement. Ordinary `ParseBlock` statement paths consume exact-owner promoted candidates and the fallback is deleted; the one purely structural helper, `HasTopLevelPipeBeforeCloseParen`, is retired. The eight surviving `HasTopLevel*` helpers ask semantic questions and are out of the clause's scope — the judgement recorded in the July 29 assessment, resolved in favour of closing. | The parser consumes the lite structure instead of re-deriving it; the `LooksLike*`/`HasTopLevel*` helpers that only answered structural questions are removed; structure agrees with today's parser across the corpus, evidenced by differential tests. |
| `TS-P2-25` | Complete — paired delimiters 2026-07-28 | Plain `{` overloaded blocks, records, dictionaries, sets, predicates, and specialized grammar groups. Position and content lookahead could silently change its meaning and prevented `LiteParser` from promoting brace-enclosed boundaries without duplicating parser grammar. | Ordinary `{ ... }` is a block; records use `{| ... |}`, dictionaries `{% ... %}`, and sets `{: ... :}` with six real delimiter tokens. Specialized parser-owned braces stay plain. Literal dispatch uses the opener alone; legacy `LooksLike*` and generic brace collection parsing are removed. Exact-owner boundary promotion, corpus/spec/tooling migration, targeted recovery diagnostics, rebuilt PDFs, and focused tests land together. |
| `TS-P2-26` | Planned — filed 2026-07-29 | The specification's multi-line **worked examples** have never been executed, and three of the four commands in one of them do not exist. "CSV Processing" used `from-csv`, `to-csv`, and `select Date, Customer, Amount`; the real spellings are `from csv`, `to csv`, and space-separated arguments. "JSON API Processing" used `from-json` and `to-json`. `SpecConformanceTests` did not catch any of it: the corpus was harvested from lines carrying a *documented expected value*, which excludes every multi-line pipeline — precisely the examples a new user copies first. Corrected in the specification 2026-07-29; the coverage gap that let them rot is what this item is for. | The worked examples are executable fixtures with fixture data, run in the suite, and a hyphenated or comma-separated form that does not exist fails at build time rather than being discovered by a reader. The corpus covers multi-line pipelines, not only single expressions with an annotated result. |
| `TS-P2-27` | Complete — decided and implemented 2026-07-29 | `from csv` yields every column as `string`, so the specification's own `\| where _.Amount > 100` fails with "Values of type 'System.String' and 'System.Int32' cannot be ordered" and needs an explicit `cast int`. This sits against the stated design — typed object pipelines — and against `from json`, which *does* produce typed values because JSON carries types. Whether CSV should infer is a decision, not obviously a defect: NuShell infers, PowerShell's `Import-Csv` does not. The diagnostic itself is good and names both types. | Decided: infer numbers and booleans, not dates. Integers narrow to `int` where they fit, decimals become `double`, `true`/`false` become `bool`; dates stay text because `01/02/26` is three different days by locale. Inference is **per column** — one disagreeing cell leaves the whole column textual, so values within a column always compare with each other. A leading zero keeps its column textual (`007`, zip codes); a thousands separator cannot be numeric because the comma is the delimiter. An empty cell is not evidence and becomes `null` in a typed column. `--raw` / `--no-infer` returns everything as text. Applies to `tsv` too, sharing the format. |
| `TS-P2-28` | Complete — fixed 2026-07-29 | A `partial` declaration split across imported files could not be assembled with the **named** import form. `require Sys from "./a.tosh"` followed by `require Sys from "./b.tosh"` failed with `require_failed` — "Export 'Sys' was not found" — on whichever file came second, in either order, while the bare `require "./b.tosh"` form worked. The diagnostic was actively misleading: the merge had succeeded. All four kinds that support `partial` shared the shape `existingDef.MergePartial(…); yield break;` — merge into the existing declaration, then return *before* declaring — so the contributing file exported nothing under the name and the named-import lookup found nothing. Modules additionally accepted `partial module X` extending a non-partial `module X` silently, where classes, records, and structs all refuse it. Partial modules were undocumented. | Both import forms assemble a split partial in either order, for modules, classes, records, and structs; the parts share one export table rather than being copied; extending a non-partial declaration raises `tosh.runtime.partial_mismatch` for all four kinds; the specification documents partial modules and the cross-file split, and states that a non-partial redeclaration replaces rather than merges. Negative control: 8 of 16 new cases fail against the unfixed engine. |
| `TS-P2-29` | Planned — filed 2026-07-29 | `source "./x.tosh"` resolves the relative path against the **working directory**, not the directory of the script doing the sourcing, so a script that sources a sibling file works only when run from its own directory. `require` resolves relative to the requiring script and gets this right. Found while testing partial-module assembly: `source "./a.tosh"` from a script in `/tmp/…/pm/` looked for `/home/komrad/projects/tosh/a.tosh`. | `source` resolves a relative path against the sourcing script's directory, matching `require`; an absolute path and a path relative to the working directory keep working; a script that sources a sibling runs identically from any working directory; the change is noted as breaking if any shipped script relied on CWD resolution. |
| `TS-P2-30` | Complete — aliases documented 2026-07-29; the two dead entries remain, see below | The C#-familiar member-modifier aliases are undocumented. `private`, `abstract`, `readonly`, `required`, `override`, `protected`, `obsolete`, `shared`, and `public` are all accepted, parsed in the same loop as their ToastScript spellings (`shy`, `hollow`, `fixed`, `vital`, `overrule`, `guarded`, `fading`, `static`, `proud`) — but the specification documents only the ToastScript words, so a reader cannot know the aliases exist. Nine working spellings undiscoverable, the same shape as partial modules before `TS-P2-28`. Related: `IsDeclarationModifierWord` lists `abstract` and `private` among *declaration* modifiers, which they are not — `abstract class C { }` and `private var x = 1` both fail. Those two entries are dead. | The specification documents each alias beside the word it means, and states that both spellings work; `IsDeclarationModifierWord`'s two dead entries are removed or the type-level positions are made to accept them, decided explicitly rather than left ambiguous. **Done:** the Member Modifiers section pairs each alias with its ToastScript word (`shy`/`private`, `fixed`/`readonly`, `vital`/`required`, `guarded`/`protected`, `overrule`/`override`, `fading`/`obsolete`, `hollow`/`abstract`) and states that both forms mean the same thing; all nine are in the keyword list and the PDF colouring list, held there by `The_specification_keyword_list_matches_the_registry`. **Still open:** `IsDeclarationModifierWord` names `abstract` and `private` among declaration modifiers where neither works. Left for a deliberate call rather than removed in passing, since honouring them at type level is the other reasonable answer. |
| `TS-P2-31` | Complete — decided and implemented 2026-07-29 | A brace-bodied property accessor silently produces a block value instead of a getter. `prop X { get => ($this.backing * 2) }` works and returns `10`; `prop X { get { return $this.backing * 2 } }` returns a `ShellBlock`, with no diagnostic. The cause is that `ParsePropertyAccessorBlock` parses each accessor body with `ParseArrowStatementBlock`, which calls `ConsumeFatArrow()` unconditionally — so the brace form was never supported, and since `TS-P2-25` made `{` block-only everywhere it now parses as a first-class block *value* rather than erroring. Silent wrong answer rather than a refusal, which is the worst shape available. Accessor blocks are otherwise undocumented. | Decided: **support it**, because a getter restricted to one expression pushes anything conditional into a helper method and `{ ... }` is what a method body already looks like. `ParseAccessorBody` routes a brace to `ParseRequiredBlock` and everything else to the arrow path, so both forms work and the choice is not observable. Multi-statement getters and setters run; `$value` is the incoming value in a setter; an unknown accessor name is still refused. The specification's class-features list now names the form, its two bodies, and `$value`. Negative control: 4 of 6 new cases fail against the unfixed parser, the two that pass being the arrow form and the unknown-accessor diagnostic. |
| `TS-P2-32` | Planned — filed 2026-07-29 | A keyword loses REPL completion to a CLR type whose name differs only in case. Typing `match` offers `Match`, `MatchCasing`, `MatchCollection`, `MatchEvaluator`, `MatchType` and the executable `match_parens` — but not the keyword `match`. `rune` offers only `Rune`. Both words are present in the completion source, so this is ranking or case-insensitive de-duplication rather than a missing entry, and it affects exactly the words that collide with a BCL type name. Found by the `TS-P2-10` completion-coverage guard, which excludes these two with the reason recorded rather than weakening itself. | A keyword ranks at least as high as a CLR type in a position where the keyword is grammatical, or keywords and types are shown as distinct groups rather than de-duplicated against each other; `match` and `rune` complete from their own prefixes, and the two exclusions are removed from the guard. |
| `TS-P2-33` | Planned — filed 2026-07-30 | The LSP feature table documents a comprehension form the language does not have. `let` reads "Example: `[for x in $items let y = x * 2 pick y]`", `pick` reads "Projection clause in a comprehension ... Example: `[for x in $items pick x * 2]`", and `get` reads "Projection clause in a comprehension (alias for `pick`)". None of those parse: comprehensions are body-first with `<|`, as in `[$y <| for x in 1..3 let y = ($x * 2)]`. `pick` is not a comprehension clause at all — it is a builtin **command**, an alias for the `get` projection command, so its hover text is wrong in both category and syntax. Users get editor guidance that fails when followed. | The three entries describe the form that exists, with examples taken from executable fixtures rather than written by hand; `pick` moves out of the keyword table to wherever command help lives; and the LSP's examples are covered by the same mechanism that keeps the specification's honest, so a hover example cannot claim syntax the parser rejects. |
| `TS-P2-34` | Complete — fixed 2026-07-30 | A module-qualified command accepted a *value* argument and refused a *delimited* one. `M.F 5`, `M.F "s"`, `M.F $v` and `M.F (1+2)` worked; `M.F { … }`, `M.F [1, 2]`, `M.F {\| a = 1 \|}`, `M.F {: 1 :}` and `M.F {% … %}` all reported `missing_pipeline_separator` at the opening delimiter. `LooksLikeStaticMemberAccessExpression` reads a dotted name in command position as a CLR member access unless the next token starts a command argument, and `NextTokenStartsCommandArgument` listed only value tokens — no `{`, `[`, or paired literal opener. Same family as `TS-P2-16`, which fixed the value case and left this behind. Reported from a real library whose helpers take block arguments: twelve parse errors from one missing token list. | The delimiter openers count as command arguments, so a qualified command accepts everything an unqualified one does. A following bareword still reads as a sibling argument, keeping `echo Config.version Config.maxRetries` as two member accesses; a delimiter on the *next* line still does not bind, as `HasLineBreakBetween` already required. Negative control across both fixes: 10 of 18 cases fail unfixed. |
| `TS-P2-35` | Complete — implemented 2026-07-30 | `require Outer.Inner from "…" as Alias` did not resolve. Only the outermost export name was looked up, so a library organised as nested modules — a namespace-like structure — reported the whole dotted string as a missing export, accurate but unhelpful given `Outer` was present and `Inner` was inside it. | A dotted import name walks module exports and declares whatever the final segment names — module, type, refinement, command, or variable — binding the final segment when no `as` alias is given. Paths of any depth work, partial modules at every level work, and importing the outer module keeps working. **Found while fixing it:** `ToshEngine` carries *two* `ImportRequiredArtifact` overloads twelve thousand lines apart, and the `require` statement path uses the second — so the first patch left the feature compiled and unreachable. Both now route through one resolver. |
| `TS-P2-36` | Planned — filed 2026-07-30 | Generic static methods do not infer their type argument. `System.Threading.Tasks.Task.FromResult(7)` fails with "No overload matched static method 'FromResult' on 'System.Threading.Tasks.Task' with 1 argument(s)", because `FromResult<TResult>` needs `TResult` inferred from the argument. This matters more now that `TS-P1-27` made explicit `await` the model: `Task.WhenAll`, `Task.FromResult` and friends are exactly the helpers that model invites, and none of them are reachable. | A generic static method infers its type arguments from the supplied arguments, as C# does; `Task.FromResult(7)`, `Task.WhenAll($a, $b)` and `Enumerable.Empty<T>()`-style calls resolve; an argument set that genuinely cannot be inferred produces a diagnostic naming the type parameter rather than reporting the overload as missing. |
| `TS-P2-37` | Planned — filed 2026-07-30 | The shell type alias `file` shadows `System.IO.File`, so `File.ReadAllTextAsync(…)` reports "No overload matched static method 'ReadAllTextAsync' on 'System.IO.FileInfo'" — the alias resolves `File` to `FileInfo` and the static is looked up on the wrong type. The fully-qualified `System.IO.File.ReadAllTextAsync` works. Same shape as the `double`/`map`/`set` collisions `TS-P2-23` handled for *bare* names, but this one is in the member-access path where a declaration cannot win. | A capitalized name that matches a shell type alias only case-insensitively resolves to the CLR type in member-access position, or the diagnostic names both candidates and says which was chosen; `File.ReadAllTextAsync` works without qualification, and `var f: file = …` keeps binding to `FileInfo`. |
| `TS-P2-38` | Instrumented — 2026-07-30: sampler built, leading hypothesis named, cause not yet proven | A **128 GB** machine was exhausted three times in one session while the suite ran. Attributing it to the suite was wrong and the measurements say so: RSS across a full run is flat — 174 MB at start, ~2.8 GB within three seconds, then dead flat at 2,744 MB for 130 seconds while 3,500 tests execute. At 32 threads the numbers are indistinguishable from 8. **The suite's normal behaviour cannot exhaust 128 GB.** The reason the real consumer stayed unknown is that the first sampler matched only `dotnet`/`testhost`, so it could not have seen a non-.NET process. | `scripts/memwatch.sh` samples **total system memory** and names every consumer over 100 MB, dumping the full picture while the consumer still exists rather than after the OOM killer has removed the evidence. Bash rather than TōSh deliberately: a diagnostic must not share a failure mode with its subject. **It vindicated itself on first run** — the largest process on this machine was `baloo_file` at **4.4 GB**, with no `dotnet` process in the top fifteen. The old sampler would have reported nothing. **Leading hypothesis, not yet proven:** KDE's file indexer. `balooctl status` reports **1,050,919 files indexed** and a **4.76 GiB** index, and `~/.config/baloofilerc` contains no exclusion rules at all — so it is watching `~/projects/tosh` (6.3 GB, 3,536 files under `bin/`, `obj/` and `artifacts/`) and `~/.nuget` (7.4 GB). A build/test cycle rewrites thousands of files under `$HOME`, baloo queues them via inotify, and runaway `baloo_file` memory during heavy indexing is a documented failure mode. It fits every fact: correlates with suite runs without being caused by the suite's own memory, is invisible to a .NET-only sampler, is unbounded, and takes the whole session down including the editor. **Confirmation requires catching the next event under `memwatch.sh`**; until then this is correlation plus mechanism, not a proven cause. The cheap preventive step is excluding build output from indexing, which is worth doing regardless since indexing `bin/obj` is pure waste. A memory-capped run remains the standing instruction — not because the suite is at fault, but because a cgroup cap turns a machine-wide failure into a killed process. |
| `TS-P2-39` | Instrumented — 2026-07-30: three suspects exonerated, not reproducible, failures now self-diagnosing | Two unrelated tests failed *only* under parallel load and passed 3/3 in isolation: `ScopeAndChannelTests.Scope_awaits_spawned_jobs_and_returns_completions` (three sightings) and `GenericClassTests.Generic_class_user_interface_constraint_accepts_implementing_class`. | **Not reproduced in six consecutive full-suite runs**, three of them at 32 parallel threads — and the knob was verified to apply rather than assumed, by measuring the suite at `MaxParallelThreads=1` (4m05s against 2m38s at 8). Three candidate causes were examined and all three are exonerated: (1) `DotNetTypeResolver._negativeResultCache` is a **CLR** type cache, and a ToastScript `interface` is a `ToshClassDefinition` in a scope, not a CLR type, so caching "no CLR type named IShape" is correct and cannot break a ToastScript constraint — which also explains why the earlier deterministic reproduction failed; (2) `ToshTypeParameterConstraintRegistry` is a `static readonly` dictionary of built-ins that nothing writes at runtime; (3) `ScopeCommand` identifies its jobs by diffing the job table before and after its block, which *would* be a race across concurrent engines — but `ToshRuntime._jobs` is a per-instance field and every test builds its own runtime, so no cross-test capture is possible. What remains plausible and unproven is external-process spawn failure under load: the scope test spawns two real `dotnet` children and asserts both reach `Completed`. Each sighting reported only "Expected Completed, Actual Failed", which cannot distinguish a child that failed to start from one that ran and exited non-zero — so the assertions now render the completion's exit code, pid, duration and stderr, all of which they previously discarded. The next sighting arrives diagnosable; chasing it further without evidence would be guessing. |
| `TS-P2-40` | Complete — fixed 2026-07-30 | Completion silently dropped one of any two members whose names differ **only in case**. A class holding `shared func icmp()` beside `shared prop Icmp` offered one of them and never both; which one depended on enumeration order, because `OrderSuggestions` ran `DistinctBy(Label, OrdinalIgnoreCase)` *before* `OrderBy`. The member-suggestion dictionaries were case-insensitive for the same reason. This wore the same symptom as `TS-P1-28` — "the property does not even show up in autocomplete" — while having nothing to do with static-ness. | De-duplication is ordinal in the two member-suggestion dictionaries and in `OrderSuggestions`, so case-distinct members both appear; an exact duplicate spelling still collapses, which is the case de-duplication is actually for. |
| `TS-P2-41` | Planned — filed 2026-07-30 | A bare sibling member reference inside a class body produces a diagnostic that suggests **shell commands**. `static prop Y => f()` beside `static func f()` reports "Command 'f' is not a registered builtin or function declared in this source — did you mean 'df', 'fg', or 'if'?". The rule itself is correct and uniform: members are reached through `ClassName.` or `$this.`, and bare `f()` fails from an instance method too, so this is not a resolution defect. But the suggestion list is actively misleading — it names three unrelated shell commands when a member of the enclosing class differs by nothing at all. | When an unresolved bare name matches a member of the lexically enclosing class or struct, the diagnostic suggests the qualified form (`did you mean 'C.f()'?`) ahead of any command-name suggestions, and says that members require a qualifier. The specification states the rule, which today it only implies via "`$this` inside methods". |
| `TS-P2-42` | Complete — fixed 2026-07-30 | **The protocol surfaces had no test referencing them at all.** A survey found `Tosh.Lsp` (527 lines), `Tosh.Mcp` (787), and `Tosh.Client` (680) with zero test-file mentions between them — and they are the surfaces where breakage is quietest, since nothing in the suite ever asks a server to answer. `Tosh.Client` is the sharper case: it deliberately depends on nothing else in the tree, so the TSSP wire format was the only thing keeping it in agreement with `TsspParser` in the stdlib, and nothing checked that agreement. Same shape as `TS-P1-30`: coverage is deep where it is cheap (in-process engine) and absent where it is expensive (protocol, tty, editor). | `ProtocolSmokeTests` drives each surface in-process over `MemoryStream`, since all three take their streams as constructor arguments — six assertions, 129 ms, no process spawning. Smoke rather than conformance: each asserts the thing starts, parses a real request, and returns a well-formed response, which is enough to make breakage loud. The TSSP case round-trips writer against parser, which is the drift-guard pattern applied to a protocol rather than to a pair of methods. Negative control broke one path per surface and failed exactly the three tests that cover them. |
| `TS-P2-43` | Complete — fixed 2026-07-30 | **Two directories under `src/` were not in the build.** `src/Tosh.Dap` (514 lines) had a `.csproj` that no solution included, so an ordinary build had not compiled it since April — while the VS Code extension contributes a `tosh` debugger, resolves `src/Tosh.Dap/bin/.../Tosh.Dap.dll`, and **builds the project on demand** when it is missing. A user pressing F5 was the only thing checking whether it still compiled. `src/Tosh.Core` (879 lines) had no `.csproj` at all. | **`Tosh.Dap` added to `Tosh.slnx`** — it still compiled cleanly against three months of Runtime changes, which was luck rather than a guarantee — with a smoke test beside the other protocol surfaces and a negative control confirming it fails when `initialize` stops being answered. **`Tosh.Core` deleted.** Its removal was deliberate, not accidental: `469e9f9` (2026-04-29, "rename Tosh.Core→Tosh.Runtime") deleted every file and removed the project from the solution. The three files present today were re-added in May by two automated "Session … checkpoint turn 0" commits — a resurrection artifact rather than a decision — and `src/Tosh.Core/ToshRuntime.cs` was a 775-line near-copy of the live 804-line one, identical but for its namespace and everything added since (it lacked `NativeTypes` from the raw-struct work). Two tracked files with the same name where one is dead is a trap for both a reader and an IDE. The stale `"Tosh.Core.dll"` entry in `ToshPublisher` went with it. |

**Implementation note for `TS-P2-11` (July 25 review recommendation).** The
`TS-P2-01`/`TS-P2-02`/`TS-P2-04`/`TS-P2-12`–`TS-P2-15` family shares one root
cause: operators and typed literals are carved out of barewords by
context-sensitive guesswork after lexing. A mode-switching lexer (command
mode versus expression mode, as in Nushell) that emits `-`, `?.`, `=`, `..`,
and typed literals as real tokens in expression position would remove the
guesswork before the Pratt rewrite, and the parser already tracks the
equivalent mode at every call site.

## P3 — Features Enabled by Stabilization

These are intentionally not part of the first repair slice.

| ID | Status | Feature | Intent |
|---|---|---|---|
| `TS-P3-01` | Proposed | `tosh check <file>` | Run lexer, parser, binder, and type analysis without execution; support human, JSON, and SARIF diagnostics. |
| `TS-P3-02` | Proposed | `let` bindings | Express runtime immutability without weakening the meaning of `const`. |
| `TS-P3-03` | Proposed | Reverse/static operator hooks | Give right operands correct behavior for noncommutative mixed-type operations. |
| `TS-P3-04` | Research | Explicit stream/collection shape | Remove cardinality lookahead while preserving object-valued pipelines and a reasonable migration path. (Concrete motivating asymmetry: `[1,2,3] \| count` is 3 while a piped dictionary counts as 1.) |
| `TS-P3-05` | Proposed | Uniform thrown-value protocol | Wrap thrown non-exception values so `catch (e)` always exposes `.Message` and a kind discriminator; today `throw "boom"` followed by `$e.Message` is a runtime error. |
| `TS-P3-06` | Proposed | Interpolation format specifiers | Support `$"{expr:F2}"`-style format clauses; today the clause is lexed into the bareword and attempts to run a command named `$pi:F2`. |
| `TS-P3-07` | Research | Unify `StorageSize`/`TemporalAmount` with the Quantity unit system | Two systems model the same domains (`10kb` versus `` 10`kB ``, `5s` versus `` 5`s ``) with a promotion bridge; evaluate making suffix literals sugar for Quantities so `TS-P2-14` lands on one system. |
| `TS-P3-08` | Proposed | Parser-owned typed structural regions | Replace brace-content classification with parser-owned regions (`Block`, member list, arm list, projection, destructuring, accessors, and other grammar roles). Regions retain exact opener/closer ownership, promote only boundaries proven to belong to statement blocks, and are shared with formatters and language services; `LiteParser` must not grow a shadow `ClassifyBrace` grammar. |
| `TS-P3-09` | Proposed | Prefix `!` negation | Accept `Bang` in prefix position so `!$x` means `not $x`. Smaller than it looks: the lexer already emits a `Bang` token as the fallthrough after `!=` and `!~` (`ToshLexer.cs`), so only the parser needs to change, alongside whatever `TS-P2-02` does for unary `-`. Two constraints: (1) it is why the dict delimiter is `{%` and not `{!` — keeping `{!` unclaimed is what leaves this open, so neither item should be changed without the other; (2) `!!`, `!$`, `!^`, and `!*` are consumed by `HistoryExpansionUtilities` on the raw REPL line *before* lexing, so scripts are unaffected but an interactive `!$x` collides with the `!$` word designator — the item must decide that case explicitly rather than let whichever layer runs first win. |
| `TS-P3-10` | Complete — decided and implemented 2026-07-29 | Collection rendering is split between two styles. Records, dicts, and sets have bespoke source-like renderings (`{| a = 1 |}`, `{% "k" => 1 %}`, `{: 1, 2 :}`) that parse back as written. Arrays and lists fall to `ObjectFormatter`'s generic container path and render with a CLR type header over multiple lines (`Int32[] [\n  1\n  2\n]`), which is a display form rather than source. Found by `FormatRoundTripTests`, which had to be scoped around it. | Decided: header at root, source-like nested. A collection keeps `Int32[] [ 1, 2, 3 ]` when it is the whole result, where the element type is informative and the rendering is display; nested inside another value it renders `[ 1, 2, 3 ]` and joins the round-trip property — the same root/nested split strings already had. `FormatRoundTripTests` covers nested arrays and pins the root header. |
| `TS-P3-11` | Complete — decided and implemented 2026-07-29 | The syntax and documentation call `{| … |}` a **record**; `type-of` reports **`table`**. `ExpandoObject` maps to the `Table` descriptor, `dynamicrecord` is already an alias for it, and `table`'s own constructor signature reads `table([record] | key, value, ...)` — so both words are in use for one concept. Not a defect: the model may intend a record to be a single-row table. | Decided: `record` wins. `type-of {| a = 1 |}` reports `record`; `table` and `dynamicrecord` remain resolvable annotations; the constructor signature reads `record(...)`; help and the specification say `record` and name the aliases. |

## Test Strategy

### 1. Specification conformance corpus

Representative examples in the specification should exist as executable
fixtures with expected typed results and diagnostics. The corpus should
include successful and failing programs.

Priority evidence (July 25 review): four specification examples currently
fail as written — `var small = 10kb` (`TS-P2-14`), `(10kb > 5kb)`
(`TS-P2-14`), `where _.Name =~ "\.cs$"` (`TS-P2-12`), and
`$myDict | entries` (the `entries` command does not exist). Extracting the
spec's examples into fixtures would have caught all four mechanically.

### 2. Differential execution

Run the same corpus through:

- the ordinary interpreter;
- any bound/optimized interpreter path;
- compiled assemblies under `Tosh.Compiler.Runtime`; and
- MCP execution where cancellation or diagnostic transport matters.

Compare values, CLR value types, stdout, stderr, diagnostic codes, and
exit behavior. Operator tests that only prove a switch case is reachable
are not semantic parity tests.

### 3. Invariant-focused tests

Assignments and construction need explicit tests for:

- evaluation count and order;
- resolve-before-mutate behavior;
- partial failure;
- annotations and generic type parameters;
- const/immutable bindings;
- nearest lexical scope; and
- interpreter/compiler agreement.

### 4. Generated-surface validation

Every parser-recognized keyword, operator, and declaration kind should
be checked against completion, hover, semantic tokens, document symbols,
help, MCP metadata, and specification generation.

### 5. Lexer/parser fuzzing

Random and grammar-aware inputs through the lexer, parser, and binder
must never produce a raw CLR exception or terminate the process. The
`tosh check` mode (`TS-P3-01`) is the natural harness. Seed classes from
the July 25 review: dotted-number barewords (`TS-P2-13`), oversized
binary/octal literals (`TS-P2-05`), deep expression nesting and recursion
(`TS-P0-07`), and malformed range forms.

## Documentation Drift to Resolve

These mismatches should be repaired alongside their owning work item:

- the specification says failed `as` returns `null`, while runtime help
  and implementation currently throw;
- storage-size suffix examples are described as literals but currently
  behave as strings when both operands are suffix forms;
- CLI help omits compilation and metadata-export modes;
- compile output is documented as requiring `-o`, though it can be
  derived;
- startup documentation disagrees about whether `--no-profile` skips
  autoload;
- operator help/MCP metadata misstates case sensitivity and supported
  operators;
- the specification's operator-precedence table disagrees with the
  implementation in four ways: the implementation parses the ternary
  below `??` (matching C#; the table says the reverse), the separate
  comparison/type-test/membership levels (6/7/8) are folded into one
  left-associative level, ranges bind at primary precedence
  (`TS-P2-03`), and `**` versus unary minus (`TS-P2-02`) — the table
  should be regenerated from the `TS-P2-10` surface registry;
- the specification's equality cascade omits both the `TypeConversion`
  coercion (`1 == "1"` is true) and the case-insensitive `ToString`
  fallback for mixed types (owning item `TS-P1-14`);
- the specification's comprehension chapter pipes `$myDict | entries`,
  but no `entries` command exists — implement it or fix the example;
- storage-size suffix behavior is worse than recorded above: value
  positions treat the suffix form as an unknown command and comparisons
  silently return wrong booleans (owning item `TS-P2-14`); and
- the specification's LaTeX build depends on the absolute personal path
  `/data/pic/Colby Family/Colby-Crest.png` for the cover image; ship the
  asset in `docs/spec/` or guard it with `\IfFileExists`.

## Non-Language Follow-Ups

- `Tosh.DevCompanion` pulls `SQLitePCLRaw.lib.e_sqlite3` 2.1.10, which has
  a known high-severity vulnerability (GHSA-2m69-gcr7-jv3q); bump the
  package.

## Decision Log

### July 25, 2026 — Stabilization program opened

- Prioritize invariant safety and semantic convergence over new syntax.
- Adopt the working semantic decisions recorded above.
- Begin with tuple assignment, channel selection, and null-coalescing
  assignment.
- Preserve existing unrelated working-tree changes and land fixes in
  reviewable slices.

### July 25, 2026 — Class construction protocol

- Construct every inheritance layer exactly once in base-to-leaf order.
- Prefer `extends Base(args)`; retain a leading `$super(args)` as the
  alternative constructor-initializer form.
- Invoke an available zero-argument base constructor implicitly.
- Diagnose duplicate, non-leading, and missing base initializers.

### July 25, 2026 — Defer unwinding protocol

- Register only reached defers and attempt every registered cleanup once
  in LIFO order, even after a cleanup failure.
- Preserve a sole failure unchanged; use the dedicated ordered defer
  aggregate only when failures compete.
- Keep cancellation outward-facing while running cleanup under a shielded
  token and retaining any cleanup failures as defer metadata.
- Preserve ordinary body output, discard cleanup output, and suppress
  cleanup-local jumps for compatibility.

### July 28, 2026 — Paired collection delimiters

- In ordinary expression and command-argument grammar, reserve
  `{ ... }` for blocks. Records use `{| ... |}`, dictionaries
  `{% ... %}`, and sets `{: ... :}`.
- Keep grammar-owned structural braces — member and arm lists,
  destructuring, projections, accessors, and similar forms — as plain
  braces. The recursive parser, not token content, owns those roles.
- Emit a distinct token for each paired opener and closer. Remove the
  old record/dictionary lookahead classifiers and generic brace
  collection literal rather than carrying compatibility ambiguity.
- `LiteParser` pairs ordered delimiters and records an exact owning
  opener for each candidate boundary. Only a parser-proven block opener
  may promote its candidates; specialized braces remain unpromoted.
- Delimiter recovery is directional, not family-interchangeable. A paired
  closer may unwind malformed inner frames only to reach its own exact
  opener. Plain `}` may terminate the nearest paired literal as a
  diagnosed recovery substitution, matching the recursive parser, but
  must not skip that literal to close a deeper ordinary block. A
  mismatched paired closer never closes an unrelated literal.
- File `TS-P3-08` for parser-owned typed structural regions shared by
  parsing, formatting, and language services. Do not evolve the
  structural pass into a second brace grammar.

## Progress Log

### July 25, 2026

- Completed repository-wide language, runtime, compiler, parser, tooling,
  and specification review.
- Reproduced the P0 and P1 defects with focused ToastScript programs.
- Opened this plan and began the first P0 implementation slice.
- Made tuple assignment resolve bound symbols and validate every target
  before committing in both execution modes, preserving annotations,
  constants, shadowing, captures, and nearest lexical scope. Compiled
  values are converted into temporaries before any target store.
- Replaced destructive channel-selection races with non-consuming
  readiness waits followed by one atomic receive. Losing values and
  valid null payloads are preserved; cancellation observes all waits.
- Implemented lazy `??=` for variable, member, and index targets in the
  interpreter and IL emitter. Assignment-only closure captures are now
  recorded by lowering.
- Replaced leaf-scoped class initialization with a recursive,
  exactly-once construction frame for every class layer. Each constructor
  binds its own arguments, constructs its base, initializes only its own
  properties, and then runs its body.
- Preserved explicit empty `extends Base()` initializers, added implicit
  zero-argument base construction, and initialized CLR bases before
  derived properties.
- Normalized a leading `$super(args)` into the same initializer phase.
  Mixed, repeated, late, missing, base-less, and repeat-at-runtime forms
  now fail through structured diagnostics rather than double-running or
  silently skipping a base.
- Closed compiler shell hierarchies over their complete source-declared
  base chain. Unsupported bases retain source replay instead of silently
  inheriting from `System.Object`; emitted shells now pass real header or
  leading-`$super` arguments to the base constructor.
- Migrated the two generic-class fixtures that redundantly combined
  header arguments with `$super(args)`, and regenerated the 468-code
  diagnostic reference and runtime manifest.
- Completed `TS-P0-05` with one shared failure protocol across the
  interpreter and emitted IL. Reached cleanup registration is distinct
  from declaration; every cleanup is exhausted in LIFO order; body,
  cleanup, and nested failures retain deterministic ordering and exact
  sole-failure identity; cancellation remains outward-facing while
  cleanup receives a shielded execution attempt.
- Added stable body/cleanup diagnostics and a public CLR inspection
  surface. Aggregate construction now enforces competing failures,
  snapshots mutable or side-effecting input once, ignores invalid
  external metadata, and preserves empty nested diagnostics through
  stable fallbacks.
- Covered pending `return`, `break`, and `continue`, cleanup-local jumps,
  nested flattening, cancellation metadata, all compiled body roots,
  reached-only registration, output preservation, and sole/competing
  failures in both execution modes.
- The defer-specific part of `TS-P1-07` is covered by this slice. Broader
  nested-control-flow streaming and materialization remain planned under
  `TS-P1-07` and `TS-P1-08`.
- Validation:
  - focused assignment/channel regressions: 16 passed;
  - combined stabilization/compiler regression selection: 322 passed;
  - compiler feature matrix: 146 passed;
  - surrounding language/concurrency suites: 226 passed;
  - lowerer/type-checker/compiler audit selection: 184 passed;
  - broad run excluding the environment-blocked SDK packaging class:
    2,656 passed with zero failures;
  - the complete first run reported 2,655 passed and 2 SDK packaging
    failures. One transient packaged-SDK failure passed alone; the
    remaining test is blocked by its local-only NuGet source lacking
    `Microsoft.AspNetCore.App.Ref`, unrelated to this language slice.
  - class-construction interpreter/compiler regressions: 19 passed;
  - class, generic, and surrounding language suites: 214 passed;
  - complete bound-unit emitter suite: 315 passed;
  - compiler feature matrix: 148 passed;
  - full solution run: 2,684 passed and one environment-blocked SDK
    packaging failure (the same local-only
    `Microsoft.AspNetCore.App.Ref` restore limitation).
  - defer runtime/interpreter/compiler regressions: 40 passed;
  - defer plus complete bound-unit emitter, compiler feature-matrix, and
    legacy defer coverage: 507 passed;
  - regenerated diagnostic manifest: 470 codes, including only the two
    public defer diagnostics rather than private exception-metadata keys;
  - rebuilt 276-page language specification and visually verified the
    complete defer section;
  - full solution validation for this slice: 2,724 passed with no skips
    and one environment-blocked SDK packaging failure. The sole failure
    is the same local-only NuGet source limitation:
    `Microsoft.AspNetCore.App.Ref` is unavailable while restoring the
    packaged-SDK fixture.

### July 25, 2026 — Second review pass

- Ran an independent end-to-end review (specification, lexer, parser,
  operator runtime, class construction, channels) with live CLI
  reproductions against the current tree.
- Verified the completed P0 fixes behaviorally: tuple swap resolves
  before mutation; `??=` is lazy for non-null targets; three-level class
  chains construct base-to-leaf with per-layer argument binding;
  `$super.method()` and virtual dispatch from base constructors behave
  correctly; channel selection is non-destructive by design.
- Reconfirmed then-open items live: `TS-P1-04`, `TS-P1-10`, `TS-P1-11`,
  `TS-P1-12`, `TS-P2-01`, `TS-P2-02`, `TS-P2-04`, `TS-P2-05`, and the
  `as`-throws documentation drift.
- Filed the new items `TS-P0-07`–`TS-P0-08`, `TS-P1-14`–`TS-P1-19`,
  `TS-P2-12`–`TS-P2-20`, and `TS-P3-05`–`TS-P3-07`; recorded the
  mode-switching-lexer recommendation under `TS-P2-11`; added the
  fuzzing test-strategy section; expanded the documentation-drift list
  (precedence table, equality cascade, `entries`, storage suffixes,
  spec cover-image path) and noted the `Tosh.DevCompanion` SQLite
  dependency advisory.
- Full solution run at the end of the review: 2,713 passed, 0 failed,
  0 skipped in 2m33s, including the previously environment-blocked SDK
  packaging test.

### July 25, 2026 — Async class execution

- Completed `TS-P0-06` by adding cancellation-aware asynchronous
  protocols for shell construction, static and instance invocation,
  record-member access, enumeration, indexing, and reflection access.
  Existing synchronous interface members remain compatibility adapters.
- Replaced interpreted class-body blocking bridges with awaited
  execution for constructors, recursive base construction, methods,
  static methods, property initialization/getters/setters, refinements,
  return annotations, operator hooks, interpolation, and `$super`.
- Routed `new`, fluent access, `call`, `call-method`, destructuring,
  spread, and string-index operations through the asynchronous protocol.
  Async lookup misses no longer retry the same shell member
  synchronously.
- Made lazy initialization transactional: cancellation or failure clears
  the in-progress marker without committing the value, successful access
  commits once, and recursive access is diagnosed. A final concurrency
  audit replaced the marker with single-flight initialization so
  unrelated concurrent readers share one computation instead of being
  mistaken for recursive re-entry.
- Made user-error wrapping and uncaught diagnostic metadata asynchronous,
  including computed `Message`, title, code, label, help, and information
  members. Cancellation is never swallowed while probing optional
  metadata.
- Closed the implicit CLR-protocol fallback in `==`, `!=`, membership,
  and switch matching. Nested collections now await ToastScript
  `Equals`/`ToString` hooks while preserving symbolic-operator
  precedence, reference/null shortcuts, left-biased dispatch, and the
  existing conversion order.
- Added per-command REPL cancellation. `SIGINT`/`CancelKeyPress` cancels
  only the active execution, reports exit code 130, and leaves the
  session ready for the next command. Prompt evaluation itself is now
  awaited rather than blocked.
- Validation:
  - class cancellation, recovery, lazy retry, concurrent lazy
    single-flight, property/refinement, indexing, enumeration,
    destructuring, and spread: 27 passed;
  - equality, membership, switch, and string-conversion cancellation
    plus semantic guards: 13 passed;
  - thrown-error cancellation and normal diagnostic mapping: 5 passed;
  - focused class/operator/MCP/REPL selection: 208 passed;
  - complete engine and language-feature suites plus focused class,
    compiler, MCP, and REPL coverage: 703 passed;
  - complete REPL line-editor/cancellation suite: 78 passed;
  - focused MCP class-method and constructor timeout cases: 2 passed in
    616 ms with a 250 ms timeout per request;
  - solution-wide run after the final lazy-property audit: 2,772 passed,
    0 skipped, and one
    environment-blocked packaged-SDK fixture. That fixture's isolated
    `local` NuGet source could not provide `Microsoft.AspNetCore.App.Ref`;
    no language or runtime test failed;
  - parity advisory passed separately with exit code 0. The unmodified
    solution command's nested parity post-build invocation stalled before
    launching a test host, so the completed full run disabled the two
    already-validated post-build advisory/spec hooks;
  - zero-warning Language and CLI builds, and `git diff --check` passed.

### July 25, 2026 — Structured recursion depth

- Completed `TS-P0-07` with one asynchronous-flow-local execution-depth
  guard shared by the interpreter, emitted code, and compiler runtime.
  The default and hard maximum are 128 active ToastScript frames; sessions
  may select a stricter limit through
  `$tosh.Config.Shell.MaxRecursionDepth`.
- Guarded scripts, functions before default-argument evaluation, class
  methods, constructors, lambdas, nested `eval`/`source`, emitted module
  and class methods, emitted blocks, and direct compiled constructors.
  Compiler leases unwind through generated `finally` handlers.
- Replaced process-killing CLR stack overflow with the stable
  `tosh.runtime.recursion_limit_exceeded` diagnostic, including the
  configured limit and a compact innermost-first ToastScript frame
  summary. A handled diagnostic releases every frame and leaves the
  engine and REPL usable.
- Added direct, mutual, default-parameter, lambda, class-method,
  constructor, `eval`, `source`, recovery, finite-depth, cancellation,
  configuration, REPL, and direct-compiled regression coverage.
- Validation:
  - focused recursion regressions: 15 passed;
  - complete solution run: 2,787 passed, 0 skipped, and one
    environment-blocked packaged-SDK fixture. Its isolated `local`
    NuGet source could not provide `Microsoft.AspNetCore.App.Ref`; no
    language, runtime, REPL, or compiler test failed;
  - parity advisory passed separately with exit code 0;
  - regenerated diagnostic manifest/reference: 471 codes;
  - rebuilt the 277-page language specification and visually verified
    the complete recursion-depth section;
  - `git diff --check` passed.

### July 25, 2026 — Single-channel receive

- Completed `TS-P0-08` with a looping readiness/commit protocol for
  one-item receives. A consumer that loses an advisory readiness race to
  another reader waits again rather than returning a fabricated null or
  completion result.
- Added public `ShellChannelReceiveResult` and
  `ReceiveResultAsync`: `HasValue: true, Value: null` is a null payload,
  while `HasValue: false` is closed-and-drained. The existing
  `ReceiveAsync` return type remains compatible and now raises
  `ChannelClosedException` at completion, making every returned null a
  real payload.
- Kept `channel-recv` as a stream and `channel-select` on its
  non-destructive readiness/commit protocol. The command reference now
  states explicitly that null emits one value and closure emits none.
- Added structured and legacy receive races, 64-consumer contention,
  null/closure, cancellation recovery, and command-stream regressions.
- Validation:
  - complete focused concurrency and channel selection: 30 passed;
  - complete solution run: 2,793 passed, 0 skipped, and one
    environment-blocked packaged-SDK fixture. Its isolated `local`
    NuGet source could not provide `Microsoft.AspNetCore.App.Ref`; no
    channel, language, runtime, or compiler test failed;
  - parity advisory passed separately with exit code 0;
  - rebuilt the 277-page language specification and visually verified
    the updated `channel-recv`/`channel-select` reference page;
  - `git diff --check` passed.

### July 25, 2026 — Canonical truthiness

- Completed `TS-P1-01` with `ToshTruthiness.IsTruthy` as the sole
  semantic primitive. Interpreter and standard-library conditions,
  compiler-host and operator compatibility wrappers, emitted control
  flow, logical operators, match/comprehension guards, event `when`
  guards, and predicate commands now converge on the same contract.
- Made null, false, numeric zero, floating/complex NaN, empty strings,
  and empty synchronous enumerables falsy. Non-zero numerics (including
  infinities), non-empty strings/enumerables, and all other non-null
  objects are truthy. General enumerables receive one disposable probe.
- Kept explicit boolean conversion distinct from truthiness and retained
  the refinement type system's strict-boolean `where` contract.
  Refinement coercion guards use broad truthiness.
- Removed boolean-only condition and predicate diagnostics from the
  interpreter and type checker. The generated catalog now contains 468
  codes.
- Updated command metadata to describe truthy/falsy predicate results,
  corrected the logical-operator documentation to state that operators
  return booleans, and documented the complete matrix and single-pass
  enumerable caveat.
- Validation:
  - truthiness conformance corpus: 11 passed;
  - truthiness, type-checker, and complete bound-unit emitter selection:
    390 passed;
  - solution-wide run excluding the one environment-blocked packaged-SDK
    restore fixture: 2,818 passed with zero failures or skips;
  - the excluded fixture still fails in isolation because its local-only
    NuGet source cannot provide `Microsoft.AspNetCore.App.Ref`; a second
    packaged-SDK process that exited 134 during the first concurrent run
    passed immediately in isolation;
  - parity advisory passed independently with exit code 0;
  - rebuilt the 279-page language specification, visually verified the
    truthiness, logical-operator, match-guard, and event-guard pages, and
    confirmed both checked-in PDF names are byte-identical;
  - zero-warning test-project build and `git diff --check` passed.

### July 25, 2026 — Exact literals and string escapes

- Completed `TS-P2-12`: ordinary single-quoted strings are raw;
  double-quoted and ANSI-C forms preserve unknown escape pairs; malformed
  empty `\x`/`\u` pairs no longer inject null characters. All eight quote
  forms and the documented regex spellings have executable cases.
- Completed `TS-P2-13`: intrinsic `DateTimeOffset` recognition has an
  exact ISO-only entry point, while explicit date commands and importers
  retain the forgiving parser. Canonical IPv4 requires four decimal
  octets, so dotted numeric typos can become neither dates nor abbreviated
  addresses.
- Signed, floating, and radix numeric range heads now split from `..`.
  Negative integer ranges execute normally; literal fractional or
  out-of-range bounds report
  `tosh.parser.range_requires_integer`.
- Completed `TS-P2-14`: every documented SI/binary storage suffix becomes
  `StorageSize` in expression context, including declarations,
  collections, arithmetic, comparison, and emitted code. Raw command
  arguments intentionally remain strings. Invalid and overflowing forms
  never silently become expression strings or leak decimal overflow.
- Validation:
  - literal-cluster and complete bound-unit emitter selection: 397 passed;
  - surrounding engine, type-checker, compiler-feature, literal, and
    bound-unit-emitter selection: 1,030 passed;
  - focused project build: zero warnings and errors;
  - local CLI probes confirm `1.2.3` is diagnosed rather than coerced,
    `1.5..3` receives the range-specific diagnostic, `-1..2` enumerates,
    and the specification's storage examples produce typed values;
  - full solution: 2,886 passed and one environment-only packaged-SDK
    restore fixture failed because its local NuGet source lacks
    `Microsoft.AspNetCore.App.Ref`;
  - regenerated 469 diagnostic codes and passed the parity advisory;
  - rebuilt the 280-page specification, visually checked the affected
    pages, and confirmed both checked-in PDF names are byte-identical.

### July 25, 2026 — Canonical collection containment

- Completed `TS-P1-02`: `collection contains value` and
  `value in collection` now share one membership contract instead of
  stringifying collections.
- Strings use ordinal substring matching. Dictionaries search keys, not
  values. CLR enumerables and shell-native collections search elements
  with canonical equality; scalar left operands return false.
- Added an explicit shell-enumeration capability marker so user-class
  scalar iteration is not mistaken for collection containment, while
  ranges, lazy sequences, trees, and classes with an enumerator retain
  their collection behavior.
- Interpreter membership uses the asynchronous equality path, preserving
  user-defined `Equals` dispatch and cancellation. Emitted code reaches
  the same runtime protocol.
- Validation:
  - containment, equality-cancellation, and complete bound-unit-emitter
    selection: 361 passed;
  - full solution: 2,897 passed; one packaged-SDK subprocess that exited
    134 under the concurrent load passed immediately in isolation, while
    the only reproducible failure remains the local-only NuGet source
    missing `Microsoft.AspNetCore.App.Ref`;
  - parity advisory and `git diff --check` passed;
  - rebuilt and visually checked the 280-page specification; both
    checked-in PDF names have SHA-256
    `cc5c68e1f74e0a7c6cbb64a507ff7ea0dca8c08664ab22632030989ec43f90e0`.

### July 25, 2026 — Canonical operators and compound assignment

- Completed `TS-P1-03` by routing emitted eager binary operators through
  `Tosh.Runtime.OperatorEvaluator.EvaluateBinaryWithDiagnostics`.
  Mutable unannotated locals retain runtime type changes; explicit
  annotations convert on every store; interpreted/source-replayed and
  emitted CLR classes share left-biased symmetric overload dispatch,
  inherited special methods, and canonical `Equals`, `ToString`, and
  record protocols.
- Added a differential corpus comparing CLR value type, value, formatted
  stdout, and structured failures. Span-aware diagnostic source text is
  embedded inline and never depends on executable source registration or
  replay.
- Completed `TS-P1-04` by routing ordinary and compound forms through the
  same operator protocol for variable, captured, member, and index
  targets, including `**=`/`//=`, post-operation annotation conversion,
  cancellation, and user-throw identity. `TS-P1-13` remains the owner of
  evaluation-order differences.
- Removed the integral-enum qualified-name fallback that could resolve a
  declared `Color.Green` as `System.Drawing.Color.Green`. Integral enum
  members now emit directly in Tier 1 and retain stable ToastScript names
  in formatted output.
- Synchronized first-use standard-library initialization so concurrent
  default runtimes/formatters cannot observe an empty command or display
  profile registry while the module initializer is still running.
- Validation:
  - compiler operator differential corpus: 41 passed;
  - focused compiler, assignment, cancellation, feature-matrix, and
    formatter closure: 616 passed;
  - test-project and focused builds: zero warnings and errors;
  - solution-wide run: 2,952 passed with no skips and one
    environment-only packaged-SDK restore failure. Its isolated `local`
    NuGet source cannot provide `Microsoft.AspNetCore.App.Ref`; no
    language, runtime, compiler, or formatter test failed;
  - complete solution build and the parity advisory passed. The build's
    only warning is the existing `NU1903` advisory for the dev-only
    DevCompanion SQLite native package;
  - `git diff --check` passed;
  - rebuilt and visually checked the 280-page specification around the
    assignment and precedence tables; both checked-in PDF names have
    SHA-256
    `eb137ccd696f859da2a3bffdbe62e2e5be435cdfb50fc04825bd00356ff8f9d0`.

### July 26, 2026 — Callable default binding (TS-P1-05)

- Adopted and documented the callable default-binding policy above:
  call-time evaluation in the callable's lexical environment,
  left-to-right, earlier bound parameters visible, no evaluation for
  losing overload candidates, and annotation/refinement conversion of
  the evaluated value.
- The callable binder now records pending defaults per candidate
  instead of nulling them; the single winning overload applies them
  through one shared `ApplyPendingParameterDefaults(Async)` pair used
  by free functions, lambdas, instance/static/special class methods,
  and constructors on both the sync and async selector paths.
- Reworked the lowerer so parameter defaults are lowered inside the
  callable's scope immediately before their parameter binds. Compiled
  defaults now see earlier parameters, and outer references are
  recorded as captures and promoted to static fields; previously both
  shapes failed emission with `unresolved variable`.
- Classes whose constructors or methods declare optional, defaulted, or
  rest parameters no longer emit a fixed-arity CLR shell — they remain
  Tier-3 source replay, so compiled `new` and member dispatch resolve
  through the engine's binder instead of failing reflection arity
  matching. `runtime`/`pure` profiles reject those shapes with the
  ordinary tier diagnostic.
- Added `tosh.runtime.parameter_default_conversion_failed` and
  regenerated the diagnostic manifest (470 codes).
- Added `ToshHost.NormalizePackedArguments` and a packed-argument
  prologue so a compiled call that mixes named arguments with declared
  defaults lands each value in its own slot. The prologue keeps the
  normalized array in a local rather than overwriting argument 0; an
  earlier `Starg_S` form produced invalid IL for callables with more
  than one defaulted parameter.
- Filed `TS-P1-21` for `$this` visibility inside method and
  constructor defaults. This slice turned the previous silent null into
  an explicit unknown-variable failure; whether a constructor default
  may observe a partially-constructed instance is a semantics decision
  and was deliberately not settled inside a bug-fix slice.
- Filed `TS-P1-20` for a pre-existing compiled/interpreted divergence
  found while validating this slice: a value-context pipeline seeded
  from a variable (`var n = ($xs | count)`) skips its stages in
  compiled mode. It is independent of default binding and is not
  addressed here.
- Coverage: sixteen interpreter regressions
  (`CallableDefaultBindingTests`) spanning free functions, lambdas,
  methods, static methods, primary and explicit constructors,
  left-to-right chains, named-argument gaps, call-time re-evaluation,
  side-effect suppression for provided arguments and losing overloads,
  annotation conversion, forward-reference rejection, and rest
  interplay; four compiled conformance rows and two tier-expectation
  rows in the compiler feature matrix.
- Validation:
  - focused callable/compiler/class selection
    (`CallableDefaultBinding`, `CompilerFeatureMatrix`,
    `BoundUnitEmitter`, `ClassConstruction`, `GenericClass`,
    `FunctionOverload`, `Lambda`): 546 passed with zero failures;
  - full solution run: 2,975 passed, zero failed, zero skipped in
    2m34s, including the packaged-SDK fixture that earlier runs
    reported as environment-blocked;
  - interpreted/compiled differential spot checks for defaulted,
    named, chained, rest, and named/positional-overlap calls agreed on
    every shape;
  - `git diff --check` passed;
  - regenerated the diagnostic reference (470 codes) and rebuilt the
    280-page specification, visually confirming the new
    default-value-semantics section.

### July 26, 2026 — Compiled pipeline value semantics (TS-P1-20)

- Adopted and documented the pipeline value-context policy above.
- Added `ToshHost.DrainSubexpressionValue`, which applies the
  interpreter's rule (none → `null`, one → the item, several → the
  shared `tosh.runtime.subexpression_requires_single_value`
  diagnostic), and threaded an `asSequence` flag through
  `EmitPipeline`/`EmitPipelineCore`/`EmitMultiStagePipeline` and the
  redirection wrapper so only iteration sources keep the list shape.
- `for … in (pipeline)` now emits through `EmitPipelineAsSequence`;
  every other value context collapses. `DrainValue` is retained for the
  sequence path and its stale rationale comment was corrected.
- The defect predated `TS-P1-05` and is unrelated to defaults: it
  affected literal, variable, and command-seeded pipelines equally, and
  only multi-stage ones, because single-stage value pipelines already
  collapsed through `InvokeValue`.
- Coverage: four compiled conformance rows plus
  `CompiledPipelineValueTests`, which asserts the collapse for one-item,
  empty, multi-item, typed, and iteration-source shapes and compares one
  case directly against the interpreter's own result.
- Validation:
  - targeted selection (`CompiledPipelineValue`, `CompilerFeatureMatrix`,
    `CallableDefaultBinding`): 184 passed with zero failures;
  - full solution run: 2,987 passed, zero failed, zero skipped in 2m37s;
  - differential spot checks confirmed identical behaviour for
    one-item, empty, multi-item, typed, `first`-bounded, tuple-assignment,
    and `for`-source shapes;
  - `git diff --check` passed.
- Observation for later: `ScopeAndChannelTests`
  `Scope_kills_jobs_and_rethrows_when_block_throws` failed once under
  heavy machine load and then passed three times in isolation and in a
  clean full re-run. It spawns an external `dotnet --version` job and
  asserts no job remains running, so it is timing-sensitive rather than
  a semantic regression; worth making deterministic if it recurs.

### July 26, 2026 — Named-argument validation (TS-P1-06)

- Duplicate names are rejected before any binding decision: a name
  supplied twice is invalid for every candidate, so treating it as an
  overload mismatch would produce a misleading "no overload matched"
  failure. `ValidateNamedArgumentUniqueness` runs at the free-function
  binder and at both overload selectors.
- Unknown names are handled in two layers so overload resolution keeps
  working. During selection an unmatched name makes that candidate lose
  (a sibling overload may declare the parameter), and when no candidate
  bound, a name that matches no parameter of *any* candidate produces
  `tosh.runtime.unknown_named_argument` naming the declared parameters.
  A single concrete definition reports the same diagnostic directly.
- The compiled packed-argument prologue applies both rules in
  `NormalizePackedArguments`, so compiled and interpreted calls fail
  with the same codes and messages.
- Ordinary arity failures are unchanged, and command-wrapper functions
  that forward arbitrary arguments are exempt from the unknown-name
  check.
- Added `tosh.runtime.unknown_named_argument` and
  `tosh.runtime.duplicate_named_argument`; regenerated the diagnostic
  manifest (472 codes).
- Filed `TS-P2-21`: a `new` expression cannot parse named arguments at
  all, so constructor named-argument validation is unreachable until the
  parser accepts the form. Function and method calls are unaffected.
- Coverage: `NamedArgumentBindingTests` covers unknown and duplicate
  names on functions and methods, overload selection by named argument
  for both, out-of-order binding, rest receiving only unconsumed
  positional values in source order, and an arity failure remaining an
  arity failure.
- Validation:
  - named-argument, default-binding, and pipeline-value selection:
    33 passed with zero failures;
  - full solution run: 2,996 passed, zero failed, zero skipped in 2m38s;
  - interpreted and compiled calls confirmed to report identical
    unknown/duplicate diagnostics;
  - `git diff --check` passed.

### July 26, 2026 — Comparison semantics (TS-P1-14)

- Adopted the strict, symmetric comparison decision recorded above.
- Ordering now rejects booleans outright and refuses to order a string
  against a non-string, which removes the silent lexicographic answer
  that made `"10" < 9` evaluate to `true`. Conversion is attempted in
  both directions, so `"abc" < 5` and `5 > "abc"` now behave the same
  instead of one answering `false` while the other threw.
- Equality dropped its case-insensitive `ToString` fallback. Case
  sensitivity is now uniform: previously mixed-type equality folded case
  while string-to-string equality did not. Conversion-backed equality is
  deliberately unchanged, so numeric strings still parse (`1 == "1"`),
  CLR enums still match their member names, and a value still equals the
  exact text form `TypeConversion` produces for it.
- Aligned `TypeChecker.CheckBinaryOperator` with the same rule. It
  previously permitted ordering only between two numerics, which made
  the specified and working expression `"a" < "b"` a hard compile error,
  so string comparison could not be compiled at all. It now rejects
  exactly what the runtime rejects and defers every other pair to the
  runtime's convertibility check.
- One consequence worth noting: invalid orderings are now caught at
  compile time rather than at run time. Both modes still refuse them, so
  the parity contract holds — the compiler simply reports earlier.
- Coverage: `ComparisonSemanticsTests` covers string-versus-number in
  both directions, symmetry of the same operand pair, boolean rejection,
  string-to-string and numeric-widening ordering, numeric-string
  equality, uniform case sensitivity, conversion-backed equality in both
  directions, element-wise collection equality, and null ordering.
- Validation: comparison selection 14 passed; full solution run 3,010
  passed, zero failed, zero skipped in 2m37s. The suite was also green at
  2,996 before the new tests were added, confirming the removed
  fallbacks were not load-bearing anywhere in the existing suite.
- Filed `TS-P1-22` for the accepted chained-comparison behaviour, which
  is a language-surface addition rather than part of this repair.

### July 26, 2026 — Enum comparability (TS-P1-15)

- `ToshEnumValue` now implements `IComparable`/`IComparable<T>` and the
  new `Tosh.Runtime.IShellEnumValue`, so ordering and equality can be
  resolved without the runtime assembly depending on the language
  assembly. `ShellEnumComparison` holds the single rule for reducing a
  member to its backing value.
- Members order by their backing value (`E.Low < E.High`), compare equal
  to that value in both directions (`E.Mid == 1` and `1 == E.Mid`), and
  keep their existing name-based equality against a string. Explicit
  values such as `enum Permissions : int { Read = 4 }` behave the same
  way, and `sort` orders members numerically rather than alphabetically.
- Two different enums are not one ordered domain. Members of `E` and `F`
  that share a backing value neither compare equal nor order against
  each other; attempting to order them is a structured failure. The
  guard lives in the evaluator as well as in `CompareTo`, because the
  evaluator's enum branch would otherwise unwrap both operands to plain
  numbers before `CompareTo` was ever reached.
- Applying this exposed that the interpreter kept its own
  `AreEqualAsync` alongside `OperatorEvaluator.AreEqual`, so the
  `TS-P1-14` equality change had landed on only one surface. Both now
  carry the same enum rule and the same removal of the case-insensitive
  `ToString` fallback. This duplication is the class of problem the
  stabilization programme exists to remove and is worth revisiting.
- Coverage: `EnumComparisonTests` covers ordering, backing-value
  equality in both directions, symmetry, member identity within and
  across enums, name equality, explicit backing values, sort order, and
  rejection of cross-enum ordering.
- Validation: enum and comparison selection 31 passed; full solution run
  3,027 passed, zero failed, zero skipped in 2m41s.

### July 26, 2026 — Withdrawing TS-P1-17, closing TS-P1-23

- `TS-P1-17` claimed `{}` produced an internal type-definition object
  rather than an empty record. That was wrong. In expression position
  `var r = {}` yields an `ExpandoObject` — the same CLR type as
  `{ a = 1 }` — and record spread (`{ ...$e, a = 1 }`) and member
  assignment (`$e.x = 5`) both work on it. The item is withdrawn rather
  than "fixed", since there was nothing to repair.
- Two accurate observations sat behind the misfiling. The first is now
  `TS-P1-23`: `type-of` returns a shell type descriptor, but
  `BuiltInShellTypeDefinition` had no `ToString`, so displaying one
  printed its own CLR class name. `type-of [1, 2]` reported
  `Tosh.Runtime.BuiltInShellTypes+BuiltInShellTypeDefinition`; it now
  reports `array<int>`. This affected every display of a built-in shell
  type descriptor, not just `type-of`, and is the same
  internal-name-leak family as `TS-P2-18`.
- The second is that `{}` means different things by position: an empty
  record in expression position, but a block (`ShellBlock`) as a bare
  command argument. That is the brace-overload ambiguity already raised
  in the review's design notes and is left for the parser work rather
  than being resolved here.
- Validation: full solution run 3,027 passed, zero failed, zero skipped
  in 2m38s.
- Method: this is a reminder that a filed symptom is not a diagnosis.
  The original note recorded what was printed, not what was verified;
  confirming the underlying value's CLR type first would have caught it.

### July 26, 2026 — `$this` in method parameter defaults (TS-P1-21)

- Implements the accepted decision: an instance method's parameter
  default may reference `$this` (and `$super`), because the instance
  exists by the time the call binds. Constructor defaults may not, since
  they bind before that layer's properties are initialised.
- The callable default binder now takes an ambient binding set that
  seeds the default scope before parameters are added, so a default sees
  `$this`, `$super`, and every earlier bound parameter —
  `func m(a, b = $this.V + $a)` resolves all three. Inherited properties
  resolve through the ordinary member path.
- The instance is threaded through the instance-method and
  special-method selectors, including the recursive base-class lookups,
  so a method found on a base class still receives the derived
  instance. Static methods and constructors pass no bindings.
- A constructor default that reaches for `$this` now fails with
  `tosh.runtime.self_unavailable_in_constructor_default`, explaining
  that the instance is still under construction, instead of the generic
  unknown-variable error. Detection keys on the evaluator's
  unknown-variable diagnostic; the regression test asserts the code and
  label so a change to that diagnostic breaks loudly rather than
  silently falling back to the generic message.
- Regenerated the diagnostic manifest (473 codes).
- Coverage: `SelfInParameterDefaultTests` covers reading an instance
  property, combining `$this` with an earlier parameter, reading an
  inherited property, the constructor rejection, unaffected ordinary and
  static defaults, and a constructor default that does not use `$this`.
- Validation: default-binding selection 22 passed; full solution run
  3,033 passed, zero failed, zero skipped in 2m36s.

### July 26, 2026 — Chained comparison (TS-P1-22)

- Implements the accepted decision. `a < b < c` now means
  `(a < b) and (b < c)`: `1 < 2 < 3` is `true` where it previously
  compared `true < 3` and silently answered `false`.
- The chain is carried as its own node — `ChainedComparisonArgumentSyntax`
  and `BoundChainedComparison` — rather than being desugared into `and`.
  A syntax-level rewrite would duplicate every interior operand, so
  `1 < (mid) < 3` would call `mid` twice. Both execution modes evaluate
  each operand at most once and short-circuit: a failing pair never
  evaluates the operands after it.
- Only relational operators chain (`<`, `<=`, `>`, `>=`, `==`, `!=`). A
  run containing `is`, `in`, `contains`, or a regex operator keeps the
  previous left-associative shape, since those have no useful chained
  reading. Single comparisons are structurally unchanged.
- Compiled emission holds each operand in a local and branches between
  pairwise comparisons, which is where the single-evaluation guarantee
  actually comes from; the interpreter mirrors it by threading the
  previous operand's value forward.
- Coverage: `ChainedComparisonTests` covers two-, three-, and
  four-operand chains, mixed directions, equality chains, plain
  comparisons, non-chaining operators, and — in both interpreted and
  compiled modes — single evaluation of an interior operand and
  short-circuit of a later one.
- Documented in the specification's operator chapter as
  "Chained Comparison", including both guarantees and the list of
  operators that chain; the 280-page PDF was rebuilt.
- Validation: chained-comparison selection 17 passed; full solution run
  3,050 passed, zero failed, zero skipped in 2m36s.

### July 26, 2026 — Duplicated-semantics audit

Prompted by two slices in which a semantic fix landed on only one
surface, the interpreter's sync/async twin methods were inventoried
mechanically rather than discovered incident by incident.

- Method: extract every method whose name has an `…Async` sibling, take
  the larger body, and classify it as *delegating* when it calls its own
  async twin or as a *parallel implementation* when it does not. Then
  count reachable call sites, excluding each method's own declaration,
  to separate live copies from dead ones.
- Result across `ToshEngine` and `ToshClassDefinition`: 23 parallel
  implementations against 6 delegations. Eighteen parallel copies are
  reachable; five are dead. `ToshClassDefinition` is the better-behaved
  of the two — every `Invoke*`/`Create*` entry point delegates — while
  `ToshEngine` had no delegating twins at all. Filed as `TS-P1-24` with
  the full breakdown; the scan is a lower bound, since twins whose
  signatures differ in shape (for example a `ValueTask` tuple return)
  are not matched by name alone.
- Divergence probe: the two highest-exposure areas were exercised for
  *current* disagreement rather than theoretical risk. Refinement types
  (`type Port = int where … coerce 80`) behave identically across seven
  contexts — variable annotation, function parameter, function return,
  class property initialiser, constructor parameter, method parameter,
  and property assignment — and annotated conversion (`int` from the
  string `"42"`) yields the same value in all of them. No live runtime
  divergence was found, so `TS-P1-24` is latent risk rather than present
  breakage.
- The probe did surface a separate real gap, filed as `TS-P2-22`: the
  type checker never visits class-member annotations, so `var x: int =
  "42"` reports `tosh.type.mismatch` while the same mistake on a
  property, constructor parameter, method parameter, or property
  assignment reports nothing at all. Runtime conversion is consistent;
  only static coverage is missing.
- Recommendation for sequencing: converging `TS-P1-24` is worth more
  than the next individual P1 repair, because every remaining semantic
  item is at risk of the same half-landing. The five dead parallel
  copies can go first — the suite proves nothing reaches them — which
  removes the traps at zero behavioural cost.

### July 26, 2026 — Removing the dead parallel copies (TS-P1-24, first slice)

- Deleted the seven unreachable sync twins the audit identified, 132
  lines in total: `ToshEngine.FormatInterpolatedValue`, and
  `ToshClassDefinition`'s `EvaluateBaseConstructorArgs`,
  `InitializeClrBase`, `RunConstructorInitializer`, `RunConstructor`,
  `SelectConstructor`, and `SelectMethod`.
- Each was verified unreferenced across `src/`, `tests/`, `bench/`,
  `tools/`, and `examples/` before removal — every one had exactly one
  occurrence, its own declaration — and the two `internal` members were
  checked for cross-assembly use as well.
- These were the residue of `TS-P0-06`'s move to asynchronous class
  invocation. Leaving them in place was the actual hazard: they encoded
  pre-`TS-P0-03`/`TS-P1-05` construction and binding semantics, so
  reviving one would have silently reintroduced behaviour the programme
  had already corrected, and they made the class file read as though two
  construction protocols were still supported.
- Validation: full solution run 3,050 passed, zero failed, zero skipped
  in 2m38s — unchanged from before the deletion, which is the intended
  evidence that nothing reached this code.
- Remaining under `TS-P1-24`: the eighteen *live* parallel pairs, led by
  `ConvertAnnotatedValue` (12 call sites), and a guard that fails when a
  new parallel sync/async pair appears.

### July 26, 2026 — Correcting the audit, and the drift guard (TS-P1-24)

The first pass of this audit was wrong in two ways, both worth recording
because they changed which work looked worthwhile.

- The classifier only asked whether a *sync* method calls its async twin.
  Delegation runs the other way in this codebase: a method may implement
  the asynchronous case and hand every other case to the synchronous
  implementation. Checking one direction reported those pairs as
  duplicated.
- The declaration pattern required a return type containing no
  parentheses, so every method returning a tuple — which is most of the
  `Try…Async` family — was skipped entirely.

The consequence was a concrete mistake: `TryConvertAnnotatedValue` was
reported as the largest duplicated pair, 147 lines over 12 call sites,
and slated for extraction. It is in fact **already converged**. Its
asynchronous overload resolves the refinement case and delegates every
non-refinement annotation straight to the synchronous method, so only
the refinement branch — roughly twenty lines — exists twice. The
extraction was cancelled rather than performed against a false premise.

Corrected counts are 23 truly parallel pairs against 6 converged, with
the same totals as the first pass but materially different membership.
The largest genuine duplication is the refinement cluster —
`EnsureRefinementSatisfied` (98 lines) plus its predicate, coercer, and
boolean-expression helpers, about 154 lines together — followed by
`ThrowDetailedSingleConstructorMismatch`, `TryGetInstanceMember`, and
`ApplyPendingParameterDefaults`. Five pairs the first scan missed
entirely (`TryInvokeShellSymbol`, `TryGetInstanceMember`,
`GetInstanceMembers`, `TryInvokeEnumerator`,
`TryInvokeSpecialInstanceMethod`) are now included.

What was kept from the slice:

- `AnnotatedConversionParityTests`, a drift guard that runs one corpus
  through both conversion paths and asserts identical success and
  identical converted shape. It covers primitives, widening, string
  parsing, failures, nullable annotations, collections,
  trait-constraint names, refinements with and without `coerce`, and
  nested refinements. It passes against the current implementations,
  which confirms the two paths agree today, and it now guards the
  refinement branch that genuinely is duplicated.
- `InternalsVisibleTo Include="Tosh.Tests"` on `Tosh.Language`, so the
  guard can compare the two primitives directly rather than inferring
  which path a script reached. This matches `Tosh.Client`, `Tosh.Cli`,
  and `Tosh.Crumb`.

Method note: an audit heuristic needs its own negative control. Both
flaws would have been caught by checking the classifier against one
pair whose structure was already known by reading it.

### July 26, 2026 — Lexer characterization corpus (TS-P2-11 groundwork)

Before reworking the bareword-versus-token boundary, the current
tokenization was pinned so the rework's effect is visible rather than
incidental.

- `LexerCharacterizationTests` renders the token stream for a corpus and
  asserts it exactly. It is explicitly a characterization suite, not a
  correctness suite: entries that encode a known defect name the item
  that will change them, so those expectations are updated in the same
  change as the fix.
- Three groups: shapes that already tokenize as intended; shapes whose
  tokenization *is* the root cause of a filed defect (`TS-P2-04`
  `$x?.Length`, `TS-P2-02` `-$x`, `TS-P2-15` `f(a="z")`, `TS-P2-05`
  `1__2`); and command-position barewords (`ls -la`, `./script.tosh`,
  `*.txt`, `read-file`) that a mode-switching lexer must keep working
  while it starts treating operators as tokens in expression position.
- One test states the thesis directly: `$x?.Length` and `$x ?. Length`
  do not tokenize the same way, and neither do `f(a=1)` and
  `f(a = 1)`. The same expression changing meaning with whitespace is
  what identifies this as a lexer problem before it is a parser problem.
- Evidence gathered while writing it: `-$x` fails while `(0 - $x)`
  works, `f(a="z")` drops the name while `f(a = "z")` binds it, and
  `$x?.Length` prints literally while `$x ?. Length` yields 3.
- One assumption did not survive contact: `1.5..3` was expected to
  collapse into a bareword, and in fact tokenizes correctly as
  `Number DotDot Number`. The `range_requires_integer` error it produces
  comes from the accepted integer-only `ToshRange` decision, enforced
  above the lexer. The entry moved to the correctly-tokenized group.

### July 26, 2026 — Mode-tracking lexer, first slice (TS-P2-11, closing TS-P2-04 and TS-P2-15)

The accepted approach is a lexer that tracks its own mode. The parser
cannot drive it: lexing completes before parsing begins, and the parser
relies on 222 `Peek(n)` lookaheads plus direct indexing over the
finished token list, so a token's mode cannot depend on a parse decision
that has not happened yet.

- The lexer now tracks bracket nesting (`_expressionDepth`) to
  distinguish command position, where greedy barewords must survive, from
  expression position, where operators must be real tokens. This extends
  a pattern the lexer already used — `IsRangeOperatorContext` decides
  `..` from the previous token.
- `TS-P2-04`: a bareword that began as a variable reference now breaks
  before `?.`, so `$x?.Length` tokenizes as `$x` `?.` `Length` and
  evaluates to 3 instead of printing itself. Nullable spellings such as
  `string?` and `name?`, and any `?` not followed by `.`, are untouched
  because the rule requires a leading `$`.
- `TS-P2-15`: inside a parenthesised or bracketed context, an identifier
  followed by a single `=` breaks, and `=` is emitted as its own token,
  so `f(a="z")` binds the parameter exactly like `f(a = "z")`. The rule
  requires a plain identifier, leaving option-style arguments such as
  `--opt=value` greedy, and excludes `==`, `=~`, and `=>` so comparison,
  regex match, lambdas, and match arms keep their spellings.
- Both defects were pure tokenization: their spaced forms already worked
  end to end, so no evaluator change was needed. `TS-P2-02` (`-$x`) was
  deliberately left out of this slice — its spaced form `- $x` also
  fails, so it needs runtime work beyond tokenization and is not a
  lexer-only fix.
- The characterization corpus did its job: the two entries moved from the
  known-defective group to the correctly-tokenized group with their new
  expectations, in the same change as the fix, and the thesis test now
  asserts that `$x?.Length` and `$x ?. Length` tokenize *identically*
  rather than differently.
- Validation: lexer corpus 21 passed; full solution run 3,100 passed,
  zero failed, zero skipped in 2m34s.

### July 26, 2026 — Numeric literal lexing (TS-P2-05)

- Digit separators are now validated: each `_` must sit between two
  digits of the literal's own radix, so `1_000` and `1_000_000` still
  fold away while `1__2`, `1_`, and `0x_FF` report
  `tosh.parser.invalid_numeric_separator`.
- A leading underscore now names an identifier instead of being stripped
  into a number. `_1` previously lexed as the literal `1`, which is why
  `var _1 = 99` failed with an unknown-command error for `var`; it now
  binds and reads back 99. Validation is confined to numeric-looking
  text, so `my_var`, `_count`, and `read_file` are untouched.
- Binary and octal literals that exceed 64 bits report
  `tosh.parser.numeric_literal_overflow` rather than escaping as the
  CLR's raw "Value was either too large or too small for a UInt64"
  under a generic runtime code. The previous handler caught only
  `FormatException`, so `OverflowException` passed straight through.
- Coverage: `NumericLiteralLexingTests` covers valid separator and radix
  forms, each misplaced-separator shape, underscore-led identifiers, and
  both oversized radix literals. The characterization corpus moved
  `1__2` out of the pinned-defect group and gained `1_000` and `_1`.
- Regenerated the diagnostic manifest (475 codes).
- Validation: lexer selection 38 passed; full solution run 3,117 passed,
  zero failed, zero skipped in 2m33s.

### July 26, 2026 — Module dispatch casing (TS-P2-16)

- Root cause was a one-line heuristic: `LooksLikeQualifiedDotNetAccess`
  ended in `char.IsUpper(firstSegment[0])`, so any dotted name starting
  with a capital was assumed to be a CLR static member access. `Geo.area
  2` therefore parsed as an expression and left `2` unattached, failing
  with a pipeline-separator error, while `geo.area 2` dispatched fine.
- At the start of a stage, a dotted name followed by a value argument on
  the same line is now treated as a command invocation, leaving the
  engine — which does have the module table — to resolve it against
  modules and CLR types alike. Operators are excluded, so `Math.PI + 1`
  keeps its static reading, as do `Math.PI` alone and `Math.Sqrt(16)`.
- The first attempt applied the rule everywhere and regressed
  `echo Config.version Config.maxRetries`: in *argument* position a
  following bareword is a sibling argument, not an argument to the
  dotted name. The check is now confined to command position, and that
  case is covered by a regression test.
- Coverage: `ModuleDispatchCasingTests` covers upper, lower, all-caps,
  kebab, and snake module names; static CLR access alone, in arithmetic,
  and as a call; and the sibling-argument case.
- Validation: module-dispatch selection 7 passed; full solution run
  3,117 passed, zero failed, zero skipped in 2m33s.
- Note for `TS-P2-11`: this fix replaces one weak heuristic with a
  narrower one rather than removing the guess. The guess exists because
  the parser has no command or module table — `ToshParser.Parse` takes
  only source text, while `Lowerer.Lower` receives the registry. Making
  resolution table-driven rather than spelling-driven is the structural
  answer and is recorded under `TS-P2-11`.

### July 26, 2026 — Declaration table: identity from facts (TS-P2-23 step 1, closing TS-P2-08)

First step of the parser roadmap: replace spelling-based identity with a
table the parser builds from the source it is parsing.

- `ScanUserFunctionNames` became `ScanDeclarations`, collecting declared
  function *and* module names. A keyword now only counts at a statement
  start — first token, or after `;`, `{`, `}`, `|`, or a line break —
  with declaration modifiers (`export`, `shy`, `shared`, …) skipped so
  `export func f()` still registers. That closes `TS-P2-08`: the old raw
  scan registered any bareword following the word `func`, so
  `echo func bar` made `bar` look declared.
- The table now decides an identity that capitalization was getting
  wrong. A capitalized user function was read as a static call on a CLR
  type of the same name: `func Foo(x)` followed by `Foo(1)` failed,
  while the byte-identical lowercase `foo(1)` returned 3. A name this
  source declares is no longer a candidate CLR type at any casing.
- Scope correction during the work: the first attempt excluded declared
  modules from qualified access entirely, which broke seven tests.
  `LooksLikeStaticMemberAccessExpression` is not CLR-only — module
  member access (`Lib.greeting`) travels the same qualified path, and
  the engine resolves both. The table is now consulted only where the
  decision is genuinely command-versus-expression, and the exclusion is
  documented in place so the next reader does not repeat it.
- What remains under `TS-P2-23`: the table covers this source's
  declarations only. Imported modules and CLR types still fall back to
  `char.IsUpper`, because `ToshParser.Parse` receives no registry. The
  fallback is now a genuine last resort rather than the primary rule,
  but removing it needs the registries at parse time.
- Coverage: `DeclarationTableIdentityTests` covers declared functions at
  four casings, unaffected CLR static access, module member access,
  keyword-as-argument poisoning, and modifiers preceding a declaration.
- Validation: identity selection 8 passed; full solution run 3,132
  passed, zero failed, zero skipped in 2m33s.

### July 26, 2026 — ParseContext: registries at parse time (TS-P2-23 step 1b)

- Added `ParseContext`, carrying the command names, module names, and
  type names a host already knows, and threaded it through
  `ToshParser.Parse(source, sourceName, context)`. The engine builds one
  from `Runtime.Commands.AllNames` plus the modules visible in its scope
  chain.
- `ParseContext.Empty` is a legitimate value rather than a compatibility
  shim. The formatter, the REPL continuation classifier, and
  interpolation-hole parsing genuinely have no environment, and parsing
  purely syntactically is the right behaviour for them. Names absent
  from the context fall through to the ordinary bareword reading and
  the engine reports an unresolved name at run time.
- The context is what recognises an *imported* module. The declaration
  table added in the previous slice only sees modules the source itself
  declares; `require Inventory from "./lib.tosh"` puts nothing in the
  text being parsed. Likewise a host-registered command with a
  capitalized name is no longer mistaken for a CLR type.
- Ordering caveat worth recording: for a single-shot script the whole
  source is parsed before `require` executes, so the context cannot yet
  know that module. Same-source modules are covered by the declaration
  table, and the command-position rule from `TS-P2-16` covers the rest,
  so the three mechanisms are complementary rather than redundant. The
  context's full value appears wherever parsing happens with state
  already loaded, which is the REPL and incremental tooling.
- Verified directly rather than through the CLI, where the require
  ordering above would have made a passing result misleading about which
  mechanism did the work: the new tests parse the same source with and
  without a context and assert on the result.
- Coverage: `DeclarationTableIdentityTests` gained host-supplied module
  dispatch, a host-supplied command not being read as a CLR type, and
  `ParseContext.Empty` keeping parsing syntactic.
- Validation: identity selection 11 passed; full solution run 3,135
  passed, zero failed, zero skipped in 2m36s.
- Remaining under `TS-P2-23`: `char.IsUpper` still exists as the final
  fallback for names no table covers. Deleting it outright is safe only
  once shape-driven argument parsing (step 3) removes the need to guess
  at all.

### July 26, 2026 — Statement starts and block comments (TS-P2-06, step 2 groundwork)

- An unterminated `##{` block comment consumed the rest of the file in
  silence. Every statement after it simply never ran and nothing
  reported why, which is the worst shape a defect can take: not a wrong
  answer but no answer. It now raises
  `tosh.parser.unterminated_block_comment` pointing at the opening
  delimiter.
- Expression-start recognition is now one predicate,
  `ToshParser.IsExpressionStartToken`. Statement-boundary detection
  carried its own shorter list that omitted interpolated strings,
  command substitution (`$(`), process substitution (`<(`), and function
  references (`&f`). No failing case was reproducible at top level or
  inside a block — the pipeline parser stops earlier on the paths tried
  — so this is consolidation against a latent divergence rather than a
  demonstrated bug, and it is recorded as such.
- The predicate is the first piece of the structural layer step 2 needs:
  boundary decisions should consult one table rather than each site
  re-enumerating token kinds.
- Regenerated the diagnostic manifest (476 codes).
- Coverage: `BlockCommentAndStatementStartTests` covers the unterminated
  and terminated comment forms, every expression-start kind and several
  non-start kinds, and new lines beginning with an interpolated string,
  a command substitution, a number, and a function reference.
- Validation: focused selection 24 passed; full solution run 3,159
  passed, zero failed, zero skipped in 2m35s.

### July 26, 2026 — Lite-parse structural pass (TS-P2-24, step 2)

- Added `LiteParser`, a structural pre-pass modelled on Nushell's
  `lite_parser`. It produces `LiteScript` → `LiteStatement` →
  `LiteStage` as token-index ranges with spans, and assigns no meaning
  to any token. Bracket depth is tracked so a `;` or `|` inside a
  subexpression, list, or block belongs to that nested construct rather
  than splitting the enclosing statement.
- It consumes the shared `IsExpressionStartToken` table from `TS-P2-06`
  for implicit line-break boundaries, so the structural pass and the
  parser cannot drift apart on what may begin a statement.
- Landed alongside the existing parser rather than replacing anything.
  Nothing consumes it yet; that is deliberate. The pass is only worth
  building if it agrees with the structure the current parser reaches
  through scattered lookahead, so agreement is established first and the
  heuristics are retired afterwards.
- Coverage: `LiteParserTests` is differential where it can be. Statement
  and stage counts are asserted against what `ToshParser` actually
  produced for the same source, across nine construct families —
  variables, functions, classes, modules, loops, try/catch, match,
  redirection, and multi-stage pipelines — plus nested-separator cases,
  empty sources, and span accuracy. 27 tests, all passing on the first
  run, which is the evidence that the structural model matches.
- Validation: lite selection 27 passed; full solution run 3,186 passed,
  zero failed, zero skipped in 2m34s.
- Next under `TS-P2-24`: have the parser consume the lite structure for
  statement and stage boundaries, then delete the helpers that only
  existed to re-derive it. That is the change that shrinks the 56
  `LooksLike*` heuristics rather than merely duplicating them.

### July 26, 2026 — Recovery: skip only when no progress was made (TS-P2-24, step 2b)

An attempt to drive error recovery from the structural pass failed, and
the failure pointed at a better fix.

- The attempt: on a `missing_statement_separator`, resynchronise to the
  next lite statement start instead of scanning tokens. This *lost*
  work. Lite boundaries are line-granular, so for
  `func f() {…} func g() {…}` — one lite statement, since nothing
  separates the two declarations — recovery jumped to the following
  line and `g` never reached the tree. Reverted.
- Measuring the revert exposed the real defect. With the skip disabled
  entirely, more statements survived than with either recovery strategy:
  `SkipToStageBoundary` was itself discarding the rest of the line. The
  parser, having already parsed `f`, was sitting exactly on `func g` —
  the correct place to continue — and the scan moved it past.
- The fix is to skip only when the statement parse made no progress,
  which is the condition that actually guarantees termination. When the
  parser advanced it is already positioned at the next construct, so
  continuing recovers it. `func f() {…} func g() {…}` now yields both
  declarations and one diagnostic; four same-line class declarations
  yield all four and two diagnostics rather than one and a truncated
  tree.
- `LiteParser` remains as built and validated; nothing consumes it yet.
  Driving recovery from it needs finer candidate boundaries than
  top-level statement starts — mid-line declaration starts in
  particular — which is recorded as remaining work.
- Method note: the regression was only visible because the recovery
  tests asserted on *surviving statements* rather than only on
  diagnostic counts. A test that checked "one diagnostic" alone would
  have passed against a tree missing half its content.
- Validation: lite selection 29 passed; full solution run 3,188 passed,
  zero failed, zero skipped in 2m33s.

### July 26, 2026 — Traversal exhaustiveness (TS-P2-07)

Nothing enforced that a new syntax node be added to the walkers that
visit the tree. Adding `ChainedComparisonArgumentSyntax` earlier today
required remembering to extend `VariableBinder` by hand; forgetting would
have produced no error, only a subtree that capture analysis silently
skipped.

- `SyntaxTraversalExhaustivenessTests` makes that mechanical. Reflection
  enumerates the syntax node types and decides which own child nodes — a
  node whose properties carry no other syntax node is a leaf and needs no
  traversal — then checks the walker covers each one. A new node type
  fails the test until it is traversed or explicitly acknowledged with a
  reason, and a second test rejects allowlist entries that no longer name
  a real type.
- It found a genuine gap on its first run: `ComparisonPatternSyntax` was
  not visited, so a variable referenced in a match arm's pattern
  (`_ > $limit`) was invisible to capture analysis. Now traversed. The
  defect was latent rather than live — the cases reachable today put such
  variables at top level, where they are promoted to static fields and
  need no capture — but it would have become real the moment a pattern
  referenced a captured local.
- It also produced a false positive that corrected the test rather than
  the code. The first version demanded the `Lowerer` name every node
  type; the lowerer is instead total *by construction*, ending in a
  fallback that wraps anything unrecognised in a `BoundDynamicExpression`
  carrying the original syntax, which is how comprehensions reach the
  engine and how the compiler reports them precisely. The assertion now
  protects that fallback, which is the actual invariant.
- Seven nodes are acknowledged rather than traversed, each with its
  reason recorded in the test: the four comprehension forms, refinement
  clauses, static method calls, and member projections.
- Validation: exhaustiveness selection 4 passed; full solution run 3,192
  passed, zero failed, zero skipped in 2m33s.

### July 26, 2026 — Candidate boundaries, and the limit they expose (TS-P2-24 step 2c)

- `LiteParser.CandidateBoundaries` reports every position where a
  statement could begin, at any brace depth, with the kind of separator
  that signalled it and the brace depth it sits at. Grouping suppression
  still applies inside parentheses and brackets, where a line break
  continues an expression.
- This is what the earlier recovery attempt lacked. Top-level statement
  starts are line-granular and cannot resynchronise inside a line or
  inside a block; candidates can.
- Verified differentially: for a function body, the number of candidates
  inside the braces equals the number of statements the parser actually
  produced for that block.
- The slice also established a hard limit, which is filed as
  `TS-P2-25`. Brace-enclosed candidates cannot be promoted to real
  boundaries structurally, because `{` opens either a block — where a
  line break separates statements — or a record literal, where it must
  not. `var r = {\n a = 1\n b = 2\n}` parses correctly today and is
  token-for-token identical in shape to a two-statement block body. The
  pass reports depth rather than filtering, so a consumer that knows
  which construct it is reading can decide.
- That ambiguity is now the gating item for the rest of step 2: any
  attempt to have the parser consume structure inherits it. Resolving it
  is a grammar change and needs a decision before implementation.
- Validation: lite selection 34 passed; full solution run 3,197 passed,
  zero failed, zero skipped in 2m34s.

### July 27, 2026 — Committing the program, and item bookkeeping

The whole stabilization program existed only as uncommitted working-tree
changes: the last commit predated it by two months, and every slice from
`TS-P0-01` onward sat in a single snapshot. One `git checkout` would have
cost all of it. Committed to `stabilization/july-2026` as ten
subsystem-ordered commits.

- Exact per-slice commits were not recoverable. A snapshot no longer
  records which slice changed what, and the files that matter most span
  many slices — `ToshEngine.cs` alone is +6,435/−3,741 across roughly
  thirteen. Splitting it by hunk would have produced commits that do not
  compile. The commits are therefore grouped by subsystem in dependency
  order, each message carrying the relevant slice narrative from this
  log. The solution build is verified clean at the final commit;
  intermediate commits are review units, not build points. The suite was
  last recorded green at 3,197 on July 26 and was not re-run here — a
  full run exhausted the editor's memory — so that figure is carried
  forward rather than reconfirmed.
- Recorded so the Definition of Done can absorb it: an item is not
  durable until it is committed. Twenty-one slices of validated work
  were one command away from loss for two months, which no amount of
  test coverage protects against.
- `examples/point_custom_error.tosh` was not stabilization work. It had
  been overwritten on 2026-07-25 by a 3.3 MB ImageMagick PostScript
  dump — 42,768 of the working tree's 56,487 insertions, with no
  ToastScript content left. Restored from `HEAD`; the stray PostScript
  was set aside rather than discarded.

Item bookkeeping, all of it clerical rather than semantic:

- `TS-P1-20` had been assigned twice: to the closed compiled
  pipeline-value item and, separately, to an open item recording that
  the pure compiler profile can report a Tier-1-clean artifact while
  emitted IL still calls into `ToshHost`. The second is renumbered
  `TS-P1-25`. It was effectively invisible — the previous Active Work
  summary listed remaining P1 work as `TS-P1-07`–`TS-P1-13` and
  `TS-P1-16`–`TS-P1-19`, excluding it along with `TS-P1-24`. Verified
  still live before renumbering: `EmitExecutionFrameEntry` calls
  `ToshHost.EnterExecutionFrame` and `Main` calls
  `RegisterCompiledAssembly`, neither guarded by profile.
- `TS-P2-22` was filed in the P1 table; moved to the P2 table.
- Blank lines had split `TS-P2-21`, `-23`, `-24`, and `-25` into four
  separate one-row tables that render without headers. Rejoined.
- Both tables are now sorted by item number; they previously ran
  ...14, 15, 16, 17, 23, 18, 19, 20, 24, P2-22, 22, 21, 20. Row content
  is unchanged: 50 rows before and after, differing only in the
  renumbered ID.
- Active Work is regenerated from the tables and now states what it is
  derived from, so the next drift is a visible inconsistency rather than
  stale prose.

Three loose ends noted, not addressed: `LiteParserTests` raises three
`xUnit2029` analyzer warnings against the zero-warning standard the
earlier slices held to; the `Tosh.DevCompanion` SQLite advisory
(`NU1903`) is still the solution build's only other warning; and
`scripts/build.tosh` carries an unrelated one-line doc-comment change
left uncommitted.

### July 27, 2026 — Converging the refinement cluster (TS-P1-24)

The largest genuine duplication in the audit, now reduced to one
implementation. `ToshEngine.cs` loses 251 lines net.

- The synchronous cluster is gone: `TryApplyGuardedRefinementCoercion`,
  `TryEvaluateRefinementPredicate`, `EvaluateRefinementPredicate`,
  `EvaluateRefinementCoercer`, and `EvaluateRefinementBooleanExpression`
  are deleted, and `EnsureRefinementSatisfied` and
  `TryApplyRefinementWithOptionalCoercion` are now thin adapters over
  their asynchronous twins. The guard, predicate, and coercion semantics
  exist once.
- No new blocking was introduced. Each deleted leaf already ended in
  `EvaluateArgumentAsync(...).GetAwaiter().GetResult()` with
  `CancellationToken.None`; the bridge moved from inside five bodies to
  two delegation points. `_scopes` is a plain instance stack rather than
  an `AsyncLocal`, so pushing the refinement scope inside the async
  method rather than outside it is not observable.
- `CreateRefinementFailedDiagnostic` is extracted so the
  `refinement_failed` help text is built once, and the comment
  explaining why guarded `coerce` clauses thread their value forward
  moved to the surviving asynchronous method rather than being deleted
  with the copy that carried it.

The interesting part is a claim that did not survive its own check.

- Reading the two copies showed a real difference: a non-diagnostic
  exception raised by the predicate *after* fallback coercion was
  attributed to the coercer's span in `EnsureRefinementSatisfied` and to
  the predicate's span everywhere else. That was reported here as a live
  divergence.
- Running the new regression against the pre-convergence engine
  contradicted it: the test passed. `ConvertAnnotatedValue` only reaches
  `EnsureRefinementSatisfied` after `TryConvertAnnotatedValue` has
  already completed the sequence *without* throwing and returned an
  unsatisfied result. A deterministic predicate cannot throw on the
  re-run having not thrown on the first pass, so the diverging line was
  unreachable without a side-effecting predicate. The audit's original
  reading — latent risk, not present breakage — was right, and the live
  claim was wrong.
- The convergence stands on its own merits regardless: the difference
  can no longer be reached by any future change that makes a predicate
  non-deterministic, and there is one implementation to fix instead of
  two.
- Method note, and the second time this programme has earned it: the
  negative control is what produced the correct answer. Reading two
  implementations tells you they differ; only executing the old one
  tells you whether anything could observe the difference. The earlier
  `TryConvertAnnotatedValue` mistake and this one share a shape —
  a difference confirmed by inspection and its reachability assumed.

Coverage: `AnnotatedConversionParityTests` gains
`Post_coercion_predicate_failure_blames_the_same_span_on_both_paths`,
the first case in that guard to compare diagnostics rather than
converted values, asserting both that the two paths agree and that they
agree on the predicate rather than the coercer. Its scope limitation is
recorded in the test itself.

Validation: refinement, conversion, type-checker, truthiness,
class-cancellation, and compiler-feature-matrix selection 283 passed,
zero failed; `Tosh.Language` builds with zero warnings. The full suite
was not run in this session — an earlier attempt exhausted the editor's
memory — so it remains outstanding for this slice.

### July 27, 2026 — Re-verifying TS-P1-23 across every display path

Asked to confirm the July 26 fix held everywhere rather than only for
`type-of`, it did not. The item is now genuinely closed.

- What the original fix covered: `BuiltInShellTypeDefinition` gained a
  `ToString`, which corrected every path that *stringifies* a
  descriptor — string concatenation, the table header, property access.
  The `Tosh.Runtime.BuiltInShellTypes+BuiltInShellTypeDefinition` leak
  the item was filed for was gone from all of them.
- What it missed: the paths that render *structurally*. A descriptor
  exposes `Name`, `FullName`, `Namespace` and the rest as ordinary
  readable properties, so `ObjectFormatter`'s record-field branch
  claimed it before the `Type` branch could. Interpolation —
  `echo $"{$t}"`, plausibly the most common way to display a type —
  produced `{ Name = "array<int>", FullName = "ToSh.array<int>",
  Namespace = "ToSh", ... }` instead of `array<int>`, as did a
  descriptor nested in a list or record.
- Fix: `FormatValue` recognises `IShellNamedType` above the record-field
  check and renders it as its shell type name, which is the rule the CLR
  `Type` branch immediately below already applied. CLR values are
  untouched — `type-of 5` still reports `System.Int32` — because
  `System.Type` does not implement the shell interface.
- Verified across list, set, tuple, dict, record, and CLR values in
  direct, interpolated, nested-in-list, nested-in-record, concatenated,
  and property-access positions.
- Method note: the original close was evidenced by `type-of` printing
  the right thing. One correct output was taken for the whole class of
  outputs, and the acceptance criterion — "displaying a built-in shell
  type descriptor shows the shell type name" — was read as satisfied by
  one display. Enumerating the display paths first would have caught it,
  and the same habit is what the July 26 note about a filed symptom not
  being a diagnosis was pointing at.

Coverage: `ObjectFormatterTests` gains
`Shell_type_descriptors_display_as_their_shell_type_name`, a theory over
list, set, and tuple asserting both interpolated and nested-in-record
rendering, and `Clr_type_values_are_unaffected_by_the_shell_descriptor_rule`
for the other half of the acceptance. Run against the unfixed formatter,
three of the four cases fail and the CLR case passes, which is the
expected shape.

Observation for a separate item: `type-of` on a record literal
(`{ a = 1 }`) reports `table`. That may be intentional, since records
and single-row tables share a representation, but it reads oddly next to
`array<int>` and `dict<object, object>`. Not changed here.

Validation: formatter, class, display, type-name, introspection, and
generic selection 442 passed, zero failed; formatter selection 24
passed. Full suite still outstanding for this session.

### July 27, 2026 — Pure-profile dependency audit (TS-P1-25, first slice)

The acceptance asks for an audit that fails independently of `RequireTier`.
Building it first turns an invisible defect into a visible one and
establishes exactly how much work the fix is.

- The premise, now asserted: a program of only tier-1 shapes
  (`func add(a: int, b: int) -> int`) emits clean under the pure profile.
  `RequireTier` reasons about the shapes present in the *source*, so it
  cannot see what the emitter unconditionally writes into every artifact.
  Nothing in the profile's own gate was ever going to catch this.
- `PureProfileDependencyAuditTests` reads the emitted PE metadata rather
  than trusting the emit result — `AssemblyReferences` for the coarse
  question and `MemberReferences` for which host entry points are called.
- Measured rather than assumed, which narrowed the item usefully. The
  emitted IL references exactly four assemblies: `System.Console`,
  `System.Private.CoreLib`, `Tosh.Compiler.Runtime`, and `Tosh.Runtime`.
  `Tosh.Language` is *not* among them. Only three unconditional
  `ToshHost` members — `Initialize` and `RegisterCompiledAssembly` from
  `Main`, `EnterExecutionFrame` from every function, method, lambda, and
  block — stand between the artifact and purity. `Tosh.Runtime` is
  permitted by the acceptance, which is where the recursion guard is
  expected to move.
- A separate problem surfaced and is *not* part of this item: the
  generated `deps.json` declares `Tosh.Compiler.IR`, `Tosh.Compiler.Runtime`,
  `Tosh.Language`, `Tosh.Runtime`, `Tosh.Stdlib`, and `Tosh.Tui` — the
  toolchain's own closure rather than the artifact's actual needs. A pure
  artifact would still ship alongside the interpreter even once its IL is
  clean. Worth its own item once the IL half lands.
- The four tests are characterizations, following `LexerCharacterizationTests`:
  they assert the defect and name this item, so the expectations invert in
  the same commit as the fix. One of them is a negative control on the
  audit itself — pure and permissive currently emit identical reference
  sets, and that equality is both the finding and the thing the fix must
  change.

Remaining under `TS-P1-25`: make bootstrap conditional or omit it, move
recursion guarding to a `Tosh.Runtime` primitive, and invert the four
characterizations.

Validation: audit selection 4 passed. Full suite still outstanding for
this session.

### July 27, 2026 — Brace disambiguation options (TS-P2-25)

`docs/BRACE_DISAMBIGUATION_RFC.md` records the options, their costs, and a
recommendation. Measuring the current behaviour contradicted the item as
filed in three ways worth carrying back here.

- **Four forms, not five.** A predicate is not a distinct parse — it is a
  block a command consumes as one, separated only by a hardcoded
  `commandName == "where"` test in `ParseCommandArgument`. `filter { … }`
  reaches the ordinary block path and behaves identically, so the special
  case is not load-bearing.
- **Position already decides, totally.** In expression position `{` is
  always a literal and a block does not parse at all
  (`var b = { echo hi }` is a syntax error). In command-argument position
  `{` is always a block unless it is set- or dict-shaped, so a record is
  unreachable there — `echo (type-of { a = 1 })` fails. The two contexts
  are disjoint today; the defect is that `{ a = 1 }` means different
  things in each.
- **The claimed indistinguishable case is not the real one.** The item
  cites `{ a = 1 \n b = 2 }` as token-identical to a two-statement block,
  but assignment targets require `$`, so a block is `$a = 1 \n $b = 2`.
  The genuine ambiguity is that shape: `var b = { $x = 1 }` yields a
  *record* keyed by `$x`, because `$x` lexes as a bareword and satisfies
  the record rule at `Peek(2)`. It is a live silent misparse, not a
  theoretical one.

Consequently the decision splits in two: how the structural pass decides
(cheap, no grammar change needed) and whether `{` should keep meaning two
things (the actual design choice). Recommendation is Option B — `{`
becomes block-only and literals take an `@{` sigil — on the grounds that
the migration is ~57 grep-findable sites, it is the only option meeting
the item's first acceptance clause, and it leaves the structural pass
needing no lookahead at all.

Method note: the RFC's first draft proposed `#{` for literals. Reading
the lexer rather than borrowing from other languages caught that any `#`
begins a comment, so `#{ a = 1 }` would have lexed as a line comment and
silently deleted the record. `@{` was then checked against the lexer
before being proposed — `@(` is already special-cased, and `@` occurs in
the corpus only inside doc comments.

### July 28, 2026 — `take-while` exhausts system memory (TS-P1-08 reproduction)

Two full-suite runs took down a 128 GB desktop. The first was recorded as an
environment limit and worked around; that was wrong, and treating it as evidence
of a defect instead found one immediately.

- Method: full suite serialised (`xUnit.MaxParallelThreads=1`,
  `ParallelizeTestCollections=false`) with total RSS across dotnet processes
  sampled every second, so a peak attributes to one test rather than to whatever
  collections happened to be co-resident.
- Result: flat at **3,867 MB for 900 seconds** across 597 passing tests, then a
  steady climb beginning one second after the last test output, reaching
  **104,741 MB in 57 seconds**. No further test ever reported.
- Attribution: the last completed test was
  `LazySequenceTests.Iterate_with_take_while`; the next by source order is
  `Recur_fibonacci_take_while`,
  `recur (0, 1) func(a, b) => ($a + $b) | take-while { _ < 100 }`.
- Mechanism: `take-while` never short-circuits the infinite `recur`. It should
  stop at 89. Because Fibonacci values are arbitrary-precision integers whose
  digit count grows linearly, total allocation grows quadratically — which is
  the curve observed.
- Corroboration: `Iterate_with_take_while` pairs the same `take-while` with
  `iterate` and fails rather than hangs, reporting `'iterate' operations must
  produce exactly one value per input item`. Two failure modes, one common
  factor.

**Severity.** `TS-P1-08` is filed as a streaming-efficiency concern — "`first`/
`any` do not evaluate an unnecessary next item". It is not. It exhausts system
memory and takes the machine down, which is the severity class of `TS-P0-07`
(stack overflow killing the process), and that was P0. A re-rating to P0 is
proposed on the item rather than applied, since moving an item between priority
tables is a judgement call for the programme owner.

**Open question, deliberately not answered here.** The suite was recorded green
at 3,197 on July 26, so `Iterate_with_take_while` passed then and fails now.
Whether the recent commits regressed it or parallel scheduling had been masking
it needs a bisect. That bisect must run under a hard memory cap.

**Operational note.** The full suite is not currently usable as a verification
gate. Do not invoke `dotnet test` during ordinary stabilization validation while
this item remains open. If a deliberate reproduction or bisect is explicitly
needed, run it inside a bounded cgroup so the test fails instead of the machine,
and disable both post-build targets. Disabling only the specification build is
insufficient: `ToshParityCheck` otherwise launches a nested `dotnet run`, which
can fan out additional MSBuild workers even for a filtered test:

```
systemd-run --user --scope -p MemoryMax=4G -- \
  dotnet test … \
  -p:DisableToshSpecBuild=true \
  -p:DisableToshParityCheck=true
```

Method note, and the second time in two days: an unfinished measurement was
reported as a result. At 11 minutes this run looked "well-behaved at 4 GB"; its
failure mode began at 15. A run that has not finished is not evidence about the
part that has not run.

### July 28, 2026 — Paired collection delimiters (TS-P2-25)

The RFC's structural principle was accepted with modified spelling.
Ordinary `{ ... }` is now a block; records use `{| ... |}`,
dictionaries `{% ... %}`, and sets `{: ... :}`. Grammar-owned plain
braces such as member lists, match arms, destructuring, projections, and
accessors retain their existing role.

- **Stage 1 — tokens and lexer.** The six paired open/close delimiters
  are real tokens. Adjacency is lexical (`|}` wins before `||`), paired
  openers enter expression mode, and an ordered brace-context stack keeps
  a nested plain block from prematurely leaving a literal. A malformed
  spaced closer still restores command-mode tokenization after its plain
  `}` recovery.
- **Stage 2 — parser and corpus.** Literal dispatch depends only on the
  opener. The set/dictionary/record `LooksLike*` classifiers and generic
  brace collection parser are gone; plain braces take the block path in
  ordinary expression and argument grammar. New diagnostics name spaced,
  missing, and mismatched paired closers. Tests, examples, command
  metadata, compiler comments, README/backlog examples, and generated
  diagnostic/command-reference surfaces use the accepted syntax.
- **Stage 3 — structural boundaries.** `LiteParser` now uses ordered
  delimiter frames. Every candidate inside a plain brace carries its
  exact `OwnerOpenTokenIndex`; `PromoteBoundariesForBlock` selects only a
  parser-proven block's candidates, so the structural pass does not
  recreate the fourteen specialized brace grammars. Nested groups and
  literals suppress their own candidates while a real nested block can
  re-enable them. An independent review caught two recovery defects
  before closure — a deeper exact closer could be lost behind a
  mismatched frame, and a multiline pipeline stage could be promoted as
  a statement. Paired-exact-closer-first unwinding and owner-scoped
  pipeline state now cover both; the plain-`}` recovery exception is
  refined in the `TS-P2-24` entry below.
- **Stage 4 — tooling and contract.** CLI/Tome colorizers, LSP semantic
  tokens, VS Code TextMate/configuration, and GtkSourceView recognize the
  paired delimiters. The editor audit also found and fixed a pre-existing
  reversed TōSh/Tome language-configuration mapping in the VS Code
  manifest. The specification and collection, syntax, and interop
  cheatsheets document adjacency and empty forms (`{||}`, `{%%}`,
  `{::}`); their PDFs were rebuilt and visually inspected. The accepted
  RFC is the decision record, and `TS-P3-08` carries the future
  parser-owned typed-region design.

Validation was intentionally bounded. The Stage 2 parser/corpus
selection passed 560 tests; the final Stage 3 `LiteParserTests` passed
82; the Stage 4 highlighter/semantic-token selection passed 20 and its
fresh test-project build succeeded. The main specification is 280 A4
pages, both copies are byte-identical (SHA-256
`fcb75e11b86cf95df361b99952e3b62c3173e17a2ef3068e49c42b20ec261363`),
and all affected specification and cheatsheet pages render without
clipping, overlap, or broken delimiter glyphs.

A later combined filtered `dotnet test` attempt produced no result and
is not counted: its post-build parity target fanned out nested MSBuild
workers, and it was cancelled at the user's direction as memory began to
grow. The full suite was not run, in accordance with the `TS-P1-08`
operational note above.

### July 28, 2026 — Exact-owner block boundary consumption (TS-P2-24)

The completed paired-delimiter work was first checkpointed as commit
`48499e3`. The next parser-roadmap slice then connected the recursive
parser to the structural candidates prepared by `LiteParser`.

- `InternalParser` computes candidate boundaries once and indexes them by
  token position. `ParseBlock` captures the exact opening-brace token
  before consuming it and maintains a nested owner stack.
- The `ParseBlock` separator and recovery paths accept a structural
  boundary only when its `OwnerOpenTokenIndex` matches the active block.
  Grammar-local command, pipeline, grouping, literal, and specialized
  brace separators deliberately retain their existing rules in this
  slice; making them inherit an outer block owner changed nested command
  substitution behavior and was rejected during review.
- Block recovery now mirrors top-level recovery: it scans only when
  statement parsing made no progress, and any structural recovery scan
  consumes the offending token before stopping at the next promoted
  boundary. This preserves a later same-line declaration instead of
  discarding it.
- Review found four defects before integration: multiline pipe-forward
  (`|>`) cleared structural pending-stage state at its adjacent `>` token;
  both recursive-parser pipe-forward branches skipped their post-stage
  statement-boundary check; `LiteParser.Parse` treated `>` as a stage
  opener; and repeated unmatched closers rescanned the entire delimiter
  stack quadratically. Pipe-forward is now one structural separator and
  observes the following statement boundary in both parser paths.
  Closing-kind counts make unsuccessful recovery lookup constant-time
  while successful unwinding remains amortized linear.
- A further recovery review rejected brace-family interchangeability.
  Exact paired closers no longer close unrelated literals. Plain `}` is
  the sole substitution: it recovers the nearest brace region, so
  `{| value = 1 }` can be diagnosed and resumed without also popping an
  enclosing function block.
- Differential coverage pins newline and explicit boundaries, ordinary
  and pipe-forward continuation, independent nested owners, doc-comment
  starts, specialized class-member separators inside a function,
  same-line declaration recovery, and the repeated-unmatched-closer path.

Validation remains bounded by the `TS-P1-08` operational note. A
single-worker, 2 GB-capped compile of `Tosh.Language` succeeded with zero
warnings and errors; the test project then compiled with project
reference builds and all post-build fan-out disabled, also with zero
warnings and errors. No tests were executed. Next: consume top-level
`LiteScript` statement/stage ranges, then retire only the structural
lookahead helpers proven redundant by the differential corpus.

### July 28, 2026 — Correcting the `take-while` attribution

A capped re-run (`MemoryMax=12G`, swap disabled) narrows and partly contradicts
the entry above. The cap did its job: the suite reported
`System.OutOfMemoryException` instead of taking the machine down, which is the
configuration any future run of this suite should use.

- The entry above concluded "two failure modes, one common factor" and named
  `take-while`. That is not established. Under the cap, `first`-based cases fail
  too — `Iterate_powers_of_2` (`iterate … | first 8`), `Recur_fibonacci`
  (`recur … | first 10`), and `Recur_single_seed` (`recur … | first 5`).
- But cause and collateral are *not* separated by this run. All tests share one
  process, so once any test exhausts the cap every later allocation fails with
  `OutOfMemoryException` as well. `EngineTests.Parser_supports_anonymous_function_arguments`,
  `FormatterTests.Lambda_arrow_form_in_assignment_round_trips`, and three
  `HelpBrowserScreenTests` appear in the failure list and are almost certainly
  victims rather than causes.
- One hypothesis was checked and rejected: every failing `LazySequenceTests` case
  uses `func(x) => …` while `Recur_with_block` (block form) passes, which
  suggested the brace/lexer work had broken anonymous-function parsing. It has
  not — `var f = func(x) => ($x * 2)` still yields a `ToshLambda`.
- What remains true and load-bearing: an infinite generator (`iterate`/`recur`)
  combined with a bounded consumer allocates without limit. Whether `first` is
  independently affected or merely downstream of the first exhaustion needs each
  test run in its own process.

Next diagnostic, deliberately not run here: execute each `LazySequenceTests`
case in a separate capped process to separate the originating failure from the
collateral. Until that is done, `TS-P1-08`'s scope should be read as "bounded
consumers over infinite generators" rather than as `take-while` specifically.

### July 28, 2026 — Capped full-suite result, and where the generator defect sits

The capped run completed, which supersedes the claim that the suite is
unusable. It is usable — under a cap.

- **3,319 passed, 12 failed, 0 skipped of 3,331 in 3m06s**
  (`MemoryMax=12G`, `MemorySwapMax=0`). The suite has grown from 3,197 on
  July 26 as the parser work added coverage.
- Failures: six `LazySequenceTests` (`Iterate_powers_of_2`,
  `Iterate_with_take_while`, `Recur_fibonacci`, `Recur_fibonacci_take_while`,
  `Recur_single_seed`, `Recur_tribonacci`),
  `IteratorCommandTests.Repeatedly_evaluates_each_time`,
  `EngineTests.Parser_supports_anonymous_function_arguments`,
  `FormatterTests.Lambda_arrow_form_in_assignment_round_trips`, and three
  `HelpBrowserScreenTests`.
- Eight report `OutOfMemoryException`. Two carry the diagnostic that actually
  localises the defect: `'iterate' operations must produce exactly one value
  per input item` and the same for `'recur'`. Two are ordinary assertion
  failures (`Strings differ`, `Collection was not empty`).

Narrowing, with one more hypothesis rejected. The generator lambda is not at
fault: `[1,2] | map (func(x) => ($x * 2))` yields exactly one value per input
and the correct values. An earlier check here was too weak — it confirmed
`func(x) => …` *parses* to a `ToshLambda` without confirming what invoking it
yields, and those are different claims.

So the defect sits in how `IterateCommand` and `RecurCommand` invoke the
generator and count its results, not in lambdas, `take-while`, or `first`.
`FunctionalCommandUtilities.RequireSingleResultAsync` is where the
"exactly one value" contract is enforced and is the place to start.

Remaining before the suite is green: that generator-invocation defect, and
the two assertion failures around anonymous-function formatting, which have
not been examined and may be unrelated.

### July 28, 2026 — The memory exhaustion was a parser regression, not `TS-P1-08`

The suite is green: **3,331 passed, 0 failed, 0 skipped in 2m36s** under
`MemoryMax=12G`. Getting there overturned the diagnosis filed earlier today,
and the proposed P0 re-rating of `TS-P1-08` is withdrawn with it.

Root cause. `ParseAnonymousFunctionArrowBody` parsed the body of an
argument-position `=>` lambda as a full pipeline. That was a deliberate widening
under `TS-P2-26`, made so `func (x) => $x + 1` would bind the whole operator
expression rather than just `$x`. It over-corrected: the body then also consumed
whatever followed it.

```
$xs | map func(x) => ($x * 2) | count     → 1, 1, 1   (should be 3)
```

The `| count` was parsed *into* the lambda, so each invocation counted its own
single value and the enclosing stage never ran. Applied to
`iterate 1 func(x) => ($x * 2) | first 8`, the `| first 8` vanished into the
body — leaving the generator unbounded. That is where 104 GB came from. Nothing
was wrong with `take-while`, with `first`, or with streaming.

Fix. `ParsePipeline` gained a `singleExpressionBody` flag. An argument-position
`=>` body parses exactly one stage and stops, so a following `|` or a following
argument belongs to the enclosing command. `TS-P2-26`'s operator-expression
behaviour is preserved — verified directly, `map func(x) => $x + 1` still binds
the whole expression.

A second, smaller regression from the same widening: the body's span started at
the body expression rather than at the `=>`, and the formatter identifies this
form by checking that the body begins with `=>`. So
`$f = func(x) => ($x + 1)` round-tripped as a block body. The span now includes
the arrow.

Two of the twelve failures were therefore real and ten were collateral — the
three `HelpBrowserScreenTests` and the rest simply allocated after the cap was
already exhausted, exactly as suspected but not proven earlier.

Method note, and the one worth keeping from this whole sequence. Three
successive diagnoses were wrong — `take-while`, then lambdas generally, then
`TS-P1-08` — and each was corrected only by executing something rather than
reading it. The memory cap is what made executing safe: it converted a defect
that killed the machine into a test failure with a stack trace. It should be
standard for this suite regardless of this fix:

```
systemd-run --user --scope -p MemoryMax=12G -p MemorySwapMax=0 -- dotnet test …
```

`TS-P1-08` remains open and P1 on its original grounds — nested generator
materialization and short-circuit consumers peeking an extra item. It simply had
nothing to do with this.

### July 28, 2026 — Block boundaries consumed, and sizing what is left (TS-P2-24)

`HasStatementBoundaryAfter` now consults `IsCurrentPromotedStatementBlockBoundary`
before falling back to the line-break heuristic, so inside a block the parser
takes the answer the structural pass already computed instead of re-deriving it.
The change is additive — it can only add boundaries — and the suite is unchanged
at 3,330 of 3,331, the single failure being the packaged-SDK fixture that exits
134 under concurrent load and passes 6 of 6 in isolation.

The more useful result came from measuring rather than declaring. With the
line-break fallback stubbed to `return false`, the parser, lite, engine, and
language-feature selection fails **57 of 947**. So the fallback still answers the
large majority of boundary questions, and the lite structure covers only
top-level statements and promoted in-block candidates.

That materially resizes the item. The previous status read "ordinary `ParseBlock`
statement paths now consume exact-owner promoted candidates; top-level and stage
integration remain", which implies two remaining integrations. The measurement
says the remaining work is most of the boundary surface, not two endpoints —
`ParsePipeline` stage division, argument and expression continuation, and every
nested construct that currently answers the question locally.

Suggested method for the rest, since the 57 are a ready-made worklist: keep the
stub as a temporary harness, take the failures in groups, and move each group's
decision onto the structural pass until the stub passes. The item is done when
the fallback can be deleted rather than when the last integration point is
wired — that is the difference between consuming the structure and consulting it.

### July 28, 2026 — Member lists become boundary owners (TS-P2-24)

Working the fallback stub as a harness rather than guessing which call sites
mattered. With the line-break fallback disabled, full-suite failures went
**113 → 16 → 6** across two changes.

- Root cause of the bulk: only `ParseBlock` ever pushed onto
  `_statementBlockOpenTokenIndices`. Every other brace-delimited member list
  called `HasStatementBoundaryAfter` without registering its opener, so
  `IsCurrentPromotedStatementBlockBoundary` could not match and the line-break
  heuristic was the only thing answering. Class bodies alone accounted for
  113 → 16 — the 13 direct `missing_class_member_separator` failures plus a
  large cascade of downstream misparses.
- `PushBoundaryOwner` now expresses the registration as a `using` scope, and
  class bodies, native bind blocks, and match arm lists use it. That took the
  remainder to 6.
- `StructuralBoundaryFallbackDisabled` is kept as an internal test hook,
  defaulted off. It is the only honest way to tell whether the item is
  finished: the fallback being *unused* is the goal, and that cannot be seen by
  reading call sites.

The residual 6 are principled rather than missed. Three are paired collection
literals — `LiteParserTests.Multi_line_paired_literals_yield_no_statement_boundaries`
asserts by name that the structural pass reports **no** statement boundaries
inside `{| |}` and `{% %}`. Record fields and dict entries separate by newline
but are not statements, so they need their own boundary concept rather than the
statement one. That is a design question, not a wiring gap, and is the next
decision this item needs.

Suite green with the hook off: 3,331 passed, 0 failed, 0 skipped in 2m35s.

### July 29, 2026 — The element-boundary model (TS-P2-24)

Adopts option C from the boundary-model choice: paired collection literals reuse
the existing ownership mechanism, and the vocabulary is renamed to match what it
actually describes. Fallback dependence measured down from 113 to **4**.

The distinction that was missing. `CandidateBoundaries` suppressed candidates
inside grouping constructs and paired literals alike, but they suppress for
opposite reasons: inside `(...)` a line break continues an expression, while
inside `{| ... |}` it separates one entry from the next. Collapsing both into
"not a boundary" is what left record and dict parsing on the line-break
heuristic.

Two corrections were needed along the way, both found by measuring rather than
reasoning:

- Enabling every boundary kind inside literals took the count **up**, 6 to 10,
  and broke `Semicolons_inside_paired_literals_are_suppressed`. A `;` is not an
  entry separator in a record or a dict. Only line breaks are, so only line
  breaks are enabled.
- Sets are not entry lists at all. `{: 1, 2 :}` separates by comma, and a line
  break inside one is whitespace. `BoundaryFrameRole` now distinguishes
  `EntryList` (`{|`, `{%`) from `Literal` (`{:`), and the test that previously
  asserted all three behave alike is split to say what is actually true of each.

Renaming, since the concept was never statement-specific. `LiteBoundary` now
documents a position where the next *element* may begin — a statement in a
block, a member in a class body, an arm in a match, a function in a bind block,
an entry in a record or dict. `HasStatementBoundaryAfter` becomes
`HasElementBoundaryAfter` (27 sites), `IsBoundaryOwnedByBlock` becomes
`IsBoundaryOwnedBy`, `PromoteBoundariesForBlock` becomes
`PromoteBoundariesForOwner`, and the owner stack drops its block-specific name.

Remaining 4, and they are a different sub-problem. `EngineTests`'
class-definition and computed-property cases fail with
`missing_pipeline_separator`, with a cancellation test failing downstream of
them. Those are stage division rather than element division — a computed
property whose body is a pipeline — which is the part of step 2 that has not
been started.

Validation: 3,331 passed, 0 failed, 0 skipped in 2m42s with the hook off.

### July 29, 2026 — The line-break fallback is deleted (TS-P2-24)

The element-boundary half of the item is finished on its real criterion: the
re-derivation is *gone*, not merely unused. `113 → 4 → 0`.

Last owner registered: `ParsePropertyAccessorBlock`. A property's accessor list
owns the boundary between `get` and `set` exactly as a class body owns the one
between members, and without the owner `get => $this.X` had no boundary after it
and its arrow body ran on into the following `set`.

That was the whole of the remaining 4. The previous entry called them "stage
division ... which has not been started", inferred from the
`missing_pipeline_separator` code without reading the source. Wrong, and wrong
the same way as the `take-while` attribution: a diagnostic code names the
symptom, not the cause.

With the harness then reporting a clean suite, the fallback was deleted rather
than left disabled, `HasElementBoundaryAfter(previousEnd)` became
`IsAtElementBoundary()` — the parameter was dead once the line-break test went,
and 23 call sites lost the argument — and `StructuralBoundaryFallbackDisabled`
was removed, its job done.

`HasElementBoundaryAfter` now consults only the structural pass. Constructs that
register as owners: blocks, class bodies, match arm lists, native bind blocks,
property accessor lists, and record/dict literals. A construct that forgets to
register no longer silently falls back to a heuristic — it gets no boundaries at
all, which fails loudly.

Remaining under `TS-P2-24`: stage division. `LiteParser` records
`LiteSeparatorKind.Pipe`/`PipeForward` per stage, but `ParsePipeline` still
decides stage division itself except for the pipe-forward check. That is a
genuinely separate integration, and this time the claim is based on reading
`ParsePipeline` rather than on a diagnostic code.

Validation: 3,331 passed, 0 failed, 0 skipped in 2m49s; zero warnings.

### July 29, 2026 — Closed-item audit, first pass

Auditing closed items for the `TS-P1-23` failure mode: closed on one observation
rather than on an enumeration of the surfaces the acceptance names. Of 23 closed
P1/P2 items, seven use universal language ("every", "all", "each",
"identical"). Three were checked.

**`TS-P2-07` — confirmed, strongest form.** "One exhaustive syntax walker visits
every child" is enforced mechanically by `SyntaxTraversalExhaustivenessTests`: a
new node type fails the suite until it is traversed or acknowledged with a
reason. This is the only closed item that cannot silently regress, and it is
worth treating as the model for what "every" should mean.

**`TS-P2-14` — confirmed, with evidence.** "Suffix forms lex as typed literals in
every expression position." Twelve positions enumerated: variable initializer,
arithmetic, comparison, list element, record field, dict value, return value,
interpolation, ternary arm, set element, and parameter default all yield
`StorageSize`. One differs — `takes 10kb` in command-argument position yields
`String` — and that is the documented exception, since the decision reads
"typed in expression context but remain strings as raw command arguments".
`takes(10kb)` and `takes (10kb)` both yield `StorageSize`. The claim holds; it
is now evidenced rather than assumed.

**`TS-P2-06` — did not hold; fixed.** "All expression-start tokens share one
source of truth." There were three sources. `IsExpressionStartToken` listed 16
kinds; `CanStartCommandSubexpressionArgument` and `CanStartPrimaryArgument` each
listed the same 16 plus `Bang`, as independent switch statements. Nothing was
missing from any of them, so no defect was visible — they agreed only because
someone had maintained all three in step.

That is precisely the drift hazard the item was filed to remove, and it has a
near-term cost: `TS-P3-09` moves `Bang` into the canonical set when `!` becomes a
prefix operator, and three places to remember is how such a change gets
half-done. Both argument predicates now read
`kind == Bang || IsExpressionStartToken(kind)`, so the relationship is explicit
and the sets cannot diverge.

Method note: the scan that found this also produced false positives —
`CanStartPrimaryArgument` was reported as missing `OpenBracePercent` when the
token was simply past the scanner's window. A crude scan is a way to find
candidates, not a source of findings; each one still has to be read.

Remaining unaudited of the seven: `TS-P1-14`, `TS-P1-22`, `TS-P2-12`,
`TS-P2-15`.

Validation: 3,331 passed, 0 failed, 0 skipped; zero warnings.

### July 29, 2026 — Closed-item audit, second pass

The remaining four items with universal acceptance language. Suite now 3,381.

**`TS-P2-12` — confirmed.** "Every quote form has a conformance case." Twelve
forms enumerated: single-quoted is raw (`'a\nb'` is 4 characters), double-quoted
processes escapes (3), unknown double escapes keep the backslash, triple-quoted
and ANSI-C and all three interpolated variants behave as documented. Both
specification examples that originally motivated the item — `("a1" =~ "\d")` and
`"file.cs" =~ "\.cs$"` — return true.

**`TS-P2-15` — confirmed.** "`name=value` and `name = value` parse identically."
Checked across free functions, instance methods, and static methods rather than
free functions alone; all three agree, and option-style `--flag=value` stays
greedy.

**`TS-P1-22` — confirmed.** Each operand evaluated once and later pairs
short-circuited: `1 < (mid()) < 3` calls `mid` once, and `5 < (mid()) < 3` stops
after the failing pair. Compiled mode is covered by
`Compiled_chains_match_the_interpreter` and
`Compiled_chains_evaluate_each_operand_once_and_short_circuit`, so the
"interpreted and compiled" clause holds.

**`TS-P1-14` — behaviour holds, but the "implemented once" clause does not, and
a real defect was hiding behind it.**

The matrix is implemented twice — `OperatorEvaluator.AreEqual` and
`ToshEngine.AreEqualAsync` — because a user-defined `Equals` may be
asynchronous, so the async path cannot delegate. They agree only because someone
maintains them in step, which is the `TS-P2-06` shape again. Worse, they had
already drifted once: `TS-P1-15` records finding the engine still carrying the
old rule after `TS-P1-14` had been applied to the evaluator alone. It was fixed
without adding a test, so nothing prevented a recurrence.

`EqualityParityTests` is that test, the equality counterpart of
`AnnotatedConversionParityTests`, and `AreEqualAsync` became `internal` for it on
the precedent `TS-P1-24` set. It found a defect on its first run, filed as
`TS-P1-26`: `true == "true"` is `true` while `"true" == true` is `false`.
Numeric-against-string and bool-against-number are symmetric in both directions,
so only this one pairing fails to coerce, and both implementations share it —
one rule applied in one direction rather than a drift. It survived `TS-P1-14`
because that item promised symmetry explicitly for ordering and never stated it
for equality.

Audit result across all seven: five confirmed, two gaps — `TS-P2-06`'s three
rival lists (fixed) and this. The `TS-P1-23` hypothesis that prompted the audit
is supported: "Complete" has meant "the named example works", not "the named
surfaces were enumerated".

Validation: 3,381 passed, 0 failed, 0 skipped in 2m40s; zero warnings.

### July 29, 2026 — Symmetric equality (TS-P1-26)

Filed as needing a semantics decision — which coercion direction was intended —
and that framing was wrong. Reading the cascade showed a plain defect with an
unambiguous fix.

`AreEqual` already attempted both directions. It returned on the first
successful *conversion* rather than the first successful *equality*:

```
"true" == true    converts true to "True", compares against "true",
                  returns false — never trying string-to-bool
true == "true"    converts "true" to true, matches
```

`bool` renders as `"True"`, and `TS-P1-14` removed the case-insensitive
`ToString` fallback that had been masking this. So no coercion policy needed
choosing: testing both directions and holding equality when *either* matches
makes the result independent of operand order by construction, because the same
two conversions are attempted whichever operand comes first.

The first attempt at that broke `ClassEqualityCancellationTests`, and the
failure was instructive. The single early return had been load-bearing for a
second reason: it prevented the fall-through to the tail, which dispatches a
user-defined `Equals`. With it removed, `"PROBE" == $left` tried harder, reached
the tail, and invoked `ValueProbe.Equals("PROBE")` — whose body reads
`$other.Value` off a `string` and throws. Both directions are now tried, but a
successful conversion still decides the answer, so the shield is explicit rather
than accidental.

Applied to both implementations. That is the point of the guard that found this:
`EqualityParityTests` would have failed had the fix landed on one surface, which
is exactly how `TS-P1-14` originally went in.

Method note: the value here came from an existing test, not the new one. The new
guard found the asymmetry; the old suite caught the over-broad fix. Neither
would have been enough alone.

Validation: 3,383 passed, 0 failed, 0 skipped in 2m37s; zero warnings.

### July 29, 2026 — An untested branch found by measuring before refactoring (TS-P2-24)

Stage division is the remaining half of `TS-P2-24`. Measuring its scope before
starting found something more important than the refactor.

`HasTopLevelPipeBeforeCloseParen` answers one structural question — does this
parenthesised group contain a top-level `|` — at two call sites: an if-condition
chooses between a pipeline and an operator expression, and an
implicit-current-item group chooses between a pipeline and a `where` predicate.

**Stubbing it to `return false` left the entire suite passing at 3,383.** Its
`true` branch had no coverage whatsoever. `if (ls | count)` could have been
broken outright and nothing would have reported it.

The behaviour itself is fine — `if ([1,2,3] | any { $_ > 2 })` takes the true
branch, `if ([] | count)` takes the false one, and the predicate form still
works. This was a coverage gap, not a defect.

It is, however, exactly the gap that would have made the planned refactor
unverifiable. `TS-P2-24` intends to replace this helper with a structural-pass
query; done against the suite as it stood, that change could have removed
pipeline-in-condition support entirely and stayed green.

`PipelineInParenthesesTests` closes it: five cases over the `true` branch —
single- and multi-stage pipelines, both truthiness outcomes — and three over the
`false` branch, since a helper that chooses between two readings needs both
pinned or the other rots. Verified by negative control: with the helper stubbed,
five fail and the three false-branch cases correctly do not.

Method note. The order mattered more than the work. Had the refactor come first
it would have passed its own tests, passed the suite, and silently deleted a
feature. "Measure before refactoring" earned its keep here in a way that reading
the code could not have — the helper looks obviously load-bearing, and it is;
what was missing was any test saying so.

Remaining under `TS-P2-24`: the refactor itself, now safe to attempt.
`LiteParser.Parse` computes stages only at top level, so answering this from the
structural pass needs stage divisions tracked per delimiter frame and exposed by
owner token index — the same ownership shape the element boundaries already use.

Validation: 3,391 passed, 0 failed, 0 skipped in 2m38s; zero warnings.

### July 29, 2026 — Stage divisions from the structural pass (TS-P2-24)

The first stage-division heuristic is retired. `HasTopLevelPipeBeforeCloseParen`
re-scanned the token stream from the parser's position with a private
bracket-depth counter; it is replaced by `GroupOwnsStageDivision`, a lookup
against the structural pass.

`LiteStageDivision` records every `|` and `|>` with the innermost frame that
owns it. Ownership is by innermost frame whatever its role, which differs from
`LiteBoundary` — a pipe inside `(...)` belongs to those parentheses, while a
boundary inside them is suppressed. Both come from the same walk: the frame
stack that decides boundary ownership is the stack that decides which construct
a `|` divides, and computing them separately would mean two implementations of
delimiter pairing to keep in step.

Order, deliberately: the capability and its differential tests landed before
either call site changed, matching how the element boundaries were done — agree
first, retire the heuristic second. `LiteStageDivisionTests` covers top-level
pipes, nested frames owning their own, a pipe inside a block not being
attributed to the enclosing parens, `|>`, paired literals, and the owner query
in the exact shapes the two call sites ask. Sixteen cases, passing on the first
run, which is the evidence the model matches.

The two call sites hold the opening token rather than its index, so a span-to-index
map resolves one to the other rather than changing their signatures.

This was only safe because of the previous entry. The branch had no coverage at
all, so this refactor could have deleted pipeline-in-condition support and left
the suite green.

Remaining under `TS-P2-24`: `HasTopLevelOperatorBeforeStageBoundary` (one call
site) is the other structural stage helper. The rest of the `HasTopLevel*` family
answers semantic questions — is there an operator, a comma, a comprehension
before some delimiter — and legitimately stays.

Validation: 3,407 passed, 0 failed, 0 skipped in 2m37s; zero warnings.

### July 29, 2026 — Measuring the last stage helper, and where TS-P2-24 stands

`HasTopLevelOperatorBeforeStageBoundary` is the remaining stage-related helper.
Measured before touching it, and the result argues against touching it.

| stub | suite |
|---|---|
| `return false` | **115 failures** |
| `return true` | **0 failures** |

So the `true` branch is heavily exercised and the `false` branch is not
distinguished by any test: always taking `ParseOperatorExpression` instead of
`ParseArgument` is indistinguishable from correct behaviour. Probing the
no-operator shapes that reach this call site — a bare list, record, string,
spread, match — shows all of them working, which is consistent with
`ParseOperatorExpression` being a superset for these inputs.

**Not acted on.** "The suite passes when stubbed" is exactly the reasoning that
would have deleted pipeline-in-condition support two entries ago. That the
distinction is untested is evidence about the tests, not about the code. The
possible redundancy is recorded here as a simplification candidate for whoever
picks it up with a way to prove it.

It is also arguably out of scope. The acceptance targets helpers that answer
*only* structural questions; this one asks a semantic question ("is there an
operator") with a structural qualifier ("before the stage boundary"), which is
not the same thing.

**Where the item stands.** Of the eight surviving `HasTopLevel*` helpers, seven
ask semantic questions — is there a comma, an operator, a comprehension before
some delimiter — and legitimately remain. The only purely structural one,
`HasTopLevelPipeBeforeCloseParen`, is retired. The `LooksLike*` family stands at
54 against the 56 recorded at filing, and is predominantly grammatical ("what
construct is this") rather than structural ("where does this end"), so it is
largely not what the clause targets either.

Against the three acceptance clauses:

1. *The parser consumes the lite structure instead of re-deriving it.* Element
   boundaries: done, the fallback is deleted rather than unused. Stage division:
   the structural helper is retired.
2. *Helpers that only answered structural questions are removed.* The one that
   did is gone.
3. *Structure agrees with today's parser, evidenced by differential tests.*
   `LiteParserTests` and `LiteStageDivisionTests`.

That reads as closeable, but the judgement on clause 2 — whether the hybrid
helper counts — belongs to the programme owner rather than to me, so the status
is left in progress with this assessment recorded.

Validation: 3,407 passed, 0 failed, 0 skipped; zero warnings; working tree clean.

### July 29, 2026 — Displayed collections round-trip again (TS-P2-25 follow-up)

Found by demonstrating the new literals rather than by a failing test: after
`TS-P2-25`, displaying a record or dict produced text the parser rejects.

```
var r = { name = "Ada" }    → tosh.parser.variable_references_require_dollar
var d = { "ada" => 36 }     → tosh.parser.missing_pipeline_separator
```

Records rendered as `{ name = "Ada" }` and dicts as `{ "ada" => 36 }` — the
pre-decision spellings. A bare `{` now opens a block, so neither could be pasted
back. Sets were already correct because `{: :}` did not change spelling, which
is why nothing looked wrong at a glance.

The scope is wider than the REPL: anything that displays a record or dict shows
it — diagnostics, logs, `echo`, table cells. Every one of them was emitting
syntax the shell would refuse.

Fixed in the two places that render them: `ObjectFormatter.FormatRecordFields`
and the object-keyed dictionary display profile, including the truncated and
empty forms (`{| ... |}`, `{||}`, `{%%}`). Verified by feeding each rendered
form back in — record, dict, set, nested record, and all three empties parse and
evaluate.

Worth noting how it surfaced. The suite was green throughout; the tests assert
what the formatter produces, not that what it produces is valid input. A
round-trip property — *format, re-parse, compare* — would have caught it
mechanically, and is the shape worth adding if this recurs.

One test needed updating: `ObjectFormatterTests` had hardcoded the old record
spelling in its nested assertion, which is the correct kind of failure — the
expectation moved in the same change as the behaviour.

Validation: 3,407 passed, 0 failed, 0 skipped in 2m43s; zero warnings.

### July 29, 2026 — Full board review and re-verification of closed items

Board: **32 complete**, 1 withdrawn (`TS-P1-17`), 5 in progress (`TS-P1-24`,
`TS-P1-25`, `TS-P2-11`, `TS-P2-23`, `TS-P2-24`), 1 partial (`TS-P1-07`),
20 planned, 7 proposed, 2 research — 68 items.

Thirty of the thirty-two closed items were re-verified behaviourally through the
CLI, independently of the suite. **All hold.**

- P0: tuple swap resolves before mutating; base-to-leaf construction runs each
  layer once; `??=` is lazy for a non-null target and eager for null; defer runs
  LIFO with body output preserved; recursion raises a structured diagnostic and
  the session survives, limit 128; `channel-recv` emits a null payload as a value
  and ends on closure without an extra one.
- P1: all thirteen — truthiness table, element containment, compound assignment,
  chained defaults and named gaps, unknown-name diagnosis, strict symmetric
  ordering, enum ordering and backing-value equality, value-context collapse,
  `$this` in a method default, chained comparison, type-descriptor display,
  equality symmetry.
- P2: fused `?.`, numeric separators, keyword-argument non-poisoning, all quote
  forms, negative ranges, storage suffixes typed in expression context, named
  arguments with and without spaces, module dispatch at every casing, and the
  paired delimiters.

Not spot-checked: `TS-P0-02` (non-destructive `channel-select`) and `TS-P0-06`
(async class cancellation). Both are race and cancellation properties that a
one-liner cannot meaningfully exercise; they rest on their dedicated tests.

**Three apparent regressions were my own errors**, worth recording because each
looked like a real failure:

- `($a, $b) = ($b, $a)` assigned a tuple rather than swapping. The right-hand
  side must be a collection (`[$b, $a]`); `(…, …)` is a tuple literal.
- A verification class declared both a primary and an explicit constructor of
  the same arity and failed with a self-ambiguity error. That is `TS-P1-18`,
  which is *planned and open* — the script reproduced a known defect rather
  than finding a new one.
- `type-of 10kb` reported `String`. Command-argument position, which is
  `TS-P2-14`'s documented exception. In expression position it is `StorageSize`.

One acceptance clause needed re-reading rather than re-fixing. `TS-P2-16` says
dispatch is "independent of module-name casing", which sounds like call casing
need not match the declaration — it need not, and does not. The specification's
wording is "works for any module-name casing", meaning whatever casing you
*declare* dispatches; and plain functions are equally case-sensitive, since
`func Foo` is not callable as `foo`. The item holds; the acceptance wording is
looser than the spec it implements.

Observations logged, none acted on:

- `type-of` on a record reports `table`.
- A dict piped to `count` yields 1, the `TS-P3-04` asymmetry, seen live.
- `tosh.type.index` anchors its span to line 1 regardless of where the indexing
  is, so the diagnostic points at unrelated source.
- `channel-recv` warns `expects 1 argument(s) but received 0` when the channel
  arrives by pipeline, then works correctly.
- Sets require commas while records and dicts also accept newlines. Faithful to
  each parser, but an inconsistency a user meets before any of the internals.

Validation: 3,407 passed, 0 failed, 0 skipped; zero warnings.

### July 29, 2026 — A format round-trip property

`FormatRoundTripTests` states one property — a rendering must parse back and
render identically — and lets it find its own instances. It exists because the
`TS-P2-25` display defect was invisible to the suite: every formatter test
asserts what the formatter *produces*, and none asserted that what it produces
is valid *input*. The bug surfaced only because the literals were being
demonstrated by hand.

Running it immediately established that the property is narrower than assumed.
Five of twenty-five cases failed, and only one was a defect:

- Arrays and lists render with a CLR type header over multiple lines
  (`Int32[] [\n  1\n  2\n]`). That is a display form and does not pretend to be
  source, so it is not a violation — it means round-trip is not a contract the
  formatter offers across the board. The property is scoped to where it is
  offered, and the split is filed as `TS-P3-10` rather than assumed away or
  "fixed" by stripping a header that may be deliberate.
- The empty dict rendered `{%%} (empty)` where the empty record and set render
  `{||}` and `{::}`. The annotation was redundant — `{%%}` already says empty —
  and it made the rendering unparseable. Removed. This one was mine: the
  suffix was carried over unexamined when the delimiters were fixed.

Also established: a bare string renders unquoted at the root, which is right for
display, so strings are exercised nested inside a container where the quoted
form is used.

The negative control asserts that `{ a = 1 }` — the pre-fix record rendering —
now fails to parse, so the property demonstrably catches the class of defect it
was built for.

Method note. Scoping a property after seeing it fail is the step where a guard
quietly becomes worthless, so the exclusion is recorded in the test itself and
filed as an item rather than left as a shrug. The distinction that matters:
arrays were excluded because the contract does not cover them, not because the
test was inconvenient.

Validation: 3,428 passed, 0 failed, 0 skipped in 2m37s; zero warnings.

### July 29, 2026 — Two diagnostic defects from the board review

Both were logged as observations during the closed-item review; both turned out
to be wider than the symptom recorded.

**Every diagnostic raised inside an interpolation hole pointed at line 1.**
Logged as "`tosh.type.index` anchors to line 1", but the index warning was
incidental. `Lowerer.TryLowerInterpolationHole` re-parses a hole's source text
standalone, so every span it produces is hole-relative — while the renderer
resolves spans against the *outer* source. Anything diagnosed inside `$"{…}"`
therefore landed at position 0.

```
$d["k"]        outside a hole  →  4:6   correct
$"{$d["k"]}"   inside          →  1:1   points at an unrelated line
```

The hole is now parsed at its true offset, so spans come out absolute. Left
padding is the cheapest route given `ToshParser` accepts only a source string;
the padding is whitespace the lexer skips and contains no line breaks, so
nothing about how the hole parses changes.

**A dictionary indexed by a string warned that it expected an integer.**
`CheckIndexAccess` assumed positional indexing for anything that was not
dynamic or a record object, so `$d["k"]` — the ordinary way to read a dict —
warned every time. It now resolves the dictionary's key type and checks against
that; an `object` key accepts anything, so the check only bites when the key
type is specific. Array-by-string still warns, and now with an accurate span.

**A command taking its subject from the pipe warned about arity.**
`$ch | channel-recv` supplies the channel through the pipeline, so no positional
argument is written, but the check counted only what was written and reported
"expects 1 argument(s) but received 0". `CheckPipeline` already knows the stage
index, so a stage after the first now discounts one required argument. A genuine
over-arity still warns.

Filed rather than fixed: `TS-P3-11`, that `type-of` reports `table` for what the
syntax calls a record. Both words are already in circulation — `dynamicrecord`
is an alias, and `table`'s constructor signature mentions `record` — so it is a
naming decision about the type system, not a defect.

Validation: 3,428 passed, 0 failed, 0 skipped in 2m37s; the only build warnings
remain the DevCompanion SQLite advisory.

### July 29, 2026 — Specification conformance corpus (Test Strategy §1)

The strategy section records that four specification examples were failing as
written, and that extracting them into fixtures "would have caught all four
mechanically". Built, and it found two more.

Extraction, by `scratchpad/spec_probe.py`: 188 `lstlisting` blocks, 242 lines
carrying a trailing comment — but most comments are prose ("Variable", "int
(System.Int32)") rather than expected results. Only **24** annotate a value.
Those are replayed with the lines that preceded them in their own block, so
`$x`, `$fn`, and friends exist.

Two genuine defects, and both are the kind only an executable corpus finds.

**The specification could not work as written.** The CLR-interop overload
examples were written `$"one:$a"` and `$"two:$a+$b"`, expecting `one:1` and
`two:1+2`. A ToastScript hole is braced, so those return the literal text
`one:$a`. Four occurrences corrected to `{$a}`. The unbraced form is now pinned
as a test in its own right, so the correction cannot regress into the old
spelling.

**Quantity equality was not unit-aware, while ordering was.**

```
5`s              → 5 seconds
5000`ms          → 5 seconds        same normalised value
5`s > 4000`ms    → true             ordering is dimension-aware
5`s == 5000`ms   → false            equality was not
```

`Quantity` implements `IComparable`, which is why ordering worked, but never
overrode `Equals` — so equality fell through to reference identity. The
specification is explicit that "comparison operators also use base-value
comparison with dimension checking" and lists `5`s == 5000`ms` as an example.
`Equals` and `GetHashCode` now match `CompareTo`: base value plus dimension.
Mismatched dimensions are unequal rather than an error, since `==` is a question
where ordering is a request with no meaningful cross-dimension answer.

This is the third instance of one shape: `TS-P1-15` found enums ordered but not
equal, `TS-P1-26` found equality asymmetric for bool against string, and this
finds quantities ordered but not equal. Ordering and equality are implemented
apart, and a type taught to one is not thereby taught the other. Worth a
standing check rather than a third individual repair.

Curated rather than extracted at test time: a generic extractor cannot tell a
documented value from a description, and fails on shapes that are not
expressions at all — `$x += 5` among them. `SpecConformanceTests` holds the
checkable examples; the probe script regenerates candidates when the
specification changes.

Validation: 3,441 passed, 0 failed, 0 skipped in 2m41s.

### July 29, 2026 — Ordering and equality must agree (standing check)

Three repairs have had one shape. `TS-P1-15` found enum members ordered but not
equal to their backing value; `TS-P1-26` found equality asymmetric for bool
against string; the conformance corpus found quantities ordered but not equal.
The cause is structural — ordering and equality are implemented apart, so a type
taught one is not thereby taught the other — so the fourth instance is worth
preventing rather than repairing.

`OrderingEqualityAgreementTests` states the invariant instead of enumerating
types. For any pair the language agrees to order, exactly one of `a < b`,
`a == b`, `a > b` holds, and `a < b` agrees with `b > a`. A pair the language
declines to order is out of scope, which keeps the property about types that do
order rather than about which pairs are orderable.

Corpus built through the engine rather than constructed directly, so the values
are exactly what a script produces: numerics across CLR types, strings, enum
members against each other and against backing values, quantities within and
across units, storage sizes, and temporals. A third case asserts the two
equality implementations agree on all of them, since `TS-P1-24` leaves those
separate.

Verified by negative control: with `Quantity.Equals` disabled, both cross-unit
pairs report "0 of three hold", naming the defect rather than merely failing.

Operational note: one full run aborted at 1,813 tests and passed cleanly on
re-run at 3,444. That matches two earlier entries — a packaged-SDK subprocess
exiting 134 under concurrent load, and `ScopeAndChannelTests` failing once then
passing in isolation. Recorded rather than dismissed; if it recurs it is worth
tracing rather than retrying.

Validation: 3,444 passed, 0 failed, 0 skipped in 2m41s.

### July 29, 2026 — The pure profile is pure (TS-P1-25)

The emitter wrote three `ToshHost` calls into every artifact regardless of
profile, so a program of only tier-1 shapes compiled clean and still carried the
compiler host. `RequireTier` could never catch it: that gate reasons about the
shapes in the *source*, not about what the emitter writes unconditionally.

All three are now profile-aware.

- `ToshHost.Initialize` exists to give the host a runtime for builtin dispatch.
  A pure artifact dispatches nothing through the host — builtin dispatch is a
  tier-2 feature the profile already rejects — so the bootstrap was initialising
  a host that is never called while forcing the reference that made the artifact
  impure. Omitted.
- `ToshHost.RegisterCompiledAssembly` serves host-backed module static access and
  the `NewObject` fallback, both of which a pure artifact cannot reach for the
  same reason. Omitted.
- `ToshHost.EnterExecutionFrame` is a thin wrapper that reads the session's
  configured depth limit and delegates to
  `ToshExecutionDepthGuard.Enter`. The pure profile now calls that primitive
  directly at `DefaultMaximumDepth` — the same ceiling, minus the ability to
  lower it per session. Dropping the host does not drop the guard, and a test
  asserts the guard is still present rather than only that the host is gone.

Measured on a real compiled artifact, not just an in-memory emit. Before, it
referenced `System.Console`, `System.Private.CoreLib`, `Tosh.Compiler.Runtime`,
and `Tosh.Runtime`. Now `Tosh.Compiler.Runtime` is absent, `ToshHost` appears
zero times, `ToshExecutionDepthGuard` appears, and the artifact runs to exit 0.
A permissive build of the same source still carries the host, so the two
profiles genuinely differ.

The four characterizations written when the audit was built are inverted, which
is the point of having written them that way: `Pure_artifact_still_references_the_compiler_host`
became `Pure_artifact_references_no_forbidden_assembly`, and the negative
control that asserted pure and permissive emit *identical* references now
asserts they differ.

Not addressed, and still worth its own item: the generated `deps.json` declares
the toolchain's whole closure — `Tosh.Compiler.IR`, `Tosh.Compiler.Runtime`,
`Tosh.Language`, `Tosh.Runtime`, `Tosh.Stdlib`, `Tosh.Tui` — so a pure artifact
would still ship alongside the interpreter even though its IL no longer
references it. That is packaging rather than emission.

Validation: 3,445 passed, 0 failed, 0 skipped in 2m39s.

### July 29, 2026 — Three decisions taken: collection rendering, the record's name, and closing step 2

Three items were sitting on a decision rather than on work. Taking them together
kept the answers consistent, since two of them are about what a displayed value
calls itself.

**`TS-P3-10` — header at root, source-like nested.** `FormatRoundTripTests` had
been scoped around arrays because they render with a CLR type header
(`Int32[] [ 1, 2, 3 ]`) that is display rather than source. The decision does not
pick one style over the other; it makes the choice positional, which is the split
strings already had — a bare string renders unquoted at the root and quoted when
nested, because the two positions want different things. So `isRoot` is threaded
into `FormatEnumerable` and the type name is emitted only there. At the root the
element type is the informative part and nothing is going to be pasted back;
nested, the type name is noise on every field and makes the whole enclosing value
unparseable.

That brought nested arrays into the round-trip property, and the property
immediately earned its keep again by exposing a second defect:

**Indentation was counted twice per level.** `FormatContainer` re-indents every
line of every item it holds, *and* indented itself by its own depth — so a nested
container's items drifted a level further right at each level, with its closing
bracket following. Three levels of array rendered items at 2, 6, and 12 spaces
instead of 2, 4, and 6. The `depth` parameter existed only to compute that
indent, so removing the arithmetic removed the parameter from all three call
sites. This was never new; the type header made it look like decoration rather
than misalignment.

**`TS-P3-11` — `record` wins.** `type-of {| a = 1 |}` answered `table` while the
syntax, the specification, and the help text all said record. The descriptor is
renamed and `table` and `dynamicrecord` stay registered as aliases, so existing
annotations keep working. Three places had to move together, and the second was
found only by running the first: the shell-type registry (`type-of`), the
annotation resolver (`var r: record = …` — a name is not an annotation until
`DotNetTypeResolver` knows it), and `GetFriendlyTypeName`. `dynamicrecord` was
documented as an alias but had never been resolvable as an annotation at all;
it is now.

The specification's own type table was wrong in a way unrelated to the rename:
it mapped `table` to `ToSh.DataTable`, "structured tabular data". The type is
`System.Dynamic.ExpandoObject` and always has been.

The rename also found a way an alias can be honoured everywhere except where it
matters. `DisplayPreferences` resolves a user's column overrides by shell type
name, and yielded only the descriptor's *current* name as a candidate — so a
profile keyed `table`, which is what anyone's config would say, silently stopped
applying the moment the type answered `record`. Nothing threw; the columns just
went back to default. `BuiltInShellTypes.AliasesFor` now supplies every name a
type answers to, and preference resolution offers all of them, which fixes the
same latent problem for `map` against `dict`. The test that found it is left
keyed on the alias, with a comment saying why, and a companion keyed on `record`
proves the primary name still resolves — an alias guard that passes because
nothing resolves is the failure mode worth guarding against.

Not renamed: `type-of` on a CLR `Type` and the `ObjectInspector` member kind
already used `record` for the *named* record declaration, so the two senses of
the word — a declared shape and an anonymous one — now share a name the way C#'s
`record` and an anonymous type do not. Recorded rather than resolved; if it turns
out to confuse, the anonymous form is the one to qualify.

**`TS-P2-24` — closed.** The July 29 assessment left the item open on one
judgement: whether `HasTopLevelOperatorBeforeStageBoundary`, which asks a
semantic question with a structural qualifier, counts against the clause about
removing structural helpers. Resolved as no. Element boundaries consume the lite
structure with the fallback deleted rather than merely unused; the one purely
structural helper is retired; `LiteParserTests` and `LiteStageDivisionTests`
carry the differential evidence. The remaining `HasTopLevel*` helpers ask what a
construct *is*, which is grammar, not structure.

### July 29, 2026 — The twin inventory, and why TS-P1-24's first clause cannot be met

Built the guard the acceptance asks for, and building it corrected the audit the
item rests on.

**The count was wrong, twice, for the same reason.** The item records "23 truly
parallel pairs against 6 that delegate", measured by text search. A regex audit
run today over `Tosh.Language` and `Tosh.Runtime` said 29 parallel and 10
delegating. Reflection says **63** pairs. Both text-based numbers missed the same
things: interface declarations, explicit implementations, and any pair whose
declaration spans lines the pattern did not anticipate. A third measurement
disagreeing with the first two is the point at which the method, not the number,
is the problem — so the guard measures by reflection, where the answer is exact
and cannot drift with formatting.

**What the real inventory shows is not a long tail.** Grouped by cause:

- **9 declarations in the project's own dual-surface interfaces** —
  `IShellRecordObject.TryGetMember`/`TryGetMemberAsync`,
  `IObjectAccessor.GetValue`, `IShellInvocableObject.InvokeInstanceMethod`,
  `IShellEnumerableObject.EnumerateShellItems`,
  `IShellStaticType.CreateInstance`, and their siblings.
- **21 implementations of those declarations**, across `ToshClassInstance`,
  `ToshClassDefinition`, the three reference kinds, and
  `ReflectionObjectAccessor`.
- **4 already converged** — the refinement cluster and the annotated-conversion
  pair, listed so that *un*-converging them fails.
- **29 genuinely parallel internals**, which is the convergeable remainder.

So the first acceptance clause — "each pair either delegates to one
implementation or is removed" — is unreachable for 30 of the 63 as long as the
interfaces declare both members. An implementer *cannot* delegate: the contract
demands both.

**And the largest of them cannot be converged even in principle without changing
semantics.** `ShellIndexingUtilities.GetIndexedValue` (84 lines) against
`GetIndexedValueAsync` is not a copy with `await` sprinkled in. The async branch
awaits `IShellRecordObject.TryGetMemberAsync` and then deliberately avoids
re-entering the synchronous record API, with a comment saying so. Making the sync
path delegate would block on a user-defined property getter that may itself be
asynchronous; making the async path delegate would drop asynchronous member
dispatch. Either direction is a behaviour change, so neither is a refactor. This
is the shape the whole contract-imposed group has.

**Decision required, and it is not mine.** Either the dual-surface interfaces are
deliberate — the interpreter genuinely needs to serve synchronous and
asynchronous callers with different member-dispatch semantics, in which case the
acceptance should say so and scope itself to the 29 internals — or the
synchronous surface should be retired in favour of one asynchronous
implementation with a single blocking bridge at the boundary, which is a much
larger change than the item describes and would want its own item.

Recorded rather than resolved. What did land is the ratchet: `KnownTwins` pins
all 63 with a note on each group, a new twin fails until it is listed, and a
converged twin fails until it is struck off. Neither direction is silent, which
is the property the two earlier one-pair repairs lacked.

Method note. The guard excludes twins imposed from outside the codebase —
`IAsyncDisposable`, `TextWriter` — by checking whether the base or interface
declaration lives in a `Tosh.*` assembly. Without that filter the inventory was
dominated by `Dispose`/`DisposeAsync` and `Write`/`WriteAsync`, which no decision
here can affect, and the pairs that matter were buried among them.

### July 29, 2026 — The type table was never filled (TS-P2-23 step 2)

`ParseContext` was given a `typeNames` set and an `IsKnownType` query on July 26.
Nothing ever populated it. `CreateParseContext` passed commands and modules and
left types null, so `IsKnownType` answered `false` for every name, and the single
call site that consulted it —
`_context.IsKnownCommand(Current.Text) && !_context.IsKnownType(Current.Text)` —
was vacuously true in its second half. The mechanism existed, was tested for the
cases that did not need it, and did nothing.

The consequence was a live, user-visible defect:

```
> int.Parse("42")
✖ error tosh.runtime.unknown_command — Command 'int.Parse' was not found.
                                       help: did you mean 'intersperse'?
```

`long.Parse`, `bool.Parse`, `double.Parse`, and `char.ToUpper` all failed the same
way. Static access on a *lower-case* type alias did not work at all.

The tell was sitting in the predicate. `LooksLikeQualifiedDotNetAccess` ended:

```csharp
return char.IsUpper(firstSegment[0]) || string.Equals(firstSegment, "string", StringComparison.Ordinal);
```

That `"string"` is not a design decision, it is a patch. Someone hit this exact
class of failure once, and fixed the one name in front of them rather than the
rule — which is the shape `TS-P2-23` exists to remove.

`CreateParseContext` now fills the table from what the engine already has:
declared classes in each scope, `Runtime.Classes`,
`DotNetTypeResolver.BuiltInAliases`, and any `using X = Y` aliases. Both casing
predicates became instance methods and consult the table first. The `"string"`
special case is deleted — not because it was wrong about `string`, but because
`string` is one entry in `BuiltInAliases` alongside every other lower-case alias
that used to fail.

Casing survives as the fallback, deliberately and documented in the method: the
platform type index holds thousands of names and is not worth materializing per
parse, so an unqualified `System.Text.Encoding.UTF8` still resolves the old way.
A test pins that, so deleting the fallback stays a deliberate act.

Verified with a negative control rather than by assertion alone — the same
programs run against `HEAD` before the change produce `unknown_command`, which is
what establishes that the table is load-bearing and not decorative. A second
negative control parses `int.Parse("42")` with and without a type table and
asserts the resulting *trees* differ, since both parse cleanly and only the shape
tells them apart.

**The table had to be narrowed, and the suite is what narrowed it.** The first
version consulted it for bare names too, and `Function_call_single_arg_no_tuple`
failed: it declares `func double(x)` and calls `double(5)`, and `double` is a
built-in alias, so the call was read as a constructor — "Construct instances with
`new double(...)`". The same trap was set for `map` and `set`, which are commands
*and* aliases for `Dictionary` and `HashSet`, and for `list`.

The fix is a precedence rule worth stating plainly: a bare name is where a
declaration wins, and a qualified name is where the type table belongs.
`int.Parse` names a type because of the dot, not because of the spelling. So
`LooksLikeQualifiedDotNetAccess` consults the table on the leading segment and
`LooksLikePotentialClrTypeName` does not consult it at all for the unqualified
case, where casing is unchanged. Four alias collisions are pinned as a theory so
the narrowing cannot be quietly undone.

Worth noting about the near-miss: had that test not existed, the type table would
have shipped claiming `double`, `map`, `set`, and `list` from every user who
declared a function by those names. The table was populated and the predicate
widened in the same edit, and only one of the two was wrong.

**Where the item stands.** Clause 1 (identity from a table rather than
capitalization) and clause 3 (a capitalized module and a lower-case CLR type both
resolve) are met and tested. Clause 2 — keyword recognition driven by the
generated language-surface registry — is blocked, because `TS-P2-10` is still
Planned and there is no registry to drive it from. 182 `Current.Text == "…"`
comparisons remain, up from the 160 recorded at filing, which is drift in the
wrong direction and an argument for `TS-P2-10` moving up the order. `TS-P2-23`
cannot close before it.

### July 29, 2026 — Running the specification's worked examples for the first time

A step-back review of the shell surface, done by executing it rather than reading
it. Most of what was tried works: typed pipelines, the paired collection
literals, unit equality across scales, list and set comprehensions with a `where`
clause, classes with methods calling into CLR statics, `try`/`catch (e)`, and
`select` followed by `to csv`.

What did not work were the specification's **worked examples** — the multi-line
pipelines in the appendix, which is what a new user copies first. Of the two
tried, both were broken, and the CSV one in three separate ways:

- `from-csv` and `to-csv` do not exist. The commands are `from csv` and `to csv`.
  `from-json` and `to-json` in the JSON example are wrong the same way.
- `select Date, Customer, Amount` does not parse — `unexpected_token ','`.
  Arguments are space-separated, as in every shell. Written with commas twice.
- With those corrected the pipeline still fails: `from csv` yields every column as
  `string`, so `where _.Amount > 100` reports "Values of type 'System.String' and
  'System.Int32' cannot be ordered" and needs an explicit `cast int`.

The first two are corrected in the specification. The third is filed as
`TS-P2-27` because it is a decision — NuShell infers CSV column types, PowerShell
does not, and this shell's stated design points one way while its implementation
points the other. Notably `from json` *does* produce typed values, so the
inconsistency is internal, not just against the document.

**Why the conformance corpus missed all of it**, which is the more useful finding.
`SpecConformanceTests` was built by harvesting specification lines that carry a
*documented expected value* — 24 of 242 candidates. That method structurally
excludes every multi-line pipeline, because a pipeline's result is not written as
a trailing comment. So the corpus covers the examples that were easiest to check
and skips the ones most likely to be copied. Filed as `TS-P2-26`.

This is the third time the specification's own examples have been found wrong by
running them, after `TS-P2-12`'s regex cases and the `$"one:$a"` interpolation
defect. The pattern is consistent enough to state as a rule: an example that has
never been executed should be assumed broken, and the corpus should be measured by
what fraction of examples it *runs*, not by how many assertions it holds.

Two syntax errors in the probe were mine rather than the language's — `catch e {`
instead of `catch (e) {`, and `cast $value int` instead of `cast int $value` —
recorded so the finding is not overstated. Both produced targeted diagnostics that
named the fix, which is the diagnostic work paying off.

### July 29, 2026 — Partial declarations across files (TS-P2-28), and a question that was not a feature request

Asked whether partial modules exist and whether they can be split across imported
files. Both answers were yes, which made this a defect slice rather than the
feature implementation it looked like.

**What already worked**, and is better built than expected: `ModuleDefinitionStatementSyntax`
carries `IsPartial`, and `EvaluateModuleDefinitionAsync` merges by *sharing* the
existing `ModuleExportTable` rather than copying into it — so every
`ToshModuleObject` view observes the merged state automatically, and prior exports
are pre-seeded into the new body's scope, which is what lets a later part call into
an earlier one.

**What did not work** was the named import form:

```tosh
require Sys from "./a.tosh"
require Sys from "./b.tosh"
✖ tosh.runtime.require_failed — Export 'Sys' was not found in '…/b.tosh'
```

Whichever file came second failed, in either order, while bare
`require "./b.tosh"` worked. The cause is four lines that all look harmless:

```csharp
existingDef.MergePartial(...);
yield break;              // ← before DeclareType/DeclareModule
```

Merge, then return before declaring. The contributing file therefore exported
nothing under that name, and `ImportRequiredArtifact` — which reads
`artifact.Exports.Modules[name]` — found nothing and threw. The bare form worked
only because it never looks a name up; the merge had already happened as a side
effect. So the diagnostic said the export was missing at the exact moment the
merge had succeeded.

**The scope grew twice while measuring, both times outward.**

- It is not module-specific. Classes, records, and structs share the identical
  shape, and a partial *class* split across two files fails the same way —
  confirmed live before assuming it. One fix pattern, four sites.
- Modules were missing a check the other three have. `module Sys { … }` followed
  by `partial module Sys { … }` merged silently; the class equivalent raises
  `tosh.runtime.partial_mismatch`. `ToshModuleObject` had no `IsPartial` to check
  against, so it now carries one.

**What was deliberately left alone.** A plain non-partial redeclaration *replaces*
the previous one and its members are gone — `module Sys` twice keeps only the
second, and `class Box` twice behaves identically. That looks like the same class
of silent loss, and it is consistent across all four kinds, and it is what a REPL
wants when you redefine something at the prompt. It is pinned by a test that says
so, precisely so a later reader does not "fix" it, and stated in the
specification as the reason `partial` is required rather than inferred.

**Also found and filed separately** as `TS-P2-29`: `source "./x.tosh"` resolves
relative to the working directory rather than the sourcing script's directory,
where `require` resolves correctly. A script that sources a sibling only works
when run from its own directory.

Negative control: 8 of the 16 new cases fail against the unfixed engine, and the 8
that pass are the ones covering behaviour that already worked — the bare form,
the same-file split, a lone partial, and plain redeclaration. A guard where every
case fails before the fix would have been the more suspicious result here, since
half the surface was already correct.

Documentation: partial modules had no entry at all — `partial` was documented for
classes and structs only, so a working feature was undiscoverable. The module
section now covers merging, declaration order, the cross-file split with both
import forms, and the replace-versus-merge rule.

### July 29, 2026 — Three decisions taken, and CSV columns arrive typed

**`TS-P1-24` — the dual surface stays; the item rescopes.** `IShellRecordObject`,
`IObjectAccessor`, `IShellInvocableObject`, `IShellEnumerableObject`, and
`IShellStaticType` each declare a sync and an async member because the interpreter
serves both kinds of caller with genuinely different dispatch semantics —
`GetIndexedValueAsync` avoids re-entering the synchronous record API on purpose,
with a comment saying so. So the 30 contract-imposed pairs are intended and the
convergence clause applies to the **29 parallel internals**, led by
`ThrowDetailedSingleConstructorMismatch` (55 lines) and
`ApplyPendingParameterDefaults` (50). Retiring the synchronous surface behind one
blocking bridge was considered and rejected as a larger change than this item
describes; it would get its own item. The inventory guard's comments now record the
decision rather than the open question, so a later reader does not re-litigate it.

**`TS-P2-27` — `from csv` infers numbers and booleans.** The narrow option, and
the interesting part is what it declines.

- **Per column, not per cell.** This was the substantive design choice. Typing
  only the cells that parse would put an `int` beside a `string` in one column, so
  values in that column could not be compared *with each other* — a failure that
  appears only on the rows that differ, which is worse than leaving the column
  textual. A column is typed only when every non-empty cell agrees.
- **Leading zeros stay text.** `007`, `01234` — an identifier, and converting it
  destroys the zero irreversibly. One such cell keeps its whole column textual.
- **No thousands separators**, because the comma is also the delimiter.
- **No dates**, because `01/02/26` is three different days by locale, and guessing
  wrong there corrupts data silently rather than loudly.
- **Empty cells are not evidence** and become `null` in a typed column, not the
  empty string a textual column keeps.
- `--raw` / `--no-infer` returns everything as text. `tsv` gets the same rules,
  sharing the format implementation.

The specification's CSV worked example — the one that found this — now runs exactly
as written, and is asserted end to end rather than by column type, because "the
documented pipeline runs" is the actual claim.

Two things worth recording from the testing rather than the implementation.

The first assertion attempt measured the wrong thing: `| each { $_.n }` over a
column with a gap reported two values rather than three, because a block yielding
`null` contributes nothing to the pipeline — PowerShell's rule, and orthogonal to
inference. Reading the rows directly is what makes the assertion about the thing it
claims to be about. Worth knowing that `each` cannot map a column with gaps; not
chased here, and not obviously wrong.

Three of the probe failures across this session were my own syntax rather than the
language's — `catch e {`, `cast $value int`, `select A, B` — and every one produced
a targeted diagnostic naming the fix. That is the diagnostic work paying off, and
it is the reason the two genuine spec defects were separable from my mistakes.

**Next: `TS-P2-10`**, the language-surface registry, chosen as the only Planned
item another in-progress item cannot proceed without.

### July 29, 2026 — The language-surface registry (TS-P2-10, first slice)

Chosen because it was the only Planned item another in-progress item could not
proceed without. Measuring it first was worth more than the implementation.

**The drift, measured.** Eight consumers keep their own idea of what a keyword is.
Between them they name **115 distinct words, of which 7 appear in all eight**:

| consumer | words |
|---|---|
| LSP feature table | 93 |
| help catalogue | 77 |
| VS Code metadata | 68 |
| CLI highlighter | 59 |
| binder suggestion pool | 40 |
| REPL completion | 36 |
| Tome colorizer | 21 |
| REPL classifier | 15 |

The consequences are ordinary rather than exotic. `const`, `defer`, `yield`,
`union`, `rune`, `event`, and `import` are real keywords that went unhighlighted at
the prompt. `interface` was the sharpest: it sat in the highlighter's
`TypeDeclarationKeywords` without being in `Keywords`, so the identifier *after* it
was coloured as a type while the keyword itself was not coloured at all — a
disagreement inside one file. The Tome colorizer coloured no control-flow keyword
at all, so `if` and `while` were highlighted in the terminal and plain in the Tome.

**Membership is established by executing each word, and that mattered twice.**

The first validation pass was a source scan: does the word appear as a literal the
parser compares against? It said all 115 were genuine. That check is too weak, and
running the words showed it: `let x = 5` fails, `quote` is an unknown command, and
`once` is not a member modifier — yet all three are documented in the LSP feature
table as keywords. `let` is not merely absent, it is a *proposal*, `TS-P3-02`. The
registry now carries a probe per word — the smallest program in which it does its
job — and a word cannot enter without one.

The second time was the reverse error, and it was mine. Having found `abstract` and
`private` in `IsDeclarationModifierWord`, I probed `abstract class C { }` and
`private var x = 1`, saw both fail, and reported that the parser listed two
modifiers the language did not have. Wrong: they are **member** modifiers, part of
an undocumented family of C#-familiar aliases parsed alongside their ToastScript
spellings — `private`/`shy`, `abstract`/`hollow`, `readonly`/`fixed`,
`required`/`vital`, `override`/`overrule`, `protected`/`guarded`,
`obsolete`/`fading`, `shared`/`static`, `public`/`proud`. Nine working spellings,
documented nowhere; filed as `TS-P2-30`. Trying a word in the wrong position and
concluding it is not real is a distinct failure from reading a predicate and
assuming it fires, and this session produced one of each.

`IsDeclarationModifierWord` does still list `abstract` and `private` among
*declaration* modifiers, where they genuinely do not work. Those two entries are
dead, and the item records the choice between removing them and honouring them.

**A guard is only as wide as its pattern.** The consumer-subset check passed for the
Tome colorizer while missing nine words, because the colorizer keeps its modifiers
in a set named `Modifiers` and the pattern only matched sets named `Keywords`,
`ControlFlowKeywords`, and friends. That is the least visible way for a guard to be
worthless — it reports success over the part it cannot see. Widened to `Modifiers`,
`Constants`, and `KeywordSuggestionPool`, and now covering five consumers.

**Landed:** `LanguageSurface` with 95 words by category, nine probes' worth of
aliases included; the CLI highlighter and Tome colorizer derive from it rather than
holding lists; the guard checks both directions plus the exact agreement of the
visibility family with `ParseDeclarationModifier`.

**Not landed:** the three prose-carrying consumers — help catalogue, LSP hover text,
VS Code metadata — still hold their own key sets. Their descriptions are editorial
and differ legitimately, so unifying them means separating identity from prose
rather than deleting one of them, which is its own slice. Operators and document
symbols are untouched. `TS-P2-23`'s clause 2 needs the keyword-recognition side,
which is the next step here.

### July 29, 2026 — Correction: the LSP was right and the registry was wrong (TS-P2-10)

The previous entry reported that the LSP feature table documented three keywords
that do not exist — `let`, `quote`, and `once` — and treated that as evidence for
validating the registry by execution. **All three are real.** So are the other
thirteen words the first registry draft was missing. The LSP was the most complete
consumer, and the registry was the incomplete one.

- `let` is a **comprehension clause**:
  `[$y <| for x in 1..3 let y = ($x * 2)]` yields `[2, 4, 6]`. `TS-P3-02` proposes
  general `let` *bindings*, which is a different feature; `let x = 5` failing does
  not mean `let` is absent.
- `quote` takes a **block in argument position**: `echo (quote { $x + 1 })` returns
  a syntax object. `quote foo` is an unknown command because the form needs a brace.
- `once` is an **event-handler clause**: `func f(e) handles X once { }`. The probe
  that condemned it put it in member position.

Sixteen words were missing from the first draft and every one is genuine:
`contains`, `starts-with`, `ends-with`, `is-in`, `is-not-in` (operator words);
`get`, `set` (property accessors); `handles`, `priority`, `when`, `once` (handler
clauses); `let`, `where` (comprehension clauses); `implements` (composition);
`leaky` (type modifier); `quote` (expression form). The registry is now 103 words
across three new categories the first draft did not know existed — accessors,
handler clauses, and contextual keywords.

**The methodological point, which is the durable part.** Execution validation is
**directional**. A passing probe proves a word is real. A failing probe proves
nothing about the word — only that the probe is wrong. This slice produced *three*
false accusations from that single confusion, plus one earlier in the same slice
(`abstract` and `private` reported as fictional when they are member-modifier
aliases). Four instances of one mistake in one item.

The pattern is now specific enough to state as a rule: **a word absent from the
registry is a claim about the registry, never about the language.** The guard's
consumer-subset check is deliberately one-way for that reason, and the negative
control that asserted `let` was not real is withdrawn rather than quietly deleted,
with its reason recorded in the test.

Also corrected: the parser's spelling comparisons went 182 → 142 in this slice, not
by deleting checks but by replacing `ParseClassMember`'s 22-branch modifier chain —
each alias written out twice, once to enter the loop and once to set its flag — with
one registry lookup and a switch on canonical spellings.

**And one live regression, caught by running the shell rather than the suite.**
Categorising `shy` as visibility-only broke `shy prop X = 1`, which broke a user
autoload file on the next launch. `shy` is the one word in two families — a
declaration modifier *and* a member modifier — and its probe (`shy func f() { }`)
kept passing throughout, because that is a real use of the other family. A word in
two families needs a probe per family; `Every_member_modifier_works_in_member_position`
is that check, and `hollow` turned out to need it too.

### July 29, 2026 — Reading the specification first, and what that found

Last entry closed with a note that this slice should have started by reading the
specification's own keyword enumeration instead of guessing. Doing that took
minutes and settled everything the four wrong guesses had cost:

- The spec's §Keywords itemize lists **81** words; its `lstdefinelanguage`
  colouring list lists **80**. Every one of them is now in the registry — **zero
  missing in either direction**, which is the first time any two lists in this
  programme have agreed exactly.
- The registry held 22 the §Keywords section did not. Eleven are operator words,
  and their absence is *correct*: the spec says so explicitly — "The words `and`,
  `or`, and `not` are operators, not keywords" — and documents them in the operator
  section instead. The remaining eleven were genuine gaps.

That comparison is now `The_specification_keyword_list_matches_the_registry`,
which makes the specification a checked consumer rather than a hand-maintained one,
directly against the item's "spec tables ... have drifted" clause. It also caught
that the spec's two lists had drifted from *each other*: `fading` appeared in the
colouring list and not in the reader's list.

**`TS-P2-30` closes on its documentation half.** The Member Modifiers section now
pairs each C#-familiar alias with the word it means — `shy`/`private`,
`fixed`/`readonly`, `vital`/`required`, `guarded`/`protected`,
`overrule`/`override`, `fading`/`obsolete`, `hollow`/`abstract` — and states that
both spellings mean the same thing. Nine working spellings stop being
undiscoverable. `IsDeclarationModifierWord`'s two dead entries stay open for a
deliberate call, since honouring `abstract` and `private` at type level is the other
reasonable answer.

**`TS-P2-31`, found by asking whether the spec documented `get` and `set`.** It did
not, and the reason nobody had noticed is that the brace-bodied accessor silently
did the wrong thing:

```tosh
prop X { get => ($this.b * 2) }        ## 10
prop X { get { return $this.b * 2 } }  ## ShellBlock, no diagnostic
```

Two correct changes met and produced a defect. Accessor bodies went through
`ParseArrowStatementBlock`, whose `ConsumeFatArrow` consumes an arrow if present and
shrugs otherwise — reasonable on its own. `TS-P2-25` made `{` block-only
everywhere — also reasonable. Together, a braced accessor body fell through the
shrug into `ParseStatement` and became a first-class block *value*. Neither change
was wrong; the combination was, and nothing was watching the intersection.

Decided to support the brace form rather than diagnose it: a getter restricted to
one expression pushes anything conditional into a helper method, and `{ ... }` is
what a method body already looks like. `ParseAccessorBody` routes a brace to
`ParseRequiredBlock`, so both forms work and the choice of body syntax is not
observable — asserted as a property rather than as two expectations, because that
is the actual claim. Negative control: 4 of 6 cases fail against the unfixed parser,
and the 2 that pass are the arrow form and the unknown-accessor diagnostic, which
already worked.

Also worth recording: this is the second feature this session found working but
undocumented, after partial modules in `TS-P2-28`. Both were found by checking
whether the documentation covered something rather than by a failing test, and in
both cases the missing documentation was the reason a defect had survived — nobody
had written the example that would have failed.

### July 29, 2026 — Completion gains two thirds of the language (TS-P2-10)

Measured the four prose-carrying consumers against the registry. The REPL
completion engine was by far the worst and the most visible:

| consumer | words | missing |
|---|---|---|
| LSP feature table | 93 | 11 |
| help catalogue | 77 | 45 (plus 19 legitimate topic pages) |
| VS Code metadata | 68 | 35 |
| REPL completion | 36 | **67** |

Two thirds of the language could not be tab-completed. Typing `def` and pressing
tab did not offer `defer`; nor `const`, `interface`, `union`, `partial`, `sealed`,
`abstract`, or any of the nine aliases. The engine's map is now derived from the
registry, with the completion label computed from the word's category — operator,
constant, modifier, or keyword — so the ordering of that computation is the only
hand-written part left.

`Repl_completion_offers_every_word_in_the_language` asserts the result through the
real completion API rather than against the derived map, because asserting a
derivation against itself is a tautology.

**That guard immediately found a second defect, and the guard's own first two
attempts were wrong in an instructive way.** Probing each word from its single first
character reported `match` and `rune` missing; that looked like ranking, so the
probe moved to the word minus its last character — and both were *still* missing.
Only then did printing the actual suggestions show why: `matc` offers `Match`,
`MatchCasing`, `MatchCollection`, `MatchEvaluator`, `MatchType`, and the executable
`match_parens`, but not the keyword `match`. `rune` offers only the CLR type `Rune`.
Both words are present in the completion source, so a keyword is losing to a BCL
type that differs from it only in case. Filed as `TS-P2-32`.

The two are excluded from the guard with that reason stated in the test, rather than
the guard being weakened until it passed. The distinction matters and it is the same
one this programme keeps returning to: scoping a property after seeing it fail is
the step where a guard quietly becomes worthless, so the exclusion names a filed
item and the 101 other words stay checked.

Method note, third instance this session: two wrong diagnoses in a row from
reasoning about a mechanism instead of printing what it produced. "Ranking crowded
it out" was plausible, cheap to believe, and wrong twice. One `Assert.True(false)`
with the actual suggestions settled it immediately.

### July 30, 2026 — `import` is not a word, and the guard that should have known

Corrected by the programme owner mid-slice: `import` is not ToastScript. It resolves
to `/usr/bin/import`, ImageMagick's screenshot tool. It had been added to the
registry, given a probe, and — worse — **written into the specification's keyword
list and its PDF colouring list** on the strength of that entry.

The provenance is the useful part. `import` reached the registry from exactly one
place: `Binder.KeywordSuggestionPool`, a list whose purpose is mapping typos like
`rquire` onto `require`. It is not a claim about the language, and treating a
consumer as authoritative is what this whole item exists to stop. Neither the LSP
nor the VS Code metadata documents `import`; both were right.

And the probe passed. `import System.Text` parses cleanly **because any bareword line
parses** — it is a command invocation as far as the parser is concerned. That trap
was recorded two entries ago for `quote foo` and then not applied here. Three
attempts to prove `import` real by behaviour also failed to distinguish it, because
`new CultureInfo("en-US")` works with no import statement at all: the type resolver
scans the whole platform index, so `using` and `import` and nothing look identical
from the outside.

**The missing check was a necessary condition, and it is now a test.** A word the
parser and lexer never *name* cannot be syntax, whatever a probe does. `import`
appears zero times in either. The two checks are complementary and neither suffices
alone:

- source presence rejects `import`, which the probes accepted;
- probes accept `let`, `quote`, and `once`, which appear in parser source for
  unrelated reasons and which source presence alone would have wrongly admitted.

**That new guard was itself wrong on its first run**, and instructively so. It
flagged `obsolete`, `override`, `protected`, and `readonly` — the four aliases whose
22-branch `string.Equals` chain had just been replaced by a registry lookup. The
parser no longer names them *because the conversion succeeded*. So the check now
legitimizes an alias through its canonical spelling being real parser syntax, which
still bottoms out in the parser and cannot be satisfied by editing the registry
alone.

Removed from: the registry, the probe table, `Binder.KeywordSuggestionPool` — where
suggesting a non-word for a typo is its own small defect — and both specification
lists.

Also filed, from reading the LSP's entries closely rather than counting them
(`TS-P2-33`): its `let`, `pick`, and `get` hover texts all describe a leading-`for`
comprehension — `[for x in $items pick x * 2]` — which does not parse. Comprehensions
are body-first with `<|`. And `pick` is a builtin *command*, an alias for the `get`
projection command, so its entry is wrong in category as well as syntax. Editor
guidance that fails when followed is worse than none.

### July 30, 2026 — Two defects from one library, and a third implementation nobody knew about

Reported from a real library rather than found by inspection: modules nested to form
a namespace-like structure, imported as
`require ToastLib.Shell from "…" as ToastShell`, with helpers invoked as
`ToastShell.HasPipe { … }`. Twelve parse errors and one require failure, from two
unrelated causes. Worth recording because the reporter's first question was whether
they were using the language wrongly, and they were not.

**`TS-P2-34` — a qualified command took values and refused structures.**

```tosh
M.F 5            ## worked
M.F "text"       ## worked
M.F (1 + 2)      ## worked
M.F { … }        ## missing_pipeline_separator
M.F [1, 2]       ## missing_pipeline_separator
M.F {| a = 1 |}  ## missing_pipeline_separator
```

`LooksLikeStaticMemberAccessExpression` reads a dotted name in command position as a
CLR member access *unless* the next token starts a command argument, and
`NextTokenStartsCommandArgument` enumerated `Number`, `String`,
`InterpolatedString`, `Boolean`, `Null`, `UnitLiteral`, `Bareword` — and no
delimiter opener at all. So the block was left as a separate stage and the diagnostic
pointed at the brace, which is exactly why it read as a limitation of blocks rather
than a hole in a token list. The reported case used a `rune`, which was a red herring:
the callee kind was never involved.

This is the same family as `TS-P2-16`, which fixed `Geo.area 2` — the value case —
and left the delimited case behind. A fix that enumerates tokens is a fix that will
be incomplete again the next time a token kind is added, and the paired delimiters
from `TS-P2-25` are precisely the tokens that arrived after that list was written.

**`TS-P2-35` — dotted import paths, and a third implementation.**

The interesting part is not the feature, it is that the first fix appeared correct
and changed nothing. `ToshEngine` carries **two** `ImportRequiredArtifact` overloads
twelve thousand lines apart — one taking name/alias arrays, one iterating
`statement.Imports` — and the `require` statement path uses the second. The
implementation sat compiled, tested by nothing, and unreachable, while the
behaviour was identical to before.

That is `TS-P1-24`'s failure mode on an axis that item does not cover. `TS-P1-24` is
scoped to sync/async twins; this is two implementations of one operation with no
async involved, which the twin inventory cannot see because neither name ends in
`Async`. The inventory measures a *shape*, and duplication does not always take that
shape.

Both now route through one resolver, with the modifier parameterized so the two
callers keep their own visibility semantics.

**Method note.** The dotted import was verified against the reporter's own library
rather than only a fixture — `ToastLib.Filesystem.GetExtension("/tmp/x.tar.gz")`
returns `.gz` through the whole chain, with `Filesystem.tosh` requiring
`ToastLib.Shell` from another file. A synthetic fixture would have passed after the
first, unreachable fix, because a fixture written alongside a fix tends to exercise
the path the fix is on.

**Also cleaned up:** two 12 MB PostScript files in the repository root, which were
screen captures. They came from probing whether `import` was a keyword by running
`import System.Text` — on Linux that is `/usr/bin/import`, ImageMagick's screen
grabber, and it did exactly what it is for. The lesson is narrower than "be careful":
a probe for whether a word is a keyword must never be a bare command line, because
that is the one shape that can execute something.

**Flake note.** `ScopeAndChannelTests.Scope_awaits_spawned_jobs_and_returns_completions`
failed once in the full run and passed 3/3 in isolation and on the next full run.
That is the recurrence of the observation already recorded twice in this log, so it
stays an observation rather than becoming an item — but it has now been seen a third
time under parallel load, which is the point at which "intermittent" starts to mean
"unfixed".

### July 30, 2026 — CLR-compatible `await` (TS-P1-27)

Reported from real code, and the report was right to suspect the language rather
than the author:

```tosh
var p = new Ping()
var reply = async { $p.SendPingAsync("8.8.8.8", 1000) }
await $reply        ## AsyncStateMachineBox`1[…PingReply…]
```

**Two systems that never met.** `async` and `await` are *builtin commands* — they
appear zero times in the lexer, parser, and specification — operating over
`ShellFuture`. A CLR method returning `Task`/`Task<T>` was never awaited by
anything: the task flowed into the pipeline untouched, `await` refused it with
`await_requires_future`, and `.Result` was the only route to a value. The display
was a symptom rather than the defect: `Task` does not override `ToString`, so the
formatter fell back to the runtime type, which is the compiler's state machine box.

The directive was that ToastScript's async/await be the same as, or compatible with,
the CLR's. Two decisions followed.

**Explicit, C#-identical.** A task-returning call yields a task; you await it.
Auto-awaiting at the call site was considered and rejected for two reasons, the
second architectural: it removes the ability to hold a task and start work
concurrently, which is the reason tasks exist; and member invocation lives on *both*
surfaces of the dual-surface interfaces, so the change would have had to land twice —
the shape that hid three duplication bugs in the preceding days. A test asserts the
concurrency the decision preserves, by timing two overlapping delays rather than by
description.

**`await` flattens.** One `await` unwraps a future whose output is a task, so the
reported code works as written. C# needs `Task.Unwrap` here; a future-of-task has no
use, and leaving it unflattened is precisely the trap that produced the report.

Three details that were easy to get wrong:

- **The awaited type comes from the declared generic argument, not from a `Result`
  property.** A method declared to return plain `Task` compiles to
  `AsyncStateMachineBox<VoidTaskResult>`, whose inherited `Result` holds an internal
  struct meaning "no value". Trusting the property would make every void async method
  emit garbage. `await (File.WriteAllTextAsync(…))` emits nothing, and a test says so.
- **Cancellation** goes through `task.WaitAsync(token)` — the same call
  `ShellFuture.AwaitAsync` already used — so Ctrl-C during an await behaves alike on
  both paths. The first draft awaited bare and would have hung.
- **A faulted task raises its own message**, not `AggregateException`'s "One or more
  errors occurred", because the reported code wraps this in `catch (e)` and
  `$e.Message` is what a user reads. One layer is unwrapped.

The specification gains an Asynchrony section; it had none, which is why the
limitation was undiscoverable — the third feature this week found working-but-
undocumented, after partial modules and accessor blocks. `async`/`await` are
deliberately **not** added to `LanguageSurface`: they are commands, not word-shaped
syntax, the same distinction that keeps `pick` out.

**A process failure worth recording.** The first negative control reported 10 of 10
passing against "unfixed" code, which should have been impossible. `git stash push`
had silently failed — `ClrAwaitable.cs` was untracked, and stderr was redirected to
`/dev/null` — so nothing was stashed and the control ran against the fix. The
subsequent `git stash pop` then applied a *pre-existing* stash from the abandoned
`TS-P2-25` attempt, leaving merge-conflict markers in three parser files. Repaired by
restoring them from `HEAD`, which already carries the committed fixes; the old stash
entry was never consumed and remains in the list.

Two lessons, both narrow enough to act on: a negative control that *passes* is a
broken control, not a reassuring result — the whole point is that it must fail. And
suppressing stderr on a state-changing git command turns a loud failure into a silent
one. The second control, done by copying files aside instead of stashing, failed 6 of
10 as it should.

**One more of my own, caught by the full suite.** The test asserting that tasks stay
first-class timed two 200ms delays against a 380ms budget. It passed alone and failed
inside the parallel run — a wall-clock assertion under parallel test load measures the
machine, not the code. Replaced with the property it was standing in for: two
un-awaited tasks held as values simultaneously, checked in C# with
`ClrAwaitable.IsAwaitable`. Overlap follows from that deterministically. A flaky test
is worse than no test, and a timing budget is a flaky test with extra steps.

**Filed alongside**, both surfaced by this work and both sharpened by it:
`TS-P2-36`, generic static methods not inferring their type argument — which now
matters more, since explicit `await` invites exactly the `Task.WhenAll`/`FromResult`
helpers that are unreachable; and `TS-P2-37`, the `file` alias shadowing
`System.IO.File` in member-access position.

### July 30, 2026 — The suite's memory, measured rather than endured (TS-P2-38)

The full suite took a 128 GB machine down three times in one session, and the editor
with it. Twice I worked around it and once I made it worse by dropping the capped-run
discipline I had adopted earlier in the session and going back to bare `dotnet test`.
That was the wrong response to a repeating symptom.

**The multiplier, found.** There was no `xunit.runner.json`, so xUnit ran at default
parallelism — one thread per core, **32** on this machine — with every collection
constructing its own engines. Capped to 8 threads the suite peaks at **6,357 MB** and
passes clean, measured by sampling total `dotnet`/`testhost` RSS under a 10 GB cgroup
cap so a recurrence would kill the run rather than the machine.

**The cap is a mitigation, and 6.2 GB is still the finding.** That is a lot for 3,554
tests, so bounding concurrency limits the blast radius without explaining the growth.
Two open items would produce exactly this if they fire mid-run — `TS-P1-08`, nested
generator statements materializing, and `TS-P1-19`, an infinite generator in command
position — and an earlier memory bomb this programme fixed was precisely that shape,
peaking at 104 GB from a lambda swallowing its `| first N`. Filed as `TS-P2-38` with
the root cause open rather than declared solved by the cap.

**A second finding, honestly graded.** Two unrelated tests fail only under parallel
load and pass 3/3 in isolation: `ScopeAndChannelTests` (three sightings now) and
`GenericClassTests.Generic_class_user_interface_constraint_accepts_implementing_class`.
Two subsystems failing only when concurrent points at shared mutable static state.

The suspect is specific: `DotNetTypeResolver._negativeResultCache` is a process-wide
dictionary of names confirmed unresolvable, never cleared, invalidated only when the
loaded-assembly count grows — which a ToastScript-declared type does not do. If a name
is cached negative before it is declared, declaring it cannot clear the entry. That
would also be a *product* defect, not merely a test artifact: a long REPL session that
references a name before defining it would inherit the stale answer.

**It did not reproduce.** Resolving `IShape` through a failing annotation, then
declaring `interface IShape` and using it as a generic constraint, produced the correct
answer — so either that path does not populate the cache or constraint resolution does
not consult it. Recorded as a suspect with the failed reproduction attached, so the
next person does not spend the same hour on it, and filed as `TS-P2-39` rather than
asserted.

**Standing instruction until `TS-P2-38` has a root cause:** run the full suite under a
memory cap.

```
systemd-run --user --scope -p MemoryMax=10G -p MemorySwapMax=0 -- dotnet test Tosh.slnx --no-build
```

### July 30, 2026 — Correction: the test suite was not the memory culprit (TS-P2-38)

The previous entry said the multiplier had been found — no `xunit.runner.json`, so 32
threads on a 32-core machine — and reported a 6,357 MB peak after capping to 8. Both
halves of that are wrong, and tracing RSS over time rather than sampling a maximum is
what showed it.

**The shape, not the maximum, was the informative measurement.**

| time | RSS |
|---|---|
| 0s | 174 MB |
| 3s | 2,588 MB |
| 15–21s | ~3,860 MB (transient) |
| 24s → 154s | **2,744 MB, dead flat** |

Flat for 130 seconds while 3,500 tests run. The tests retain nothing; the memory is
committed in the first three seconds. That is a working set, not a leak — and the
earlier "6,357 MB" was a maximum caught during the transient with build processes
still alive, reported without the curve that would have made it interpretable.

**Parallelism is not a multiplier.** Re-running with the old 32-thread default under a
12 GB cap: peak **3,737 MB**, mean 2,760 MB — indistinguishable from 8 threads
(3,860 MB / 2,744 MB), with identical wall time, 2m33s against 2m34s. So the cap I
committed does nothing for memory. It is kept for a weaker and honestly-stated reason
— the two tests that have flaked did so only under parallel load — and the csproj
comment now says that instead of the disproven claim.

**So the suite cannot be what exhausted 128 GB.** A single shell invocation is 174 MB.
A single test in the host is 196 MB. The whole suite is 2.8 GB steady. Three orders of
magnitude short.

**What actually did it is unknown**, and the measurement that would have told me was
one I never took: my sampler matched only `dotnet` and `testhost`, so it could not see
VS Code, its Roslyn host — measured at 1.27 GB in a single process while I was working
— the KDE file indexer at 2.1 GB, or the MSBuild node-reuse workers that reached 21
processes at one point. I attributed the crashes to the suite because the suite was
what I was running, which is availability rather than evidence.

Two candidates remain, and both stay open: a rare unbounded path firing occasionally
(`TS-P1-08`, nested generators materializing; `TS-P1-19`, an infinite generator in
command position) would show as an occasional spike rather than in the steady state
these traces measure — which is exactly the signature the traces cannot rule out. Or
the consumer was never `dotnet` at all.

**The lesson is about the instrument.** A peak number without a curve invited the wrong
conclusion, and a process filter chosen for convenience made a whole class of causes
invisible. The corrected acceptance asks for total-system sampling on the next
occurrence, so the consumer is identified rather than assumed. A capped run remains the
standing instruction regardless — not because the suite is guilty, but because a cgroup
cap converts a machine-wide failure into one killed process.

### July 30, 2026 — One symptom, two defects, and a rule that is not one (TS-P1-28, TS-P2-40)

Reported from a real library: a `hermit class State` whose static functions worked while
its static properties "do not work, at all" and "do not even show up in autocomplete".
That is one sentence describing two unrelated defects and one correct language rule, and
separating them mattered more than fixing either.

**`TS-P1-28` — computed static properties were never evaluated.** Static properties were
only ever *initialized*. Both initialization sites read
`IsStatic && Initializer is not null && !IsComputed`, so a computed property never
entered `_staticValues`, and `TryGetStaticMember` fell through to a line whose comment
was already the whole bug:

```csharp
if (_staticValues.TryGetValue(memberName, out var stored)) { value = stored; return true; }
return true; // null default        ← every computed static property landed here
```

`static prop Y => 7` answered `null`. So did an accessor-block form. No diagnostic. The
fix mirrors the instance path — `CreateLocals` already accepts a null instance and omits
`$this`, so evaluating a static getter needed no new plumbing.

Stored static properties worked the whole time, which is exactly why the report read as
"static properties don't work" instead of "computed ones don't". Isolating that took four
probes and was the difference between a one-line fix and a search.

**`TS-P2-40` — completion dropped members differing only in case.** Nothing to do with
static-ness. The reporter's class held `func icmp()` beside `prop Icmp`, and
`OrderSuggestions` ran `DistinctBy(Label, OrdinalIgnoreCase)` *before* `OrderBy`, so one
of the pair vanished and *which* one depended on enumeration order. Fixing the two
suggestion dictionaries alone was not enough — it flipped the winner rather than keeping
both, which is how the second dedupe was found. Ordinal throughout now; an exact
duplicate spelling still collapses, which is what de-duplication is for.

**And a rule that is not a defect.** `static prop Icmp => icmp()` fails, and should:
members are reached through `ClassName.` or `$this.`, never bare. Bare `f()` fails from
an instance method too, so the rule is uniform. What is wrong is the diagnostic —
"Command 'f' is not a registered builtin … did you mean 'df', 'fg', or 'if'?" — which
names three unrelated shell commands while a member of the enclosing class sits one
qualifier away. Filed as `TS-P2-41` rather than fixed here, because the fix belongs with
the suggestion machinery and not with static properties.

Verified against the reporter's own file rather than a fixture:
`ToastLib.Network.State.Icmp` now returns `true`, running a real ICMP ping through two
nested partial modules and a `require`. Their `IsNetworkUp` still needs one change of its
own — it calls `State.Icmp()` with parentheses, which asks for a method and reports
"Static method 'Icmp' was not found on class 'State'"; a property is read without them.
Worth noting that this diagnostic is *good*: it says exactly what was looked for and
where.

### July 30, 2026 — Two diagnostics that pointed the wrong way (TS-P2-41, TS-P2-33)

Both of these are cases where the language was right and the thing *describing* the
language was wrong. Neither changes behaviour; both change what a user is told.

**`TS-P2-41` — a bare member reference now names its missing qualifier.** Writing
`static prop Icmp => icmp()` beside `static func icmp()` reported:

```
Command 'icmp' is not a registered builtin or function declared in this source.
  did you mean 'df', 'fg', or 'if'?
```

Three unrelated shell commands, while a member of the enclosing class sat one qualifier
away. The rule is not the defect — members are reached through the type name or `$this`,
and bare `f()` fails from an instance method too — so the fix is the message:

```
'icmp' is a member of 'State' and cannot be called as a bare name.
  write 'State.icmp' or '$this.icmp'
```

The binder now tracks the class bodies it is walking with the member names each declares,
and consults that **before** the Levenshtein path. That ordering is the part worth
keeping: the old path only reported when it happened to find a near-miss command, so
`icmp` was flagged only because `df`, `fg`, and `if` exist. A member named `zzqqxx`
produced **no diagnostic at all** — silence, not a wrong suggestion. Four probes confirmed
the new check covers methods, properties, static and instance position, and that a
genuine command typo still gets command suggestions.

**`TS-P2-33` — five LSP hover entries described syntax the language does not have.**
`let`, `pick`, and `get` all documented a leading-`for` comprehension,
`[for x in $items pick x * 2]`, which does not parse — comprehensions are body-first with
`<|`. `pick` is not a comprehension clause at all but a pipeline command. `when`'s example
omitted the parameter list its form requires.

Every replacement was verified by parsing it before being written, which caught that
`func f handles X when { … }` fails without `(event)`.

**The durable half is `HoverExampleParityTests`**: every backticked `Example:` in the
keyword table must parse. Its negative control names all three fictional comprehensions
against the uncorrected table, so it demonstrably catches the class of defect rather than
passing vacuously. Same shape as the specification keyword guard — a document that makes
claims about the language is a consumer, and a consumer can be checked.

Worth recording why this was missed for so long: counting entries made the LSP table look
like the *best*-covered consumer at 93 words. Reading three of them found wrong syntax in
all three. Coverage and correctness are different measurements, and only one of them was
ever taken.

**Flake note.** `GenericClassTests.Generic_class_user_interface_constraint_accepts_implementing_class`
failed in the verification run and passed 2/2 in isolation — the second sighting of that
specific test and the fourth overall across three subsystems, all under parallel load.
`TS-P2-39` holds the suspicion of shared static state, still unreproduced deterministically.
Distinguishing it from the `TS-P2-41` regression mattered: that one also looked like a flake
until it failed in isolation, which is the only signal that separated a real defect from
this noise.

### July 30, 2026 — Three P1 semantics repairs, and one that had to be narrowed twice

All three had the same shape: the language held two answers for one question.

**`TS-P1-10` — record equality ignored nothing, including order.**
`{| a = 1, b = 2 |} == {| b = 2, a = 1 |}` was `false`. `AreEqual` opens with an
element-wise enumerable comparison, and an `ExpandoObject` is an
`IEnumerable<KeyValuePair<…>>`, so two records were compared as *ordered sequences*.
Insertion order is not part of a record's identity. Fixed by comparing field by field
ahead of the sequence path, keys matched case-insensitively because member lookup is.

**The first fix changed nothing, and the second broke eleven tests.** Both errors are
worth recording because both were predictable.

The first landed only on `OperatorEvaluator.AreEqual`. `==` goes through
`ToshEngine.AreEqualAsync` — the parallel twin `TS-P1-24` exists for — so the defect
survived a change that looked complete and verified. The two now share one helper rather
than carrying two copies of the rule; the second implementation delegates.

The second used `ShellRecordUtilities.IsRecordLike`, which also matches
`ToshClassInstance`. That made *class instances* structurally equal and broke left-biased
equality dispatch, so a user's own `Equals` stopped being consulted — 11 failures, led by
`ClassEqualityCancellationTests.Equality_dispatch_is_left_biased`, a test written to
protect exactly that. Narrowed to string-keyed dictionaries, which is what an anonymous
record is. A declared class decides its own identity; that is the point of letting it
define `Equals`.

Arrays stay order-*sensitive*, asserted explicitly — both records and arrays are
`IEnumerable`, which is how records came to be compared as sequences in the first place.

**`TS-P1-11` — `_` both discarded and bound.** `var [a, _, c] = [1, 2, 3]` left `$_`
holding `2`. Worse than a leaked name: `_` is the current pipeline item, so a destructuring
inside a predicate silently changed what `_` meant downstream. Both destructuring forms now
skip it. The specification already promised this — "Skip elements with `_`" — so the
runtime was behind its own documentation, which is the third time this week.

**`TS-P1-16` — division by zero, and the item's framing was wrong.** Filed as depending on
the zero operand's type. It does not: the split is **folded versus evaluated**. Literal
operands are constant-folded with C# semantics and give `Infinity`; the same doubles in
variables reach `OperatorEvaluator`, whose floating lambda threw. `TS-P1-24`'s shape on the
arithmetic axis, and the third instance of it in this one slice.

One rule per family now: integral and decimal division by zero throw, matching C#;
floating division is IEEE — `±Infinity`, `NaN` for `0.0/0.0` and for floating modulo. The
folded and evaluated paths are asserted to agree rather than each asserted separately.

**Filed:** `TS-P1-29`. `ShellRecordUtilities.TryGetFields` throws on an object-keyed
dictionary — its `IDictionary` branch iterates as `DictionaryEntry`, which only the
non-generic enumerator yields. A `{% … %}` literal is object-keyed, so any caller handing
one over crashes with `unexpected_exception` instead of a diagnostic. Found because
`TS-P1-10`'s first draft called it on dicts; the fix was narrowed away from that rather
than growing to cover it.

Negative control: 13 of 24 new cases fail against the unfixed runtime.

### July 30, 2026 — TS-P1-08: half already fixed, half measurable

Opened this expecting the 104 GB memory bomb. It is gone, and has been for most of this
programme — but the item still said otherwise, which is its own kind of defect.

**The headline symptom no longer reproduces.** `recur (0, 1) func(a, b) => ($a + $b) |
take-while { _ < 100 }` yields `0, 1, 1, 2, 3, 5, 8, 13, 21, 34, 55, 89` and stops. The
sibling `iterate 1 func(x) => ($x * 2) | take-while { _ <= 64 }` yields `1, 2, 4, 8, 16, 32,
64` with no "must produce exactly one value per input item" error.
`LazySequenceTests.Recur_fibonacci_take_while` asserts exactly that and is green — it has
been green since the parser repair earlier in this programme, which found that
`ParseAnonymousFunctionArrowBody` was **swallowing `| take-while …` into the lambda body**
and leaving the generator unbounded. The 104 GB was the consequence of a parse error, not
of the iteration machinery.

That also closes a loop on `TS-P2-38`: the 104 GB event was a *test* in this suite, so
before that repair a full run could balloon exactly that far. The suite's measured 2.8 GB
today is the same suite after the fix. It does not explain the later crashes — the suite
was already innocent by then — but it explains the shape of the first one.

**The second clause was real, and is now measured.** Short-circuit consumers pulled one
item more than they needed:

| consumer | pulled before | pulled now | needed |
|---|---|---|---|
| `first 1` | 2 | **1** | 1 |
| `first 2` | 2 | 2 | 2 |
| `any { _ > 0 }` | 2 | **1** | 1 |
| `take-while { _ < 3 }` | 3 | 3 | 3 — inherent |

`first 2` pulling exactly two ruled out a simple off-by-one and pointed away from
`FirstCommand`, which is correct: it breaks the moment it has emitted its last item. The
surplus was in `ShellIterationUtilities.ReplaySingleInputCollectionAsync`, which pulls a
second item to decide whether the input is a *lone* collection to expand element-wise.

That lookahead only earns its cost when the first item is expandable. Expanding a scalar
yields the scalar, so for a generator of numbers the second pull answered a question with
no consequence — while costing a unit of the producer's work and surfacing an error if the
surplus item threw. It is now taken only when the first item is actually expandable, and
every expansion behaviour is pinned: a lone array still expands, two arrays stay two items,
records and strings stay atoms.

`take-while` is included in the table at 3 deliberately. It *must* evaluate the item that
fails its predicate to know to stop, so three is correct rather than surplus, and recording
that stops a later reader "optimising" it.

**Method note.** The first attempt to measure pull counts used `echo` inside the generator
and measured nothing — those values *are* the pipeline, so `first 1` consumed them and the
evidence disappeared into the thing being tested. `writeline` writes directly, which is what
makes the count visible. Negative control: 2 of 12 cases fail against the unfixed runtime,
and the 10 that pass are the expansion semantics, which is the right split — the fix was
meant to change pull counts and nothing else.

### July 30, 2026 — Capturing external output, and a fix proposed for a defect that did not exist

Reported from an interactive session: `var x = git rev-parse --show-toplevel` printed the
path and left `$x` as `null`. Five forms behaved that way; `| collect` did not.

**The cause was one question answered by the wrong flag.**
`ExternalProcessCommand.DetermineSpawnMode` asked "is my output being consumed?" and read
`context.IsPipelined`, which is true only when a **downstream stage** exists. So
`git … | collect` captured, while assignment, `( … )`, `$( … )`, `return`, and a `for`
source all consumed the value without being pipelined and took
`SpawnMode.TerminalPassthrough`, where stdout is inherited and nothing is captured.

`CommandContext` now carries `OutputIsCaptured` alongside `IsPipelined` — deliberately
separate, because the two answer different questions and other code reads `IsPipelined` to
decide about *input*. It threads from `EvaluatePipelineAsync` through
`ExecuteCommandSyntaxAsync`, the same path `isPipelined` already took, and is set at the
13 sites that collect a pipeline's values.

**The mode was already there.** A capturing context routes to the existing `SpawnMode.Hybrid`
— `RedirectStandardOutput = true`, stdin and stderr inherited — so `var x = $(fzf)` still
draws its UI and `curl`'s progress meter still appears while the value is captured. The
code's own NOTE described this hybrid as the intended destination; it existed but was
reachable only through an opt-in allowlist.

**Why 3,602 tests missed it, and what that demanded of the fix.** A test process has no TTY,
so `hasTerminal` is false and the same code captures correctly. Every test exercised the
branch that worked. `TtyCaptureTests` therefore runs the built CLI under a real pty via
`script(1)` — without that it would re-verify the working branch and prove nothing. Negative
control: 5 of 8 fail against the unfixed runtime, and the 3 that pass are `| collect`,
top-level display, and the interpolation characterization.

**Part two of the approved plan was withdrawn after measuring it.** The plan proposed making
`ShellTextLine` string-like, on the evidence that `TypeConversion` has no entry for it. That
reading was correct and worthless: the conversion happens elsewhere, and on a single
`ShellTextLine` all of `.Trim()`, `.Length`, `.ToUpper()`, `.Split()`, `== "…"`,
`var y: string = $x` and `cast string $x` already work. I proposed a fix for a defect that
did not exist, from a mechanism I had not run — the same error this log keeps recording, and
the fourth instance of it in two days.

What the reporter actually hit was two things: capture failing (above), so `.Trim()` ran on
an empty value; and `collect` yielding an **array**, so `$x` was an array of one
`ShellTextLine` and `.Trim()` failed with "No overload matched instance method 'Trim' on
'System.Object[]'". A subexpression `( … | collect)` unwraps a single value, which is why
that spelling behaved differently and made the type look inconsistent.

One real finding survives from the detour, filed as `TS-P1-33`: `members` on a
`ShellTextLine` lists only `Text` while the whole string surface is callable. Introspection
contradicting behaviour is what convinced both of us the type was broken.

**Left open, `TS-P1-32`:** an interpolation hole still does not capture. It re-parses its text
and runs it as a whole statement through `EvaluateAsync`, reaching the pipeline via
`EvaluateStatementAsync` — the engine's hottest dispatch — rather than any marked consuming
site. Four defaultable signatures would carry the flag there; it wants its own slice rather
than riding along at the end of a long one. Pinned as a characterization so the gap is
visible and flipping it later is a deliberate edit.

### July 30, 2026 — A qualified name, and the defect it was hiding (TS-P1-34, TS-P1-35)

Reported as "I can not access `type`s defined inside of modules that I am requiring":

```
❯ var x: ToastLib.Math.IntPercent = 60
✖ error tosh.runtime.annotation_unknown_type
  'x' uses unknown type annotation 'ToastLib.Math.IntPercent'.
```

**The report understated it.** Probing before editing anything established that the bare
`IntPercent` worked *and enforced* — `150` clamped to `100` — while both `ToastLib.Math.IntPercent`
and an `as`-aliased `M.IntPercent` failed. So this was not a missing type, it was a name that
could not be spelled. One more probe widened it past the report: a qualified **class**
annotation failed identically, `var v: Outer.Inner.Widget = (new Outer.Inner.Widget())`. That
one probe decided where the fix went. Had it not been run, the natural move was to teach
`TryGetRefinementType` about dotted names and declare the reported symptom fixed, leaving
classes and records broken in exactly the same way for the next report to find.

The cause was uniform and unremarkable: **every** lookup on the annotation path took a flat
name. So the fix is one walk, `TryResolveQualifiedModuleMember`, hung beneath the flat lookups
in both `TryGetNamedType` and `TryGetRefinementType` — unqualified names never reach it, and
dotted ones resolve through `ExportTable.Modules` to the leaf. Fixing the *lookups* rather than
`IsKnownAnnotatedType` is the part that matters. Making the check permissive would have made
the annotation accepted; making the lookup work makes it resolve to the actual definition,
which is why `Outer.Inner.SmallInt = 99` coerces to `10` and `Outer.Inner.Port = 0` still fails.
An annotation that is known but toothless would have looked like a fix and been worse than the
error.

**Then the fix exposed a second defect — the reporter's own case still failed.**

```
✖ error Member 'Clamp' was not found on module 'Math'.
  4 │ export type IntPercent = int where (…) coerce Math.Clamp(_, 0, 100)
```

Their coercion calls `Math.Clamp` from inside `module Math`, so the module name shadows
`System.Math`. This had never worked; it had only never been *reached*. While the qualified
annotation was broken, the refinement was only usable through the name that leaked unqualified
into the requiring scope — and there `Math` is not a bound module, so it fell through to the CLR
type. Resolving the qualified name evaluates the coercion with the module in scope, and the
shadow became visible. Worth stating plainly: for one probe cycle the fix appeared to have
broken the reporter's file, when it had exposed a second defect standing behind the first.

Filed as `TS-P1-35` and fixed rather than deferred, because deferring would have handed the
reporter a different error for the same line. `ToshModuleObject.InvokeInstanceMethod` now falls
back to the shadowed CLR type **on a miss only**, so a module's own export still wins — asserted
first, since that is what the fallback must not break — and a miss on a module shadowing nothing
still errors, so mistakes are not swallowed into a silent null. This is `TS-P2-37`'s collision
(`file` versus `System.IO.File`) answered for the general case; that item should now be re-checked
against this rule rather than fixed separately.

**Validation.** Fourteen new assertions in `QualifiedModuleTypeTests`, including the reported
route end to end through a required file with `partial module`, since partial merging declares
through a different path (`TS-P2-28`) than a plain module. The negative control — restored by
**file copy**, after a `git stash` silently no-opped earlier in this programme — failed 8 and
passed 6 against the unfixed code, and the split is the one it should be: every assertion of new
behaviour failed, and the six that passed are exactly the preservation assertions (four
unknown-name rejections, two shadowing controls). A control where those six had also failed would
have meant the tests were asserting the walk's mechanics rather than its behaviour. Full suite
3,624 passing, 1 skipped.

**Noted, not fixed:** exported names leak inconsistently. A `partial module`'s exported refinement
types resolve unqualified in the requiring scope, while a plain `module`'s do not — `var d: SmallInt`
fails where the reporter's `var x: IntPercent` succeeds. Both are now reachable qualified, which is
the spelling that should always have worked, so this is no longer blocking anyone; but two module
forms with different name-visibility rules is a semantic decision that has never been made
deliberately. It wants its own item and a decision, not a quiet patch.
