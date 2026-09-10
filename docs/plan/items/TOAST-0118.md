---
id: TOAST-0118
title: "A generic method's own type parameter is unbound in anything it constructs"
status: complete
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

## What the fix turned out to be

Three things, not one.

**The bindings existed and had nowhere to go.** A generic call's type arguments were
computed — from the call site, the target annotation, or inference — and used for exactly
one purpose, converting the return value. Nothing evaluated inside the body could see them.
They now enter a saved-and-restored field for the body's duration, next to
`_currentReturnAnnotation`, which is the same shape and the same lifetime.

**Static methods never received them at all.** `InvokeStaticMethodAsync` on a ToastScript
class had no type-arguments parameter, and neither did the `IShellStaticType` overload on
the invoker. So `A.NoArg<int>()` had nothing to bind and — worse —
`A.WithArg<double>(1)` silently used what inference made of the argument instead of what
the call site asked for. A wrong answer, not an error.

**A class reached through a module took a third path.** `M.A.NoArg<int>()` resolves the
class as a *member* of the module, so the call went through the instance-method overload
even though the target is a class definition and the call is static. That path dropped the
type arguments too, and it is the one ToastLib's factories use.

Precedence is by name: `methodBindings` holds only what the method declared, so consulting
it before the class's bindings is exactly the C# rule that a method's `<T>` shadows the
enclosing type's. The other order converted `func Shadowed<T>(v: T)` inside `class A<T>` to
the *instance's* `T` and rejected the argument the caller passed.

## What it exposed

`Vector2D.Zero<double>()` now fails, and should be understood before it is read as a
regression. It used to "work" only because `T` was unbound and a null binding is read as
"accept anything" — the check was skipped, not passed. With `T` bound to `Double` the check
runs and refuses the integer `0`, exactly as `new Vector2D<double>(0, 0)` has always been
refused, on any build. That strictness is filed separately as `TOAST-0124`.

## Acceptance

- [x] `A.Make<int>(1)` reports `A<Int32>` for both `shared` and `static` forms
- [x] A free `func FreeMake<T>(v: T) -> A<T>` binds it too
- [x] A method with no parameters binds from the call site alone
- [x] A class inside a module binds them
- [x] An explicit type argument beats inference rather than losing to it
- [x] The constructed value enforces its constraint
- [x] A method parameter shadows a class parameter of the same name, and the class's is
      still reachable where the method declares none
- [x] ToastLib's `Point2D.Empty<int>()` reports `Point2D<Int32>`
