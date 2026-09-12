---
id: TOAST-0131
title: "A generic body cannot ask what its type parameter is bound to"
status: proposed
area: toast
priority: 3
opened: 2026-09-12
---

## Problem

Inside `func f<T>()`, nothing in the language reaches the type `T` is bound to.

```tosh
func f<T>() => nameof(T)     # "T"  — the parameter's own name
func f<T>() => T             # "T"  — a bareword string
func f<T>() => typeof T      # tosh.bind.unknown_command
func f<T>() => (T as type)   # tosh.runtime.expression_failed
func f<T>() => (default T)   # tosh.runtime.unknown_command
```

The runtime knows. `EnterTypeParameterBindings` puts `T → System.Double` in scope for
the body's duration (`TOAST-0118`), and `TryResolveTypeParameterFromReceiver(name, out
Type? bound)` is an existing internal engine method that answers exactly this question.
The gap is surface only: no expression form consults that dictionary.

Today the type is reachable only through a *value* of it — `func f<T>(sample: T) =>
($sample | type-of)` — which forces a parameter the function may not otherwise need, or
through an instance, which knows its own closure (`(new Box<double>(1.0)) | type-of`
renders `Box<Double>`).

## Why the obvious spelling does not mean what it looks like

Raised as:

```tosh
static func Empty<T>() -> Point2D<T> => match (T) {
    _ is double => new Point2D<double>(0.0, 0.0)
    _ is float  => new Point2D<float>(0.0, 0.0)
}
```

In `match ($x) { _ is double => … }` the `_` is the **matched value** and `is` tests
that value's type. If `T` evaluated to a `Type` object, `_ is double` would be asking
whether a `Type` object is a `double` — false for every arm. The syntax reads correctly
and the semantics underneath point the other way, so this needs a form of its own
rather than falling out of what exists.

## Two candidate spellings

**A — a boolean primitive.** True when the type bound to `T` is that type. Composes
into `if`, a ternary, a `match` guard — anywhere a bool goes, which makes it the
smaller and more general of the two.

```tosh
static func Empty<T>() {
    if (T is double)  { return new Point2D<double>(0.0, 0.0) }
    if (T is decimal) { return new Point2D<decimal>(0, 0) }
    return new Point2D<T>(0, 0)
}
```

**B — `match` over the type parameter**, with bare type names as arms. Closest to the
shape that was asked for, but a special form rather than a primitive.

```tosh
static func Empty<T>() => match (T) {
    double  => new Point2D<double>(0.0, 0.0)
    float   => new Point2D<float>(0.0, 0.0)
    default => new Point2D<T>(0, 0)
}
```

B can be built over A's resolution if both are wanted.

## Why this is not scheduled

**The case that raised it does not need it.** `new Point2D<T>(0, 0)` already widens the
literal to `T` — `Empty<double>()` stores a `Double`, `Empty<float>()` a `Single`,
`Empty<decimal>()` a `Decimal` — which is what `TOAST-0124` put in deliberately, and it
is why a generic factory can be written at all. The reported failure was
`TOAST-0130`, in the annotation on the *receiving* side, and had nothing to do with the
factory.

**And a type switch inside a generic is the thing traits exist to avoid.** It has to
enumerate, so it silently stops working the moment the generic is closed over a type
the switch does not list — whereas `where T: Numeric` with a trait member extends to
types the author never saw. Both already exist. The legitimate uses are the ones where
the set really is closed and known: marshalling widths, interop shapes, format
selection.

Recorded rather than built, so the design work is not lost if one of those cases turns
up. Deferred by decision on 2026-09-12, not by omission.
