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
withdrawn as misfiled rather than fixed. Ten P2 items:
`TS-P2-04`–`TS-P2-08` and `TS-P2-12`–`TS-P2-16`. All three July 26
semantic decisions (comparison, chained comparison, `$this` in method
defaults) are implemented.

**In progress.**

- `TS-P1-24`, duplicated sync/async semantics. Five dead parallel copies
  removed, then the refinement cluster — the largest — converged onto a
  single asynchronous implementation. Thirteen live pairs remain, led by
  `ThrowDetailedSingleConstructorMismatch` and `TryGetInstanceMember`.
  The `AnnotatedConversionParityTests` drift guard now compares
  diagnostics as well as values.
- `TS-P2-11`, `TS-P2-23`, and `TS-P2-24`, the parser architecture. The
  mode-tracking lexer, declaration table, `ParseContext`, and
  `LiteParser` structural pass are built and validated. Nothing consumes
  the structural pass yet, which is deliberate: agreement with the
  current parser is established before the heuristics are retired.

**Blocked on a decision.** `TS-P2-25` gates the remainder of parser step
2. `{` opens a block, record, dict, set, or predicate, and a
brace-enclosed statement boundary cannot be resolved structurally until
that is settled. It is a grammar change, and the July 26 breaking-change
decision permits one.

**Remaining.**

- P1: `TS-P1-07` (partial — the defer case is closed, other nested
  control-flow shapes are not), `TS-P1-08`–`TS-P1-13`, `TS-P1-16`,
  `TS-P1-18`, `TS-P1-19`, and `TS-P1-25`.
- P2: `TS-P2-01`–`TS-P2-03`, `TS-P2-09`, `TS-P2-10`, and
  `TS-P2-17`–`TS-P2-22`.

