---
id: TOAST-0121
title: "A range whose left operand is a variable is read as member access"
status: complete
area: toast
priority: 2
opened: 2026-09-10
---

## Problem

`$a..$b` did not make a range. It scanned as a single bareword, the parser read the two
dots as an accessor, and the failure was reported as

```
Member '$b' was not found on type 'Int32'
```

— a message about member access, against a line that contains none. `$a..3` failed the same
way. Both are warnings first and a runtime error second, so a script gets some way in
before anything says so.

Only the **left** operand mattered:

```tosh
1..3        # fine
1..$b       # fine   — a number cannot continue into a bareword
$a..3       # broken
$a..$b      # broken
$a .. $b    # fine   — spaced
($a)..($b)  # fine   — bracketed
```

That is what made it look arbitrary, and it means the spelling that fails —
`for i in $first..$last` — is the one most likely to be written. Found writing an ordinary
loop over a computed span.

## Cause

A bareword beginning with `$` swallows dots, so that `$x.Member.Path` stays one token. The
lexer never ended the word, so the range operator it would have emitted next was never
reached.

The lexer already broke a *numeric* prefix before `..` for exactly this reason
(`IsNumericRangePrefix`), and already broke a variable reference before `?.`. This is the
same break, missing.

A second guard then rejected what the lexer newly produced: the parser reports
`tosh.parser.accidental_double_dot` when a range operator is glued to a variable or member
access, on the theory that a finger slipped on the dot. Reasonable for `$obj..Name`; wrong
for `$a..$b`, where no member of that name could exist and a range is the only reading.

## Fix

Break the bareword before `..` when the text so far is a plain variable reference —
`$name` or `$name.Member` — so that a path keeps its dots and `$dir/../x` is untouched.
Then narrow the double-dot diagnostic to fire only when the right operand is a bareword
identifier, which is the only case where the alternative reading exists.

## Acceptance

- [x] `$a..$b`, `$a..3` and `$r.Low..$r.High` make ranges
- [x] `for i in $first..$last` iterates
- [x] `1..$b`, `$a .. $b` and `($a)..($b)` are unchanged
- [x] `$obj..Name` still asks whether a single dot was meant
- [x] `$HOME/..`, `../x` and `$HOME/...` stay single words
- [x] `$x.Y` and `$x?.Y` are unchanged
