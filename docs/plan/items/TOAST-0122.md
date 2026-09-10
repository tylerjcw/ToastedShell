---
id: TOAST-0122
title: "A library's own type annotations resolve in the caller's namespace, so an aliased require breaks its overloads"
status: proposed
area: toast
priority: 2
opened: 2026-09-10
---

## Problem

`require X from "…" as Alias` binds the alias and nothing else — which is a defensible
selective import. What is not defensible is what it does to the imported code:

```tosh
require ToastLib.Math from "…/ToastLib.Math.tosh" as M

var p = new M.Point2D<double>(3.0, 4.0)
var v = new M.Vector2D<double>(10.0, 0.0)
$p.Translate($v)     # No overload matched 'Point2D.Translate' with 1 argument(s).
```

`Translate` is declared inside the library as

```tosh
func Translate(amount: ToastLib.Math.Vector2D<T>) -> Point2D<T>
```

and that annotation is resolved **where the call happens**, not where the method was
written. Under an alias the caller's namespace has no `ToastLib`, so the parameter type
resolves to nothing, the argument matches nothing, and the failure is reported as a missing
overload — a message about arity, for a problem about names.

The same call works after a plain `require "…"`, which registers the canonical names
globally. So the library appears to work or not depending on how the *caller* imported it,
which is the wrong dependency entirely.

## Why it matters more than it looks

The alias form is the readable one and the one the shipped examples use
(`require ToastLib.Gl from "…" as Gl`). It works for those because that module's public
methods happen not to annotate their own types. Any library that does — and annotating your
own types is good practice — is quietly unusable under an alias.

It is also silent until the call: the import succeeds, construction succeeds, and only the
annotated method fails, with a message that sends the reader to count arguments.

## Cause

Annotations are resolved by name against the ambient scope at the point of use. A
declaration does not carry the scope it was written in, so `ToastLib.Math.Vector2D` in
`Point.tosh` means "whatever `ToastLib.Math.Vector2D` resolves to for whoever is calling".

## Shape of a fix

A declaration's annotations should resolve in its declaring module's scope. That means
capturing that scope when the member is bound and consulting it first, falling back to the
ambient scope so unqualified annotations naming caller-supplied types keep working.

Two cheaper things are worth weighing first, because the full fix reaches every annotated
declaration:

- Have an aliased module import also make the module reachable by its canonical path.
  It fixes this case, but it makes a selective import non-selective, which is a real cost.
- Report it properly. Whatever else changes, "no overload matched with 1 argument"
  when the arity is right and a *parameter type* failed to resolve should say so and name
  the type it could not find.

The last one is worth doing regardless of which route the rest takes.

## Workaround

Require plainly to register the canonical names, then alias for readability:

```tosh
require "…/ToastLib.Math.tosh"
require ToastLib.Math.Geometry from "…/ToastLib.Math.tosh" as Geo
```

## Acceptance

- [ ] `$p.Translate($v)` works after an aliased import alone
- [ ] A selective import still does not bind names the caller did not ask for, or the
      decision to change that is recorded with its reasoning
- [ ] An unresolvable *parameter type* is reported as that, naming the type, rather than as
      an overload-arity mismatch
- [ ] The tutorials drop the workaround they currently explain