**Sequencing note.** The July 26 duplicated-semantics audit concluded
that converging `TS-P1-24` is worth more than the next individual P1
repair, because every remaining semantic item is at risk of the same
half-landing. The slices that followed went to the parser track instead.
With `TS-P2-25` now blocking that track pending a decision, `TS-P1-24`
is the substantial work that does not require one.

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
| `TS-P1-08` | Planned | Nested generator statements materialize output, while short-circuit consumers peek a second upstream item. | Nested `yield` streams promptly; `first`/`any` do not evaluate an unnecessary next item; infinite-source tests complete. |
| `TS-P1-09` | Planned | Class hierarchy lookup loses generic bindings, inherited overloads, `vital` validation, and private visibility rules. | Recursive hierarchy test matrix covers generic intermediaries, overload sets, required members, private/protected access, and partial statics. |
| `TS-P1-10` | Planned | Anonymous-record equality depends on dictionary insertion order. | Records with the same names and canonically equal values compare equal regardless of insertion order. |
| `TS-P1-11` | Planned | `_` in destructuring is bound and overwritten instead of discarding the matched value. | Every `_` target skips without creating or modifying a binding; nested/rest patterns are covered. |
| `TS-P1-12` | Planned | `const` currently accepts arbitrary runtime pipelines and behaves as a readonly binding rather than a constant. | Constant-expression rules are specified and enforced; `let` covers runtime immutability before compatibility behavior is removed. |
| `TS-P1-13` | Planned | Compiled ordinary member/index assignments evaluate target components before the RHS, while the interpreter preserves RHS-first order; only `??=` intentionally uses target-first order. | Side-effecting target, index, and RHS probes produce the same ordering in interpreted and compiled modes for every assignment operator. |
| `TS-P1-14` | Complete — 2026-07-26 | Cross-type equality and ordering are incoherent: `==` coerces numerically (`1 == "1"` is true) and falls back to case-insensitive `ToString` comparison for mixed types while string-to-string stays case-sensitive; ordered comparison converts right-to-left only, so `"abc" < 5` silently string-compares to `false` while `5 > "abc"` throws; booleans participate in ordering, so `1 < 2 < 3` silently evaluates to `false`. | One documented equality/ordering conversion matrix implemented once and used by every surface (extends the `TS-P1-01`/`TS-P1-03` corpus); conversion is symmetric or produces a structured diagnostic; no silent lexicographic fallback for mixed numeric comparisons; the chained-comparison shape is either supported or diagnosed; interpreted and compiled modes agree. |
| `TS-P1-15` | Complete — 2026-07-26 | Enum values are not orderable or number-comparable: `E.A < E.C` throws (`ToshEnumValue` cannot be compared) and `E.B == 1` is false, despite the specification's numeric-backed enum examples. | Enum values compare and order canonically against members of the same enum and against their underlying numeric values; diagnostics for genuinely incompatible enum comparisons name the shell-level enum type; the specification's `Permissions : int` examples pass as conformance cases. |
| `TS-P1-16` | Planned | Float division-by-zero depends on the zero operand's type: `10.0 / 0` throws "Division by zero" while `10.0 / 0.0` returns `Infinity`, exposing a second arithmetic path inside the interpreter. | One documented rule per numeric family (integral, float, decimal) for division and modulo by zero; the zero operand's declared type does not change the outcome; interpreted and compiled modes agree. |
| `TS-P1-17` | Withdrawn — 2026-07-26 | Filed as "the empty brace literal `{}` evaluates to an internal type-definition object instead of an empty record". Re-examination showed `{}` is already a correct empty record: in expression position `var r = {}` produces an `ExpandoObject`, the same CLR type as `{ a = 1 }`, and spread and member assignment both work. The original observation was `type-of` rendering, now fixed as `TS-P1-23`, plus `{}` in *command-argument* position parsing as a block (`ShellBlock`) rather than a record, which is the brace-overload ambiguity tracked separately. | n/a — not a defect as filed. |
| `TS-P1-18` | Planned | A class that declares both a primary constructor and an explicit constructor of the same arity registers duplicate overloads, so every instantiation fails with a self-ambiguity error (`Multiple constructor overloads matched class 'C' with 1 argument(s): C(x); C(x)`). | A declaration-time rule is documented and enforced (the explicit constructor either replaces the synthesized primary overload or produces a structured declaration diagnostic); instantiation never reports a class as ambiguous with itself; interpreted and compiled construction agree. |
| `TS-P1-19` | Planned | An infinite generator invoked in command position (`gen \| first 3`) silently produces no output and exits cleanly, while the call form (`gen() \| first 3`) hangs; both diverge from the accepted stream-producer decision (companion to `TS-P1-08`). | Command-position and call-position generator invocations produce identical streams; infinite generators stream promptly and terminate under `first`/`any`; the silent-empty shape is covered by a regression test. |
| `TS-P1-20` | Complete — 2026-07-26 | A compiled multi-stage pipeline in value context never applied the interpreter's single-value subexpression rule: `var n = ([1, 2, 3] \| count)` produced a one-element `List<object>` rather than `3`, and a pipeline yielding several values returned a list silently where the interpreter raises `tosh.runtime.subexpression_requires_single_value`. Single-stage value pipelines already collapsed through `InvokeValue`, so the two shapes disagreed inside the host itself. Found while validating `TS-P1-05`. | Value-context pipelines collapse identically in both modes (none → `null`, one → the item, several → the shared diagnostic); iteration sources still receive every item; literal, variable, and command seeds behave alike; conformance rows and differential regressions cover each shape. |
| `TS-P1-21` | Complete — 2026-07-26 | A parameter default on a class method or constructor cannot reference `$this`: `func m(a, b = $this.V)` fails with `tosh.runtime.unknown_variable` because defaults are evaluated during callable binding, before the `this`/`super` bindings are seeded. `TS-P1-05` made this an explicit failure rather than the previous silent null. Needs a recorded decision, not just a fix: an instance method default may clearly see `$this`, but a **constructor** default would observe a partially-constructed instance whose properties have not been initialized yet (base-to-leaf construction binds arguments first), so allowing it exposes uninitialized state while rejecting it makes methods and constructors inconsistent. | A decision-log entry states whether `$this` is in scope for method defaults, constructor defaults, or both; the callable default binder seeds the agreed bindings; the rejected case keeps a targeted diagnostic naming `$this` rather than the generic unknown-variable help; interpreted and compiled modes agree; the specification's default-value semantics section records the rule. |
| `TS-P1-22` | Complete — 2026-07-26 | `a < b < c` parses left-associatively, so `1 < 2 < 3` compares `true < 3` and silently answers `false`. The accepted decision is real chaining. | `a < b < c` evaluates as `(a < b) and (b < c)` with each operand evaluated once and short-circuit preserved, in interpreted and compiled modes; the parser, binder traversal, type checker, and emitter all handle the new shape; precedence and formatting round-trip. |
| `TS-P1-23` | Complete — 2026-07-26; structural display paths 2026-07-27 | `type-of` yields a shell type descriptor for shell-typed values, but the descriptor rendered as its own CLR class name, so `type-of [1, 2]` reported `Tosh.Runtime.BuiltInShellTypes+BuiltInShellTypeDefinition` instead of the type being asked about. | Displaying a built-in shell type descriptor shows the shell type name; `type-of` reports usable names for lists, records, and other shell-typed values; CLR values are unaffected. |
| `TS-P1-24` | In progress — refinement cluster converged 2026-07-27 | The interpreter carries sync/async twin methods that are *parallel implementations* rather than delegations, so a semantic fix can land on one surface and silently miss the other. This has happened twice: `OperatorEvaluator.AreEqual` versus `ToshEngine.AreEqualAsync` (`TS-P1-14`/`TS-P1-15`) and `ToshHost.DrainValue` versus `InvokeValue` (`TS-P1-20`). A corrected audit on 2026-07-26 counted 23 truly parallel pairs against 6 that delegate. The refinement cluster, the largest, is now converged. Remaining largest duplications: `ThrowDetailedSingleConstructorMismatch` (55 lines), `TryGetInstanceMember` (51), `ApplyPendingParameterDefaults` (50), `InvokeQualifiedMethod` (47), `ConvertPropertyValue` (44), `TrySetInstanceMember` (43), `SelectBestCallableMatches` (41), `GetInstanceMembers` (38), `ConvertConstructorParameterValue` (35). | Each pair either delegates to one implementation or is removed; a test or analyzer fails when a new parallel sync/async pair is introduced; behaviour is unchanged, evidenced by the existing suite plus the annotated-conversion drift guard. |
| `TS-P1-25` | In progress — audit built 2026-07-27 | (Filed 2026-07-26 under a duplicate `TS-P1-20`; renumbered 2026-07-27.) The pure compiler profile can report a Tier-1-clean artifact while emitted IL still unconditionally calls `ToshHost.Initialize`/`RegisterCompiledAssembly` from `Main` and `ToshHost.EnterExecutionFrame` from functions, methods, lambdas, and blocks. | A pure artifact contains no metadata references or calls to `Tosh.Compiler.Runtime`, `ToshHost`, or `ToshEngine`; bootstrap is omitted or conditional; recursion guarding uses a stable `Tosh.Runtime` primitive; and a post-emit IL dependency audit fails independently of `RequireTier` diagnostics. Verified 2026-07-27: the emitted IL references exactly `System.Console`, `System.Private.CoreLib`, `Tosh.Compiler.Runtime`, and `Tosh.Runtime`, so only the three unconditional `ToshHost` members stand between the artifact and purity; the over-declared `deps.json` is a separate packaging concern. |

