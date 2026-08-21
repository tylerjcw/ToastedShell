---
id: TOAST-0029
title: "`is` matches a CLR value's exact type name only, so `$x is IEnumerable` and `$e is Exception` are always false"
status: complete
area: toast
priority: 2
opened: 2026-08-20
closed: 2026-08-21
---

## Problem

For a value that is a CLR object rather than a declared class instance, `is` compares the
**type name** and nothing else. Base types and interfaces are not matched:

```tosh
echo (5 is object)                  # true  -- `object` is special-cased
echo (5 is ValueType)               # false -- the base is not walked
echo ([1,2] is IEnumerable)         # false -- interfaces are not matched
var ex = new System.InvalidOperationException("x")
echo ($ex is Exception)             # false
echo ($ex is InvalidOperationException)  # true -- only the exact name
```

A declared class instance behaves correctly — `ToshClassInstance.IsInstanceOf` walks its
base chain, its interfaces and its traits — so the two halves of `is` disagree about what
the operator means.

## Why it matters beyond tidiness

`$x is IEnumerable` is the natural way to ask "can I iterate this", and it is always false.
`$e is Exception` is the natural way to ask "did I catch a CLR failure", and it is always
false — which is half of why a caught runtime diagnostic cannot be identified
(`TOAST-0018`'s exception-semantics section records the other half).

## The decision this needs first

Making `is` mean CLR assignability is the obvious fix and it **contradicts a rule already
specified**. A `str` is an atom, not a sequence — `§Collection Shape` says so and
`"abc" | count` is 1 — but `string` implements `IEnumerable<char>`, so pure assignability
would make `"abc" is IEnumerable` true. The operator would then disagree with the pipeline
about what a string is.

So the question is what `is` tests:

1. **CLR assignability**, accepting that `"abc" is IEnumerable` is true and the operator
   describes the host's type graph rather than the language's value model.
2. **Assignability, with the language's own atoms excluded** — a string is not a sequence
   for `is` either, matching `§Collection Shape`. Consistent, and needs the exception list
   written down and kept in step with the shape rules.
3. **Named types only, as today**, specified as such — no base or interface matching for
   CLR values, and `is` is only useful against an exact type.

Option 2 is the one that keeps the value model coherent, and it is also the one that needs
a list nobody has written yet.

## Acceptance

- [x] `is` agrees with itself: a declared class instance and a CLR value answer the same
      kind of question
- [x] `$ex is Exception` is true for a CLR exception
- [x] Whatever is decided about `"abc" is IEnumerable`, it agrees with `§Collection Shape`
      and both say so — and they share one predicate rather than two lists
- [x] A caught runtime diagnostic can be identified by a **portable** spelling — **the
      defect half is done and the design half is `TOAST-0031`.** `$e is Exception` now
      works where only `$e is ToshDiagnosticException` did, so a handler can tell a
      diagnostic from a declared error from a plain thrown value. `Exception` is still a
      CLR name, and giving the category a Tōast one is a naming decision this item was not
      scoped to make — split out rather than left hanging here
- [x] `is` against a declared class, interface and trait is unchanged, pinned as controls
- [x] A negative control — 9 of 24

## Resolution — 2026-08-21

**The defect was name resolution, not assignability.** `IsType` already used
`IsInstanceOfType` at three points; it simply could not turn a bare name into a type,
because its only general fallback was `Type.GetType`, which needs an assembly qualifier. A
bare name now resolves against the same platform index an import consults, so a name means
one thing wherever it is written.

That measurement changed the item's framing entirely: the options as filed argued about
what `is` should *mean*, when the operator already meant the right thing and could not
find the type.

### The fork the decision did not cover

The options assumed `"abc" is IEnumerable` was false and asked whether to keep it so.
Measuring found the **qualified** spelling was already `true`:

```tosh
("abc" is System.Collections.IEnumerable)   # true, before any change
```

So "exclude the language's atoms" had to say *which spellings* it applied to, which the
decision as taken could not. Asked and answered: a **bare** name asks about the language's
value model and answers per `§Collection Shape`; a **namespace-qualified** name asks about
the host type graph and is answered literally, because answering it any other way would
mislead code written to bridge to .NET.

### One predicate, not two lists

The acceptance asked for the exception list to be "kept in step with the shape rules". It
is kept in step by not existing: `is` consults
`ShellIterationUtilities.IsExpandableForIteration`, the same predicate the pipeline uses to
decide what spreads. A test asserts the two agree per value rather than trusting them to.

The rule fires only for a *sequence* question — a target that is an interface assignable
from `IEnumerable` — so `is string` and `is IComparable` are untouched.

## Notes

Found closing `TOAST-0018`'s exception-semantics box. The neighbouring half of the defect —
a class declared `extends Error` not being `is Error` — was fixed there, along with a second
bug the same probe exposed: the CLR base was consulted only on the instance's own
definition, so **two** levels of inheritance from a built-in matched nothing at all.

The portable-spelling half is the part Phase A actually needs: a `no_clr` target has no
`ToshDiagnosticException` to name, so a script that catches a runtime error today is
written against an implementation detail.
