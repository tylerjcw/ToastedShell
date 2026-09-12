---
id: TOAST-0132
title: "A trait's default body runs in the adopting class's scope, so a library trait cannot name its own types"
status: proposed
area: toast
priority: 2
opened: 2026-09-12
---

## Problem

`TOAST-0122` made a library's *annotations* resolve where they were written. A
trait's **body** still does not.

```tosh
require ToastLib.Math          from "…/ToastLib.Math.tosh" as M
require ToastLib.Math.Geometry from "…/ToastLib.Math.tosh" as Geo

class Square(origin, side) uses Geo.Polygonal, Geo.Shape2D, Geo.Enclosing {
    func Vertices() -> array { … }
}

(new Square((P 0 0), 4.0)).Area()     # 16 — fine
(new Square((P 0 0), 4.0)).Bounds()   # Unable to resolve type
                                      #   'ToastLib.Math.Point2D<double>'
```

`Polygonal.Bounds()` is a default body that builds its answer with

```tosh
return new ToastLib.Math.Geometry.Rectangle(
    new ToastLib.Math.Point2D<double>($minX, $minY), …)
```

Trait defaults are **copied** into the adopting class as ordinary methods
(`ToshEngine.Types.cs`, the two-pass injection), which loses where they came
from. The body then executes with the *class's* scope — the user's file — and an
aliased import has no `ToastLib` there.

So a trait is usable from a library only if its default bodies never name a type,
which is the opposite of what a trait is for: `Polygonal` exists precisely to
derive `Bounds`, `Center` and `Area` for you.

## Why it is not `TOAST-0122`

Different mechanism, and the fix for one does not reach the other. That row was
about *annotations*, resolved through `AnnotationResolutionExports` /
`TryGetNamedType` at overload-scoring time. This is a `new` expression in a
running body, resolved through ordinary type lookup.

## Shape of a fix

`ToshTraitDefinition` does not carry a `DeclaringExports` at all — classes gained
one, traits never did. Capturing it at trait declaration (the same scope-stack
walk used for a class) and carrying it on the injected
`ToshClassMethodDefinition` would give the body its home module back.

Installing that as `AnnotationResolutionExports` for the body's duration may be
enough on its own, since `TryGetNamedType` consults it — but it would also need
the **ancestor** case that `TOAST-0122` deliberately left out: the trait lives in
`ToastLib.Math.Geometry` and names `ToastLib.Math.Point2D`, so stripping the
module's *own* prefix does not reach it. A parent link on `ModuleExportTable`
would let the lookup walk outward through enclosing modules, which is the piece
that row stopped short of.

## Workaround

Require the library plainly as well as by alias, in the file that adopts the
trait. `docs/geometry/09-traits.md` explains this at the one place it is needed.

## Acceptance

- [ ] A class using a library trait resolves the types that trait's bodies name,
      after an aliased import alone
- [ ] A selective import still binds only what was asked for
- [ ] `geometry/09-traits.md` drops the last plain `require` and its note
