---
id: TOAST-0044
title: "A compiled `new` of a declared class can resolve to an unrelated CLR type of the same name"
status: open
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

## Acceptance

- [ ] A minimal reproduction, smaller than `Token` + `Lexer`
- [ ] A declared class always wins over a same-named CLR type, compiled and interpreted
- [ ] The readiness probe (`TOAST-0038`) runs compiled and matches the interpreted output
- [ ] A control: a CLR type that the program does *not* declare still resolves
- [ ] A negative control

## Notes

Found by `TOAST-0038`, after `TOAST-0043` removed the failure that was masking it. This is
the last thing between here and Phase B's exit: the probe compiles, and this is why it does
not yet run.
