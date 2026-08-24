---
id: TOAST-0065
title: "An emitted class inherited object.ToString, so it converted to its CLR name and a match value arm missed"
status: complete
area: toast
priority: 2
opened: 2026-08-22
---

## Problem

`match` with **type patterns** over a declared hierarchy compiles cleanly and then produces
`null` at run time:

```tosh
class Shape  { prop K: string = "s" }
class Circle extends Shape { prop K: string = "c" }

var s: Shape = new Circle()
var r: dynamic = match ($s) {
    Circle => "circle"
    Shape  => "shape"
}
echo $r
```

| Backend | Result |
|---|---|
| interpreted | `circle` |
| compiled | throws — *"'r' produced a value that could not be converted to 'dynamic'"* |

The diagnostic names the annotation because that is where the null is *noticed*; even
`dynamic` refuses it, which is what makes the value clearly null rather than merely the wrong
type. Annotating `r: string` reports the same thing about `string`.

## What is and is not affected

Measured on 2026-08-22:

| Shape | Interpreted | Compiled |
|---|---|---|
| `match` on an `int` with literal arms | `two` | `two` |
| `match` on a class hierarchy with type-pattern arms | `circle` | **null** |

So it is narrowing by type that is broken, not `match`.

## Why it was not caught

`TOAST-0036` measured this exact shape and recorded "compiles" — which was true, and was the
question being asked at the time. Nothing ran it. That is the whole finding: a compiled
backend can pass a compilation check and produce a different answer, and only running both
tells you.

It is now in `DifferentialExecutionTests.KnownDivergences()`, asserted to **still** diverge,
so the test fails and says so when it is fixed.

## The diagnosis was wrong — corrected 2026-08-24

**It is not a `match` defect, and it is not about narrowing.** The spec defines the type
pattern as `_ is <type>` (§Match), and that shape works on both backends, base types
included — measured before changing anything, and now four corpus rows say so.

The failing arm was spelled `Circle => "circle"`, which the same section calls a **value
pattern**, matching *by equality*. It matched interpreted because equality converts, and
converting a class instance to a string yields its type name:

| Expression | Interpreted | Compiled (before) |
|---|---|---|
| `$s as string` | `Circle` | `p.Circle` |
| `$s == "Circle"` | `true` | `false` |
| `match ($s) { Circle => … }` | `circle` | *(no arm matched)* |

So one cause with three faces, and the `match` row is the least of them: an emitted class
inherited `object.ToString()`, which answers the CLR type's full name and carried the
assembly's namespace into a value the reader wrote.

## Resolution — 2026-08-24

The emitter declares the `ToString` an emitted class was inheriting, returning the class's
Tōast name — which is exactly what `ToshClassInstance.ToString()` answers when the author
declared none. Another instance of the gap `TOAST-0022` predicted: "a compiled class not
answering an interface the interpreted one does".

It is marked compiler-generated, because the renderer has to tell it from a `ToString` the
author wrote. One is a rendering declaration to prefer over structural output; the other is
the fallback structural output exists to beat. A corpus row asserts a declared `ToString`
still wins.

A separate regression surfaced while measuring this and is fixed with it: `TOAST-0022`'s
structural rendering emitted a shadowed property twice, since a `prop` overriding a base
class's is two CLR members.

## Acceptance

- [x] A compiled `match` with type-pattern arms returns the arm's value — it already did;
      the four rows now asserting it are the value the item actually had here
- [x] Narrowing binds the value at its narrowed type inside the arm, as interpreted
- [x] The case moves from `KnownDivergences()` into `Corpus()` — with six companions, since
      the cause reaches `as string` and `==` and not only `match`
- [x] An inherited match — a type pattern naming a base of the runtime type — behaves the
      same on both backends. Worth recording: `_ is Shape` matches a `Circle` and the value
      pattern `Shape =>` does *not*, because one tests the type and the other compares a name
- [x] A negative control — dropping the emitted `ToString` fails three of the moved rows

## Notes

Found while verifying `TOAST-0036`'s control shapes by running them rather than compiling
them. Related: `TOAST-0022` and `TOAST-0030` record the other interpreted/compiled
divergences by the same mechanism.
