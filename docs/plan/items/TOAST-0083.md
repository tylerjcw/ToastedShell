---
id: TOAST-0083
title: "Generic unions can spell `Option` and `Result`, but the core library does not provide their contract"
status: partial
area: toast
priority: 2
opened: 2026-08-28
---

## Problem

`TOAST-0052` made this legal user code:

```tosh
union Result<T, E> { Ok(T) Error(E) }
union Option<T> { Some(T) None }
```

That proves the type machinery, but every module must still invent names, fields, combinators,
null conversions and error policy. The self-hosting RFC already promises `Option<T>` for domain
optionality and `Result<T,E>` for expected failure, and says compiler diagnostics return through
`Result`; no portable core implementation currently fulfils that contract.

Exceptions should remain the shell-friendly path for unexpected failures and pipelines. The new
types are for absence or failure that the API expects its caller to handle.

## Candidate surface

```tosh
func parse-port(text: string) -> Result<int, ParseError> {
    return Result.Error(new ParseError($text)) unless ($text is Numeric)
    return Result.Ok(cast int $text)
}

match (parse-port "8080") {
    Ok(port)  => connect $port
    Error(e)  => echo $e.Message
}

var loaded = attempt { read-file $path } # Result<string, Error>
```

`attempt` converts an ordinary language failure at a deliberate boundary. It must not catch
`return`, `break`, `continue`, cancellation or process-exit control flow. Foreign exceptions are
wrapped using the existing portable `Error` policy rather than losing their identity.

## Acceptance

- [x] Portable core exports canonical `Option<T>` (`Some`, `None`) and `Result<T,E>` (`Ok`, **`Err`**)
- [x] The prelude/import and user-shadowing rules are stated, so existing user unions do not become
      ambiguous silently
- [x] Both types provide the agreed map, bind/and-then, inspect, unwrap-or and query helpers without
      special cases in the evaluator
- [x] `attempt` converts a language/foreign failure to `Result::Err` and preserves its structured data
- [x] `attempt` does not consume cancellation or language control-flow signals
- [x] `null`, `T?`, `Option<T>` and foreign-null conversions are explicit and match the RFC
- [x] A `Result` or `Option` remains one pipeline value; its payload is not implicitly flattened
- [x] Pattern destructuring uses `TOAST-0053`, and exhaustiveness uses `TOAST-0054`
- [x] Compiler-facing parsing/checking fixtures accumulate diagnostics in `Result` while invariant
      failures still throw
- [~] Interpreter, docs, help and type metadata share one contract; **compiled .NET does not**,
      and `no_clr` is future — see below

## Decisions (2026-08-29)

Taken with the user before any of this was built; recorded in `DECISIONS.md`.

- **`Option` and `Result` are ToastScript source, loaded as a prelude before user source** — not
  CLR types in the alias table beside `Error`. As ordinary unions they inherit pattern matching,
  exhaustiveness, `::` and serialisation; a CLR implementation would need
  `TryDescribePatternSubject` and the exhaustiveness checker taught a second shape. **No prelude
  mechanism exists today**, so building one is part of this item rather than an assumption it can
  lean on.
- **A user declaration shadows a core name, and is warned about.** Resolution follows the rule
  the parser already documents — "a bare name is where a declaration should win" — but silence
  would let it happen by accident.
- **Serialisation is deferred to `TOAST-0092`.** A union currently serialises as
  `{"Variant": "Ok", "Item1": 5}` and records nothing about the declaring union, so it cannot
  round-trip without knowing the target type. That gap belongs to every declared type, not to
  these two, and deciding it twice is the thing to avoid.

## Cleared before starting

Two defects would have made these types worse than useless, both found by probing this item and
both fixed first:

- `TOAST-0095` — a **qualified variant pattern never matched**. `Result.Ok(v)` is the spelling a
  core type invites, and every such arm was silently dead, with the exhaustiveness checker going
  quiet rather than reporting the arms it could not account for.
- `TOAST-0096` — a **unit variant could not infer its type arguments from its target**, so
  `Option::None<int>()` was required even where the annotation or signature said `int`. Shipping
  before this would have baked the repetition into every example of the feature.

## Built 2026-08-29

`src/Tosh.Language/CorePrelude.cs`, loaded from the engine constructor beside `BuiltinRunes` —
**the prelude mechanism the item needed already existed**, because the engine had been loading
ToastScript source at startup for the built-in runes all along.

- `Option<T>` / `Result<T, E>` as ordinary unions.
- Combinators in `extend` blocks: `is-some`/`is-none`/`is-ok`/`is-err`, `unwrap-or`, `map`,
  `map-err`, `and-then`, `inspect`, and `Result.ok()` to Option. A union body takes variants
  only, so `extend` is the form a user has for adding to any union — the core types are built
  out of the same material as everything else, which is what "without special cases in the
  evaluator" asks for.
- `attempt` as a rune over `try`/`catch`.

`tests/Tosh.Tests/CorePreludeTests.cs` — 28 tests.

### `Err`, not `Error`

The acceptance text said `Error`. `Error` already names the base class user error types extend,
so `Result.Error(new Error("x"))` would put two unrelated meanings of one word in one
expression. The item's surface was marked illustrative rather than binding.

### A defect this uncovered: `extend` did not work on a generic union

`extend Option { … }` silently failed to attach. A *bound* generic union names itself with its
arguments (`Option<int>`) while `extend Option` registers under the bare name, so the extension
lookup never matched — and since `extend Option<T>` does not parse, the bare name is the only
thing an author can key on. `EnumerateReceiverTypeNames` now also yields a union variant's
declaring union name. Without this the combinators had no idiomatic home at all.

