---
id: TOAST-0107
title: "The path operator is unusable inside an interpolation hole, because `::` is read as a format clause"
status: complete
area: toast
priority: 2
opened: 2026-09-02
---

## Problem

`$"{Level::Novice}"` did not run. The hole splitter scans for the `:` that begins a format
clause, and `::` is two colons, so the hole split into the expression `Level` and the format
`:Novice`:

```
✖ error  tosh.runtime.invalid_format_clause
  A value of type 'bool' cannot be formatted with ':Thing'.
  echo $"{$t is IR::Thing}"
                  ┄┄┄┄┄┄┄┄─▶ ':Thing' does not apply to this value
```

Written as a statement it also produced `Command 'Level' is not a registered builtin`, because
the truncated expression is a bareword at command position. Neither message names the real
cause, and both point at the wrong half of the line.

Found immediately after `TOAST-0090` shipped, by writing `echo $"{$t is IR::Thing}"` in a
verification script for an unrelated item. Interpolation is one of the most common places a
value is named, so the path operator was unusable in a large fraction of real code while every
test of it passed — the corpus never wrote a path inside a hole.

## Cause and fix

`SplitInterpolationClauses` already had this exact shape of problem and the fix for it. A `?`
opens a conditional whose `:` is not a format clause, and `??` is null-coalescing and opens
nothing — so the scanner looks ahead one character and skips the pair. `::` needed the same
treatment and did not have it.

The one-line risk is over-correcting into "ignore every colon", which would take the format
clause away from a path. It does not: `::` is skipped, and the next single `:` still begins the
clause.

## Acceptance

- [x] `$"{Level::Novice}"` renders the member
- [x] A union variant path in a hole renders
- [x] A format clause *after* a path still binds — `{Fuel::Uranium.UnderlyingValue:X}` is `C`
- [x] A path composes with an alignment — `{Rank::Novice,10}`
- [x] The ternary colon, `??`, a plain format clause and a plain alignment are unchanged
- [x] The tests live with the other interpolation-clause tests, and the class docstring records
      this as the *second* ambiguity after the ternary

## Notes

The general lesson is narrow and worth keeping: a new operator whose spelling reuses a character
that a *different* scanner treats as a delimiter has to be checked against every such scanner,
not only against the parser. The parser was fine throughout — it was the interpolation lexer,
which never sees the grammar, that was wrong.
