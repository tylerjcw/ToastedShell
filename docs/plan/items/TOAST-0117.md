---
id: TOAST-0117
title: "A missing unary or indexer operator is reported as a binary mismatch against an operand nobody wrote"
status: proposed
area: toast
priority: 3
opened: 2026-09-10
---

## Problem

`TOAST-0056` gave classes `func -()`, `func +()` and `func [](i)`. The messages for a class
that *has not* declared them were never revisited, and neither names the thing that is
actually missing.

```tosh
class Plain(x) { prop X = $x }
var p = new Plain(1)

-$p        # Operator operands 'System.Int32' and 'Plain' are not compatible.
+$p        # Operator operands 'System.Int32' and 'Plain' are not compatible.
$p[0]      # Type 'Tosh.Language.ToshClassInstance' does not support index access
           # with 'System.Int32'.
```

The unary message describes a *binary* operation against a `System.Int32` that appears
nowhere in the source — an implementation detail of how unary minus is evaluated, offered
to the reader as though they had written it. The reader's next move is to look for the
integer, and there isn't one.

The indexer message is worse in a smaller way: `Tosh.Language.ToshClassInstance` is the
CLR class that backs *every* user-defined object. The name in the error should be `Plain`.

Neither message says what to write. `TOAST-0056` made both operators declarable, so both
have a one-line answer to point at.

## Shape of a fix

Where the unary path falls through, it knows the operator and the operand's Tōast type;
report those rather than the synthesised binary form, and offer `func -()` in the help.
The indexer path needs the receiver's Tōast type name in place of its CLR type — the
descriptor is already on the instance — and `func [](i)` in the help.

Worth checking while there: whether the same synthesised-operand phrasing reaches any
other unary site, and whether `$p[0] = 1` on a class without `func []=(i, v)` reports
better or worse than the getter does.

## Acceptance

- [ ] `-$p` on a class without `func -()` names `Plain` and the operator, with no invented operand
- [ ] `+$p` likewise
- [ ] `$p[0]` names `Plain`, not `ToshClassInstance`
- [ ] Each help line names the member to declare
- [ ] `$p[0] = 1` without a setter is checked and reported to the same standard
