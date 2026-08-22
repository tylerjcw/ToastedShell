---
id: TOAST-0044
title: "A compiled `new` of a declared class can resolve to an unrelated CLR type of the same name"
status: complete
area: toast
priority: 1
opened: 2026-08-21
closed: 2026-08-21
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

## The dropped instruction — fixed 2026-08-21

**A single-byte instruction between two marked labels was dropped when the assembly was
persisted.**

`EmitLogicalOr` ended `… br.s done / truthy: ldc.i4.1 / done:`. Proved by instrumenting the
emitter: it reported writing `ldc.i4.1` at offset `0x40` and the while's exit test at
`0x41`; the written body has the exit test at `0x40` and no `ldc.i4.1` anywhere. Every later
branch operand is consistent with exactly one byte missing — restore it and the loop's `br`
operand `C1 FF FF FF` targets `0x12`, which is where the condition starts.

`and`, chained comparisons, and one `match`-range pattern had the identical shape. All four
now accumulate into a local, so no single-byte instruction ever sits between two labels.

`bench/probes/toast-0044-repro.tosh` gets past it: compiled, it now reaches `TOAST-0045`'s
record-conversion error instead of dying with `InvalidProgramException`.

### The guard, and an honest note about it

Three synthetic corpus cases using `or` and `and` in a loop condition were added — and
**none of them reproduce the defect.** Reverting the fix leaves all three passing. The drop
needs the surrounding method to place the two labels at particular offsets, which small
examples do not. They document the shape; the reproduction file holds the line, and
reverting the fix fails it.

`EmittedIl.Faults` is the tool that found it, and it is now permanent. It decodes each
method and asserts branches land on instruction boundaries and `finally` handlers end with
`endfinally`. **`RuntimeHelpers.PrepareMethod` does not catch any of this** — it reports
success for the very IL that throws when run, which is why the check added in the previous
commit was replaced rather than kept alongside.

## The finally handler — fixed 2026-08-21

The verifier named it: `handler ends 0x2A not endfinally`, `0x2A` being `ret`.

And it was **the same one-byte drop**. The handler's own bytes gave it away:

```
0x0236: 11 08        ldloc.s 8
0x0238: 2D 01        brtrue.s -> 0x023B
0x023A: 11 08        ldloc.s 8      ← starts at 0x23A, not 0x23B
```

The `endfinally` in the null-check's skip path — one byte, immediately before a marked
label — was dropped, so the handler ran on into the epilogue `ret`.

The fix removes the branch rather than working around the drop: the null check was
**unreachable**. The enumerator is obtained by `callvirt GetEnumerator` *before*
`BeginExceptionBlock`, so control cannot be inside the `try` unless the local holds one. A
null source throws at that call, outside the protected region. With no branch there is no
label, and with no label there is nothing to lose.

Three earlier attempts at this handler looked like they did nothing. They were measured
while the dropped byte was still shifting every offset.

## One more, found once the IL was sound — a class could not assign its own private property

`shy prop` is emitted as a private field. Member *reads* had a direct `ldfld` fast path;
*writes* always went through `ToshHost.SetMember`, which resolves by reflection over
**public** members. So `$this.n_ = …` failed at runtime with "Member 'index_' was not found
on type 'Parser'" — from inside the class that declares it.

Widening the reflective accessor to private members would have fixed the symptom by making
every CLR object's internals reachable from script, which is a much larger decision.
Emitting `stfld` when the emitter knows the field keeps the member private to everyone
except the code compiled alongside it.

## Acceptance

- [x] A minimal reproduction — six lines, and the mechanism is named
- [x] A declared class always wins over a same-named CLR type, compiled and interpreted
- [x] The readiness probe (`TOAST-0038`) runs compiled and matches the interpreted output
- [x] A control: a CLR type that the program does *not* declare still resolves
- [x] A negative control

## Notes

Found by `TOAST-0038`, after `TOAST-0043` removed the failure that was masking it. This is
the last thing between here and Phase B's exit: the probe compiles, and this is why it does
not yet run.
