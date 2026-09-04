---
id: TOAST-0113
title: "A qualified refinement type in a type test is evaluated as module member access"
status: complete
area: toast
priority: 2
opened: 2026-09-04
---

## Problem

`TOAST-0111` made `is` answer for a refinement type. It answers for the *unqualified* spelling
only:

```tosh
module M {
    module T {
        export type Base = string where _.Length > 0
    }
}

"hi" is M.T.Base

✖ tosh.runtime.expression_failed
  Member 'Base' was not found on type 'Tosh.Language.ToshModuleObject'.
```

The right operand of `is` is evaluated as an expression before the operator sees it, and
`M.T.Base` is read as member access on a module object. A refinement type lives in a module's
`RefinementTypes` table rather than its `Types` table, so the member lookup finds nothing and the
whole expression fails.

A declared class or record qualified the same way works, because those *are* in `Types`.

## Why it matters

Annotations already accept the qualified spelling — `var x: M.T.Base = "hi"` is the ordinary way
to name a library type — so this is another instance of the same disagreement `TOAST-0105` and
`TOAST-0111` were about: a name that one surface resolves and another does not. It is louder than
those were, which is the only good thing about it.

## Direction

Either the module member lookup consults `RefinementTypes`, or `is` recognises a qualified
refinement name before its right operand is evaluated — the way `TOAST-0111` intercepts the
unqualified one. The second is narrower; the first probably also fixes whatever else reads a
module's members expecting types.

## Acceptance

- [x] `$v is M.T.Base` answers the refinement, for both the dotted and `::` spellings
- [x] `is-not` negates it
- [x] A qualified refinement over a qualified refinement chains
- [x] `as M.T.Base` converts, and runs the coercer
- [x] A qualified name that is genuinely not a member still reports usefully
- [x] Declared classes and records qualified the same way are unaffected

## Notes

Found while fixing `TOAST-0104`, by probing whether the alias resolved at all. It did — through
the annotation path. The type test was the surface that did not.

## Fix — 2026-09-04

Taken at the module rather than at the operator: `ToshModuleObject.TryGetMember` consults
`RefinementTypes` alongside `Types`. That was the direction worth preferring, because the member
lookup is what every surface goes through — the operator-side alternative would have fixed `is`
and left the next reader of a module's members with the same hole.

Two consequences followed from the member now resolving:

**`is` receives the definition, not a name.** An unqualified test arrives as text and an alias has
to be looked up; a qualified one arrives as the `RefinementTypeDefinition` itself. The
definition-testing core is shared, so both spellings check the same chain.

**`as` had to resolve the alias in its own module.** The definition's `Name` is the bare `Base`,
which means nothing where the cast is written — the first attempt reported
`unknown type annotation 'Base'`. Installing the declaring scope, exactly as `TOAST-0104` does
for a base chain, is what makes the bare name resolvable again.

Full suite green with no changes: 7,146 passing.

## The three-item pattern this closes

`TOAST-0102`, `TOAST-0104` and this one were filed as possibly sharing a cause. They do not share
a *mechanism* — they sit in the parser, the annotation resolver and the module member table
respectively — but they share a shape, and it is worth writing down:

> A name behaves differently depending on whether it is written qualified, because the
> unqualified path consults a table the qualified path does not, or vice versa.

Each fix was to make the two paths consult the same thing rather than to special-case the
spelling. `TOAST-0102` went further and removed the distinction from the parser entirely, since
whitespace decides it without any table at all — which is the ideal version of this fix, and the
one to reach for when a fourth instance appears.
