# Semantic decisions

Behaviour TōSh commits to, and the reasoning behind each choice. Extracted from the
stabilization plan on 2026-08-16; the decisions themselves are unchanged.

These are policy rather than work. When one of them changes, the implementation,
conformance tests, specification, help, LSP and MCP metadata change together — that
rule is why they were written down in the first place.

---

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


---

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

