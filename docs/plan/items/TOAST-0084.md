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
- [~] `x is T` narrows in the true path, for `if` and for a match arm; `x is-not T` and
      `not (x is T)` narrow the **else** path. Subtracting `T` from the other branch of a
      positive test is still not done — it needs a type the model cannot spell.
- [x] A union variant pattern gives every payload binding its declared substituted type —
      positional and named, generic and not, for a union declared in this source *or* ambient.
      A binding inside a *nested* pattern is not typed; that needs the payload type's shape
      rather than the union's, and is a slice of its own.
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

## Second slice — 2026-09-04: the else branch of a negative test

`if ($n is-not Leaf) { … } else { HERE }` knows exactly as much as
`if ($n is Leaf) { HERE }` does, and threw it away. Only `is` narrowed, and only its
then-branch, so the same fact written the other way round bought nothing. `not ($n is Leaf)`
reads identically, and negations nest — a doubled one returns the fact to the then-branch.

**Which branch gets the fact is the whole content of this slice.** The narrowed branch is the one
where the value *is* the tested type: the then-branch of a positive test, the else-branch of a
negative one. Two controls pin that, and they assert the *presence* of a diagnostic — the
then-branch of `is-not` must stay unnarrowed, because there the value is precisely not a `Leaf`
and a checker that narrowed it would be unsound rather than generous.

Still not done, and still for the same reason: subtracting `T` from the else of a positive test.
That is the closed-alternative rule and it needs a type the model cannot write down. So the two
spellings are not interchangeable, which the specification now says outright, with the advice
that follows from it — write the test whose narrowed branch is the one carrying the work.

## Third slice — 2026-09-04: variant payload bindings

`Full(v) => $v.Nope` reported nothing. The binding carried no type at all, so a member that does
not exist on the declared payload was never checked — which made destructuring a shape that
happens to carry the right value at run time rather than something the checker knows about.

A payload binding now takes the field type the union declared, positionally and by name, in both
the `Full { v }` shorthand and the `Full { v: got }` renaming form.

**The union comes from the matched value's type, not from the variant name.** A name would need
an index and would be ambiguous the moment two unions share a variant — the mistake `TOAST-0108`
had to undo in the exhaustiveness checker two days ago. The matched value's type is already in
hand, so there is nothing to guess.

A field with no declared type contributes nothing, and there is a test for that: a union that did
not say what it holds cannot have anything claimed about it.

### Substitution — done 2026-09-04

`MyOpt<Payload>` resolves to a `GenericInstanceType` wrapping the union, and that is where the
type arguments live. Binding the union's own parameter names to them lets a field declared `T`
take what the use site supplied; without it the field type is the text `T`, which names no type
anywhere, so the binding stayed dynamic and nothing was checked.

Both payload spellings substitute: `Some(value: T)` and the bare `Some(T)` the core prelude
itself uses. An *uninstantiated* `MyOpt` narrows nothing, and has a test — there is nothing to
substitute and claiming a type there would be inventing one.

### Ambient unions — done 2026-09-04

`Lowerer.Lower` takes an optional ambient tree whose declarations are in scope without appearing
in this source, and the engine passes the core prelude. So `Option<Payload>` and
`Result<Payload, Problem>` now type their payloads, both of `Result`'s parameters included.

The plumbing was far smaller than estimated, for one reason worth recording: **the prelude is
already a cached static `ParseResult` on the engine**, loaded exactly as the built-in runes are.
Nothing new is parsed, and no new snapshot shape was needed — the estimate of "a richer snapshot
threaded from the engine to the front end" was written without checking whether the syntax was
still in hand. It was.

Ambient declarations are visited *before* the source ones, and `Register` assigns rather than
`TryAdd`, so a source declaration displaces an ambient one. That is the shadowing rule the engine
already warns about but does not refuse, so the registry has to agree about who wins — checked by
a test that declares its own `Option`. It is also the precise mistake `TOAST-0108` had to undo two
days ago in the exhaustiveness checker, where the same seeding used `TryAdd` and the ambient
entry could never be displaced.

The parameter is optional because this entry point has 113 callers, and a control asserts that
one supplying nothing behaves exactly as before.





Both spellings now report, and both accept their real members:

```tosh
func f(o: MyOpt<Payload>)  { match ($o) { Some(v) => $v.Nope … } }   # reported
func g(o: Option<Payload>) { match ($o) { Some(v) => $v.Nope … } }   # reported
```
