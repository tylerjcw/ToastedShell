---
id: TOAST-0036
title: "There is no concrete function type, so no higher-order value can be typed"
status: complete
area: toast
priority: 2
opened: 2026-08-21
closed: 2026-08-22
---

## Problem

Phase B's third bullet asks that "higher-order calls, interfaces, unions, narrowing,
generics, and method references" be made reliable. Measured, **four of those six already
compile**, and the two that do not share one cause: there is no way to write the type of a
function.

`func` is a keyword, not a concrete type. Annotating with it does not make a value
concrete:

```tosh
var f: func = func(x: int) -> int => $x + 1   # tosh.compile.implicit_dynamic
```

The annotation is present and still means "some function", so the compiler learns nothing
from it — no parameter types, no return type, nothing to emit a typed call against.

## Measured 2026-08-21

Each compiled on its own with `tosh --compile`, no flags:

| Shape | Result |
|---|---|
| interface with an implementing class | compiles |
| union declaration and use | compiles |
| generic class `Box<T>` | compiles |
| method reference `$k.M(1)` | compiles |
| `match` narrowing over a hierarchy | compiles |
| anonymous function in a variable | **`implicit_dynamic`** |
| function as a parameter (`g: func`) | **`missing_type_annotation`** |
| function reference `&dbl` | **`implicit_dynamic`** |
| closure capture returning a `func` | **`implicit_dynamic`** |

So the bullet is mostly already satisfied, and what remains is not a reliability problem
but a **missing piece of the type system**: a signature type, something like
`func(int) -> int`, that says what a function value accepts and returns.

## Correction — 2026-08-21

**`FunctionType` already exists.** `BoundType.cs` declares it, with a `DisplayName` of
`(int, string) -> bool`. What is missing is narrower than this item states:

- nothing constructs one — no `new FunctionType` anywhere in the tree
- `TypeNameResolver` never mentions it, and the type-name grammar has no function node, so
  `func(int) -> int` cannot parse

So the representation is done and the *surface* and *inference* are not. Found by the audit
in `TOAST-0048`.

One thing to fix regardless of the spelling chosen: **`func` currently resolves to
`System.Func\`1`** — the platform-index fallback added in `TOAST-0034` finds the CLR type by
simple name. `var f: func` is therefore concrete and wrong rather than merely vague.

## The decision this needs

What is the spelling, and how much does it have to express?

1. **`func(int) -> int`** — mirrors the declaration syntax, reads the same way as the thing
   it describes. Wants a rule for optional and rest parameters.
2. **A named delegate declaration** — `delegate Transform(x: int) -> int`, then
   `var f: Transform`. Cheap to resolve and emit; adds a declaration form.
3. **Structural only** — infer from the assigned lambda and require the annotation only at
   boundaries. Least new syntax; does nothing for a `func` *parameter*, which is where the
   `missing_type_annotation` above comes from.

Option 1 or 2, and the question underneath is whether a function type is a *value* the
language names or a *shape* it merely checks.

## Acceptance

- [x] A function value can be given a concrete type, and that type is used
- [x] A function-typed parameter compiles without `--compile-allow-dynamic`
- [x] A function reference (`&name`) and a closure both compile typed
- [x] The five shapes that already compile stay compiling, as controls — **four of them.**
      The fifth turned out not to work at all, which is `TOAST-0065` below
- [x] `docs/spec/` states the function type
- [x] A negative control — removing the parser's `func(` production fails 16 tests and
      leaves the four controls and the inference control passing

## Resolution — 2026-08-22

**`func(int) -> int`**, chosen by the user over a named `delegate` declaration: it mirrors
the declaration syntax, so the type reads like the thing it describes, and a signature used
once needs no name.

The change is in four places, and the bound tree was not one of them — `FunctionType` was
already there, with a `DisplayName`, and had never been constructed.

1. **The type-name grammar** gained a `FunctionNode`. The return is parsed with the full type
   parser, so the form is greedy and therefore right-associative:
   `func(int) -> func(int) -> int` is a function returning a function, which is the only
   reading that makes currying writable.
2. **The annotation parser** produces the text, and **the lookahead** learned to span it —
   the same two-place fix `TOAST-0050` needed for tuples, for the same reason: a type name
   several tokens long is invisible to a predicate that only knows barewords.
3. **The runtime check** asks what can be asked when a value arrives: is it callable, and can
   it be called with this many arguments. `IShellCallable` reports a required and a maximum
   count, so optional and rest parameters are handled as a range rather than an equality.
   Parameter *types* are a promise the compiler checks; comparing them here would be guessing.
4. **The lowerer infers a lambda's own signature.** `func(x: int) -> int => $x + 1` already
   says what it takes and returns, and requiring that again on the variable holding it would
   be asking the author to repeat themselves to tell the compiler what they had told it.
   Everything or nothing: one unannotated parameter, no declared return, or a rest or
   optional parameter, and it stays `dynamic` — a half-known signature is worse to emit
   against than an honest unknown.

### `func` meant `System.Func\`1`

The platform-index fallback from `TOAST-0034` resolved the *keyword* to a CLR type by simple
name, so `var f: func = 5` reported *"Cannot assign value of type 'Int32' to variable 'f' of
type 'Func`1'"*. It was concrete and wrong rather than vague: it rejected every ToastScript
function while accepting nothing useful. A bare `func` now means "some callable".

### The fifth control shape does not work

Running the five features this item was supposed to leave alone — rather than only compiling
them — found that **`match` with type-pattern arms over a class hierarchy yields null when
compiled** and the arm's value when interpreted. `match` itself is fine: the same statement
over an `int` with literal arms agrees.

That is `TOAST-0065`, filed and added to `KnownDivergences()` as a tripwire. It is worth
being precise about why this item's own table missed it: the table says "compiles", and that
was true. The question asked was whether the compiler accepted the shape, and a compiled
backend can accept a shape and produce a different answer.

## Notes

The bullet's wording implied six unreliable features. Measuring first turned it into one
missing type and five working ones — which is a much smaller item, and a different kind of
work than "make X reliable" suggests.