### Exhaustiveness now reaches ambient unions

The binder built its union table from *the source being bound*, so a `match` over the prelude's
`Result` was neither judged exhaustive nor reported incomplete — the two types whose entire
purpose is exhaustive dispatch were the two without it. `Binder.Bind` already took the command
table from the engine, so the engine now supplies its known unions the same way, and a
declaration in the source still overrides an ambient one of the same name.

This also fixed a weakness the qualified-pattern work had exposed rather than caused. The union
table is keyed by *variant* name, and a name two unions share — `Some` belongs to `Option` and
to anything else declaring one — keeps only the last collected. A qualified arm looked up that
way disagreed with its own qualifier and the whole check bailed, silently. Qualified arms now
resolve through a union-name index, which is what qualifying a name is for.

### `attempt` and control flow, verified rather than assumed

`catch` already declines `ShellControlFlowException`, so `return`, `break` and `continue` travel
through `attempt` untouched — `attempt { return x }` returns from the enclosing function rather
than yielding `Ok`. Both cases are in the corpus, because this is the property that makes
`attempt` safe to reach for and the one most likely to regress quietly.

### `null`, `T?` and `Option<T>` cross only by name

The RFC lists this as required design decision #5 rather than answering it, so it was decided
with the user on 2026-08-29: **explicit in both directions, no implicit conversion anywhere.**

`T?` says a slot may hold nothing; `Option<T>` says absence is part of the domain. Neither
becomes the other on its own, so optionality cannot silently appear or disappear:

```tosh
option-from $nullable        # null -> None, value -> Some
$opt.or-null()               # Some -> value, None -> null

var o: Option<int> = null    # still refused
var x: int? = $opt           # still refused
```

There is no special foreign-boundary rule: the CLR has no `Option` to offer, so its nulls arrive
as nulls and are named across like any other. `T?` still admits `null` and a bare `T` still does
not, unchanged and matching the RFC.

**The spelling fell short of the decision.** What was put to the user read `Option::from(...)`,
and that cannot exist: a union body takes variants only, `extend` adds instance methods, and a
`static func` inside an `extend` block parses but is never found. It shipped as the free function
`option-from`; `TOAST-0097` carries the gap and the rename.

### A checker's diagnostics ride in a Result

`cp-check` in `CorePreludeTests` accumulates every problem with its input into a
`Result<string, list<CpDiag>>` and returns them together, which is the shape the self-hosting
compiler needs. Its companion test is the other half of the contract: a **broken invariant still
throws** rather than becoming an `Err` a caller might handle as ordinary input trouble.

**Writing that fixture found three defects**, none of them in the core types:

- **A generic type argument inferred from a value was spelled in raw CLR.** `list<int>` inferred
  as ``System.Collections.Generic.List`1[[System.Int32, System.Private.CoreLib, …]]``, and
  `IsInstanceOf` compares annotation text against that — so a function returning
  `Result<string, list<int>>` failed its own return-type conversion. It only ever worked for
  arguments whose two spellings coincide, like `int`. `DescribeClrType` now renders shell
  spelling, arrays and nesting included.
- **Value inference outranked the annotation.** Every declared record is one
  `ToshRecordInstance`, so a `list<Diag>` payload can *never* be recovered from the value's type
  — the annotation is the only thing that knows. The target now wins; a genuine mismatch is
  still refused by the variant field's own type check.
- **The return annotation was an `AsyncLocal`.** Its value was invisible to the function body
  the moment any statement in it ran a command — `echo "x"` before a `return` was enough —
  because the write did not reach the context the body's continuations resumed on. It is a plain
  field now, saved and restored the way `_functionCallStack` already is. The symptom was that a
  `return` at the top of a function inferred and the same `return` after one unrelated line did
  not, which is as arbitrary to debug as it sounds.

### Interpreter and compiled .NET do *not* share the contract

The core types reach the compiled backend — they are prelude ToastScript, not a builtin — but
nothing useful can be done with them there, for two reasons that predate this item:

- `extend` blocks are not emitted, so every combinator is missing.
- Variant patterns are not emitted at all, so `match` over **any** union is interpreter-only.

Three cases in `KnownDivergences`. Not implemented, per the standing decision that compiled
ToastScript is an experiment and the interpreter is authoritative; the acceptance box stays open
rather than being quietly reworded, because the two really do not agree.

Docs, help and type metadata *do* share it: `help Option` describes it as a ToSh type and
`name-of` answers.

## Left open

- **`Option::from` is spelled `option-from`** until `TOAST-0097` allows a static on a union.
- **Serialisation** is deferred to `TOAST-0092` by decision.
- **A combinator cannot name its receiver's type arguments.** `Result::Ok<dynamic, dynamic>` is
  written inside `map` because `Ok(f(v))` fixes `T` and says nothing about `E`, and there is no
  spelling for "the `E` this value already had". The values behave correctly; the static types
  are erased crossing a combinator.
- `Result::Ok(5)` with no target still refuses, since `E` is uninferable. In practice a `Result`
  is returned from a typed function, where the signature supplies both.
- Docs, help and type metadata; the `no_clr` target.

## Dependencies

Generic typed unions landed in `TOAST-0052`. `TOAST-0053` is partial for destructuring and
`TOAST-0054` is partial for exhaustive nested patterns; both should complete before these types
are presented as the preferred control-flow style.
