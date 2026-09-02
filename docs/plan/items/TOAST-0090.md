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

`::` reaches into a *type*; `.` reaches into a *value*.

**Decided 2026-08-28**: both spellings are accepted and **neither is discouraged**. The original
"`.` becomes the discouraged form" line is withdrawn — see `DECISIONS.md`. `::` reaches every
type-level member, static methods included; instance access stays `.`-only.

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

- [x] `::` resolves a name inside a type: enum members, union variants, static members, nested types
- [x] `.` continues to resolve members of a value; the two are distinguishable in the AST
- [x] Existing `Type.Member` source keeps working
- [~] A stated migration and a `prefer-path` analysis — **deferred by decision, 2026-08-28.**
      Neither spelling is preferred; `.` on a type is not being deprecated. Revisit only when
      `TOAST-0092`'s notation needs the distinction enforced.
- [ ] Formatter, LSP, hover, completion and syntax highlighting treat the two distinctly
- [x] Diagnostics say which operator was expected when a member access is written as a path
- [ ] …and when a path is written as a member access
- [ ] `§Type System` and the operator table document the distinction
- [x] Interpreter and compiler agree; the differential corpus covers both spellings

## Progress (2026-08-28)

The core is in and `tests/Tosh.Tests/PathOperatorTests.cs` covers it — 20 tests, negative
control confirms 12 of them fail with the operator disabled.

**The lexer needed no change.** `::` already stayed inside a bareword, which is why
`System::Math::PI` reached the engine as an unknown *command* rather than a parse error.
Recognition was the whole of the missing part.

**A path canonicalises to dots at parse time**, so every consumer below the parser — resolution,
lowering, the compiler — sees one spelling and needed no change. `StaticMemberAccessArgumentSyntax`
gains `UsedPathOperator`, which is what the formatter, hover and the migration analysis will read.
Interpreter/compiler agreement is close to structural for the same reason, and the differential
corpus asserts it anyway: if the two ever stop sharing that parse step, those cases say so.

**Recognition was spread across more routes than reading a path suggests.** Four separate places
reach a type name and each had to learn the operator on its own — a constructor target
(`new Outer::Inner()`), a static call (`System::Math::Max(…)`), an assignment target
(`B::S = 5`), and a type annotation (`var i: Outer::Inner`). All four were found by probing, not
by reading, which is why the test corpus covers each explicitly.

**`$p::X` was the sharpest find.** Applying the path operator to a *value* did not fail — the
token missed every member-access route and surfaced as the literal string `"$p.X"`, which reads
as success. It is now `tosh.parser.path_operator_on_value`. This is the operator confusion the
item exists to make visible, arriving from the direction the acceptance list did not name.

### What the operator already buys

`.` decides between a static path and a command invocation by **capitalisation** — a dotted name
is a path if its first segment is a known type or starts uppercase. That heuristic is why
`Geo.area 2` needed the `TS-P2-16` carve-out. `::` says which is meant, so it never takes the
command reading:

```tosh
module geo { func area(r) { return 2 } }

geo.area(1)         # 2     — parenthesised call, both spellings agree
geo::area(1)        # 2

geo.area            # argument-count mismatch: read as a *call* with no arguments
geo::area           # the function itself — a reference, not an invocation

geo.area 1          # 2     — command-style invocation
geo::area 1         # unknown_command: `::` is never a command invocation
```

**This cuts both ways and is not a strict improvement.** `::` retires the `TypeName` prefix
ambiguity by construction, and makes a bare path mean the member rather than a zero-argument
call. It also removes command-style invocation, which is a core TōSh spelling — so `::` is the
wrong operator to reach a module function you intend to call shell-style, and the two operators
are not interchangeable in command position.

### Found while probing, filed separately

`TOAST-0095` — `is` against a nested type is always false, and a qualified variant pattern
(`Result.Ok(v)`) never matches. Both reproduce in the dotted spelling and predate this item.
Once the pattern case matches, this item's corpus should gain it.
