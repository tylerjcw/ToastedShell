---
id: TOAST-0117
title: "A missing unary or indexer operator is reported as a binary mismatch against an operand nobody wrote"
status: complete
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

## What the fix turned out to be

The indexer half could not simply report when `func [](i)` is missing, because a class
*is* indexable by another route: `$p["X"]` reads a member the way a record does, and that
had to keep working. So the general path still runs and only its refusal is rewritten —
which needed a distinct exception rather than a match on message text, since the message
was the thing being changed.

Naming the value's shell type instead of its CLR type turned out to be worth doing at the
source, in `ShellIndexingUtilities`, rather than only for classes: every ToastScript object
is one `ToshClassInstance`, so the CLR name told the reader about the implementation and
nothing about their program. A genuine CLR value still reports its CLR name, which is what
a reader wants there.

`not` is deliberately left alone. Its fallback tests truthiness, which is a real answer for
any object, unlike `-` and `+`.

Regenerating the diagnostic manifest turned up a second thing: the generator's default
output path still pointed at `src/Tosh.Runtime/`, where the manifest lived before the
assembly split. A plain run wrote a stray copy into a directory no project compiles and
left the real manifest untouched — silently, because creating a file is not an error, and
easy to miss because the namespace is still `Tosh.Runtime.Generated`. Fixed with the rest.

## Acceptance

- [x] `-$p` on a class without `func -()` names `Plain` and the operator, with no invented operand
- [x] `+$p` likewise
- [x] `$p[0]` names `Plain`, not `ToshClassInstance`
- [x] Each help line names the member to declare
- [x] `$p[0] = 1` without a setter is checked and reported to the same standard
- [x] `$p["X"]` still reads a member, and `not $p` still tests truthiness
- [x] A non-class value keeps its CLR name in the same message
