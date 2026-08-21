---
id: TOAST-0043
title: "A compiled match arm reads a member off the declared type, not the narrowed one"
status: open
area: toast
priority: 2
opened: 2026-08-21
---

## Problem

```tosh
class N { prop K: string = "n" }
class Leaf(v: double) extends N { prop V: double = $v }

class E {
    func Visit(n: N) -> double => match ($n) {
        _ is Leaf => $n.V
        default   => throw new Error("x")
    }
}
echo $"{((new E()).Visit(new Leaf(3.0)))}"
```

| | |
|---|---|
| interpreted | `3` |
| compiled | **`null`** |

The arm matched — `default` did not run, so no error was raised — and then `$n.V` read
nothing. `V` is declared on `Leaf`; `$n` is annotated `N`, which does not have it. The
interpreter reads the member off the *value*, which is a `Leaf`. The compiled code appears
to read it off the annotation.

This is the visitor pattern, and it is what a tree-walking pass over an AST is made of.

## How it was found

Typing `bench/probes/compiler_shape.tosh` end to end for `TOAST-0038`. The probe compiled
for the first time and then failed at runtime with
`System.InvalidProgramException: Common Language Runtime detected an invalid program`,
which reduced to this shape — though the reduction reports `null` rather than crashing, so
the probe is hitting **at least** this and possibly more.

`TOAST-0022` already records that a compiled class does not reach its `Display` trait, and
`match` narrowing is listed as *working* in `TOAST-0036`'s measurements — narrowing the
control flow is not the same as narrowing the type used to read a member, and only the
first was measured.

## Acceptance

- [ ] A member declared on a narrowed type is readable inside the arm that narrowed it,
      compiled
- [ ] The interpreted and compiled results agree, in the differential corpus
- [ ] The `InvalidProgramException` from the readiness probe is accounted for — either this
      is its cause, or the remainder is filed separately
- [ ] A negative control

## Notes

Blocks `TOAST-0038`, which is otherwise complete: the probe now compiles strict with no
flags, and only the compiled *run* diverges.
