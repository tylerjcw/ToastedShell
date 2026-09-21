---
id: TOAST-0137
title: "`to ton` wrote documents `from ton` refuses, for a class with a constructor and for every struct"
status: complete
area: toast
priority: 2
opened: 2026-09-20
closed: 2026-09-20
---

## Why this exists

`TonWriter` opens by stating its own rule: *the writer must only emit what the reader accepts*.
It broke that rule for two shapes, and [`TOAST-0092`](TOAST-0092.md) closed with "every declared
shape round-trips: record, struct, class, …" ticked, because the round-trip test uses
`TonVillager` — a class taking no constructor arguments, which is the one class shape the
literal form can rebuild.

```tosh
class P(x) { prop X = $x }
(new P(3)) | to ton                    # new P {| X = 3 |}
((new P(3)) | to ton) | from ton       # error: No constructor matched class 'P' with 0 argument(s)

struct S(x) { prop X = $x }
(new S(3)) | to ton                    # new S {| x = 3, X = 3 |}
((new S(3)) | to ton) | from ton       # error: No value was provided for 'x'
```

## Two causes, not one

**A class.** The literal form `new P {| … |}` *constructs* and then assigns, so the reader needs
a zero-argument way in. `Nameable` asked only whether the name resolves. A primary constructor
leaves no zero-argument path, and neither does an explicit constructor that takes arguments.

**A struct.** Worse, and differently: a struct is immutable — neither a field nor a property can
be assigned once it exists — so the literal form could *never* fill one, whatever its
constructor looks like. Every struct carrying state failed to read back. It also wrote a field
and the property derived from it side by side, asserting one value twice.

## What changed

| Shape | Before | After |
|---|---|---|
| class, no constructor arguments | `new P {\| X = 3 \|}` | unchanged — reads back |
| class, primary or argument-taking constructor | `new P {\| X = 3 \|}`, **refused** | `{\| X = 3 \|}` — anonymous, reads back |
| struct with fields | `new S {\| x = 3, X = 3 \|}`, **refused** | `new S(x = 3)` — reads back |
| record | `new R(a = 1, b = 2)` | unchanged |

The struct fix *keeps* the type name: a struct's fields are its constructor parameters, which is
the record case exactly, so it takes the same named-argument spelling. Its properties are
derived from those fields and are no longer written.

The class fix gives the type name up. A class's properties are **not** its constructor
parameters — `class P(x) { prop X = $x * 2 }` cannot be rebuilt by handing `X` back as `x` — so
unlike a record there is no call form to fall back to, and an anonymous record is what is left.
It carries the data and it reads back, which is the same degradation a class declared inside a
module already takes.

## Why not fix the reader instead

`TOAST-0092`'s design table says a class is reconstructed by "property assignment" and names
"populating without a constructor does not re-establish invariants" as an accepted residual
hole — which reads like the reader should construct without running a constructor at all.

It should not, for two reasons. `new P {| … |}` is an ordinary ToastScript expression, not a
notation-only form — the specification documents it at `§Records and Classes` — so changing
what it means would change the language for every caller. And `TOAST-0092` requires the document
to be read by the real parser and evaluator, "one grammar, one parser, no second implementation
to drift", so `from ton` cannot be given a private construction path either.

Naming a class in the notation at all is therefore limited by what `new Name {| … |}` can do.
Lifting that limit is a language decision about object initialisers, not a serialisation fix.

## Acceptance

- [x] `to ton` never writes a named form its own reader refuses
- [x] A class with a primary constructor round-trips, anonymously
- [x] A class the reader can rebuild keeps its name — regression guard on the fix
- [x] A struct round-trips *and* keeps its name
- [x] A struct's derived property is not written beside the field it comes from
- [x] Tests per row of the table above, in `TonWriterTests`

## Notes

Found by probing [`TOAST-0099`](TOAST-0099.md), which describes a different gap in the same
area — a declared type cannot state its qualified name, so a module-scoped type degrades to an
anonymous record. That degradation is deliberate and documented in `TonWriter`; this was not.
