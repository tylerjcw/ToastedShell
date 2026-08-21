---
id: TOAST-0032
title: "`...` spreads into a pipeline, so a collection's shape can be stated rather than inferred"
status: complete
area: toast
priority: 2
opened: 2026-08-21
closed: 2026-08-21
---

## Problem

The spread operator worked in three places — an array literal, a record literal and an
argument list — and stopped at the one a shell needs most:

```tosh
var xs = [1, 2, 3]
[0, ...$xs, 4]        # works
take3 ...$xs          # works
...$xs | count        # error: Command '...$xs' was not found
```

`...$xs` lexes as a single bareword, so in pipeline position it reached command dispatch
and was reported as an unknown command.

## Why it is worth more than the convenience

`TS-P3-04` asked for the cardinality lookahead to be removed "while preserving
object-valued pipelines and **a reasonable migration path**". The migration path was the
clause nobody had built.

Without a way to *ask* for spreading, changing the default is all-or-nothing across the
whole standard library — which is exactly how both attempts at `TOAST-0028` failed. The
only spelling available was `| each { $_ }`, which works, reads as a puzzle, and allocates
a block invocation per item to say "these are separate things".

With `...` in pipeline position there is an answer to "what do I write instead", so the
shape change stops being a cliff.

## Acceptance

- [x] `...$xs | count` sends elements, one item each
- [x] It spreads one level, and only what `§Collection Shape` calls a sequence — a record,
      a dictionary and a string are single values and pass through whole
- [x] The three existing spread contexts are unchanged, pinned as controls
- [x] `docs/spec/` carries it, and says it is the one pipeline form where shape is stated
      rather than inferred
- [x] A negative control — 8 of 11

## Resolution — 2026-08-21

Two small changes. The pipeline-stage parser recognises a spread ahead of command dispatch
and builds an expression stage carrying the existing `SpreadElementArgumentSyntax`; the
expression-stage evaluator yields its elements through
`ShellIterationUtilities.ExpandIterationItems` — the same predicate the rest of the
pipeline uses, so `...` cannot come to disagree with `§Collection Shape` about what a
sequence is.

**Purely additive**: the full suite passed unchanged on the first run. Nothing had to be
decided about existing behaviour, which is what made this worth doing while `TOAST-0028`
waits for a design pass.

### One test was written against a future

The first version of the migration test asserted `one | first` gives the array, which is
what it will do *after* `TOAST-0028` — today the lookahead spreads it and the answer is
`10`. A second version then drew a contrast that does not exist: a variable already
spreads, so `$v | first` and `...$v | first` agree. Both were corrected rather than
argued with; the test now asserts what is true and says why it does not assert more.

## Notes

Prompted by the user asking whether an `expand` or `spread` keyword existed. It does not,
and the naming is a trap: `expand` resolves to `/usr/bin/expand`, the coreutils tab
expander, which is why `[1,2,3] | expand | count` appeared to answer `3` when probed. A
word would shadow or be shadowed by whatever is on `PATH`; extending the operator that
already exists avoids the question entirely.

Prerequisite for `TOAST-0028`, and recorded there as such.
