---
id: TOAST-0113
title: "A qualified refinement type in a type test is evaluated as module member access"
status: proposed
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

- [ ] `$v is M.T.Base` answers the refinement, for both the dotted and `::` spellings
- [ ] `is-not` negates it
- [ ] A qualified refinement over a qualified refinement chains
- [ ] `as M.T.Base` converts, and runs the coercer
- [ ] A qualified name that is genuinely not a member still reports usefully
- [ ] Declared classes and records qualified the same way are unaffected

## Notes

Found while fixing `TOAST-0104`, by probing whether the alias resolved at all. It did — through
the annotation path. The type test was the surface that did not.
