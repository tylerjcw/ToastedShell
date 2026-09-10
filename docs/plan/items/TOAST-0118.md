---
id: TOAST-0118
title: "A generic method's own type parameter is unbound in anything it constructs"
status: proposed
area: toast
priority: 2
opened: 2026-09-10
---

## Problem

`TOAST-0116` fixed the case where a *class* rebuilds itself and `T` is the class's type
parameter. The same name written inside a generic **method** is still unbound, because it
comes from a different place:

```tosh
class A<T>(x: T) {
    prop X: T = $x
    shared func Make<U>(v: U) -> A<U> => new A<U>($v)
}

(type-of (new A<int>(1))).Name        # A<Int32>
(type-of A.Make<int>(1)).Name         # A<U>,  TypeArguments [null]
```

Static and instance forms behave the same way, and so does a free generic function:

```tosh
func MakeRec<T>(v: T) -> R<T> => new R<T>($v)   # R's T is unbound
```

This is the same soundness hole as `TOAST-0116` — a null binding is read as "nominal only,
accept anything", so the constructed value stops enforcing its own constraint — reached
through a second door.

## Where it bites

ToastLib's factories are exactly this shape: `Point2D.Empty<T>()`, `Vector2D.Zero<T>()`
and `Vector3D.Zero<T>()` each end in `new …<T>(…)` inside a method that declares its own
`<T>`. So `Point2D.Empty<int>()` answers a `Point2D` whose `T` is unbound, and equality
against it still passes because equality is componentwise — which is why the suite did not
notice.

## Cause

`BindFunctionParameters` builds a `typeBindings` dictionary for a generic call — from
explicit call-site arguments first, then from the target-type annotation, then by
inference. That dictionary is used for one thing: converting the return value
(`ConvertFunctionReturnValue`). It never enters a scope, so nothing evaluated inside the
body can see it.

`TOAST-0116`'s fix reaches the *receiver's* bindings through `this`, which is in scope
because the body genuinely has one. A method's own type parameters have no such carrier.

## Shape of a fix

Give the call's `typeBindings` a scope of their own for the duration of the body, and have
`TryResolveTypeParameterFromReceiver`'s sibling consult it before the receiver — the
method's parameter shadows the class's, as it does in C#, so a method that reuses the name
`T` gets its own. The two lookups then compose: a method parameter first, then the
enclosing instance's.

The shadowing itself is worth a warning independently: a method that declares `<T>` inside
`class A<T>` almost always means the class's, and today the two are indistinguishable at
the point of use.

## Acceptance

- [ ] `A.Make<int>(1)` reports `A<Int32>` for both `shared` and `static` forms
- [ ] A free `func MakeRec<T>(v: T) -> R<T>` binds the record's `T`
- [ ] The constructed value enforces its constraint
- [ ] A method parameter shadows a class parameter of the same name, and the class's is
      still reachable where the method declares none
- [ ] ToastLib's `Point2D.Empty<int>()` reports `Point2D<Int32>`, with a suite check that
      asks about the closure rather than the components
