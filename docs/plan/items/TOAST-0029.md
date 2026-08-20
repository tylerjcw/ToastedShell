---
id: TOAST-0029
title: "`is` matches a CLR value's exact type name only, so `$x is IEnumerable` and `$e is Exception` are always false"
status: open
area: toast
priority: 2
opened: 2026-08-20
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

- [ ] `is` agrees with itself: a declared class instance and a CLR value answer the same
      kind of question
- [ ] `$ex is Exception` is true for a CLR exception
- [ ] Whatever is decided about `"abc" is IEnumerable`, it agrees with `§Collection Shape`
      and both say so
- [ ] A caught runtime diagnostic can be identified by a **portable** spelling, not by the
      implementation type name it happens to have
- [ ] `is` against a declared class, interface and trait is unchanged, pinned as controls
- [ ] A negative control

## Notes

Found closing `TOAST-0018`'s exception-semantics box. The neighbouring half of the defect —
a class declared `extends Error` not being `is Error` — was fixed there, along with a second
bug the same probe exposed: the CLR base was consulted only on the instance's own
definition, so **two** levels of inheritance from a built-in matched nothing at all.

The portable-spelling half is the part Phase A actually needs: a `no_clr` target has no
`ToshDiagnosticException` to name, so a script that catches a runtime error today is
written against an implementation detail.