## P2 — Parser, Binder, Diagnostics, and Surface Generation

| ID | Status | Problem | Required acceptance |
|---|---|---|---|
| `TS-P2-01` | Planned | Lowercase user calls such as `f()` do not compose normally inside operator expressions. | Calls are ordinary postfix expressions independent of capitalization or surrounding operators. |
| `TS-P2-02` | Planned | Unary variable negation is lexically/runtime broken and binds on the wrong side of exponentiation. | `-$x`, `- $x`, folded literals, and compiled forms agree; `-2 ** 2` follows the documented precedence. |
| `TS-P2-03` | Planned | Ranges bind at primary precedence instead of below additive expressions. | Precedence corpus covers both range bounds and explicit-parenthesis controls. |
| `TS-P2-04` | Complete — 2026-07-26 | The documented compact `$value?.Member` syntax silently becomes a bareword. | Fused safe navigation tokenizes correctly or produces a targeted diagnostic; spacing does not change meaning. |
| `TS-P2-05` | Complete — 2026-07-26 | Numeric separator validation permits forms such as `1__2`, `_1` is misclassified, and large binary/octal values leak overflow exceptions. | Lexer distinguishes identifiers from numerics, validates separator placement, and recovers with structured overflow diagnostics. |
| `TS-P2-06` | Complete — 2026-07-26 | Newline statement detection omits legal expression starts; unterminated block comments are silently accepted. | All expression-start tokens share one source of truth; unterminated comments report a span-aware diagnostic. |
| `TS-P2-07` | Complete — 2026-07-26 | Binder and variable-binder visitors miss pipe-forward, substitution, and other nested forms. | One exhaustive syntax walker visits every child; a reflection/exhaustiveness test fails when a new syntax node lacks traversal. |
| `TS-P2-08` | Complete — 2026-07-26 | The raw function-name pre-scan can reinterpret later commands after unrelated text containing `func`. | Declarations are discovered structurally without non-local token poisoning. |
| `TS-P2-09` | Planned | LSP maps warnings to errors and MCP `explain_error` stops runtime analysis when only warnings exist. | Severity is preserved end-to-end; warnings do not suppress independent runtime explanations. |
| `TS-P2-10` | Planned | Operators, keywords, document symbols, help, MCP, LSP, and spec tables are hand-maintained and have drifted. | A machine-readable language-surface registry generates or validates every consumer. |
| `TS-P2-11` | In progress — characterization corpus 2026-07-26 | Parser expression layers rely on scattered lookahead and special cases. | Adopt an explicit precedence/postfix architecture, preferably Pratt-style, without changing accepted syntax unintentionally. |
| `TS-P2-12` | Complete — 2026-07-25 | String escape semantics violate the specification's quoting table: single-quoted strings process escape sequences (`'a\nb'` has length 3) despite being documented as raw, and unknown escapes in double-quoted strings silently drop the backslash (`"\d+"` becomes `d+`), so `("a1" =~ "\d")` is false and the specification's own `=~ "\.cs$"` example matches incorrectly. No single-line quote form preserves a backslash literally. | Single-quoted strings are raw (no escape processing); unknown double-quote escapes are preserved verbatim or produce a targeted diagnostic; every quote form has a conformance case; a migration note records the contract change. |
| `TS-P2-13` | Complete — 2026-07-25 | Expression-position barewords silently coerce to `DateTimeOffset` through the permissive `DateTimeOffset.TryParse` fallback: `1.2.3` and the malformed range `1.5..3` both evaluate to dates in 2003. Relatedly, float-headed and negative-headed ranges (`1.5..3`, `-1..5`) never lex as ranges at all (companion to `TS-P2-03`). | Intrinsic temporal literals parse only through the exact documented format list; dotted-number typos yield barewords or diagnostics, never dates; float and negative range bounds lex correctly or produce a targeted diagnostic. |
| `TS-P2-14` | Complete — 2026-07-25 | Storage-size suffix forms are only recognized as binary-operator operands: `var s = 10kb` fails as unknown command `10kb`, `10kb + 10kb` concatenates to the string `"10kb10kb"`, and `(10kb > 5kb)` silently returns `false` via lexicographic string comparison (the specification says `true`). | Suffix forms lex as typed literals in every expression position (mirroring backtick unit literals), or the suffix syntax is formally deprecated in favor of unit literals with a migration note; the specification's `var small = 10kb` and `(10kb > 5kb)` examples pass as conformance cases; no silent string fallback remains. |
| `TS-P2-15` | Complete — 2026-07-26 | Named arguments are whitespace-sensitive with silent misbehavior: `f(host = "x")` binds the parameter while `f(host="x")` lexes as one bareword and is silently passed positionally as the literal text `host="x"` (companion to `TS-P1-06`). | `name=value` and `name = value` parse identically inside call argument lists, or the fused form produces a targeted diagnostic; a bareword containing `=` is never silently forwarded as a positional argument. |
| `TS-P2-16` | Complete — 2026-07-26 | Module-qualified command dispatch is casing-sensitive despite the documented any-casing promise: `geo.area 2` dispatches, while `Geo.area 2` is a parse error because the capitalized form routes into static CLR member parsing (companion to `TS-P2-01`). | Module-qualified dispatch is independent of module-name casing; the corpus covers capitalized, kebab, underscore, and nested module names in both command and expression position. |
| `TS-P2-17` | Planned | Dictionary-comprehension keys reject operator expressions: `{ $x % 2 => $x <\| for x in 1..4 }` fails with a missing-list-separator parse error. | Key expressions accept the same operator grammar as value expressions, or the diagnostic explicitly says to parenthesize the key; conformance cases cover operator keys, parenthesized keys, and the specification's examples. |
| `TS-P2-18` | Planned | Member diagnostics leak internal implementation types and misdescribe visibility: denied `shy` access reports "Member 'Secret' was not found on type 'Tosh.Language.ToshClassInstance'", and enum comparison failures name `ToshEnumValue`. | Diagnostics name the shell-level type (`S`, the enum's name) and the true cause (private access versus absence); no `Tosh.Language.*` implementation type name appears in user-facing diagnostics. |
| `TS-P2-19` | Planned | An unparenthesized postfix conditional (`return "big" if $x > 5`) fails with a generic "insert a newline or ';'" error instead of the documented `tosh.parser.expected_postfix_condition` guidance. | Unparenthesized operator conditions after a postfix `if`/`unless` produce a targeted diagnostic that suggests parenthesizing the condition. |
| `TS-P2-20` | Planned | `nameof($foo.Bar)` returns `"foo"` — the parser strips member access and reports the root identifier. | `nameof` on a member chain returns the final segment (matching C#) or produces a targeted diagnostic; the specification documents the chosen behavior. |
| `TS-P2-21` | Planned | A `new` expression cannot take named arguments at all: `new D(1, b = 7)` and `new R("w", Qty = 5)` both fail while parsing with `tosh.parser.assignment_in_predicate`, so the runtime binder is never reached. Function and method calls accept the same syntax. This bounds `TS-P1-06`: constructor named-argument validation is unreachable until the parser accepts the form. | `new Type(name = value)` parses as a named argument for classes, records, and structs; the runtime binder's unknown/duplicate diagnostics apply; a genuine assignment mistake keeps a targeted diagnostic rather than the predicate-assignment message. |
| `TS-P2-22` | Planned | The type checker does not walk class-member annotations, so static checking is materially weaker inside class bodies. `var x: int = "42"` and `func f(x: int)` both report `tosh.type.mismatch`, while the equivalent `prop X: int = "42"`, constructor parameter, method parameter, and property assignment report nothing. Runtime behaviour is consistent (all convert), so this is a static-coverage hole rather than a semantic divergence. | Class property, constructor-parameter, method-parameter, and property-assignment annotations are checked with the same rule and severity as `var` and `func` annotations; a corpus covers matching and mismatching cases in both positions. |
| `TS-P2-23` | In progress — declaration table 2026-07-26 | Parse-time identity decisions rest on *spelling* rather than on facts the runtime already holds. Two casing tests remain (`char.IsUpper` in `LooksLikeQualifiedDotNetAccess` and `LooksLikePotentialClrTypeName`) deciding whether a dotted name is a CLR type, and 160 hardcoded `Current.Text == "…"` comparisons decide keyword and construct identity. `TS-P2-16` narrowed one such rule but did not remove the guess. The parser cannot do better today because `ToshParser.Parse` receives only source text, while the command, module, and type registries arrive later at `Lowerer.Lower`. | Identity is resolved against a real table rather than inferred from capitalization: either the parser is given the registries, or the decision is deferred to a later phase that has them. Keyword and construct recognition is driven by the generated language-surface registry (`TS-P2-10`) rather than by scattered literal comparisons. A capitalized module and a lowercase CLR type both resolve correctly. |
| `TS-P2-24` | In progress — pass built and validated 2026-07-26 | Step 2 of the parser roadmap. Structural questions — where a statement ends, where a pipeline stage divides — are answered by heuristics scattered through the recursive-descent parser, each re-deriving the answer with local lookahead. `LiteParser` decides them once over the whole token stream, with bracket depth tracked so a separator inside a nested construct does not split the enclosing statement. | The parser consumes the lite structure instead of re-deriving it; the `LooksLike*`/`HasTopLevel*` helpers that only answered structural questions are removed; structure agrees with today's parser across the corpus, evidenced by differential tests. |
| `TS-P2-25` | Proposed — options prepared 2026-07-27, needs a decision | `{` is structurally ambiguous and this now blocks the structural pass. A line break inside braces separates statements in a block body but must not split a multi-line record literal, and `{ a = 1 \n b = 2 }` is token-for-token indistinguishable from a two-statement block. `LiteParser.CandidateBoundaries` therefore reports brace-enclosed positions as *candidates* with their depth, leaving a semantic consumer to decide. Every remaining structural decision inherits that dependency. The brace form is overloaded five ways: block, record, dict, set (`{: :}`), and predicate. | A `{` opens exactly one construct decidable from the token stream, or the ambiguity is confined to a form the structural pass can recognise. Landing it includes the specification, examples, cheatsheets, and test corpus in the same change. Breaking syntax is acceptable per the July 26 decision. Options, costs, and a recommendation are in `docs/BRACE_DISAMBIGUATION_RFC.md`. |

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
