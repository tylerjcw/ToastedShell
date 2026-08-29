---
id: TOAST-0090
title: "Static member access and instance member access are the same operator, so a path cannot be told from a lookup"
status: proposed
area: toast
priority: 2
opened: 2026-08-28
---

## Problem

`Type.Member` and `$value.Member` use the same `.`, so nothing in the syntax says which is
happening:

```tosh
Profession.Librarian        # an enum member — a name in a type
System.Math.PI              # a static property — a name in a type
System.DateTime.Now         # a static property that returns a different value each call
$villager.Name              # a member of a value
```

The reader resolves the difference from knowledge of the types, and so must every tool. The
language already draws exactly this distinction for variables: `$name` says *this is a
variable, not a command word*. Static access has no equivalent mark.

## Candidate surface

```tosh
Profession::Librarian
Result::Ok(5)
System::Math::PI
$villager.Name              # unchanged
```

`::` reaches into a *type*; `.` reaches into a *value*. Both spellings would be accepted for a
transition, with `.` on a type-qualified path becoming the discouraged form.

## Why it earns its place beyond readability

Found while designing `TOAST-0092`. A data notation must admit enum members and reject static
property access, because a static getter can be nondeterministic or ambient — measured, not
assumed:

```
System.Math.PI                  → 3.141592653589793
System.DateTime.Now.Year        → 2026            (nondeterministic)
System.Environment.MachineName  → valinor         (reads the machine)
System.Guid.NewGuid()           → works           (side effect)
```

Under one operator, the notation's rule has to be *semantic* — "this looks like member access
but means a table lookup" — and a validator bug turns a document into a script. Under two
operators the rule is *syntactic*: member access is not in the grammar at all, so no bug in the
validator can admit it.

## Acceptance

- [ ] `::` resolves a name inside a type: enum members, union variants, static members, nested types
- [ ] `.` continues to resolve members of a value; the two are distinguishable in the AST
- [ ] Existing `Type.Member` source keeps working, with a stated migration and a `prefer-path` analysis
- [ ] Formatter, LSP, hover, completion and syntax highlighting treat the two distinctly
- [ ] Diagnostics say which operator was expected when a path is written as a member access
- [ ] `§Type System` and the operator table document the distinction
- [ ] Interpreter and compiler agree; the differential corpus covers both spellings
