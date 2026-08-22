---
id: TOAST-0044
title: "A compiled `new` of a declared class can resolve to an unrelated CLR type of the same name"
status: partial
area: toast
priority: 1
opened: 2026-08-21
---

## Problem

The readiness probe declares `class Token(kind, text, pos)`. Compiled, it fails at runtime
with:

```
No constructor matched 'System.Runtime.InteropServices.PosixSignalRegistration+Token' with 3 argument(s).
```

`new Token(…)` resolved to a **nested CLR type** that happens to share the simple name,
rather than to the class the program declares twenty lines above.

## Why this is priority 1

It is a silent capture of a user's own type name by an unrelated implementation detail of
the host, and the name involved — `Token` — is one of the most likely a compiler-shaped
program will choose. The failure surfaces as a constructor-arity complaint about a type the
author has never heard of.

`TOAST-0030` added the platform-index fallback to the compiled `new` so that Tōast's own
names (`Error`, `Failure`) would resolve, and `TOAST-0034` added the same index to
annotation resolution. Both put it **last**, after user types — which is correct, and is
evidently not sufficient here: something makes the earlier user-type lookups miss.

## What is known

Narrowed to `Token` + `Lexer` from the probe (`U1` in the bisect), and **not** reproduced by
any of these in isolation:

- a class named `Token` with a one- or three-argument constructor
- `export class Token`, with and without a `ToString()` override
- `new Token(…)` inside a class method, including one returning `-> Token`
- a class returning `list<Token>` and accumulating into it
- a property named `Token` of type `Token`
- a computed property with an expression body

So it needs a combination not yet isolated. The bisect scripts and the failing minimal file
are the starting point, not a mystery.

## Cause, and fix — 2026-08-21

**A class the emitter cannot shell stays on source replay, and replayed source resolves
through the engine — which knew nothing about the shells emitted beside it.**

Six lines reproduce it, and the computed property is load-bearing:

```tosh
class Token(k: string) { prop K: string = $k }
class Mk {
    prop N: int = 1
    prop Computed: bool => $this.N > 0     # ← without this, Mk gets a shell and it works
    func Make() -> Token { return new Token("a") }
}
```

`CanEmitClrClassShellOwnShape` rejects any class with a getter body, so `Mk` is replayed.
Its replayed `new Token(…)` then went: engine named types (nothing — `Token` is an emitted
CLR shell, not an engine class), then CLR resolution, then the platform index, which found
`PosixSignalRegistration+Token`.

`RegisterCompiledAssembly` now registers every `[ToshType]` shell as an alias on the
runtime's resolver, so a declared name resolves to the program's own type first. It already
walked the assembly for module shells; this is the same walk. Registering there rather than
teaching the engine about compiled shells keeps the dependency pointing the right way —
`Tosh.Language` does not learn about `Tosh.Compiler.Runtime`.

### What ruled the guesses out

Not reproduced by: a one- or three-argument constructor, `export`, a `ToString()` override,
`new` inside a class method, a method returning `-> Token`, a class returning `list<Token>`,
a property named `Token` of type `Token`, a computed property *alone*, a public method
calling a private one, or a constructor taking `list<Token>`. Each was tried and passed.
What none of them had was **two classes where one is replayed and the other is shelled**.

## Still open — the failure this was masking

With names resolving, the readiness probe fails differently:
`System.InvalidProgramException: Common Language Runtime detected an invalid program`,
reported at `Main`.

Narrowed: the lexer alone runs (`tokens=4`). Constructing a `Parser` runs. **Calling
`Parser.ParseExpr()` from `Main` does not**, for every input tried — `1`, `x`, `-1`, `(1)`,
`1 + 2` — and whether the result is bound to a typed local, a `dynamic` local, or used
inline. So it is neither the input nor the local.

Also ruled out as the cause: a public method calling a private one, a recursive private
chain, a method returning a user class, and a constructor taking `list<Token>` — all emit
and run.

The next step is reading the emitted IL for `ParseExpr` rather than bisecting the source
further; six rounds of source bisection have stopped paying.

## Acceptance

- [x] A minimal reproduction — six lines, and the mechanism is named
- [x] A declared class always wins over a same-named CLR type, compiled and interpreted
- [ ] The readiness probe (`TOAST-0038`) runs compiled and matches the interpreted output —
      **still blocked**, by a different failure this one was masking
- [x] A control: a CLR type that the program does *not* declare still resolves
- [x] A negative control

## Notes

Found by `TOAST-0038`, after `TOAST-0043` removed the failure that was masking it. This is
the last thing between here and Phase B's exit: the probe compiles, and this is why it does
not yet run.
