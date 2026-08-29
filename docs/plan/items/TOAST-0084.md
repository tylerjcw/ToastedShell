---
id: TOAST-0084
title: "A successful null, type or variant test does not narrow later uses of the value"
status: proposed
area: toast
priority: 2
opened: 2026-08-28
---

## Problem

Tōast has nullable annotations, refinements, `is`/`is-not`, closed unions and binding patterns,
but they do not form one flow-sensitive type system. A programmer can prove a fact in an `if` or
`match` and still meet dynamic member lookup or a broad type after the branch.

This matters more once `Option` and `Result` become core types: destructuring `Ok(value)` should
make `value` a `T`, not a dynamic object that happens to carry the right value at runtime.

## Examples

```tosh
func length-or-zero(text: string?) -> int {
    return 0 if ($text is null)
    return $text.Length                 # text is string here
}

func render(shape: Circle | Rectangle) {
    if ($shape is Circle) {
        echo $shape.Radius              # Circle in this branch
    } else {
        echo $shape.Width               # Rectangle after subtraction
    }
}

match ($result) {
    Ok(value) => use($value)             # value has Result's T
    Error(e)  => warn($e)                # e has Result's E
}
```

The anonymous-union spelling above remains subject to `TOAST-0048`; the same narrowing rule must
work first with nullable types, named base/derived types and declared union variants.

## What is actually there today — measured 2026-08-28

Before designing the flow analysis, the non-flow-sensitive half was probed. It is thinner than
this item assumes.

| written | member access checked? |
|---|---|
| `var n: Node = …` then `$n.Value` | **yes** — `tosh.type.member_not_found` |
| `var s: string = …` then `$s.Nonexistent` | **yes** — `tosh.type.member_not_found` |
| `func f(n: Node)` then `$n.Value` | **no diagnostic at all** |
| `func f(s: string)` then `$s.Nonexistent` | no — a *runtime* `expression_failed` |

**A parameter's declared type does not reach the member checker; a variable's does.** The
compiler agrees with the interpreter here — it emitted the third row without complaint.

Every example this item opens with is a *parameter*. So narrowing built today would be
unobservable: there is nothing to narrow *from*, because the un-narrowed access is already
permitted everywhere the item cares about. `if ($n is Leaf) { $n.Value }` and
`$n.Value` alone are both accepted, so the test would pass without the feature.

### Which splits the item in two

**Stage 1 — a parameter's annotation types references to it in the body.** This is a
prerequisite, it is not flow analysis, and it pays on its own: it catches a typo on any typed
parameter, which is most of a program's typed surface. The likely blast radius is the open
question — it turns on checking for bodies that have never been checked, so it needs measuring
against the repository's own scripts and a real library before it is switched from warning to
error.

**Stage 2 — the control-flow graph this item describes.** Unchanged in scope, but it now has
something to refine: narrowing means replacing a parameter's declared type with a narrower one
for the branch, rather than inventing a type where none was tracked.

Attempting stage 2 first would produce a feature whose tests pass before it is written.

## First slice — 2026-08-28

Two changes, and the first is why the second is worth anything.

**A returned expression is now walked.** It was checked for its *type* against the declaration
and never descended into, so nothing inside it was examined: `writeline $s.Nonexistent`
reported a missing member and `return $s.Nonexistent` reported nothing. The checker stopped at
the statement a function is most likely to end with — which is why the probes above read as
"parameters are not typed" when parameters were typed all along.

That mattered more than a missed diagnostic. Narrowing built on top would have been
*unobservable*: `if ($n is Leaf) { return $n.Value }` and a bare `return $n.Value` were both
silent, so a test for narrowing would have passed before the feature existed.

**`if ($x is T)` narrows its then-branch.** A match arm already did — the machinery
(`PushNarrowing` / `LookupNarrowed`) was there, applied to `ComparisonPatternSyntax`, and an
`if` condition is an ordinary binary operator instead. The specification's `§Type Narrowing`
claims *"Both `if` and a `match` arm narrow, and they narrow identically"* and its own verbatim
example failed; it passes now.

Only the then-branch. Subtracting the type in the else-branch is this item's closed-alternative
rule and needs a type the model cannot yet spell.

### Blast radius: none

The concern was that walking returns would light up every typed function. Measured across the
repository's `examples/`, `scripts/` and `tests/` and the author's own `~/.config/tosh` —
**57 files, zero diagnostics** — with the harness demonstrably non-vacuous, since it reports the
probe case it was built for.

## Soundness boundary

The checker needs a control-flow graph, not textual substitution. Reassignment invalidates facts;
facts from only one branch are joined rather than leaked; closures, `ref` aliases and calls that
can mutate a captured slot force conservative widening. Short-circuit `and`/`or`, early `return`
and exhaustive matches contribute reachability facts.

## Acceptance

- [ ] `x is null` / `x is-not null` narrows `T?` in the true and false paths
- [~] `x is T` narrows in the true path, for `if` and for a match arm. Subtracting `T` in the
      false path still needs a type the model cannot spell
- [ ] A union variant pattern gives every payload binding its declared substituted type
- [ ] Refinement tests preserve the refinement type rather than only its base type
- [ ] Early exit, short-circuit logic and exhaustive match arms contribute correct reachability facts
- [ ] Reassignment, aliasing, captures and effectful calls invalidate facts where required for soundness
- [ ] Branch joins compute the safe common type and do not adopt a type from a path that may not run
- [ ] Impossible tests and unreachable arms have stable diagnostics without rejecting dynamic `Any` code
- [ ] LSP hover/completion shows the narrowed type at the use site
- [ ] Interpreter and compiler consume the same checked facts; the differential corpus covers each join

## Dependencies

Typed generic unions are complete in `TOAST-0052`; binding patterns are partial in `TOAST-0053`;
nested usefulness/exhaustiveness remains in `TOAST-0054`. This item generalizes their local facts
into the surrounding control-flow graph rather than adding another matcher.
