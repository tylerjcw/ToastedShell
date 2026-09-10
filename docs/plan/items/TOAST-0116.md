---
id: TOAST-0116
title: "A generic class that rebuilds itself inside its own method loses what it was closed over, constraint included"
status: complete
area: toast
priority: 1
opened: 2026-09-10
---

## Problem

`new A<T>(…)` written *inside* a method of `A<T>` produces an instance whose `T` is
unbound. The value is right; the type is not:

```tosh
class A<T>(x: T) {
    prop X: T = $x
    func Same() -> A<T> => new A<T>($this.X)
}
var a = new A<int>(1)

(type-of $a).Name             # A<Int32>,  TypeArguments [System.Int32]
(type-of $a.Same()).Name      # A<T>,      TypeArguments [null]
```

It does not recover on a second pass — `$a.Same().Same()` is still `A<T>` — and it is not
cosmetic, because the binding is what the constraint is checked against:

```tosh
class A<T>(x: T) where T: Numeric { … }
var a = new A<int>(1)

$a.X = "not a number"           # refused, correctly
$a.Same().X = "not a number"    # accepted
```

So a generic class stops enforcing its own `where` clause the moment a value has passed
through any method that rebuilds it — which for an immutable type is every method.

## Where it bites

ToastLib's `Math` types are exactly this shape. `Componentwise.Combine` finishes with
`$this.FromComponents($out)`, and each `FromComponents` ends `new Point2D<T>($vx, $vy)`.
Every operator result, every `WithX`, every `Normalize` therefore comes back unbound:

```tosh
var p = new ToastLib.Math.Point2D<int>(3, 4)
(type-of $p).Name          # Point2D<Int32>
(type-of ($p + 1)).Name    # Point2D<T>
(type-of (-$p)).Name       # Point2D<T>
(type-of $p.WithX(9)).Name # Point2D<T>
```

The suite did not catch it because its type checks ask about the *component* —
`(type-of ($pint + 1).X).Name` is `Int32`, and that is true — rather than about the
value's own closure.

## Cause

`ToshEngine.Arguments.cs` resolves a `new`'s type arguments through
`ResolveTypeArgument`, which is `ResolveTypeName` plus a named-type guard
(`ToshEngine.cs:5636`). Neither knows about type *parameters*, so `"T"` resolves to
`null`, and `CreateGenericInstanceAsync` is handed `[null]` with the display list
`["T"]` — which is precisely what `type-of` then reports.

Nothing is missing from the runtime: `TOAST-0114`'s sibling fix (`11407d4`) already made
the self-reference hand back the instance's own descriptor, so `(type-of $this).TypeArguments[0]`
inside the method answers `System.Int32`. The binding is in hand at the moment `new A<T>`
is evaluated; the `new` path simply does not consult it.

## Shape of a fix

Before falling back to `ResolveTypeName`, resolve a type-argument name against the type
parameters of the class whose method is currently executing, using the receiver's
bindings. The lookup wants to be by name rather than by position, so that
`class Pair<K, V>` rebuilding as `new Pair<V, K>` swaps rather than silently keeps its
order.

Records have the same construction path (`TryInferTypeArgumentsFromRecordFields` and the
explicit branch below it) and should be checked for the same hole. Unions bind through
`ToshUnionDefinition.BindTypeArguments`, which takes a different route and may already be
correct.

## Acceptance

- [x] `new A<T>(…)` inside `A<T>` yields an instance closed over the receiver's `T`
- [x] The rebuilt instance still enforces `where T: Numeric`
- [x] `class Pair<K, V>` rebuilding as `new Pair<V, K>` swaps the bindings
- [x] A rebuild that names a real type — `new A<int>(…)` inside `A<T>` — is unaffected
- [x] The same holds for generic records, and unions are confirmed either way
- [x] ToastLib's `Point2D<int> + 1` reports `Point2D<Int32>`, with a suite check that asks
      about the value's own closure rather than its components'

## What the fix turned out to be

Three construction paths, one cause. Classes resolve through `ResolveTypeArgument`, records
through a bare `ResolveTypeName` beside it, and unions through
`ToshUnionDefinition.BindTypeArguments`, which binds annotation *names* rather than CLR
types. All three now consult the receiver first, reached through `this` in scope so the
lookup follows the same path the method body does and needs no separate notion of a
"current class". Bindings are keyed on the accessor, so a base method writing
`new Base<T>` resolves `T` as *Base* declared it.

The union symptom was different enough to look unrelated: rather than a wrong type it was
`'union 'Box'' uses unknown type annotation 'T'`, reported against the union's own
declaration — often another file from where the mistake was made.

Generic *methods* remain: `A.Make<int>(1)` where `Make<T>` is the method's own parameter is
still unbound, because a call's type bindings are computed for return-value conversion and
never enter a scope the constructor can see. Filed separately as `TOAST-0118`.
