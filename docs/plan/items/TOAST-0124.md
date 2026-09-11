---
id: TOAST-0124
title: "A value bound to a type parameter is checked for an exact type, so an integer literal cannot fill a double"
status: complete
area: toast
priority: 2
opened: 2026-09-10
---

## Problem

A generic class whose parameter is annotated with its own type parameter refuses any value
that is not already exactly that type — including a widening every other part of the
language performs silently:

```tosh
new ToastLib.Math.Vector2D<double>(0, 0)
```

```
'Vector2D.x' produced a value that could not be converted to 'System.Double'.
```

`0` is an `Int32` and the bound `T` is `Double`. Nothing is lost by widening it, and
ToastScript widens ints to doubles everywhere else — `$p + 0.5` promotes a whole point.
Here it is refused, so a `Vector2D<double>` cannot be written with whole-number
coordinates and `new Vector2D<double>(0.0, 0.0)` is the only spelling that works.

The library cannot work around it in a generic factory, which is where it hurts most:

```tosh
shared func Zero<T>() -> Vector2D<T> => new Vector2D<T>(0, 0)
```

There is no way to write "zero of T": `0` is right when `T` is `int` and wrong when it is
`double`, and the language offers no conversion to a type parameter.

## Why it surfaced now

It did not surface before because `TOAST-0118` was in the way: a generic method's type
parameter was never bound, so `Zero<double>()` produced a value whose `T` was null, and a
null binding is read as "nominal only, accept anything". The check was being skipped rather
than passed. With the binding correct, the check runs and refuses.

So this is pre-existing — `new Vector2D<double>(0, 0)` fails identically on a build from
before that work — and only the factories were shielded from it.

## Cause

`ToshClassDefinition.EnforceStrictBinding` accepts only `boundType.IsInstanceOfType(value)`.
It is a *check*, not a conversion, which is why permitting the widening is not enough on
its own: a `Vector2D<double>` holding an `Int32` in `X` would be a different lie from the
one being prevented. The value has to be converted, and the eight call sites currently
discard the checked value.

The strictness exists for a good reason — it is what stops a `Point2D<int>` quietly
accepting 3.5 — so the fix is to distinguish the two directions, not to relax the check.
Widening is lossless and should convert; narrowing should still be refused.

## Shape of a fix

Have the binding check return the value it approved, converting where a lossless numeric
widening exists, and have the call sites use the result. `int`→`double`, `int`→`long`,
`float`→`double` and the rest of the standard widenings; `double`→`int` and anything with a
fractional part stays an error.

Worth checking at the same time whether the same asymmetry applies to a `where T: Numeric`
constraint and to refinement types over a type parameter.

## Fix

`EnforceStrictBinding` became `CoerceStrictBinding` and returns the value to store, so the
eight call sites — property, constructor parameter, method parameter, rest element, return
value — write back what it approved rather than discarding it. Widening without converting
would have swapped one lie for another: a `Vector2D<double>` holding an `Int32` in `X`.

The permitted set is exactly C#'s implicit numeric conversions, written out as a table so
that what is allowed can be read rather than derived. `TypeConversion.TryConvert` was
deliberately *not* reused: it parses strings and truncates doubles, so it would take the
3.5 this check exists to refuse.

`int`→`float` and `long`→`double` lose precision and are implicit in C# regardless; this
follows C# rather than inventing a stricter rule, so that a reader who knows one knows the
other.

## Acceptance

- [x] `new Vector2D<double>(0, 0)` constructs, with `X` a `Double`
- [x] `new Point2D<int>(3.5, 0)` is still refused, as are strings and `long`←`1.5`
- [x] `Vector2D.Zero<double>()` and `Point2D.Empty<double>()` work
- [x] A widened value is *converted*, not merely permitted — `$v.X` is a Double
- [x] The same holds for method parameters and rest parameters, which share the check
- [x] A generic factory — `static func Zero<U>() -> Box<U> => new Box<U>(0)` — can be
      written and closes over both `int` and `double`
