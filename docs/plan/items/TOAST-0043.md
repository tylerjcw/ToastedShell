---
id: TOAST-0043
title: "A compiled class method with an expression body returned null"
status: complete
area: toast
priority: 2
opened: 2026-08-21
closed: 2026-08-21
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

## What it actually was — 2026-08-21

**The title above was wrong, and narrowing was a red herring.** The reduction that produced
`null` had a class method with an expression body, and *that* was the cause:

```tosh
class E { func M() -> int => 7 }     # null compiled, 7 interpreted
class E { func M() -> int { return 7 } }   # 7 both — correct throughout
func m() -> int => 7                 # 7 both — correct throughout
```

Nothing to do with `match`, with `_ is Leaf`, or with reading a member off a base-typed
reference. Every one of those works once the method returns anything at all. The original
repro combined all three, and I filed the most interesting-looking of them.

A free function's body ending in a bare expression is collapsed into a return —
`CollapseTrailingExpressionIntoReturn`, and the comment there already described this exact
symptom: *"the block was emitted for effect, its value dropped, and the fall-through
returned `default(T)` … silently, and for the most idiomatic way to write a function in the
language."* Class methods never got it, so they fell through to their implicit
`return null`.

The rule is shared now rather than written twice, which is what let the two drift.

### Bisecting is what corrected the title

Working outward from the repro: a free function was fine, a block body was fine, a `match`
with a literal default still failed, and `func M() -> int => 42.0` — no match, no
narrowing, no members — failed too. Three of the four things in the original example were
irrelevant.

### One divergence found alongside, and not this

`class E { func M() -> dynamic { echo 1; echo 2 } }` counts 2 interpreted and 1 compiled.
Confirmed present before this change by reverting and re-running, so it is independent.
`dynamic` is deliberately excluded from the collapse — it is the documented way to opt out
of an annotation, and a method declared that way yields a stream.

## Acceptance

- [x] A class method with an expression body returns its expression, compiled — which is
      what the narrowing symptom actually was
- [x] The interpreted and compiled results agree, asserted per case
- [x] The `InvalidProgramException` from the readiness probe is accounted for — it was
      **not** this, and the remainder is filed as `TOAST-0044`
- [x] A negative control

## Notes

Blocks `TOAST-0038`, which is otherwise complete: the probe now compiles strict with no
flags, and only the compiled *run* diverges.
