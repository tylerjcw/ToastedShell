---
id: TOAST-0038
title: "The readiness probe is untyped and does not compile, and it is Phase B's exit"
status: partial
area: toast
priority: 2
opened: 2026-08-21
---

## Problem

Phase B's exit is *"the probe compiles and runs through the normal IL path without an
interpreter dependency"*. The probe exists — `bench/probes/compiler_shape.tosh`, 371 lines
of lexer, recursive-descent parser, AST hierarchy and two visitor passes — and **it does
not compile**.

It is also not yet a *typed* probe, which the bullet asks for. Its own methods declare no
return types:

```tosh
export class Lexer(source: string) {
    func Tokenize() { … }        # no return type
}
export class Parser(tokens) {    # no parameter type
    func ParseExpr() { … }       # no return type
}
```

So the exit criterion cannot currently be evaluated at all: the probe would fail whether or
not the compiler were ready.

## Measured 2026-08-21

`tosh --compile bench/probes/compiler_shape.tosh` — six errors, two kinds:

| Error | Cause |
|---|---|
| `Compile` is missing a return-type annotation | written that way |
| Parameter `scope` of `Compile` is missing a type annotation | written that way |
| `tokens` could not be pinned down | `(new Lexer($source)).Tokenize()` — a call |
| `ast` could not be pinned down | `(new Parser($tokens)).ParseExpr()` — a call |
| `globals` could not be pinned down | `new System.Collections.Hashtable()` |
| `r` could not be pinned down | `Compile($src, $globals)` — a call |

`--compile-allow-dynamic` removes the four `implicit_dynamic` errors and **not** the two
annotation errors, which are unconditional. So the probe does not compile by either route.

Four of the six are calls, which is `TOAST-0034`. Two are the probe being untyped, which is
this item.

## What this item is, and is not

**It is**: typing the probe end to end — annotating every method, parameter and return —
and treating whatever fights back as the finding. That is the exercise the bullet describes,
and it is how `TOAST-0034` and `TOAST-0036` were found in the first place, from six error
messages.

**It is not**: making the probe compile by weakening it. Annotating a return as `dynamic`
would satisfy the compiler and defeat the point, since the probe exists to find out which
parts of ToastScript fight back when you write compiler-shaped code.

## Progress — 2026-08-21

**The probe compiles.** Strict, no flags, from six errors to none. That is the first half of
Phase B's exit sentence; the second half — running through the IL path — is blocked by
`TOAST-0043`.

Annotating it end to end found **five** things, four of which are now fixed. None had been
reported by anyone; the probe exists to be written against, and this is what it caught.

### 1. A collection could not be annotated with a declared element type

`list<Token>`, `array<Token>` and `Token[]` all failed with "could not be converted";
`list<int>` and `array<int>` worked. `List<int>` is a real CLR type to convert to, and a
tōast class is a `ToshClassInstance`, so `List<Token>` does not exist for the conversion to
target.

Elements are now checked rather than converted, using the same `is` that answers "is this
value that type" everywhere else. A lexer returning `list<Token>` is the ordinary shape of
compiler-shaped code, so this was a hard stop.

### 2. A property annotated with its own type reported a mismatch against itself

`prop Operand: Node = $operand` → *"Cannot assign value of type 'Node' to property 'Operand'
of type 'Node'."* Two different types with one name: the checker resolved member
annotations with a resolver built `userTypes: null`, and once `TOAST-0034` gave resolution
the platform index, a user type name found whatever CLR type shared it.

**A regression introduced by `TOAST-0034` and caught by this item within the hour**, which
is a fair argument for doing them adjacently.

### 3. A refused shape crashed the assembly writer instead of reporting

The emitter serialized unconditionally. A shape it had already declined leaves incomplete
IL — a branch whose target was never marked — so `PersistedAssemblyBuilder` threw
`InvalidOperationException: Label 5 has not been marked`, and the diagnostic naming the
actual problem was discarded with a stack trace on top of it.

Nothing is serialized after a refusal now. Every caller already checked `IsClean`; what
changes is that they get the reason.

### 4. A `match` arm could not throw

`default => throw …` is how an arm says it cannot happen, and it was refused in value
context — which is what produced the crash above.

### 5. A compiled class method with an expression body returned null — `TOAST-0043`

Filed as a narrowing bug and it was not one: `class E { func M() -> int => 7 }` answered
null compiled, with no `match` and no members involved. Free functions collapse a trailing
expression into a return; class methods never did. Fixed, and the rule is now shared.

### 6. Still blocking — `TOAST-0044`

With that fixed the probe compiles *and* gets further, then fails because `new Token(…)`
resolves to `System.Runtime.InteropServices.PosixSignalRegistration+Token` rather than the
class the probe declares. A user's own type name captured by a host implementation detail,
which is why it is priority 1.

### One local left unannotated, on purpose

`var tokens = []` inside `Tokenize`. An array literal already infers, and annotating it
`list<Token>` made the very next line — `$tokens = $tokens + [...]` — fail to convert back,
because `+` over collections does not preserve the element type. The reason is in the
source rather than only here.

## Acceptance

- [x] Every function, method, parameter and return in the probe is annotated concretely —
      no `dynamic`. One *local* is deliberately unannotated, with the reason in the source
- [x] `tosh --compile` accepts it with no flags
- [ ] The compiled probe produces the same output as the interpreted probe — **blocked by
      `TOAST-0044`**
- [ ] It runs without an interpreter dependency — the Phase B exit sentence, checked
      explicitly rather than assumed from a successful compile
- [x] Whatever fights back is filed, not worked around — four fixed, one filed
- [ ] A negative control

## Notes

Depends on `TOAST-0034` for four of the six errors. The other two can be fixed immediately
and will change what the remaining errors are — which is the point of doing this alongside
rather than after.

Blocks Phase C, which asks that the interpreter and IL pass the differential corpus; that
corpus is `DifferentialExecutionTests`, now down to three recorded divergences after
`TOAST-0030`.
